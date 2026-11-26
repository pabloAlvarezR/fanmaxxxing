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
    private bool _noUacTaskOriginal;
    private bool _tasksKnown;
    private CancellationTokenSource? _testCts;
    private Task? _testTask;

    public AppSettings? Result { get; private set; }

    /// <summary>Se ha encontrado una actualización con «Buscar ahora»: la ventana principal mostrará el aviso.</summary>
    public bool CheckUpdatesNow { get; private set; }

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

        // Idioma: "Automático" + los disponibles
        AddLanguagePill(null, Loc.T("set.language.auto"));
        foreach (var (code, name) in Loc.Languages) AddLanguagePill(code, name);
        UpdatesCheck.IsChecked = s.CheckUpdates;

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

        var st = s.Stats;
        var (rank, next) = Memes.RankFor(st.TotalCooldowns);
        StatsText.Text = Loc.F("set.stats.text", rank, next, st.TotalCooldowns, st.TotalDegreesDropped.ToString("0"),
            st.BestDrop.ToString("0"), st.NightsSaved, TimeSpan.FromSeconds(st.TotalCoolingSeconds).ToString(@"hh\:mm\:ss"));

        VersionText.Text = $"FanMaxxxing v{UpdateChecker.CurrentVersion.ToString(3)} · {(Elevation.IsAdmin ? Loc.T("set.admin.yes") : Loc.T("set.admin.no"))} · {AppPaths.DataDir}";

        UpdateSliderLabels();
        RefreshConfigList();
        RefreshDirectList();
        UpdateBackendPanels();
        CustomMaxRow.IsEnabled = UseCustomMax.IsChecked == true;
        _loading = false;

        // Consultar la tarea programada tarda un poco: en segundo plano.
        NoUacCheck.IsEnabled = false;
        _noUacTaskOriginal = await Task.Run(() => WindowsIntegration.TaskExists(WindowsIntegration.ElevatedTask));
        NoUacCheck.IsChecked = _noUacTaskOriginal;
        _tasksKnown = true;
        NoUacCheck.IsEnabled = Elevation.IsAdmin;
        if (!Elevation.IsAdmin) NoUacCheck.ToolTip = Loc.T("set.needadmin");
    }

    private void AddLanguagePill(string? code, string name)
    {
        LanguagePanel.Children.Add(new RadioButton
        {
            Style = (Style)FindResource("Pill"),
            GroupName = "lang",
            Content = name,
            Tag = code,
            IsChecked = _edit.Language == code,
        });
    }

    private string? SelectedLanguage =>
        LanguagePanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    // ---------------------------------------------------------------------
    //  Actualizaciones
    // ---------------------------------------------------------------------

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateResult.Text = Loc.T("set.updates.checking");
        var info = await UpdateChecker.CheckAsync();
        CheckNowButton.IsEnabled = true;
        if (info == null)
        {
            UpdateResult.Text = Loc.F("update.none.text", UpdateChecker.CurrentVersion.ToString(3));
            return;
        }
        UpdateResult.Text = Loc.F("set.updates.found", info.Version.ToString(3));
        CheckUpdatesNow = true;
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
                    : new BackendStatus(true, Loc.T("be.direct.name"), Loc.T("set.direct.onsave"));
                break;
            default:
                st = new BackendStatus(true, Loc.T("be.sim.title"), Loc.T("be.sim.text"));
                break;
        }
        string prefix = SelectedBackend == BackendKind.Auto ? Loc.F("set.engine.autoprefix", kind) + " " : "";
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
            Title = Loc.T("set.fc.browse.title"),
            Filter = "FanControl|FanControl.exe|*.exe|*.exe",
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
            BkStatus.Text = Loc.T("set.fc.detect.none");
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
            Content = configs.Count > 0 ? Loc.F("set.fc.auto.now", Path.GetFileName(configs[0])) : Loc.T("set.fc.auto.none"),
            Tag = null,
        };
        ConfigList.Items.Add(auto);
        ListBoxItem selected = auto;
        foreach (var c in configs)
        {
            var item = new ListBoxItem
            {
                Content = $"{Path.GetFileName(c)}   ·   {File.GetLastWriteTime(c):g}",
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
        var dlg = new OpenFileDialog { Title = Loc.T("set.fc.normal"), Filter = "FanControl|*.json" };
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
            ControlsPanel.Children.Add(Hint(Loc.T("set.fc.noconfig")));
            return;
        }

        try
        {
            var controls = FanControlConfigTool.ListControlsFromFile(path);
            if (controls.Count == 0)
            {
                ControlsPanel.Children.Add(Hint(Loc.T("set.fc.nofans")));
                return;
            }
            foreach (var c in controls)
            {
                string state = c.Enabled == false ? " · " + Loc.T("set.fc.disabled") : c.ManualNow ? $" · manual {c.ManualValue:0}%" : "";
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
            ControlsPanel.Children.Add(Hint(Loc.T("set.fc.readerror") + " " + ex.Message));
        }
    }

    private void RefreshDirectList()
    {
        CaptureExclusions();
        DirectControlsPanel.Children.Clear();
        _directChecks.Clear();
        if (_app.Sensors is not HardwareService { HasMotherboard: true } hw)
        {
            DirectControlsPanel.Children.Add(Hint(Loc.T("set.direct.choose")));
            return;
        }
        var list = hw.ControlList;
        if (list.Count == 0)
        {
            DirectControlsPanel.Children.Add(Hint(HardwareService.PawnIoInstalled() ? Loc.T("set.direct.none") : Loc.T("be.direct.nopawnio")));
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
        var dlg = new OpenFileDialog { Title = Loc.T("set.fc.custommax"), Filter = "FanControl|*.json" };
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
        s.Language = SelectedLanguage;
        s.CheckUpdates = UpdatesCheck.IsChecked == true;
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
                TestResult.Text = Loc.T("set.test.directfirst");
                return;
            default:
                TestResult.Text = Loc.T("set.test.sim");
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
        TestButtonText.Text = Loc.T("set.test.stop");
        TestResult.Text = Loc.T("set.test.starting");
        _testTask = RunTestAsync(backend, _testCts.Token);
        await _testTask;
        _testCts.Dispose();
        _testCts = null;
        _testTask = null;
        SaveButton.IsEnabled = CancelButton.IsEnabled = true;
        TestButtonText.Text = Loc.T("set.test");
    }

    private async Task RunTestAsync(IFanBackend backend, CancellationToken ct)
    {
        bool failed = false;
        try
        {
            await backend.BoostAsync(ct);
            for (int i = 10; i > 0; i--)
            {
                TestResult.Text = Loc.F("set.test.running", i);
                await Task.Delay(1000, ct);
            }
            TestResult.Text = Loc.T("set.test.restoring");
        }
        catch (OperationCanceledException)
        {
            TestResult.Text = Loc.T("set.test.cancelled");
        }
        catch (Exception ex)
        {
            failed = true;
            TestResult.Text = "✖ " + ex.Message;
            Log.Warn("Prueba fallida: " + ex.Message);
        }
        finally
        {
            try
            {
                await backend.RestoreAsync();
                if (!failed) TestResult.Text = Loc.T("set.test.ok");
            }
            catch (Exception ex)
            {
                TestResult.Text = "✖ " + Loc.T("set.test.restorefail") + " " + ex.Message;
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
        MakeShortcut(Loc.T("shortcut.cooloff.name"), "--start --then shutdown", Loc.T("shortcut.cooloff.desc"));

    private void ShortcutCool_Click(object sender, RoutedEventArgs e) =>
        MakeShortcut(Loc.T("shortcut.cool.name"), "--start --then nothing", Loc.T("shortcut.cool.desc"));

    private void MakeShortcut(string name, string args, string description)
    {
        var err = WindowsIntegration.CreateDesktopShortcut(name, args, description);
        ExtrasResult.Text = err == null
            ? Loc.F("set.shortcut.created", name) + (NoUacCheck.IsChecked == true ? "" : " " + Loc.T("set.shortcut.tip"))
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
            bool wantNoUac = NoUacCheck.IsChecked == true;
            if (wantNoUac != _noUacTaskOriginal)
            {
                var err = wantNoUac
                    ? WindowsIntegration.CreateTask(WindowsIntegration.ElevatedTask, "--from-task", atLogon: false)
                    : WindowsIntegration.DeleteTask(WindowsIntegration.ElevatedTask);
                if (err != null)
                    MessageBox.Show(this, Loc.T("set.nouac") + ": " + err, "FanMaxxxing", MessageBoxButton.OK, MessageBoxImage.Warning);
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
