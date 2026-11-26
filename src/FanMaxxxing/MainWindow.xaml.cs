using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FanMaxxxing.Core;
using FanMaxxxing.UI;

namespace FanMaxxxing;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly CooldownEngine _engine = new();
    private readonly Random _rng = new();

    // Temporizadores: todos creados una vez, parados cuando no hacen falta.
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _memeTimer;
    private readonly DispatcherTimer _tagTimer;
    private readonly DispatcherTimer _powerTimer;
    private readonly DispatcherTimer _toastTimer;

    // Bucle de render
    private bool _renderHooked;
    private TimeSpan _lastRenderTime;
    private double _frameAcc;
    private double _time;
    private double _shakeLeft;
    private double _heat = 0.5, _targetHeat = 0.5;
    private double _wind, _targetWind;

    // Estado
    private bool _loaded;
    private bool _reallyClosing;
    private bool _suppressPillEvents;
    private LaunchOptions? _pendingLaunch;
    private SensorSnapshot? _coolStart;
    private AfterAction _runAfter;
    private BackendKind _runBackendKind;
    private bool _displayOffDone;
#pragma warning disable CS0649 // solo se asigna en Debug
    private TimeSpan? _debugDuration;
#pragma warning restore CS0649
#if DEBUG
    private bool _debugLoop;
    private int _debugCycles;
#endif
    private DateTime _doneUntil = DateTime.MinValue;
    private bool _lastRunFailed;
    private MemeMood _memeMood = (MemeMood)(-1);
    private int _uiTicks;
    private int _powerLeft;
    private AfterAction _powerAction;

    // Diálogo de confirmación y actualizaciones
    private TaskCompletionSource<bool>? _confirmTcs;
    private bool _closePromptOpen;
    private UpdateInfo? _update;
    private CancellationTokenSource? _updateCts;

    // Pool de etiquetas flotantes estilo texto de TikTok (tamaño fijo)
    private readonly Border[] _tags = new Border[6];
    private int _tagNext;
    private static readonly (Brush Bg, Brush Fg)[] TagStyles =
    {
        (Frozen(Colors.White), Frozen(Colors.Black)),
        (Frozen(Color.FromRgb(0x7C, 0x5C, 0xFF)), Frozen(Colors.White)),
        (Frozen(Color.FromRgb(0x22, 0xD3, 0xEE)), Frozen(Colors.Black)),
        (Frozen(Color.FromRgb(0xFF, 0x2E, 0x88)), Frozen(Colors.White)),
        (Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A)), Frozen(Colors.Black)),
    };

    private static readonly Brush IdleButtonBrush = MakeGradient("#22D3EE", "#7C5CFF", "#FF2E88");
    private static readonly Brush CancelButtonBrush = MakeGradient("#FF6B35", "#FF3B3B", "#B0124F");
    private static readonly Brush BusyButtonBrush = MakeGradient("#3A4470", "#2B3560", "#3A4470");

    private static readonly AnimationTimeline TagFade = MakeTagFade();
    private static readonly IEasingFunction BackOut = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += UiTimer_Tick;
        _memeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(7) };
        _memeTimer.Tick += (_, _) => ShowMeme(_memeMood);
        _tagTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.1) };
        _tagTimer.Tick += (_, _) => SpawnTag();
        _powerTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _powerTimer.Tick += PowerTimer_Tick;
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background);
        _toastTimer.Tick += (_, _) => HideToast();

        _engine.PhaseChanged += Engine_PhaseChanged;
        _engine.Ticked += Engine_Ticked;

        var tagFont = new FontFamily("Segoe UI Black, Microsoft YaHei UI, Segoe UI");
        for (int i = 0; i < _tags.Length; i++)
        {
            var tag = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 3, 10, 5),
                Opacity = 0,
                RenderTransform = new RotateTransform(),
                Child = new TextBlock { FontFamily = tagFont, FontWeight = FontWeights.Black },
            };
            _tags[i] = tag;
            DogeCanvas.Children.Add(tag);
        }

        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => UpdateRenderHook();
        StateChanged += (_, _) => UpdateRenderHook();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public bool IsCooling => _engine.IsBusy;
    public bool IsPowerPending => _powerTimer.IsEnabled;

    // =====================================================================
    //  Ciclo de vida
    // =====================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ApplySettingsToUi();
        RefreshBackendStatus();
        RefreshRank();
        UiTimer_Tick(null, EventArgs.Empty);
        ShowMeme(MemeMoodNow(), force: true);
        _uiTimer.Start();
        _memeTimer.Start();
        UpdateRenderHook();

        SimBadge.Visibility = _app.ForceSimulation || _app.Settings.Backend == BackendKind.Simulation ? Visibility.Visible : Visibility.Collapsed;
        NoAdminBadge.Visibility = Elevation.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

        if (_pendingLaunch != null)
        {
            var l = _pendingLaunch;
            _pendingLaunch = null;
            Dispatcher.BeginInvoke(() => HandleLaunch(l), DispatcherPriority.ApplicationIdle);
        }

        // Buscar actualizaciones unos segundos después de arrancar (sin molestar al inicio)
        if (_app.Settings.CheckUpdates)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                await CheckForUpdatesAsync(manual: false);
            };
            t.Start();
        }
    }

    /// <summary>Órdenes de línea de comandos (al arrancar o reenviadas por otra instancia).</summary>
    public void HandleLaunch(LaunchOptions o)
    {
        if (!_loaded)
        {
            _pendingLaunch = o;
            return;
        }
#if DEBUG
        if (o.DebugSeconds is double secs) _debugDuration = TimeSpan.FromSeconds(secs);
        if (o.DebugLoop) _debugLoop = true;
#endif
        _suppressPillEvents = true;
        if (o.Minutes is int m) SelectDuration(m);
        if (o.Then is AfterAction a) SelectAfter(a);
        _suppressPillEvents = false;

        if (o.StartNow && !_engine.IsBusy)
        {
            CancelPowerCountdown();
            _ = StartCooldownAsync(o.Minutes ?? _app.Settings.DurationMinutes, o.Then ?? _app.Settings.AfterAction);
        }

#if DEBUG
        if (o.SnapPath is string snap)
        {
            FrameworkElement target = RootGrid;
            SettingsWindow? sw = null;
            if (o.SnapSettings)
            {
                sw = new SettingsWindow(_app) { Owner = this };
                sw.Show();
                target = (FrameworkElement)sw.Content;
            }
            if (o.SnapConfirm) _ = AskCloseAsync();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(o.SnapDelay) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)(target.ActualWidth * dpi.DpiScaleX), (int)(target.ActualHeight * dpi.DpiScaleY),
                        dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    if (sw != null)
                    {
                        var dv = new DrawingVisual();
                        using (var dc = dv.RenderOpen()) dc.DrawRectangle(sw.Background, null, new Rect(0, 0, target.ActualWidth, target.ActualHeight));
                        bmp.Render(dv);
                    }
                    bmp.Render(target);
                    sw?.Close();
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var fs = System.IO.File.Create(snap);
                    enc.Save(fs);
                }
                catch (Exception ex) { Log.Error("Snap", ex); }
                _engine.Cancel();
                Dispatcher.InvokeAsync(async () =>
                {
                    while (_engine.IsBusy) await Task.Delay(50);
                    _reallyClosing = true;
                    _app.ExitApp();
                });
            };
            t.Start();
        }
#endif
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_reallyClosing)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        _ = AskCloseAsync();
    }

    /// <summary>«¿Seguro que quieres cerrar?» y, si se confirma, cierra de verdad.</summary>
    private async Task AskCloseAsync()
    {
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        try
        {
            BringToFront();
            bool ok = await ConfirmAsync(Loc.T("confirm.close.title"),
                _engine.IsBusy ? Loc.T("confirm.close.cooling") : Loc.T("confirm.close.text"),
                Loc.T("confirm.close.yes"), Loc.T("confirm.close.no"));
            if (ok) await ExitAsync();
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    /// <summary>Sale ya: si hay enfriamiento, primero devuelve los ventiladores a la normalidad.</summary>
    private async Task ExitAsync()
    {
        if (_engine.IsBusy)
        {
            _engine.Cancel();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (_engine.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(100);
        }
        CancelPowerCountdown();
        _reallyClosing = true;
        _app.ExitApp();
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Llamado por App al salir: corta timers y eventos para no dejar nada colgando.</summary>
    public void Teardown()
    {
        _reallyClosing = true;
        _uiTimer.Stop();
        _memeTimer.Stop();
        _tagTimer.Stop();
        _powerTimer.Stop();
        _toastTimer.Stop();
        try { _updateCts?.Cancel(); } catch { /* ignorar */ }
        _confirmTcs?.TrySetResult(false);
        if (_renderHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderHooked = false;
        }
        _engine.PhaseChanged -= Engine_PhaseChanged;
        _engine.Ticked -= Engine_Ticked;
    }

    public void EmergencyRestore() => _engine.EmergencyRestore();

    /// <summary>Windows se está apagando (o el instalador nos cierra): no se pregunta nada.</summary>
    public void MarkSessionEnding() => _reallyClosing = true;

    // =====================================================================
    //  Diálogo de confirmación
    // =====================================================================

    private Task<bool> ConfirmAsync(string title, string text, string yes, string no)
    {
        _confirmTcs?.TrySetResult(false);
        _confirmTcs = new TaskCompletionSource<bool>();
        ConfirmTitle.Text = title;
        ConfirmText.Text = text;
        ConfirmYesText.Text = yes;
        ConfirmNoText.Text = no;
        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmNoButton.Focus();
        return _confirmTcs.Task;
    }

    private void CloseConfirm(bool result)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        var tcs = _confirmTcs;
        _confirmTcs = null;
        tcs?.TrySetResult(result);
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => CloseConfirm(true);
    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => CloseConfirm(false);

    // =====================================================================
    //  Actualizaciones
    // =====================================================================

    /// <summary>Busca una versión nueva en GitHub. Si <paramref name="manual"/>, avisa también cuando no hay.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        var info = await UpdateChecker.CheckAsync();
        if (_reallyClosing) return;
        if (info == null)
        {
            if (manual) ShowToast("✔", Loc.T("update.none.title"), Loc.F("update.none.text", UpdateChecker.CurrentVersion.ToString(3)), TimeSpan.FromSeconds(5));
            return;
        }
        _update = info;
        UpdateText.Text = Loc.F("update.available", info.Version.ToString(3));
        UpdateInstallText.Text = Loc.T("update.install");
        UpdateInstallButton.IsEnabled = true;
        UpdatePanel.Visibility = Visibility.Visible;
        ShowToast("🚀", Loc.F("update.toast.title", info.Version.ToString(3)), Loc.T("update.toast.text"), TimeSpan.FromSeconds(8));
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        var info = _update;
        if (info == null) return;
        if (_engine.IsBusy)
        {
            ShowToast("⏳", Loc.T("update.busy.title"), Loc.T("update.busy.text"), TimeSpan.FromSeconds(5));
            return;
        }
        if (info.MsiUrl == null)
        {
            UpdateChecker.OpenPage(info.PageUrl);
            return;
        }

        UpdateInstallButton.IsEnabled = false;
        _updateCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => UpdateInstallText.Text = Loc.F("update.downloading", (int)(p * 100)));
        try
        {
            var path = await UpdateChecker.DownloadAsync(info, progress, _updateCts.Token);
            UpdateInstallText.Text = Loc.T("update.launching");
            UpdateChecker.LaunchInstaller(path);
            // El instalador cerrará la app si sigue abierta; salimos ya para no estorbar.
            await ExitAsync();
        }
        catch (OperationCanceledException)
        {
            // cerrando la app
        }
        catch (Exception ex)
        {
            Log.Error("Fallo descargando la actualización", ex);
            ShowToast("⚠", Loc.T("update.fail.title"), Loc.T("update.fail.text"), TimeSpan.FromSeconds(10), error: true);
            UpdateInstallText.Text = Loc.T("update.install");
            UpdateInstallButton.IsEnabled = true;
            UpdateChecker.OpenPage(info.PageUrl);
        }
        finally
        {
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
        => UpdateChecker.OpenPage(_update?.PageUrl ?? UpdateChecker.ReleasesPage);

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdatePanel.Visibility = Visibility.Collapsed;

    // =====================================================================
    //  Bucle de animación (uno solo, y solo mientras la ventana se ve)
    // =====================================================================

    private void UpdateRenderHook()
    {
        bool shouldRun = IsVisible && WindowState != WindowState.Minimized && !_reallyClosing;
        if (shouldRun && !_renderHooked)
        {
            _lastRenderTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
            _renderHooked = true;
            if (_loaded) _memeTimer.Start();
            if (_engine.Phase == CooldownPhase.Cooling) _tagTimer.Start();
        }
        else if (!shouldRun && _renderHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderHooked = false;
            _memeTimer.Stop();
            _tagTimer.Stop();
            Particles.Clear();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        if (re.RenderingTime == _lastRenderTime) return; // WPF puede llamar dos veces por frame
        double dt = _lastRenderTime == TimeSpan.Zero ? 1 / 60.0 : (re.RenderingTime - _lastRenderTime).TotalSeconds;
        _lastRenderTime = re.RenderingTime;
        if (dt <= 0 || dt > 0.25) dt = 1 / 60.0;

        // Limitamos a ~40 fps (24 en modo reducido): menos trabajo para la GPU que queremos enfriar.
        double frame = _app.Settings.ReducedAnimations ? 1 / 24.0 : 1 / 40.0;
        _frameAcc += dt;
        if (_frameAcc < frame) return;
        dt = Math.Min(_frameAcc, 0.1);
        _frameAcc = 0;
        _time += dt;

        Fan.Advance(dt);

        _heat += (_targetHeat - _heat) * Math.Min(1, dt * 0.8);
        _wind += (_targetWind - _wind) * Math.Min(1, dt * 1.5);
        Fan.SetHeat(_heat);
        ApplyBackgroundHeat(_heat);

        Particles.Wind = _wind;
        Particles.Reduced = _app.Settings.ReducedAnimations;
        Particles.Update(dt);

        LogoSpin.Angle = (LogoSpin.Angle + Fan.CurrentSpeed * dt * 0.5) % 360;

        if (_shakeLeft > 0)
        {
            _shakeLeft -= dt;
            double amp = Math.Max(0, _shakeLeft) * 14;
            ShakeTransform.X = (_rng.NextDouble() - 0.5) * amp;
            ShakeTransform.Y = (_rng.NextDouble() - 0.5) * amp;
        }
        else if (ShakeTransform.X != 0 || ShakeTransform.Y != 0)
        {
            ShakeTransform.X = ShakeTransform.Y = 0;
        }

        double pulse = _engine.IsBusy ? 1 : 1 + 0.018 * Math.Sin(_time * 3.2);
        MainButtonScale.ScaleX = MainButtonScale.ScaleY = pulse;

        if (PowerOverlay.Visibility == Visibility.Visible)
        {
            double s = 1 + 0.06 * Math.Max(0, Math.Sin(_time * Math.PI * 2));
            PowerCountScale.ScaleX = PowerCountScale.ScaleY = s;
        }
    }

    private void ApplyBackgroundHeat(double h)
    {
        BgStop0.Color = FanView.Lerp(Color.FromRgb(0x07, 0x10, 0x22), Color.FromRgb(0x14, 0x08, 0x10), h);
        BgStop1.Color = FanView.Lerp(Color.FromRgb(0x0C, 0x1E, 0x3E), Color.FromRgb(0x2A, 0x10, 0x1C), h);
        BgStop2.Color = FanView.Lerp(Color.FromRgb(0x0B, 0x33, 0x4D), Color.FromRgb(0x4A, 0x16, 0x10), h);
    }

    // =====================================================================
    //  Refresco de datos (1 Hz)
    // =====================================================================

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        _uiTicks++;
        var s = _app.Sensors?.Latest ?? SensorSnapshot.Empty;

        CpuCard.Push(s.CpuTemp);
        GpuCard.Push(s.GpuTemp);
        CpuCard.SetSubtitle(s.CpuName ?? (_app.Sensors?.Status ?? ""));
        GpuCard.SetSubtitle(s.GpuName ?? "");

        if (_engine.IsBusy && _coolStart != null)
        {
            CpuCard.SetDelta(s.CpuTemp - _coolStart.CpuTemp);
            GpuCard.SetDelta(s.GpuTemp - _coolStart.GpuTemp);
        }
        else if (DateTime.UtcNow > _doneUntil)
        {
            CpuCard.SetDelta(null);
            GpuCard.SetDelta(null);
        }

        if (s.FanRpm is double rpm)
        {
            RpmText.Text = $"{rpm:0} RPM";
            RpmLabel.Text = Loc.T("main.rpm.label");
        }
        else
        {
            RpmText.Text = "";
            RpmLabel.Text = "";
        }

        if (s.MaxTemp is double t) _targetHeat = Math.Clamp((t - 38) / 40, 0, 1);
        else if (!_engine.IsBusy) _targetHeat = DateTime.UtcNow < _doneUntil ? 0.1 : 0.45;

        if (!_engine.IsBusy && !IsPowerPending)
        {
            Fan.SetMood(FanMoodNow(s));
            if (DateTime.UtcNow < _doneUntil && !_lastRunFailed) Fan.SetHub("OK", Loc.T("hub.cool"));
            else if (s.MaxTemp is double mt) Fan.SetHub($"{mt:0}°", "AUTO");
            else Fan.SetHub("AUTO", "");
            Particles.Mode = _targetHeat > 0.45 ? ParticleMode.Embers : ParticleMode.None;
            Particles.Intensity = Math.Clamp((_targetHeat - 0.35) * 1.6, 0.15, 1);
            var mood = MemeMoodNow();
            if (mood != _memeMood) ShowMeme(mood, force: true);
            if (!_lastRunFailed || DateTime.UtcNow > _doneUntil) UpdateIdleTexts();
        }

        if (_uiTicks % 10 == 0 && !_engine.IsBusy) RefreshBackendStatus();
    }

    private FanMood FanMoodNow(SensorSnapshot s)
    {
        if (DateTime.UtcNow < _doneUntil) return _lastRunFailed ? FanMood.Neutral : FanMood.Happy;
        return s.MaxTemp switch
        {
            double t when t >= 68 => FanMood.Hot,
            double t when t < 48 => FanMood.Happy,
            _ => FanMood.Neutral,
        };
    }

    private MemeMood MemeMoodNow()
    {
        if (IsPowerPending) return MemeMood.Night;
        if (_engine.IsBusy) return MemeMood.Cooling;
        if (DateTime.UtcNow < _doneUntil) return _lastRunFailed ? MemeMood.Error : MemeMood.Done;
        var t = _app.Sensors?.Latest.MaxTemp;
        return t is double v && v < 52 ? MemeMood.IdleCool : MemeMood.IdleHot;
    }

    private void UpdateIdleTexts()
    {
        if (_engine.IsBusy) return;
        int min = _app.Settings.DurationMinutes;
        var after = _app.Settings.AfterAction;
        TimerText.Text = $"{min:00}:00";
        if (DateTime.UtcNow > _doneUntil)
            StatusText.Text = after == AfterAction.Nothing
                ? Loc.F("status.ready", min)
                : Loc.F("status.ready.then", min, PowerActions.Describe(after));
        MainButtonSub.Text = after == AfterAction.Nothing
            ? Loc.F("main.button.sub", min)
            : Loc.F("main.button.sub.then", min, PowerActions.Describe(after));
    }

    public void RefreshBackendStatus()
    {
        try
        {
            var backend = _app.CreateBackend();
            var st = backend.GetStatus();
            BackendTitle.Text = st.Title;
            BackendDetail.Text = st.Detail;
            BackendDetail.ToolTip = st.Detail;
            StatusDot.Fill = st.Ready
                ? (backend.Kind == BackendKind.Simulation ? (Brush)FindResource("Yellow") : (Brush)FindResource("Lime"))
                : (Brush)FindResource("Danger");
            SimBadge.Visibility = backend.Kind == BackendKind.Simulation ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.ErrorThrottled("Error comprobando el motor", ex);
        }
    }

    private void RefreshRank()
    {
        var (rank, next) = Memes.RankFor(_app.Settings.Stats.TotalCooldowns);
        RankText.Text = rank.ToUpper(System.Globalization.CultureInfo.CurrentUICulture);
        RankNext.Text = next;
    }

    // =====================================================================
    //  Memes y efectos
    // =====================================================================

    private void ShowMeme(MemeMood mood, bool force = false)
    {
        if (mood != _memeMood || force)
        {
            _memeMood = mood;
            _memeTimer.Stop();
            _memeTimer.Interval = TimeSpan.FromSeconds(mood == MemeMood.Cooling ? 6 : 8);
            if (_renderHooked) _memeTimer.Start();
        }
        var m = Memes.Next(mood);
        TopCaption.Text = m.Top;
        BottomCaption.Text = m.Bottom;
        PopIn(TopCaption, TopCaptionScale, 0);
        PopIn(BottomCaption, BottomCaptionScale, 0.12);
    }

    private static void PopIn(UIElement el, ScaleTransform scale, double delay)
    {
        var begin = TimeSpan.FromSeconds(delay);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35)) { BeginTime = begin };
        var grow = new DoubleAnimation(0.7, 1, TimeSpan.FromSeconds(0.45)) { BeginTime = begin, EasingFunction = BackOut };
        fade.Freeze();
        grow.Freeze();
        el.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void Flash(string text)
    {
        FlashText.Text = text;
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.8) };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8))));
        opacity.Freeze();
        var scale = new DoubleAnimation(0.3, 1.0, TimeSpan.FromSeconds(0.5)) { EasingFunction = BackOut };
        scale.Freeze();
        FlashText.BeginAnimation(OpacityProperty, opacity);
        FlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        FlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void SpawnTag()
    {
        if (_app.Settings.ReducedAnimations || !_renderHooked) return;
        double w = DogeCanvas.ActualWidth, h = DogeCanvas.ActualHeight;
        if (w < 100 || h < 100) return;

        var tag = _tags[_tagNext];
        _tagNext = (_tagNext + 1) % _tags.Length;
        var tb = (TextBlock)tag.Child;
        var (bg, fg) = TagStyles[_rng.Next(TagStyles.Length)];
        tb.Text = Memes.NextTag();
        tb.FontSize = 18 + _rng.Next(10);
        tb.Foreground = fg;
        tag.Background = bg;
        // Por los laterales del escenario (columna izquierda), sin tapar el ventilador ni el panel derecho
        var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, FontStyles.Normal, FontWeights.Black, FontStretches.Normal), tb.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double tagW = ft.WidthIncludingTrailingWhitespace + tag.Padding.Left + tag.Padding.Right + 10;
        double stageLeft = 34, stageRight = w - 18 - 430 - 18 - 16; // márgenes + panel derecho de 430 px
        bool leftSide = _rng.Next(2) == 0;
        double x = leftSide
            ? stageLeft + _rng.NextDouble() * 70
            : Math.Max(stageLeft, stageRight - tagW - _rng.NextDouble() * 70);
        double y = 120 + _rng.NextDouble() * Math.Max(50, h - 320);
        Canvas.SetLeft(tag, x);
        Canvas.SetTop(tag, y);
        ((RotateTransform)tag.RenderTransform).Angle = _rng.Next(-8, 9);
        tag.BeginAnimation(OpacityProperty, TagFade);
    }

    /// <summary>Aparece rápido, se mantiene y se desvanece. Congelada y compartida por todo el pool.</summary>
    private static AnimationTimeline MakeTagFade()
    {
        var kf = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.6) };
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.7)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        kf.Freeze();
        return kf;
    }

    public void ShowToast(string icon, string title, string text, TimeSpan? duration = null, bool error = false)
    {
        ToastIcon.Text = icon;
        ToastTitle.Text = title;
        ToastText.Text = text;
        ToastText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        ToastIcon.Foreground = error ? (Brush)FindResource("Danger") : (Brush)FindResource("Yellow");
        ToastBorderA.Color = error ? Color.FromRgb(0xFF, 0x4D, 0x5E) : Color.FromRgb(0x22, 0xD3, 0xEE);
        ToastBorderB.Color = error ? Color.FromRgb(0xFF, 0x6B, 0x35) : Color.FromRgb(0xFF, 0x2E, 0x88);

        var fade = new DoubleAnimation(1, TimeSpan.FromSeconds(0.3));
        var slide = new DoubleAnimation(-24, 0, TimeSpan.FromSeconds(0.45)) { EasingFunction = BackOut };
        fade.Freeze();
        slide.Freeze();
        Toast.BeginAnimation(OpacityProperty, fade);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, slide);

        _toastTimer.Stop();
        _toastTimer.Interval = duration ?? TimeSpan.FromSeconds(error ? 10 : 6);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.4));
        fade.Freeze();
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static TF Freeze<TF>(TF f) where TF : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Brush MakeGradient(string a, string b, string c)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop((Color)ColorConverter.ConvertFromString(a), 0),
                new GradientStop((Color)ColorConverter.ConvertFromString(b), 0.5),
                new GradientStop((Color)ColorConverter.ConvertFromString(c), 1),
            },
        };
        g.Freeze();
        return g;
    }

    // =====================================================================
    //  Enfriamiento
    // =====================================================================

    private async Task StartCooldownAsync(int minutes, AfterAction after)
    {
        if (_engine.IsBusy) return;
        CancelPowerCountdown();

        IFanBackend backend;
        try
        {
            backend = _app.CreateBackend();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear el motor", ex);
            ShowToast("⚠", Loc.T("toast.cantstart"), ex.Message, error: true);
            return;
        }

        var status = backend.GetStatus();
        if (!status.Ready)
        {
            _app.Sound.PlayBoop();
            ShowToast("⚠", status.Title, status.Detail, error: true);
            RefreshBackendStatus();
            return;
        }

        var s = _app.Settings;
        _runAfter = after;
        _runBackendKind = backend.Kind;
        _coolStart = _app.Sensors.Latest;
        _displayOffDone = false;
        _lastRunFailed = false;
        _doneUntil = DateTime.MinValue;

        _app.Sound.PlayTurbine();
        _shakeLeft = 0.8;
        Flash(Loc.T("flash.start"));
        Fan.SetMood(FanMood.Excited);
        Fan.TargetSpeed = 1500;
        Fan.Vibration = 1;
        _targetWind = 1;
        Particles.Mode = ParticleMode.Snow;
        Particles.Intensity = 1;
        ShowMeme(MemeMood.Cooling, force: true);
        if (_renderHooked) _tagTimer.Start();

        var duration = _debugDuration ?? TimeSpan.FromMinutes(minutes);
        // En enfriamientos cortos (1-2 min) el mínimo para la parada inteligente no puede pasar del 60 % del total
        var smartMin = TimeSpan.FromSeconds(Math.Min(s.SmartStopMinSeconds, duration.TotalSeconds * 0.6));
        var req = new CooldownRequest(duration, s.SmartStop, s.SmartStopCpuTemp, s.SmartStopGpuTemp, smartMin);

        CooldownResult result;
        try
        {
            result = await _engine.RunAsync(backend, req, () => _app.Sensors.Latest);
        }
        catch (Exception ex)
        {
            Log.Error("Fallo inesperado en el enfriamiento", ex);
            result = new CooldownResult(CooldownEnd.Error, TimeSpan.Zero, null, null, null, null, ex.Message, null);
        }

        OnCooldownFinished(result);

#if DEBUG
        if (_debugLoop && !_reallyClosing)
        {
            _debugCycles++;
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
            {
                long managed = GC.GetTotalMemory(forceFullCollection: true);
                proc.Refresh();
                Log.Info($"[MEM] ciclo {_debugCycles}: gestionada={managed / 1024} KB · privada={proc.PrivateMemorySize64 / 1024} KB · " +
                         $"handles={proc.HandleCount} · GDI={GetGuiResources(proc.Handle, 0)} · USER={GetGuiResources(proc.Handle, 1)} · hilos={proc.Threads.Count}");
            }
            await Task.Delay(1500);
            if (_debugLoop && !_reallyClosing) _ = StartCooldownAsync(minutes, after);
        }
#endif
    }

#if DEBUG
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
#endif

    private void Engine_PhaseChanged(CooldownPhase phase)
    {
        switch (phase)
        {
            case CooldownPhase.Starting:
                MainButton.IsEnabled = false;
                MainButton.Background = BusyButtonBrush;
                MainButtonText.Text = Loc.T("main.button.starting");
                MainButtonSub.Text = Loc.T("main.button.starting.sub");
                StatusText.Text = Loc.T("status.starting");
                Fan.SetHub("MAX", Loc.T("hub.starting"));
                SetPillsEnabled(false);
                break;
            case CooldownPhase.Cooling:
                MainButton.IsEnabled = true;
                MainButton.Background = CancelButtonBrush;
                MainButtonText.Text = Loc.T("main.button.cancel");
                MainButtonSub.Text = Loc.T("main.button.cancel.sub");
                break;
            case CooldownPhase.Restoring:
                MainButton.IsEnabled = false;
                MainButton.Background = BusyButtonBrush;
                MainButtonText.Text = Loc.T("main.button.landing");
                MainButtonSub.Text = Loc.T("main.button.landing.sub");
                StatusText.Text = Loc.T("status.restoring");
                Fan.TargetSpeed = 220;
                Fan.Vibration = 0;
                Fan.SetHub("AUTO", Loc.T("hub.restoring"));
                _targetWind = 0;
                _tagTimer.Stop();
                break;
            case CooldownPhase.Idle:
                MainButton.IsEnabled = true;
                MainButton.Background = IdleButtonBrush;
                MainButtonText.Text = Loc.T("main.button.start");
                Fan.TargetSpeed = 40;
                Fan.SetProgress(0);
                SetPillsEnabled(true);
                UpdateIdleTexts();
                break;
        }
    }

    private void Engine_Ticked(CooldownTick t)
    {
        TimerText.Text = $"{(int)t.Remaining.TotalMinutes:00}:{t.Remaining.Seconds:00}";
        Fan.SetProgress(t.Progress);

        string temps = "";
        if (t.Sensors.CpuTemp is double c) temps += $"CPU {c:0}°";
        if (t.Sensors.GpuTemp is double g) temps += (temps.Length > 0 ? " · " : "") + $"GPU {g:0}°";
        string status = Loc.T("status.cooling") + (temps.Length > 0 ? " " + temps + "." : "");
        if (t.SmartStopPending) status += " " + Loc.T("status.smartpending");
        else if (_runAfter != AfterAction.Nothing) status += " " + Loc.F("status.then", PowerActions.Describe(_runAfter));
        StatusText.Text = status;

        Fan.SetMood(t.Progress > 0.8 ? FanMood.Happy : FanMood.Excited);
        Fan.SetHub("MAX", t.Sensors.FanRpm is double rpm ? $"{rpm:0} RPM" : "100 %");

        if (_app.Settings.TurnOffDisplay && !_displayOffDone && t.Elapsed.TotalSeconds > 8)
        {
            _displayOffDone = true;
            PowerActions.TurnOffDisplay(new WindowInteropHelper(this).EnsureHandle());
        }
    }

    private void OnCooldownFinished(CooldownResult r)
    {
        var s = _app.Settings;
        _doneUntil = DateTime.UtcNow.AddSeconds(25);
        Fan.Vibration = 0;
        _targetWind = 0;
        Particles.Mode = ParticleMode.None;

        string temps = TempSummary(r);

        if (r.RestoreError != null)
        {
            ShowToast("⚠", Loc.T("toast.restorefail.title"), r.RestoreError + " " + Loc.T("toast.restorefail.text"),
                TimeSpan.FromSeconds(15), error: true);
        }

        switch (r.End)
        {
            case CooldownEnd.Error:
                _lastRunFailed = true;
                _app.Sound.PlayBoop();
                Fan.SetMood(FanMood.Neutral);
                ShowMeme(MemeMood.Error, force: true);
                StatusText.Text = Loc.F("status.failed", r.Error);
                if (r.RestoreError == null) ShowToast("⚠", Loc.T("toast.failed"), r.Error ?? "?", error: true);
                return;

            case CooldownEnd.Cancelled:
                StatusText.Text = Loc.T("status.cancelled") + " " + temps;
                _doneUntil = DateTime.UtcNow;
                ShowMeme(MemeMoodNow(), force: true);
                if (r.RestoreError == null) ShowToast("✋", Loc.T("toast.cancelled.title"), Loc.T("toast.cancelled.text"), TimeSpan.FromSeconds(4));
                return;
        }

        // Completado (tiempo o parada inteligente)
        s.Stats.TotalCooldowns++;
        s.Stats.TotalCoolingSeconds += r.Elapsed.TotalSeconds;
        if (r.BestDrop is double drop && drop > 0)
        {
            s.Stats.TotalDegreesDropped += drop;
            s.Stats.BestDrop = Math.Max(s.Stats.BestDrop, drop);
        }
        _app.SaveSettings();
        RefreshRank();

        Fan.SetMood(FanMood.Happy);
        ShowMeme(MemeMood.Done, force: true);
        Particles.Confetti(_app.Settings.ReducedAnimations ? 40 : 110);
        Flash(Loc.T("flash.done"));
        _app.Sound.PlayChime();
        StatusText.Text = (r.End == CooldownEnd.SmartStop ? Loc.T("status.done.smart") : Loc.T("status.done")) + " " + temps;

        var (title, sub) = Memes.Achievement(r);
        if (r.RestoreError == null)
            ShowToast("🏆", Loc.F("toast.achievement", title), sub, TimeSpan.FromSeconds(7));

        if (_runAfter != AfterAction.Nothing)
            StartPowerCountdown(_runAfter);
    }

    private static string TempSummary(CooldownResult r)
    {
        var parts = new List<string>();
        if (r.CpuStart is double a && r.CpuEnd is double b) parts.Add($"CPU {a:0}→{b:0} °C");
        if (r.GpuStart is double c && r.GpuEnd is double d) parts.Add($"GPU {c:0}→{d:0} °C");
        return string.Join(" · ", parts);
    }

    // =====================================================================
    //  Cuenta atrás de apagado
    // =====================================================================

    private void StartPowerCountdown(AfterAction action)
    {
        _powerAction = action;
        _powerLeft = _app.Settings.PowerCountdownSeconds;
        string k = action switch
        {
            AfterAction.Shutdown => "shutdown",
            AfterAction.Hibernate => "hibernate",
            _ => "sleep",
        };
        PowerSub.Text = Loc.T("power.sub." + k);
        PowerNowText.Text = Loc.T("power.now." + k);
        var night = Memes.Next(MemeMood.Night);
        PowerTitle.Text = night.Top;
        PowerHint.Text = night.Bottom.ToLower(System.Globalization.CultureInfo.CurrentUICulture) + " · " + Loc.T("power.hint");
        PowerCount.Text = _powerLeft.ToString();
        PowerOverlay.Visibility = Visibility.Visible;
        Fan.SetMood(FanMood.Sleepy);
        Fan.SetHub("OFF", Loc.T("hub.night"));
        ShowMeme(MemeMood.Night, force: true);

        // Que se vea aunque esté minimizada o detrás de otra ventana
        BringToFront();
        PowerCancelButton.Focus();
        _powerTimer.Start();
    }

    private void PowerTimer_Tick(object? sender, EventArgs e)
    {
        _powerLeft--;
        PowerCount.Text = Math.Max(0, _powerLeft).ToString();
        if (_powerLeft <= 5 && _powerLeft > 0) _app.Sound.PlayBoop();
        if (_powerLeft > 0) return;

        _powerTimer.Stop();
        ExecutePowerAction();
    }

    private void ExecutePowerAction()
    {
        _powerTimer.Stop();
        var s = _app.Settings;
        if (_powerAction is AfterAction.Shutdown or AfterAction.Hibernate) s.Stats.NightsSaved++;
        _app.SaveSettings();

        if (_runBackendKind == BackendKind.Simulation)
        {
            // En simulación NUNCA se apaga de verdad.
            Log.Info($"Simulación: aquí se haría '{_powerAction}'");
            PowerOverlay.Visibility = Visibility.Collapsed;
            ShowToast("🎭", Loc.F("toast.sim.power", PowerActions.Describe(_powerAction)), Loc.T("toast.sim.power.text"), TimeSpan.FromSeconds(6));
            Fan.SetMood(FanMood.Happy);
            ShowMeme(MemeMoodNow(), force: true);
            return;
        }

        var err = PowerActions.Execute(_powerAction, s.ForceCloseApps);
        if (err != null)
        {
            PowerOverlay.Visibility = Visibility.Collapsed;
            ShowToast("⚠", Loc.T("toast.powerfail"), err, error: true);
            Fan.SetMood(FanMood.Neutral);
            return;
        }
        if (_powerAction == AfterAction.Sleep)
        {
            PowerOverlay.Visibility = Visibility.Collapsed;
            ShowMeme(MemeMoodNow(), force: true);
        }
        else
        {
            PowerCount.Text = "zZz";
            PowerSub.Text = Loc.T("power.goingdown");
        }
    }

    public void CancelPowerCountdown()
    {
        if (!_powerTimer.IsEnabled && PowerOverlay.Visibility != Visibility.Visible) return;
        _powerTimer.Stop();
        PowerOverlay.Visibility = Visibility.Collapsed;
        Fan.SetMood(FanMood.Neutral);
        ShowMeme(MemeMoodNow(), force: true);
        UpdateIdleTexts();
    }

    private void PowerCancel_Click(object sender, RoutedEventArgs e)
    {
        CancelPowerCountdown();
        ShowToast("😎", Loc.T("toast.awake.title"), Loc.T("toast.awake.text"), TimeSpan.FromSeconds(5));
    }

    private void PowerNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_powerTimer.IsEnabled) return;
        _powerLeft = 0;
        ExecutePowerAction();
    }

    // =====================================================================
    //  Controles
    // =====================================================================

    private void ApplySettingsToUi()
    {
        _suppressPillEvents = true;
        SelectDuration(_app.Settings.DurationMinutes);
        SelectAfter(_app.Settings.AfterAction);
        SmartStopCheck.IsChecked = _app.Settings.SmartStop;
        SmartStopText.Text = Loc.F("main.smartstop", _app.Settings.SmartStopCpuTemp.ToString("0"), _app.Settings.SmartStopGpuTemp.ToString("0"));
        SoundButton.Content = _app.Settings.SoundEnabled ? "" : "";
        _suppressPillEvents = false;
        UpdateIdleTexts();
    }

    private static readonly int[] PresetDurations = { 1, 2, 3, 5 };

    /// <summary>Marca la pastilla de esa duración; si no es 1/2/3/5, usa «Personalizado».</summary>
    private void SelectDuration(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 30);
        _app.Settings.DurationMinutes = minutes;
        bool wasSuppressed = _suppressPillEvents;
        _suppressPillEvents = true;
        if (PresetDurations.Contains(minutes))
        {
            foreach (var rb in DurationPanel.Children.OfType<RadioButton>())
                if ((string)rb.Tag == minutes.ToString()) rb.IsChecked = true;
            CustomDurationPill.Content = Loc.T("main.custom");
            CustomDurationRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            CustomDurationSlider.Value = Math.Max(6, minutes);
            CustomDurationPill.IsChecked = true;
            CustomDurationPill.Content = Loc.F("main.custom.value", minutes);
            CustomDurationText.Text = $"{minutes} min";
            CustomDurationRow.Visibility = Visibility.Visible;
        }
        _suppressPillEvents = wasSuppressed;
    }

    private void CustomDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CustomDurationText == null || CustomDurationPill == null) return; // durante InitializeComponent
        int m = (int)e.NewValue;
        CustomDurationText.Text = $"{m} min";
        if (_suppressPillEvents || CustomDurationPill.IsChecked != true) return;
        CustomDurationPill.Content = Loc.F("main.custom.value", m);
        _app.Settings.DurationMinutes = m;
        _app.SaveSettings();
        UpdateIdleTexts();
    }

    private void SelectAfter(AfterAction a)
    {
        foreach (var rb in AfterPanel.Children.OfType<RadioButton>())
            if ((string)rb.Tag == a.ToString()) rb.IsChecked = true;
        _app.Settings.AfterAction = a;
    }

    private void SetPillsEnabled(bool enabled)
    {
        DurationPanel.IsEnabled = enabled;
        CustomDurationRow.IsEnabled = enabled;
        SmartStopCheck.IsEnabled = enabled;
        // "Al terminar" se puede cambiar en pleno enfriamiento
    }

    private void Duration_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || _suppressPillEvents) return;
        int m;
        if ((string)rb.Tag == "custom")
        {
            m = (int)CustomDurationSlider.Value;
            rb.Content = Loc.F("main.custom.value", m);
            CustomDurationRow.Visibility = Visibility.Visible;
        }
        else
        {
            m = int.Parse((string)rb.Tag);
            CustomDurationPill.Content = Loc.T("main.custom");
            CustomDurationRow.Visibility = Visibility.Collapsed;
        }
        _app.Settings.DurationMinutes = m;
        _app.SaveSettings();
        UpdateIdleTexts();
    }

    private void After_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || _suppressPillEvents) return;
        if (Enum.TryParse<AfterAction>((string)rb.Tag, out var a))
        {
            _app.Settings.AfterAction = a;
            _runAfter = a; // también afecta al enfriamiento en curso
            _app.SaveSettings();
            UpdateIdleTexts();
            if (a != AfterAction.Nothing && !_engine.IsBusy)
                ShowToast("🌙", Loc.F("toast.after.title", PowerActions.Describe(a)),
                    Loc.F("toast.after.text", _app.Settings.PowerCountdownSeconds), TimeSpan.FromSeconds(4));
        }
    }

    private void SmartStopCheck_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.SmartStop = SmartStopCheck.IsChecked == true;
        _app.SaveSettings();
    }

    private void MainButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPowerPending)
        {
            CancelPowerCountdown();
            return;
        }
        if (_engine.Phase == CooldownPhase.Cooling)
        {
            _engine.Cancel();
            return;
        }
        if (_engine.IsBusy) return;
        _ = StartCooldownAsync(_app.Settings.DurationMinutes, _app.Settings.AfterAction);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (ConfirmOverlay.Visibility == Visibility.Visible) { CloseConfirm(false); e.Handled = true; }
        else if (IsPowerPending) { CancelPowerCountdown(); e.Handled = true; }
        else if (_engine.Phase == CooldownPhase.Cooling) { _engine.Cancel(); e.Handled = true; }
    }

    private void SoundButton_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.SoundEnabled = !_app.Settings.SoundEnabled;
        _app.Sound.Enabled = _app.Settings.SoundEnabled;
        SoundButton.Content = _app.Settings.SoundEnabled ? "" : "";
        _app.SaveSettings();
        if (_app.Settings.SoundEnabled) _app.Sound.PlayBoop();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsBusy)
        {
            ShowToast("⏳", Loc.T("toast.wait.title"), Loc.T("toast.wait.text"), TimeSpan.FromSeconds(4));
            return;
        }
        var win = new SettingsWindow(_app) { Owner = this };
        if (win.ShowDialog() != true) return;

        string oldLang = Loc.Current;
        _app.ApplyNewSettings(win.Result!);
        ApplySettingsToUi();
        RefreshBackendStatus();
        RefreshRank();
        SimBadge.Visibility = _app.ForceSimulation || _app.Settings.Backend == BackendKind.Simulation ? Visibility.Visible : Visibility.Collapsed;

        if (win.CheckUpdatesNow) await CheckForUpdatesAsync(manual: true);

        // El idioma se aplica reiniciando la app
        if (Loc.Resolve(_app.Settings.Language) != oldLang)
        {
            Loc.Init(_app.Settings.Language);
            bool restart = await ConfirmAsync(Loc.T("confirm.restart.title"), Loc.T("confirm.restart.text"),
                Loc.T("confirm.restart.yes"), Loc.T("confirm.restart.no"));
            if (restart)
            {
                _reallyClosing = true;
                _app.Restart();
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
