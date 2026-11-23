using System.Windows;
using System.Windows.Threading;
using FanMaxxxing.Core;
using FanMaxxxing.UI;

namespace FanMaxxxing;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private bool _exiting;
    private int _fatalHandled;

    public AppSettings Settings { get; private set; } = new();
    public SoundFx Sound { get; private set; } = new();
    public ISensorSource Sensors { get; private set; } = null!;
    public bool ForceSimulation { get; private set; }
    public bool HasTray => _tray != null;

    private HardwareService? Hardware => Sensors as HardwareService;
    private SimulatedSensors? Sim => Sensors as SimulatedSensors;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var args = e.Args.ToList();
        var opts = LaunchOptions.Parse(args);
        if (opts.FromTask)
        {
            // Lanzado por la tarea programada "sin UAC": los argumentos reales están en un fichero.
            args = WindowsIntegration.TakePendingArgs().ToList();
            opts = LaunchOptions.Parse(args);
        }
        Log.Info($"=== Arranque v{typeof(App).Assembly.GetName().Version} · admin={Elevation.IsAdmin} · args=[{string.Join(' ', args)}] ===");

        // 1) ¿Ya hay una abierta? Le pasamos la orden y nos vamos.
        if (SingleInstance.AnotherInstanceExists() && SingleInstance.SendToExisting(args))
        {
            Shutdown();
            return;
        }

        // 2) Permisos de administrador (FanControl y los sensores de CPU los necesitan)
        if (!Elevation.IsAdmin && !opts.NoElevate && !opts.Simulation)
        {
            if (WindowsIntegration.TryLaunchElevatedViaTask(args))
            {
                Shutdown();
                return;
            }
            var r = Elevation.RelaunchAsAdmin(args.Append("--no-elevate"));
            if (r == Elevation.RelaunchResult.Started)
            {
                Shutdown();
                return;
            }
            Log.Warn($"Sin elevación ({r}); seguimos con permisos limitados");
        }

        // 3) Instancia única
        _single = new SingleInstance();
        if (!_single.TryAcquire())
        {
            SingleInstance.SendToExisting(args);
            Shutdown();
            return;
        }
        _single.StartServer(a => Dispatcher.BeginInvoke(() => OnRemoteArgs(a)));

        // 4) Estado
        Settings = SettingsStore.Load();
        ForceSimulation = opts.Simulation;
        Sound.Enabled = Settings.SoundEnabled;
        ReconfigureSensors();
        SessionEnding += OnSessionEnding;

        try
        {
            _tray = new TrayIcon(this);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear el icono de la bandeja", ex);
            _tray = null;
        }

        // 5) Ventana
        _main = new MainWindow(this);
        MainWindow = _main;
        bool startHidden = opts.Tray && !opts.StartNow && _tray != null;
        if (!startHidden) _main.Show();
        _main.HandleLaunch(opts);

        _ = RecoverIfNeededAsync();
    }

    private void OnRemoteArgs(string[] args)
    {
        if (_exiting || _main == null) return;
        var o = LaunchOptions.Parse(args);
        Log.Info($"Orden recibida de otra instancia: [{string.Join(' ', args)}]");
        if (!o.Tray || o.StartNow) _main.ShowFromTray();
        _main.HandleLaunch(o);
    }

    // =====================================================================
    //  Sensores y motores
    // =====================================================================

    /// <summary>Crea (o reutiliza) la fuente de sensores adecuada para el motor elegido.</summary>
    public void ReconfigureSensors()
    {
        var kind = BackendFactory.Resolve(Settings, ForceSimulation);
        bool wantSim = kind == BackendKind.Simulation;
        bool wantBoard = kind == BackendKind.Direct;

        var current = Sensors;
        bool ok = current switch
        {
            SimulatedSensors => wantSim,
            HardwareService hw => !wantSim && hw.HasMotherboard == wantBoard,
            _ => false,
        };
        if (ok) return;

        ISensorSource fresh = wantSim ? new SimulatedSensors() : new HardwareService(wantBoard);
        fresh.Start();
        Sensors = fresh;
        if (current != null)
        {
            // Cerrar LibreHardwareMonitor puede tardar un poco: fuera del hilo de UI.
            Task.Run(() =>
            {
                try { current.Dispose(); }
                catch (Exception ex) { Log.Error("Error cerrando sensores", ex); }
            });
        }
        Log.Info($"Sensores: {(wantSim ? "simulados" : wantBoard ? "LHM + placa" : "LHM")}");
    }

    public IFanBackend CreateBackend()
    {
        ReconfigureSensors();
        return BackendFactory.Resolve(Settings, ForceSimulation) switch
        {
            BackendKind.FanControl => new FanControlBackend(Settings),
            BackendKind.Direct => new DirectBackend(Hardware!, Settings),
            _ => new SimulationBackend(Sim!),
        };
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    public void ApplyNewSettings(AppSettings s)
    {
        s.Stats = Settings.Stats; // las estadísticas no se editan en Ajustes
        Settings = s;
        Sound.Enabled = s.SoundEnabled;
        SaveSettings();
        ReconfigureSensors();
    }

    /// <summary>Si la última vez nos cerramos a mitad de enfriamiento, dejamos todo como estaba.</summary>
    private async Task RecoverIfNeededAsync()
    {
        var m = RecoveryMarker.Read();
        if (m == null) return;
        Log.Warn($"Marcador de recuperación encontrado ({m.Backend}, {m.StartedUtc:u}). Restaurando…");
        try
        {
            switch (m.Backend)
            {
                case BackendKind.FanControl:
                    await Task.Run(() => FanControlBackend.RestoreCore(m.FanControlExe, m.NormalConfig, m.FanControlWasRunning,
                        Settings.CloseFanControlIfItWasClosed));
                    break;
                case BackendKind.Direct:
                    if (Hardware is { HasMotherboard: true } hw)
                    {
                        await hw.SetBoostAsync(false);
                    }
                    else
                    {
                        await Task.Run(async () =>
                        {
                            using var temp = new HardwareService(true);
                            temp.Start();
                            await temp.SetBoostAsync(false);
                        });
                    }
                    RecoveryMarker.Delete();
                    break;
                default:
                    RecoveryMarker.Delete();
                    break;
            }
            _main?.ShowToast("🩹", "Recuperado de un cierre inesperado",
                "La última vez me cerré a mitad de enfriamiento. Ya he dejado los ventiladores en su modo normal.",
                TimeSpan.FromSeconds(9));
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en la recuperación", ex);
            _main?.ShowToast("⚠", "No he podido restaurar", "Revisa FanControl y carga tu config normal a mano. " + ex.Message,
                TimeSpan.FromSeconds(12), error: true);
        }
    }

    // =====================================================================
    //  Bandeja
    // =====================================================================

    public void ShowMain() => _main?.ShowFromTray();

    public void ShowTrayBalloon(string title, string text) => _tray?.Balloon(title, text);

    public void ShowTrayBalloonIfHidden(Window w, string title, string text)
    {
        if (!w.IsVisible || w.WindowState == WindowState.Minimized) _tray?.Balloon(title, text);
    }

    public void SetTrayTooltip(string text) => _tray?.SetTooltip(text);

    public bool IsCooling => _main?.IsCooling == true;

    public void StartFromTray(AfterAction? after)
    {
        _main?.StartFromTray(after);
    }

    public void CancelFromTray() => _main?.CancelCooldown();

    public void RequestExit()
    {
        if (_main != null) _ = _main.RequestExitAsync();
        else ExitApp();
    }

    // =====================================================================
    //  Salida y errores
    // =====================================================================

    /// <summary>Cierre ordenado: todo lo que tiene recursos nativos se libera aquí.</summary>
    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Saliendo…");
        try
        {
            if (_main != null)
            {
                if (_main.IsCooling) _main.EmergencyRestore();
                _main.Teardown();
                _main.Close();
            }
        }
        catch (Exception ex) { Log.Error("Error cerrando la ventana", ex); }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { /* ignorar */ }
        _tray = null;
        try { Sensors?.Dispose(); } catch (Exception ex) { Log.Error("Error cerrando sensores", ex); }
        try { Sound.Dispose(); } catch { /* ignorar */ }
        try { _single?.Dispose(); } catch { /* ignorar */ }
        PowerActions.KeepSystemAwake(false);
        Log.Info("=== Fin ===");
        base.OnExit(e);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Windows se apaga o cierra sesión. Si estábamos enfriando, dejamos FanControl en su config normal YA.
        Log.Info($"Fin de sesión de Windows ({e.ReasonSessionEnding})");
        try
        {
            _main?.MarkSessionEnding();
            if (_main?.IsCooling == true) _main.EmergencyRestore();
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception ex) { Log.Error("Error en SessionEnding", ex); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Excepción no controlada (UI)", e.Exception);
        e.Handled = true; // seguimos vivos: los ventiladores no se pueden quedar a lo loco
        try
        {
            _main?.ShowToast("💥", "Algo ha petado (pero sigo vivo)", e.Exception.Message, TimeSpan.FromSeconds(10), error: true);
        }
        catch { /* ignorar */ }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _fatalHandled, 1) == 1) return;
        Log.Error("Excepción fatal", e.ExceptionObject as Exception);
        try { _main?.EmergencyRestore(); } catch { /* ignorar */ }
        try { _tray?.Dispose(); } catch { /* ignorar */ }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Excepción en tarea no observada", e.Exception);
        e.SetObserved();
    }
}
