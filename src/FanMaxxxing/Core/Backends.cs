using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FanMaxxxing.Core;

/// <summary>Error "esperable" con un mensaje para humanos (se muestra tal cual en la UI).</summary>
public sealed class FriendlyException : Exception
{
    public FriendlyException(string message) : base(message) { }
}

public sealed record BackendStatus(bool Ready, string Title, string Detail);

public interface IFanBackend
{
    BackendKind Kind { get; }
    string Name { get; }
    BackendStatus GetStatus();
    Task BoostAsync(CancellationToken ct);
    /// <summary>Idempotente: se puede llamar aunque el boost no haya llegado a aplicarse.</summary>
    Task RestoreAsync();
    /// <summary>Versión síncrona para emergencias (cierre de sesión, excepción fatal).</summary>
    void RestoreBlocking(TimeSpan timeout);
}

// =====================================================================================
//  FanControl (Rem0o): cambiamos de configuración con "FanControl.exe -c archivo.json"
// =====================================================================================
public sealed class FanControlBackend : IFanBackend
{
    private readonly AppSettings _settings;
    private string? _exe;
    private string? _normalConfig;
    private bool _wasRunning = true;
    private volatile bool _boostAttempted;

    public FanControlBackend(AppSettings settings) => _settings = settings;

    public BackendKind Kind => BackendKind.FanControl;
    public string Name => "FanControl";

    public BackendStatus GetStatus()
    {
        var exe = FanControlLocator.FindExe(_settings.FanControlExePath);
        if (exe == null)
            return new(false, Loc.T("be.fc.notfound.title"),
                Loc.T("be.fc.notfound.text"));

        var normal = FanControlLocator.ResolveNormalConfig(exe, _settings.NormalConfigPath);
        if (normal == null)
            return new(false, Loc.T("be.fc.noconfig.title"),
                Loc.T("be.fc.noconfig.text"));

        if (_settings.UseCustomMaxConfig && !File.Exists(_settings.CustomMaxConfigPath))
            return new(false, Loc.T("be.fc.nomax.title"), Loc.T("be.fc.nomax.text"));

        bool running = FanControlLocator.IsRunning();
        return new(true, running ? Loc.T("be.fc.connected") : Loc.T("be.fc.ready"),
            Loc.F("be.fc.normal", Path.GetFileName(normal)));
    }

    public async Task BoostAsync(CancellationToken ct)
    {
        _exe = FanControlLocator.FindExe(_settings.FanControlExePath)
               ?? throw new FriendlyException(Loc.T("be.fc.notfound.text"));
        _normalConfig = FanControlLocator.ResolveNormalConfig(_exe, _settings.NormalConfigPath)
                        ?? throw new FriendlyException(Loc.T("be.fc.noconfig.text"));

        string maxConfig = _settings.UseCustomMaxConfig && File.Exists(_settings.CustomMaxConfigPath)
            ? _settings.CustomMaxConfigPath!
            : await Task.Run(() => GenerateMaxConfig(_exe, _normalConfig), ct).ConfigureAwait(true);

        _wasRunning = FanControlLocator.IsRunning();
        RecoveryMarker.Write(new RecoveryMarker
        {
            Backend = BackendKind.FanControl,
            FanControlExe = _exe,
            NormalConfig = _normalConfig,
            FanControlWasRunning = _wasRunning,
        });
        _boostAttempted = true;

        Log.Info($"FanControl: cargando MAX ({maxConfig}); normal={_normalConfig}; estabaAbierto={_wasRunning}");
        await RunFanControlAsync(_exe, $"-c \"{maxConfig}\"", ct).ConfigureAwait(true);
    }

    private string GenerateMaxConfig(string exe, string normalConfig)
    {
        string json;
        try
        {
            json = File.ReadAllText(normalConfig);
        }
        catch (Exception ex)
        {
            throw new FriendlyException(Loc.F("be.fc.read", Path.GetFileName(normalConfig), ex.Message));
        }

        string maxJson;
        int boosted, total;
        try
        {
            maxJson = FanControlConfigTool.BuildMaxConfig(json, _settings.ExcludedControlIds, out boosted, out total);
        }
        catch (Exception ex)
        {
            throw new FriendlyException(Loc.F("be.fc.parse", ex.Message));
        }

        if (total == 0)
            throw new FriendlyException(Loc.T("be.fc.nofans"));

        if (boosted == 0)
            throw new FriendlyException(Loc.T("be.fc.allexcluded"));

        // Preferimos dejarla junto a las demás configs de FanControl; si no hay permisos, en nuestra carpeta.
        string target = Path.Combine(Path.GetDirectoryName(normalConfig)!, FanControlConfigTool.MaxFileName);
        try
        {
            AppPaths.WriteAllTextAtomic(target, maxJson);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            target = Path.Combine(AppPaths.DataDir, FanControlConfigTool.MaxFileName);
            AppPaths.WriteAllTextAtomic(target, maxJson);
        }
        Log.Info($"Config MAX generada: {target} ({boosted}/{total} controles al 100 %)");
        return target;
    }

    public async Task RestoreAsync()
    {
        if (!_boostAttempted) return;
        await Task.Run(() => RestoreCore(_exe, _normalConfig, _wasRunning, _settings.CloseFanControlIfItWasClosed)).ConfigureAwait(true);
        _boostAttempted = false;
    }

    public void RestoreBlocking(TimeSpan timeout)
    {
        if (!_boostAttempted) return;
        var t = Task.Run(() => RestoreCore(_exe, _normalConfig, _wasRunning, _settings.CloseFanControlIfItWasClosed));
        if (t.Wait(timeout)) _boostAttempted = false;
    }

    /// <summary>Vuelve a la config normal. También lo usa la recuperación tras un cierre inesperado.</summary>
    public static void RestoreCore(string? exe, string? normalConfig, bool wasRunning, bool closeIfWasClosed)
    {
        if (!FanControlLocator.IsValidExe(exe) || string.IsNullOrWhiteSpace(normalConfig))
        {
            Log.Warn("Restaurar FanControl: faltan datos (exe/config)");
            RecoveryMarker.Delete();
            return;
        }

        Log.Info($"FanControl: restaurando config normal {normalConfig}");
        RunFanControlAsync(exe!, $"-c \"{normalConfig}\"", CancellationToken.None).GetAwaiter().GetResult();

        if (!wasRunning && closeIfWasClosed)
        {
            // FanControl no estaba abierto al empezar: lo dejamos como estaba.
            Thread.Sleep(2500);
            Log.Info("FanControl no estaba abierto antes: cerrándolo");
            RunFanControlAsync(exe!, "-e", CancellationToken.None).GetAwaiter().GetResult();
        }
        RecoveryMarker.Delete();
    }

    private static async Task RunFanControlAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // ERROR_ELEVATION_REQUIRED: FanControl pide admin y nosotros no lo somos.
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try
            {
                p = Process.Start(psi);
            }
            catch (Win32Exception ex2) when (ex2.NativeErrorCode == 1223)
            {
                throw new FriendlyException(Loc.T("be.fc.uac"));
            }
        }
        catch (Exception ex)
        {
            throw new FriendlyException(Loc.F("be.fc.launch", ex.Message));
        }

        using (p)
        {
            if (p == null) return;
            // Si FanControl ya estaba abierto, esta segunda instancia pasa el encargo y se cierra enseguida.
            // Si no lo estaba, esta ES la instancia principal y se queda abierta (no la esperamos).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                Log.Info($"FanControl {args} -> salió con {p.ExitCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Info($"FanControl {args} -> sigue abierto (instancia principal nueva)");
            }
        }
        // Un momento para que FanControl aplique la config.
        await Task.Delay(800, CancellationToken.None).ConfigureAwait(false);
    }
}

// =====================================================================================
//  Control directo con LibreHardwareMonitor (experimental)
// =====================================================================================
public sealed class DirectBackend : IFanBackend
{
    private readonly HardwareService _hw;
    private readonly AppSettings _settings;
    private volatile bool _boostAttempted;

    public DirectBackend(HardwareService hw, AppSettings settings)
    {
        _hw = hw;
        _settings = settings;
    }

    public BackendKind Kind => BackendKind.Direct;
    public string Name => Loc.T("be.direct.name");

    public BackendStatus GetStatus()
    {
        if (FanControlLocator.IsRunning())
            return new(false, Loc.T("be.direct.fcopen.title"),
                Loc.T("be.direct.fcopen.text"));
        if (!Elevation.IsAdmin)
            return new(false, Loc.T("be.direct.admin.title"), Loc.T("be.direct.admin.text"));
        if (!_hw.WaitOpenedAsync().IsCompleted)
            return new(true, Loc.T("be.direct.detecting.title"), Loc.T("be.direct.detecting.text"));
        int n = _hw.ControlList.Count;
        if (n == 0)
            return new(false, Loc.T("be.direct.nofans.title"),
                HardwareService.PawnIoInstalled()
                    ? Loc.T("be.direct.nofans.text")
                    : Loc.T("be.direct.nopawnio"));
        return new(true, Loc.T("be.direct.ok.title"), Loc.F("be.direct.ok.text", n));
    }

    public async Task BoostAsync(CancellationToken ct)
    {
        var st = GetStatus();
        if (!st.Ready) throw new FriendlyException(st.Detail);
        _hw.SetExcludedControls(_settings.ExcludedControlIds);
        RecoveryMarker.Write(new RecoveryMarker { Backend = BackendKind.Direct });
        _boostAttempted = true;
        int n = await _hw.SetBoostAsync(true).WaitAsync(ct).ConfigureAwait(true);
        if (n == 0) throw new FriendlyException(Loc.T("be.direct.none"));
        Log.Info($"Control directo: {n} ventiladores al 100 %");
    }

    public async Task RestoreAsync()
    {
        if (!_boostAttempted) return;
        await _hw.SetBoostAsync(false).ConfigureAwait(true);
        _boostAttempted = false;
        RecoveryMarker.Delete();
    }

    public void RestoreBlocking(TimeSpan timeout)
    {
        if (!_boostAttempted) return;
        if (_hw.SetBoostAsync(false).Wait(timeout))
        {
            _boostAttempted = false;
            RecoveryMarker.Delete();
        }
    }
}

// =====================================================================================
//  Simulación
// =====================================================================================
public sealed class SimulationBackend : IFanBackend
{
    private readonly SimulatedSensors _sim;

    public SimulationBackend(SimulatedSensors sim) => _sim = sim;

    public BackendKind Kind => BackendKind.Simulation;
    public string Name => Loc.T("be.sim.name");

    public BackendStatus GetStatus() => new(true, Loc.T("be.sim.title"), Loc.T("be.sim.text"));

    public async Task BoostAsync(CancellationToken ct)
    {
        await Task.Delay(700, ct).ConfigureAwait(true);
        _sim.Boosting = true;
    }

    public Task RestoreAsync()
    {
        _sim.Boosting = false;
        return Task.CompletedTask;
    }

    public void RestoreBlocking(TimeSpan timeout) => _sim.Boosting = false;
}

public static class BackendFactory
{
    /// <summary>Qué motor se usará realmente con estos ajustes.</summary>
    public static BackendKind Resolve(AppSettings s, bool forceSimulation)
    {
        if (forceSimulation) return BackendKind.Simulation;
        if (s.Backend != BackendKind.Auto) return s.Backend;
        return FanControlLocator.FindExe(s.FanControlExePath) != null ? BackendKind.FanControl : BackendKind.Direct;
    }
}
