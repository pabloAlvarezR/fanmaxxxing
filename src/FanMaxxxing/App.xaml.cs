using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FanMaxxxing.Core;

namespace FanMaxxxing;

public partial class App : Application
{
    private SingleInstance? _single;
    private MainWindow? _main;
    private bool _exiting;
    private int _fatalHandled;

    public AppSettings Settings { get; private set; } = new();
    public SoundFx Sound { get; private set; } = new();
    public ISensorSource Sensors { get; private set; } = null!;
    public bool ForceSimulation { get; private set; }

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
        Log.Info($"=== Arranque v{UpdateChecker.CurrentVersion} · admin={Elevation.IsAdmin} · args=[{string.Join(' ', args)}] ===");

        // 1) ¿Ya hay una abierta? Le pasamos la orden y nos vamos.
        if (!opts.Restarted && SingleInstance.AnotherInstanceExists() && SingleInstance.SendToExisting(args))
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

        // 3) Instancia única (tras un reinicio por cambio de idioma, esperamos a que la anterior suelte el mutex)
        _single = new SingleInstance();
        if (!_single.TryAcquire(opts.Restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
        {
            SingleInstance.SendToExisting(args);
            Shutdown();
            return;
        }
        _single.StartServer(a => Dispatcher.BeginInvoke(() => OnRemoteArgs(a)));

        // 4) Estado e idioma
        Settings = SettingsStore.Load();
        ForceSimulation = opts.Simulation;
        Loc.Init(Settings.Language);

#if DEBUG
        if (opts.SnapFirstRun && opts.SnapPath is string fr)
        {
            var w = new FirstRunWindow(Settings);
            w.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(opts.SnapDelay) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var root = (FrameworkElement)w.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(fr)) enc.Save(fs);
                Shutdown();
            };
            t.Start();
            return;
        }
#endif

        // 5) Primera vez: idioma y sonido (solo una vez en la vida)
        if (!Settings.SetupDone)
        {
            var welcome = new FirstRunWindow(Settings);
            welcome.ShowDialog();
            Settings.SetupDone = true;
            SaveSettings();
            Loc.Init(Settings.Language);
        }

        Sound.Enabled = Settings.SoundEnabled;
        ReconfigureSensors();
        SessionEnding += OnSessionEnding;
        if (Elevation.IsAdmin) Task.Run(RemoveLegacyStartupTask);

        // 6) Ventana
        _main = new MainWindow(this);
        MainWindow = _main;
        _main.Show();
        _main.HandleLaunch(opts);

        _ = RecoverIfNeededAsync();
    }

    /// <summary>La versión 1.1 podía arrancar con Windows; esa opción ya no existe, así que limpiamos su tarea.</summary>
    private static void RemoveLegacyStartupTask()
    {
        try
        {
            if (WindowsIntegration.TaskExists(WindowsIntegration.StartupTask))
            {
                WindowsIntegration.DeleteTask(WindowsIntegration.StartupTask);
                Log.Info("Tarea antigua de inicio con Windows eliminada");
            }
        }
        catch (Exception ex) { Log.Warn("No se pudo borrar la tarea de inicio antigua: " + ex.Message); }
    }

    private void OnRemoteArgs(string[] args)
    {
        if (_exiting || _main == null) return;
        var o = LaunchOptions.Parse(args);
        Log.Info($"Orden recibida de otra instancia: [{string.Join(' ', args)}]");
        _main.BringToFront();
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
        Sensors = fresh;
        if (current == null)
        {
            fresh.Start();
        }
        else
        {
            // Primero se cierra el antiguo y luego se abre el nuevo (LibreHardwareMonitor comparte estado global).
            // Cerrar puede tardar un poco: fuera del hilo de UI.
            Task.Run(() =>
            {
                try { current.Dispose(); }
                catch (Exception ex) { Log.Error("Error cerrando sensores", ex); }
                fresh.Start();
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
            _main?.ShowToast("🩹", Loc.T("toast.recovered.title"), Loc.T("toast.recovered.text"), TimeSpan.FromSeconds(9));
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en la recuperación", ex);
            _main?.ShowToast("⚠", Loc.T("toast.recoverfail.title"), Loc.T("toast.recoverfail.text") + " " + ex.Message,
                TimeSpan.FromSeconds(12), error: true);
        }
    }

    // =====================================================================
    //  Salida, reinicio y errores
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

    /// <summary>Reinicia la app (para aplicar un idioma nuevo).</summary>
    public void Restart()
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--restarted");
            psi.ArgumentList.Add("--no-elevate");
            if (ForceSimulation) psi.ArgumentList.Add("--sim");
            // Soltamos el mutex antes, para que la nueva instancia pueda quedárselo.
            _single?.Dispose();
            _single = null;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo reiniciar", ex);
        }
        ExitApp();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Sensors?.Dispose(); } catch (Exception ex) { Log.Error("Error cerrando sensores", ex); }
        try { Sound.Dispose(); } catch { /* ignorar */ }
        try { _single?.Dispose(); } catch { /* ignorar */ }
        PowerActions.KeepSystemAwake(false);
        Log.Info("=== Fin ===");
        base.OnExit(e);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Windows se apaga o cierra sesión (o el instalador nos pide salir). Si estábamos enfriando, restauramos YA.
        Log.Info($"Fin de sesión de Windows ({e.ReasonSessionEnding})");
        try
        {
            _main?.MarkSessionEnding();
            if (_main?.IsCooling == true) _main.EmergencyRestore();
        }
        catch (Exception ex) { Log.Error("Error en SessionEnding", ex); }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Excepción no controlada (UI)", e.Exception);
        e.Handled = true; // seguimos vivos: los ventiladores no se pueden quedar a lo loco
        try
        {
            _main?.ShowToast("💥", Loc.T("toast.crash.title"), e.Exception.Message, TimeSpan.FromSeconds(10), error: true);
        }
        catch { /* ignorar */ }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (Interlocked.Exchange(ref _fatalHandled, 1) == 1) return;
        Log.Error("Excepción fatal", e.ExceptionObject as Exception);
        try { _main?.EmergencyRestore(); } catch { /* ignorar */ }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Excepción en tarea no observada", e.Exception);
        e.SetObserved();
    }
}
