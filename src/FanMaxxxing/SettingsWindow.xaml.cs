using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanMaxxxing.Core;
using Microsoft.Win32;

namespace FanMaxxxing;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly AppSettings _edit;
    private readonly List<(string Id, CheckBox Box)> _controlChecks = new();
    private readonly List<(string Id, CheckBox Box)> _directChecks = new();
    private bool _loading = true;
    private bool _startupTaskOriginal, _noUacTaskOriginal;
    private bool _tasksKnown;
    private CancellationTokenSource? _testCts;
    private Task? _testTask;

    public AppSettings? Result { get; private set; }

    public SettingsWindow(App app)
    {
        _app = app;
        _edit = app.Settings.Clone();
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = _edit;
        switch (s.Backend)
        {
            case BackendKind.FanControl: BkFanControl.IsChecked = true; break;
            case BackendKind.Direct: BkDirect.IsChecked = true; break;
            case BackendKind.Simulation: BkSim.IsChecked = true; break;
            default: BkAuto.IsChecked = true; break;
        }
        if (_app.ForceSimulation)
        {
            BkSim.IsChecked = true;
            BkAuto.IsEnabled = BkFanControl.IsEnabled = BkDirect.IsEnabled = false;
        }

        ExePathBox.Text = FanControlLocator.FindExe(s.FanControlExePath) ?? s.FanControlExePath ?? "";
        UseCustomMax.IsChecked = s.UseCustomMaxConfig;
        CustomMaxBox.Text = s.CustomMaxConfigPath ?? "";
        CloseFcCheck.IsChecked = s.CloseFanControlIfItWasClosed;

        DurationSlider.Value = s.DurationMinutes;
        SmartStopCheck.IsChecked = s.SmartStop;
        CpuTargetSlider.Value = s.SmartStopCpuTemp;
        GpuTargetSlider.Value = s.SmartStopGpuTemp;
        MinSecondsSlider.Value = s.SmartStopMinSeconds;
        DisplayOffCheck.IsChecked = s.TurnOffDisplay;
        CountdownSlider.Value = s.PowerCountdownSeconds;
        ForceCloseCheck.IsChecked = s.ForceCloseApps;
        SoundCheck.IsChecked = s.SoundEnabled;
        ReducedCheck.IsChecked = s.ReducedAnimations;
        TrayCheck.IsChecked = s.CloseToTray;

        var st = s.Stats;
        var (rank, next) = Memes.RankFor(st.TotalCooldowns);
        StatsText.Text =
            $"Rango: {rank} ({next})\n" +
            $"Enfriamientos completados: {st.TotalCooldowns}\n" +
            $"Grados de calor residual eliminados: {st.TotalDegreesDropped:0} °C (récord: -{st.BestDrop:0} °C en una sesión)\n" +
            $"Noches salvadas (apagados tras enfriar): {st.NightsSaved}\n" +
            $"Tiempo total con las turbinas a tope: {TimeSpan.FromSeconds(st.TotalCoolingSeconds):hh\\:mm\\:ss}";

        VersionText.Text = $"FanMaxxxing v{typeof(App).Assembly.GetName().Version?.ToString(3)} · {(Elevation.IsAdmin ? "administrador ✓" : "sin admin")} · datos en {AppPaths.DataDir}";

        UpdateSliderLabels();
        RefreshConfigList();
        RefreshDirectList();
        UpdateBackendPanels();
        CustomMaxRow.IsEnabled = UseCustomMax.IsChecked == true;
        _loading = false;

        // Consultar las tareas programadas tarda un poco: en segundo plano.
        StartupCheck.IsEnabled = NoUacCheck.IsEnabled = false;
        var (startup, noUac) = await Task.Run(() =>
            (WindowsIntegration.TaskExists(WindowsIntegration.StartupTask), WindowsIntegration.TaskExists(WindowsIntegration.ElevatedTask)));
        _startupTaskOriginal = startup;
        _noUacTaskOriginal = noUac;
        StartupCheck.IsChecked = startup;
        NoUacCheck.IsChecked = noUac;
        _tasksKnown = true;
        StartupCheck.IsEnabled = NoUacCheck.IsEnabled = Elevation.IsAdmin;
        if (!Elevation.IsAdmin)
        {
            StartupCheck.ToolTip = NoUacCheck.ToolTip = "Abre FanMaxxxing como administrador para cambiar esto.";
        }
    }

    // ---------------------------------------------------------------------
    //  Motor
    // ---------------------------------------------------------------------

    private BackendKind SelectedBackend =>
        BkFanControl.IsChecked == true ? BackendKind.FanControl :
        BkDirect.IsChecked == true ? BackendKind.Direct :
        BkSim.IsChecked == true ? BackendKind.Simulation : BackendKind.Auto;

    private void Backend_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateBackendPanels();
    }

    private void UpdateBackendPanels()
    {
        var temp = Collect();
        var kind = BackendFactory.Resolve(temp, _app.ForceSimulation);
        FanControlPanel.Visibility = kind == BackendKind.FanControl || SelectedBackend == BackendKind.FanControl ? Visibility.Visible : Visibility.Collapsed;
        DirectPanel.Visibility = kind == BackendKind.Direct ? Visibility.Visible : Visibility.Collapsed;

        BackendStatus st;
        switch (kind)
        {
            case BackendKind.FanControl:
                st = new FanControlBackend(temp).GetStatus();
                break;
            case BackendKind.Direct:
                st = _app.Sensors is HardwareService { HasMotherboard: true } hw
                    ? new DirectBackend(hw, temp).GetStatus()
                    : new BackendStatus(true, "Control directo", "Los ventiladores se detectarán al guardar.");
                break;
            default:
                st = new BackendStatus(true, "Simulación", "Nada de hardware real: para probar la app.");
                break;
        }
        string prefix = SelectedBackend == BackendKind.Auto ? $"Automático → {kind}: " : "";
        BkStatus.Text = $"{prefix}{st.Title}. {st.Detail}";
        BkDot.Fill = st.Ready ? (Brush)FindResource("Lime") : (Brush)FindResource("Danger");
    }

    private void ExePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        RefreshConfigList();
        UpdateBackendPanels();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "¿Dónde está FanControl.exe?",
            Filter = "FanControl|FanControl.exe|Ejecutables|*.exe",
            FileName = "FanControl.exe",
        };
        if (File.Exists(ExePathBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(ExePathBox.Text);
        if (dlg.ShowDialog(this) == true)
        {
            ExePathBox.Text = dlg.FileName;
            RefreshConfigList();
            UpdateBackendPanels();
        }
    }

    private void DetectExe_Click(object sender, RoutedEventArgs e)
    {
        var found = FanControlLocator.FindExe(null);
        if (found == null)
        {
            BkStatus.Text = "No encuentro FanControl en los sitios habituales. Usa «Buscar…».";
            BkDot.Fill = (Brush)FindResource("Danger");
            return;
        }
        ExePathBox.Text = found;
        RefreshConfigList();
        UpdateBackendPanels();
    }

    private string? ExePath => FanControlLocator.IsValidExe(ExePathBox.Text.Trim()) ? ExePathBox.Text.Trim() : null;

    private void RefreshConfigList()
    {
        bool wasLoading = _loading;
        _loading = true;
        ConfigList.Items.Clear();
        var configs = FanControlLocator.ListConfigs(ExePath).ToList();

        // Si el usuario eligió una config fuera de la carpeta, también la mostramos
        if (!string.IsNullOrWhiteSpace(_edit.NormalConfigPath) && File.Exists(_edit.NormalConfigPath) &&
            !configs.Any(c => c.Equals(_edit.NormalConfigPath, StringComparison.OrdinalIgnoreCase)))
            configs.Add(_edit.NormalConfigPath!);

        var auto = new ListBoxItem
        {
            Content = configs.Count > 0 ? $"Automática  ·  ahora mismo: {Path.GetFileName(configs[0])}" : "Automática (no hay configs todavía)",
            Tag = null,
        };
        ConfigList.Items.Add(auto);
        ListBoxItem selected = auto;
        foreach (var c in configs)
        {
            var item = new ListBoxItem
            {
                Content = $"{Path.GetFileName(c)}   ·   {File.GetLastWriteTime(c):dd/MM HH:mm}",
                Tag = c,
                ToolTip = c,
            };
            ConfigList.Items.Add(item);
            if (c.Equals(_edit.NormalConfigPath, StringComparison.OrdinalIgnoreCase)) selected = item;
        }
        ConfigList.SelectedItem = selected;
        _loading = wasLoading;
        RefreshControls();
    }

    private void ConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _edit.NormalConfigPath = (ConfigList.SelectedItem as ListBoxItem)?.Tag as string;
        RefreshControls();
        UpdateBackendPanels();
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Tu config normal de FanControl", Filter = "Config de FanControl|*.json" };
        if (ExePath != null && Directory.Exists(FanControlLocator.ConfigDir(ExePath))) dlg.InitialDirectory = FanControlLocator.ConfigDir(ExePath);
        if (dlg.ShowDialog(this) == true)
        {
            _edit.NormalConfigPath = dlg.FileName;
            RefreshConfigList();
            UpdateBackendPanels();
        }
    }

    private void RefreshControls()
    {
        // Guardamos lo que había marcado antes de redibujar
        CaptureExclusions();
        ControlsPanel.Children.Clear();
        _controlChecks.Clear();

        var path = FanControlLocator.ResolveNormalConfig(ExePath, (ConfigList.SelectedItem as ListBoxItem)?.Tag as string);
        if (path == null)
        {
            ControlsPanel.Children.Add(Hint("Sin config de FanControl todavía. Abre FanControl, configura tus ventiladores y vuelve."));
            return;
        }

        try
        {
            var controls = FanControlConfigTool.ListControlsFromFile(path);
            if (controls.Count == 0)
            {
                ControlsPanel.Children.Add(Hint("Esta config no tiene ventiladores controlables. Crea una config MAX a mano y márcala abajo."));
                return;
            }
            foreach (var c in controls)
            {
                string state = c.Enabled == false ? " · desactivado en FanControl" : c.ManualNow ? $" · manual {c.ManualValue:0}%" : "";
                var cb = new CheckBox
                {
                    Style = (Style)FindResource("Switch"),
                    IsChecked = !_edit.ExcludedControlIds.Contains(c.Id),
                    Content = new TextBlock
                    {
                        Inlines =
                        {
                            new System.Windows.Documents.Run(c.Name) { FontWeight = FontWeights.SemiBold },
                            new System.Windows.Documents.Run($"   {c.Kind}{state}") { Foreground = (Brush)FindResource("TextFaint"), FontSize = 11.5 },
                        },
                    },
                    ToolTip = c.Id,
                };
                _controlChecks.Add((c.Id, cb));
                ControlsPanel.Children.Add(cb);
            }
        }
        catch (Exception ex)
        {
            ControlsPanel.Children.Add(Hint("No se pudo leer la config: " + ex.Message));
        }
    }

    private void RefreshDirectList()
    {
        CaptureExclusions();
        DirectControlsPanel.Children.Clear();
        _directChecks.Clear();
        if (_app.Sensors is not HardwareService { HasMotherboard: true } hw)
        {
            DirectControlsPanel.Children.Add(Hint("Elige «Directo» y guarda para detectar los ventiladores."));
            return;
        }
        var list = hw.ControlList;
        if (list.Count == 0)
        {
            DirectControlsPanel.Children.Add(Hint(HardwareService.PawnIoInstalled()
                ? "No se han detectado ventiladores controlables (o todavía se están buscando)."
                : "No está instalado el driver PawnIO: sin él no se puede tocar la placa base."));
            return;
        }
        foreach (var (id, name) in list)
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                IsChecked = !_edit.ExcludedControlIds.Contains(id),
                Content = name,
                ToolTip = id,
            };
            _directChecks.Add((id, cb));
            DirectControlsPanel.Children.Add(cb);
        }
    }

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 4) };

    private void CaptureExclusions()
    {
        foreach (var (id, box) in _controlChecks.Concat(_directChecks))
        {
            _edit.ExcludedControlIds.Remove(id);
            if (box.IsChecked != true) _edit.ExcludedControlIds.Add(id);
        }
    }

    private void UseCustomMax_Changed(object sender, RoutedEventArgs e)
    {
        CustomMaxRow.IsEnabled = UseCustomMax.IsChecked == true;
        if (!_loading) UpdateBackendPanels();
    }

    private void BrowseCustomMax_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Tu config MAX de FanControl (todo al 100 %)", Filter = "Config de FanControl|*.json" };
        if (ExePath != null && Directory.Exists(FanControlLocator.ConfigDir(ExePath))) dlg.InitialDirectory = FanControlLocator.ConfigDir(ExePath);
        if (dlg.ShowDialog(this) == true)
        {
            CustomMaxBox.Text = dlg.FileName;
            UseCustomMax.IsChecked = true;
        }
    }

    // ---------------------------------------------------------------------
    //  Sliders
    // ---------------------------------------------------------------------

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Los sliders disparan ValueChanged durante InitializeComponent, antes de que existan las etiquetas.
        if (IsLoaded) UpdateSliderLabels();
    }

    private void UpdateSliderLabels()
    {
        if (DurationValue == null || CpuTargetValue == null || GpuTargetValue == null || MinSecondsValue == null || CountdownValue == null) return;
        DurationValue.Text = $"{DurationSlider.Value:0} min";
        CpuTargetValue.Text = $"{CpuTargetSlider.Value:0} °C";
        GpuTargetValue.Text = $"{GpuTargetSlider.Value:0} °C";
        MinSecondsValue.Text = $"{MinSecondsSlider.Value:0} s";
        CountdownValue.Text = $"{CountdownSlider.Value:0} s";
    }

    // ---------------------------------------------------------------------
    //  Recoger ajustes
    // ---------------------------------------------------------------------

    private AppSettings Collect()
    {
        CaptureExclusions();
        var s = _edit.Clone();
        s.Backend = SelectedBackend;
        s.FanControlExePath = ExePath;
        s.NormalConfigPath = (ConfigList.SelectedItem as ListBoxItem)?.Tag as string;
        s.UseCustomMaxConfig = UseCustomMax.IsChecked == true;
        s.CustomMaxConfigPath = string.IsNullOrWhiteSpace(CustomMaxBox.Text) ? null : CustomMaxBox.Text.Trim();
        s.CloseFanControlIfItWasClosed = CloseFcCheck.IsChecked == true;
        s.DurationMinutes = (int)DurationSlider.Value;
        s.SmartStop = SmartStopCheck.IsChecked == true;
        s.SmartStopCpuTemp = CpuTargetSlider.Value;
        s.SmartStopGpuTemp = GpuTargetSlider.Value;
        s.SmartStopMinSeconds = (int)MinSecondsSlider.Value;
        s.TurnOffDisplay = DisplayOffCheck.IsChecked == true;
        s.PowerCountdownSeconds = (int)CountdownSlider.Value;
        s.ForceCloseApps = ForceCloseCheck.IsChecked == true;
        s.SoundEnabled = SoundCheck.IsChecked == true;
        s.ReducedAnimations = ReducedCheck.IsChecked == true;
        s.CloseToTray = TrayCheck.IsChecked == true;
        s.Sanitize();
        return s;
    }

    // ---------------------------------------------------------------------
    //  Prueba de 10 s
    // ---------------------------------------------------------------------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testCts != null)
        {
            _testCts.Cancel();
            return;
        }

        var temp = Collect();
        var kind = BackendFactory.Resolve(temp, _app.ForceSimulation);
        IFanBackend backend;
        switch (kind)
        {
            case BackendKind.FanControl:
                backend = new FanControlBackend(temp);
                break;
            case BackendKind.Direct when _app.Sensors is HardwareService { HasMotherboard: true } hw:
                backend = new DirectBackend(hw, temp);
                break;
            case BackendKind.Direct:
                TestResult.Text = "Guarda primero con el motor «Directo» para detectar los ventiladores.";
                return;
            default:
                TestResult.Text = "En simulación no hay nada que probar (pero queda muy bonito).";
                return;
        }

        var st = backend.GetStatus();
        if (!st.Ready)
        {
            TestResult.Text = "✖ " + st.Detail;
            return;
        }

        _testCts = new CancellationTokenSource();
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        TestButtonText.Text = "Parar prueba";
        TestResult.Text = "Subiendo ventiladores… escucha 👂";
        _testTask = RunTestAsync(backend, _testCts.Token);
        await _testTask;
        _testCts.Dispose();
        _testCts = null;
        _testTask = null;
        SaveButton.IsEnabled = CancelButton.IsEnabled = true;
        TestButtonText.Text = "🧪  Probar 10 segundos";
    }

    private async Task RunTestAsync(IFanBackend backend, CancellationToken ct)
    {
        try
        {
            await backend.BoostAsync(ct);
            for (int i = 10; i > 0; i--)
            {
                TestResult.Text = $"🌀 Al 100 %… vuelven a automático en {i} s";
                await Task.Delay(1000, ct);
            }
            TestResult.Text = "Volviendo a automático…";
        }
        catch (OperationCanceledException)
        {
            TestResult.Text = "Prueba cancelada. Volviendo a automático…";
        }
        catch (Exception ex)
        {
            TestResult.Text = "✖ " + ex.Message;
            Log.Warn("Prueba fallida: " + ex.Message);
        }
        finally
        {
            try
            {
                await backend.RestoreAsync();
                if (!TestResult.Text.StartsWith("✖"))
                    TestResult.Text = "✔ Listo. Si has oído el despegue, todo funciona. Ventiladores en automático.";
            }
            catch (Exception ex)
            {
                TestResult.Text = "✖ No se pudo volver a automático: " + ex.Message;
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_testTask != null)
        {
            // No dejamos cerrar a mitad de prueba: primero se restaura
            e.Cancel = true;
            _testCts?.Cancel();
        }
    }

    // ---------------------------------------------------------------------
    //  Extras
    // ---------------------------------------------------------------------

    private void ShortcutShutdown_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut("Enfriar y apagar (FanMaxxxing)", "--start --then shutdown", "Pone los ventiladores al máximo y apaga el PC al terminar");

    private void ShortcutCool_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut("Enfriar ahora (FanMaxxxing)", "--start --then nothing", "Pone los ventiladores al máximo unos minutos");

    private void MakeShortcut(string name, string args, string description)
    {
        var err = WindowsIntegration.CreateDesktopShortcut(name, args, description);
        ExtrasResult.Text = err == null
            ? $"✔ Creado «{name}» en el escritorio." + (NoUacCheck.IsChecked == true ? "" : " Consejo: activa «Abrir sin UAC» para que no pida permisos.")
            : "✖ " + err;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ExtrasResult.Text = "✖ " + ex.Message;
        }
    }

    // ---------------------------------------------------------------------
    //  Guardar / cancelar
    // ---------------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Collect();

        if (_tasksKnown && Elevation.IsAdmin)
        {
            var errors = new List<string>();
            bool wantStartup = StartupCheck.IsChecked == true;
            bool wantNoUac = NoUacCheck.IsChecked == true;
            if (wantStartup != _startupTaskOriginal)
            {
                var err = wantStartup
                    ? WindowsIntegration.CreateTask(WindowsIntegration.StartupTask, "--tray", atLogon: true)
                    : WindowsIntegration.DeleteTask(WindowsIntegration.StartupTask);
                if (err != null) errors.Add("Inicio con Windows: " + err);
            }
            if (wantNoUac != _noUacTaskOriginal)
            {
                var err = wantNoUac
                    ? WindowsIntegration.CreateTask(WindowsIntegration.ElevatedTask, "--from-task", atLogon: false)
                    : WindowsIntegration.DeleteTask(WindowsIntegration.ElevatedTask);
                if (err != null) errors.Add("Abrir sin UAC: " + err);
            }
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", errors), "FanMaxxxing", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_testTask != null)
        {
            _testCts?.Cancel();
            return;
        }
        DialogResult = false;
    }
}
