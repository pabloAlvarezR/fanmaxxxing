using LibreHardwareMonitor.Hardware;

namespace FanMaxxxing.Core;

public sealed record SensorSnapshot(double? CpuTemp, double? GpuTemp, double? FanRpm, string? CpuName, string? GpuName)
{
    public static readonly SensorSnapshot Empty = new(null, null, null, null, null);

    public double? MaxTemp => (CpuTemp, GpuTemp) switch
    {
        (null, null) => null,
        (double c, null) => c,
        (null, double g) => g,
        (double c, double g) => Math.Max(c, g),
    };
}

public interface ISensorSource : IDisposable
{
    /// <summary>Se dispara en un hilo de fondo (~1 Hz). Los suscriptores deben pasar al hilo de UI.</summary>
    event Action<SensorSnapshot>? Updated;
    SensorSnapshot Latest { get; }
    string Status { get; }
    void Start();
}

/// <summary>
/// Sensores reales (y control directo opcional) con LibreHardwareMonitor.
/// Todo el acceso al hardware va serializado con un lock y en un único hilo de fondo para el sondeo.
/// </summary>
public sealed class HardwareService : ISensorSource
{
    private readonly bool _withMotherboard;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly WaitHandle[] _waitHandles;
    private readonly TaskCompletionSource _openedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IHardware> _watched = new();
    private readonly List<ISensor> _fans = new();
    private readonly List<ISensor> _controls = new();
    private Thread? _thread;
    private Computer? _computer;
    private ISensor? _cpuTemp;
    private ISensor? _gpuTemp;
    private bool _boosted;
    private DateTime _lastBoostApply;
    private volatile bool _disposed;
    private volatile SensorSnapshot _latest = SensorSnapshot.Empty;
    private volatile string _status = Loc.T("sensors.starting");
    private volatile IReadOnlyCollection<string> _excluded = Array.Empty<string>();

    public HardwareService(bool withMotherboard)
    {
        _withMotherboard = withMotherboard;
        _waitHandles = new[] { _cts.Token.WaitHandle, _wake };
    }

    public event Action<SensorSnapshot>? Updated;
    public SensorSnapshot Latest => _latest;
    public string Status => _status;
    public bool HasMotherboard => _withMotherboard;

    /// <summary>Controles detectados (id, nombre) para el modo directo.</summary>
    public IReadOnlyList<(string Id, string Name)> ControlList
    {
        get
        {
            lock (_sync)
            {
                return _controls.Select(c => (c.Identifier.ToString(), $"{c.Hardware.Name} · {c.Name}")).ToList();
            }
        }
    }

    public void SetExcludedControls(IEnumerable<string> ids) => _excluded = ids.ToHashSet();

    public Task WaitOpenedAsync() => _openedTcs.Task;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "FanMaxxxing.Hardware",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// LibreHardwareMonitor tiene estado global (driver PawnIO, mutex del bus ISA): abrir un Computer
    /// mientras otro se está cerrando lo rompe. Serializamos aperturas y cierres entre instancias.
    /// </summary>
    private static readonly SemaphoreSlim Lifecycle = new(1, 1);

    private void Loop()
    {
        try
        {
            Lifecycle.Wait();
            try { Open(); }
            finally { Lifecycle.Release(); }
        }
        catch (Exception ex)
        {
            _status = Loc.T("sensors.none") + " " + ex.Message;
            Log.Error("No se pudo abrir LibreHardwareMonitor", ex);
        }
        finally
        {
            _openedTcs.TrySetResult();
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                lock (_sync)
                {
                    if (_computer != null && !_disposed)
                    {
                        foreach (var hw in _watched) UpdateHardware(hw);
                        ReapplyBoostIfDue();
                        _latest = BuildSnapshot();
                    }
                }
                if (!_disposed) Updated?.Invoke(_latest);
            }
            catch (Exception ex)
            {
                Log.ErrorThrottled("Error leyendo sensores", ex);
            }
            WaitHandle.WaitAny(_waitHandles, 1000);
        }
    }

    private void Open()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = _withMotherboard,
            IsControllerEnabled = false,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };
        computer.Open();

        lock (_sync)
        {
            if (_disposed)
            {
                computer.Close();
                return;
            }
            _computer = computer;
            Discover();
        }
    }

    private static void UpdateHardware(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw)
    {
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
            foreach (var s in sub.Sensors)
                yield return s;
    }

    private void Discover()
    {
        _watched.Clear();
        _fans.Clear();
        _controls.Clear();
        _cpuTemp = null;
        _gpuTemp = null;
        if (_computer == null) return;

        ISensor? bestGpu = null;
        int bestGpuRank = int.MaxValue;
        ISensor? boardCpu = null;

        foreach (var hw in _computer.Hardware)
        {
            UpdateHardware(hw);
            bool watch = false;
            var sensors = AllSensors(hw).ToList();

            // Sin historial: LibreHardwareMonitor guarda 24 h de valores por sensor si no se lo impedimos.
            foreach (var s in sensors) s.ValuesTimeWindow = TimeSpan.Zero;

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    var cpu = PickByName(sensors.Where(s => s.SensorType == SensorType.Temperature),
                        "CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "Tdie", "Tctl", "Core Max", "Core Average", "CPU");
                    if (cpu != null && _cpuTemp == null) { _cpuTemp = cpu; watch = true; }
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    int rank = hw.HardwareType == HardwareType.GpuIntel ? 1 : 0;
                    var gpu = PickByName(sensors.Where(s => s.SensorType == SensorType.Temperature),
                        "GPU Core", "GPU Hot Spot", "GPU Memory Junction", "GPU");
                    if (gpu != null && rank < bestGpuRank) { bestGpu = gpu; bestGpuRank = rank; }
                    if (gpu != null) watch = true;
                    foreach (var f in sensors.Where(s => s.SensorType == SensorType.Fan)) { _fans.Add(f); watch = true; }
                    if (_withMotherboard)
                        foreach (var c in sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
                        { _controls.Add(c); watch = true; }
                    break;

                case HardwareType.Motherboard:
                    if (_withMotherboard)
                    {
                        foreach (var f in sensors.Where(s => s.SensorType == SensorType.Fan)) { _fans.Add(f); watch = true; }
                        foreach (var c in sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
                        { _controls.Add(c); watch = true; }
                        boardCpu ??= PickByName(sensors.Where(s => s.SensorType == SensorType.Temperature), "CPU Core", "CPU");
                        if (boardCpu != null) watch = true;
                    }
                    break;
            }

            if (watch) _watched.Add(hw);
        }

        _gpuTemp = bestGpu;
        _cpuTemp ??= boardCpu;

        var parts = new List<string>();
        parts.Add(_cpuTemp != null ? "CPU ✓" : "CPU ✗");
        parts.Add(_gpuTemp != null ? "GPU ✓" : "GPU ✗");
        if (_withMotherboard) parts.Add(Loc.F("sensors.controls", _controls.Count));
        _status = Loc.T("sensors.label") + " " + string.Join(" · ", parts);
        Log.Info($"LHM abierto. {_status}. CPU={_cpuTemp?.Hardware.Name}/{_cpuTemp?.Name} GPU={_gpuTemp?.Hardware.Name}/{_gpuTemp?.Name} PawnIO={PawnIoInstalled()}");
    }

    public static bool PawnIoInstalled()
    {
        try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
        catch { return false; }
    }

    private static ISensor? PickByName(IEnumerable<ISensor> sensors, params string[] names)
    {
        var list = sensors.ToList();
        foreach (var n in names)
        {
            var exact = list.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var n in names)
        {
            var partial = list.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (partial != null) return partial;
        }
        return list.FirstOrDefault();
    }

    private SensorSnapshot BuildSnapshot()
    {
        double? fan = null;
        foreach (var f in _fans)
        {
            if (f.Value is float v && v > 0 && v < 20000) fan = Math.Max(fan ?? 0, v);
        }
        return new SensorSnapshot(
            Sane(_cpuTemp?.Value),
            Sane(_gpuTemp?.Value),
            fan,
            _cpuTemp?.Hardware.Name,
            _gpuTemp?.Hardware.Name);
    }

    private static double? Sane(float? v) => v is float f && f > 1 && f < 130 ? Math.Round(f, 1) : null;

    // ---------- Control directo ----------

    /// <summary>Pone al 100 % (o devuelve al control de la BIOS/driver) todos los ventiladores no excluidos.</summary>
    public Task<int> SetBoostAsync(bool boost) => Task.Run(async () =>
    {
        await _openedTcs.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        lock (_sync)
        {
            if (_computer == null || _disposed) return 0;
            _boosted = boost;
            _lastBoostApply = DateTime.UtcNow;
            return ApplyControls(boost);
        }
    });

    private int ApplyControls(bool boost)
    {
        int n = 0;
        var excluded = _excluded;
        foreach (var c in _controls)
        {
            try
            {
                var id = c.Identifier.ToString();
                if (boost && excluded.Contains(id)) continue;
                if (boost) c.Control.SetSoftware(100);
                else c.Control.SetDefault();
                n++;
            }
            catch (Exception ex)
            {
                Log.ErrorThrottled($"No se pudo controlar {c.Hardware.Name}/{c.Name}", ex);
            }
        }
        return n;
    }

    private void ReapplyBoostIfDue()
    {
        // Algunas BIOS/drivers "recuperan" el control; reaplicamos cada 10 s mientras dure el boost.
        if (!_boosted || (DateTime.UtcNow - _lastBoostApply).TotalSeconds < 10) return;
        _lastBoostApply = DateTime.UtcNow;
        ApplyControls(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignorar */ }
        try { _wake.Set(); } catch { /* ignorar */ }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(4000);

        lock (_sync)
        {
            try
            {
                if (_boosted) ApplyControls(false);
                _boosted = false;
            }
            catch (Exception ex) { Log.Error("Error devolviendo los ventiladores a automático", ex); }
            bool gotLock = Lifecycle.Wait(TimeSpan.FromSeconds(15));
            try { _computer?.Close(); }
            catch (Exception ex) { Log.Error("Error cerrando LibreHardwareMonitor", ex); }
            finally { if (gotLock) Lifecycle.Release(); }
            _computer = null;
            _watched.Clear();
            _fans.Clear();
            _controls.Clear();
        }
        Updated = null;
        _cts.Dispose();
        _wake.Dispose();
    }
}

/// <summary>Sensores de mentira para probar la app sin tocar hardware.</summary>
public sealed class SimulatedSensors : ISensorSource
{
    private readonly object _sync = new();
    private readonly Random _rng = new();
    private System.Threading.Timer? _timer;
    private double _cpu = 81, _gpu = 76, _rpm = 1150;
    private volatile bool _boosting;
    private volatile SensorSnapshot _latest = SensorSnapshot.Empty;
    private bool _disposed;

    public event Action<SensorSnapshot>? Updated;
    public SensorSnapshot Latest => _latest;
    public string Status => Loc.T("sensors.sim");

    public bool Boosting
    {
        get => _boosting;
        set => _boosting = value;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _timer != null) return;
            _timer = new System.Threading.Timer(_ => Step(), null, 0, 500);
        }
    }

    private void Step()
    {
        SensorSnapshot snap;
        lock (_sync)
        {
            if (_disposed) return;
            const double dt = 0.5;
            bool b = _boosting;
            double tau = b ? 55 : 600;
            double tCpu = b ? 31 : 50, tGpu = b ? 29 : 46, tRpm = b ? 2950 : 1100;
            _cpu += (tCpu - _cpu) * (dt / tau) + (_rng.NextDouble() - 0.5) * 0.6;
            _gpu += (tGpu - _gpu) * (dt / (tau * 1.15)) + (_rng.NextDouble() - 0.5) * 0.4;
            _rpm += (tRpm - _rpm) * (dt / 2.5) + (_rng.NextDouble() - 0.5) * 30;
            snap = new SensorSnapshot(Math.Round(_cpu, 1), Math.Round(_gpu, 1), Math.Round(_rpm), "CPU Simulada 9000X", "GPU Simulada RTX 9090");
            _latest = snap;
        }
        Updated?.Invoke(snap);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        Updated = null;
    }
}
