using System.Diagnostics;

namespace FanMaxxxing.Core;

public enum CooldownPhase { Idle, Starting, Cooling, Restoring }

public enum CooldownEnd { TimeUp, SmartStop, Cancelled, Error }

public sealed record CooldownRequest(TimeSpan Duration, bool SmartStop, double CpuTarget, double GpuTarget, TimeSpan SmartStopMin);

public sealed record CooldownTick(TimeSpan Elapsed, TimeSpan Remaining, double Progress, SensorSnapshot Sensors, bool SmartStopPending);

public sealed record CooldownResult(
    CooldownEnd End,
    TimeSpan Elapsed,
    double? CpuStart, double? CpuEnd,
    double? GpuStart, double? GpuEnd,
    string? Error,
    string? RestoreError)
{
    public double? BestDrop
    {
        get
        {
            double? c = CpuStart is double a && CpuEnd is double b ? a - b : null;
            double? g = GpuStart is double x && GpuEnd is double y ? x - y : null;
            if (c == null && g == null) return null;
            return Math.Max(c ?? double.MinValue, g ?? double.MinValue);
        }
    }

    public bool Completed => End is CooldownEnd.TimeUp or CooldownEnd.SmartStop;
}

/// <summary>
/// Orquesta un enfriamiento. Vive en el hilo de UI (async/await), así no hay carreras con la interfaz.
/// Pase lo que pase (cancelación, error, excepción), el bloque finally devuelve los ventiladores a la normalidad.
/// </summary>
public sealed class CooldownEngine
{
    private CancellationTokenSource? _cts;
    private IFanBackend? _active;

    public CooldownPhase Phase { get; private set; } = CooldownPhase.Idle;
    public bool IsBusy => Phase != CooldownPhase.Idle;
    public IFanBackend? ActiveBackend => _active;

    public event Action<CooldownPhase>? PhaseChanged;
    public event Action<CooldownTick>? Ticked;

    private void SetPhase(CooldownPhase p)
    {
        if (Phase == p) return;
        Phase = p;
        PhaseChanged?.Invoke(p);
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* ya terminó */ }
    }

    /// <summary>Restauración de emergencia (cierre de sesión de Windows, excepción fatal).</summary>
    public void EmergencyRestore()
    {
        var b = _active;
        if (b == null) return;
        try
        {
            Log.Warn("Restauración de emergencia");
            b.RestoreBlocking(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en la restauración de emergencia", ex);
        }
    }

    public async Task<CooldownResult> RunAsync(IFanBackend backend, CooldownRequest req, Func<SensorSnapshot> sensors)
    {
        if (IsBusy) throw new InvalidOperationException("Ya hay un enfriamiento en marcha.");

        _cts = new CancellationTokenSource();
        _active = backend;
        var ct = _cts.Token;
        var sw = new Stopwatch();
        var start = sensors();
        CooldownEnd end = CooldownEnd.TimeUp;
        string? error = null, restoreError = null;
        SensorSnapshot last = start;

        Log.Info($"Enfriamiento: {req.Duration.TotalMinutes:0.#} min con {backend.Name}. Inicio CPU={start.CpuTemp} GPU={start.GpuTemp}");
        SetPhase(CooldownPhase.Starting);
        try
        {
            await backend.BoostAsync(ct);
            SetPhase(CooldownPhase.Cooling);
            PowerActions.KeepSystemAwake(true);
            sw.Start();

            int coolStreak = 0;
            DateTime lastSmartCheck = DateTime.MinValue;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (true)
            {
                var elapsed = sw.Elapsed;
                var remaining = req.Duration - elapsed;
                last = sensors();

                // Parada inteligente: si ya está todo fresquito durante 20 s seguidos, no hace falta seguir.
                if (req.SmartStop && elapsed >= req.SmartStopMin && (DateTime.UtcNow - lastSmartCheck).TotalSeconds >= 1)
                {
                    lastSmartCheck = DateTime.UtcNow;
                    bool any = last.CpuTemp != null || last.GpuTemp != null;
                    bool cpuOk = last.CpuTemp is not double c || c <= req.CpuTarget;
                    bool gpuOk = last.GpuTemp is not double g || g <= req.GpuTarget;
                    coolStreak = any && cpuOk && gpuOk ? coolStreak + 1 : 0;
                }

                Ticked?.Invoke(new CooldownTick(elapsed, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining,
                    Math.Clamp(elapsed.TotalSeconds / req.Duration.TotalSeconds, 0, 1), last, coolStreak > 0));

                if (remaining <= TimeSpan.Zero) { end = CooldownEnd.TimeUp; break; }
                if (coolStreak >= 20) { end = CooldownEnd.SmartStop; break; }

                await timer.WaitForNextTickAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            end = CooldownEnd.Cancelled;
        }
        catch (FriendlyException ex)
        {
            end = CooldownEnd.Error;
            error = ex.Message;
            Log.Warn("Enfriamiento abortado: " + ex.Message);
        }
        catch (Exception ex)
        {
            end = CooldownEnd.Error;
            error = ex.Message;
            Log.Error("Enfriamiento abortado por error inesperado", ex);
        }
        finally
        {
            sw.Stop(); // el tiempo de restauración no cuenta como enfriamiento
            SetPhase(CooldownPhase.Restoring);
            try
            {
                await backend.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            catch (Exception ex)
            {
                restoreError = ex is TimeoutException
                    ? "FanControl no ha respondido a tiempo al volver a la config normal."
                    : ex.Message;
                Log.Error("No se pudo restaurar el modo normal", ex);
            }
            PowerActions.KeepSystemAwake(false);
            sw.Stop();
            _active = null;
            var cts = _cts;
            _cts = null;
            cts?.Dispose();
            SetPhase(CooldownPhase.Idle);
        }

        var final = sensors();
        var result = new CooldownResult(end, sw.Elapsed,
            start.CpuTemp, final.CpuTemp ?? last.CpuTemp,
            start.GpuTemp, final.GpuTemp ?? last.GpuTemp,
            error, restoreError);
        Log.Info($"Enfriamiento terminado: {end} en {sw.Elapsed:mm\\:ss}. CPU {result.CpuStart}->{result.CpuEnd}, GPU {result.GpuStart}->{result.GpuEnd}");
        return result;
    }
}
