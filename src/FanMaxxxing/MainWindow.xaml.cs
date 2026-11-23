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
    private readonly DispatcherTimer _dogeTimer;
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
    private bool _trayHintShown;

    // Pool de palabras doge (tamaño fijo)
    private readonly TextBlock[] _doge = new TextBlock[6];
    private int _dogeNext;
    private static readonly Brush[] DogeBrushes =
    {
        Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A)), Frozen(Color.FromRgb(0x22, 0xD3, 0xEE)), Frozen(Color.FromRgb(0xFF, 0x2E, 0x88)),
        Frozen(Color.FromRgb(0xA3, 0xFF, 0x12)), Frozen(Color.FromRgb(0xFF, 0x6B, 0x35)), Frozen(Color.FromRgb(0xC0, 0x84, 0xFC)),
    };

    private static readonly Brush IdleButtonBrush = MakeGradient("#22D3EE", "#7C5CFF", "#FF2E88");
    private static readonly Brush CancelButtonBrush = MakeGradient("#FF6B35", "#FF3B3B", "#B0124F");
    private static readonly Brush BusyButtonBrush = MakeGradient("#3A4470", "#2B3560", "#3A4470");

    private static readonly AnimationTimeline DogeFade = MakeDogeFade();
    private static readonly IEasingFunction BackOut = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
    private static readonly IEasingFunction CubicOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += UiTimer_Tick;
        _memeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(7) };
        _memeTimer.Tick += (_, _) => ShowMeme(_memeMood);
        _dogeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.1) };
        _dogeTimer.Tick += (_, _) => SpawnDoge();
        _powerTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _powerTimer.Tick += PowerTimer_Tick;
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background);
        _toastTimer.Tick += (_, _) => HideToast();

        _engine.PhaseChanged += Engine_PhaseChanged;
        _engine.Ticked += Engine_Ticked;

        for (int i = 0; i < _doge.Length; i++)
        {
            var tb = new TextBlock
            {
                FontFamily = new FontFamily("Comic Sans MS"),
                FontWeight = FontWeights.Bold,
                Opacity = 0,
                RenderTransform = new RotateTransform(),
            };
            _doge[i] = tb;
            DogeCanvas.Children.Add(tb);
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

        if (!_app.Settings.FirstRunDone)
        {
            _app.Settings.FirstRunDone = true;
            _app.SaveSettings();
            Dispatcher.BeginInvoke(ShowWelcome, DispatcherPriority.ApplicationIdle);
        }

        if (_pendingLaunch != null)
        {
            var l = _pendingLaunch;
            _pendingLaunch = null;
            Dispatcher.BeginInvoke(() => HandleLaunch(l), DispatcherPriority.ApplicationIdle);
        }
    }

    private void ShowWelcome()
    {
        var kind = BackendFactory.Resolve(_app.Settings, _app.ForceSimulation);
        string text = kind switch
        {
            BackendKind.FanControl => "He encontrado FanControl: usaré tu config y crearé una copia con todo al 100 %. Revisa Ajustes si quieres afinar.",
            BackendKind.Simulation => "Estás en modo simulación: nada de hardware real, solo vibes.",
            _ => "No encuentro FanControl, así que usaré el control directo (experimental). Puedes indicar FanControl en Ajustes.",
        };
        ShowToast("👋", "¡Bienvenido a FanMaxxxing!", text, TimeSpan.FromSeconds(9));
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
                        // la ventana de ajustes tiene fondo propio: lo pintamos debajo
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
        if (_app.Settings.CloseToTray && _app.HasTray)
        {
            HideToTray();
            return;
        }
        _ = RequestExitAsync();
    }

    public void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _app.ShowTrayBalloon("Sigo aquí abajo 👇", IsCooling
                ? "El enfriamiento continúa. Clic derecho en el icono para más opciones."
                : "FanMaxxxing está en la bandeja. Clic derecho para enfriar o salir.");
        }
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Salida "de verdad". Si hay enfriamiento, primero devuelve los ventiladores a la normalidad.</summary>
    public async Task RequestExitAsync()
    {
        if (_engine.IsBusy)
        {
            ShowFromTray();
            var r = MessageBox.Show(this,
                "Hay un enfriamiento en marcha. ¿Lo cancelo, devuelvo los ventiladores a automático y salgo?",
                "FanMaxxxing", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _engine.Cancel();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (_engine.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(100);
        }
        CancelPowerCountdown();
        _reallyClosing = true;
        _app.ExitApp();
    }

    /// <summary>Llamado por App al salir: corta timers y eventos para no dejar nada colgando.</summary>
    public void Teardown()
    {
        _reallyClosing = true;
        _uiTimer.Stop();
        _memeTimer.Stop();
        _dogeTimer.Stop();
        _powerTimer.Stop();
        _toastTimer.Stop();
        if (_renderHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderHooked = false;
        }
        _engine.PhaseChanged -= Engine_PhaseChanged;
        _engine.Ticked -= Engine_Ticked;
    }

    public void EmergencyRestore() => _engine.EmergencyRestore();

    /// <summary>Windows se está apagando: no hay que esconderse en la bandeja ni bloquear el cierre.</summary>
    public void MarkSessionEnding() => _reallyClosing = true;

    public void CancelCooldown() => _engine.Cancel();

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
        }
        else if (!shouldRun && _renderHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderHooked = false;
            _memeTimer.Stop();
            _dogeTimer.Stop();
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

        // Suavizados
        _heat += (_targetHeat - _heat) * Math.Min(1, dt * 0.8);
        _wind += (_targetWind - _wind) * Math.Min(1, dt * 1.5);
        Fan.SetHeat(_heat);
        ApplyBackgroundHeat(_heat);

        Particles.Wind = _wind;
        Particles.Reduced = _app.Settings.ReducedAnimations;
        Particles.Update(dt);

        LogoSpin.Angle = (LogoSpin.Angle + Fan.CurrentSpeed * dt * 0.5) % 360;

        // Temblor de pantalla al arrancar la turbina
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

        // Latido del botón cuando está esperando
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
            RpmLabel.Text = "ventilador más rápido";
        }
        else
        {
            RpmText.Text = "";
            RpmLabel.Text = "";
        }

        // Calor objetivo para colores/partículas
        if (s.MaxTemp is double t) _targetHeat = Math.Clamp((t - 38) / 40, 0, 1);
        else if (!_engine.IsBusy) _targetHeat = DateTime.UtcNow < _doneUntil ? 0.1 : 0.45;

        if (!_engine.IsBusy && !IsPowerPending)
        {
            Fan.SetMood(FanMoodNow(s));
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
        TimerText.Text = $"{min:00}:00";
        string after = _app.Settings.AfterAction switch
        {
            AfterAction.Shutdown => " y luego apago el PC",
            AfterAction.Hibernate => " y luego hiberno el PC",
            AfterAction.Sleep => " y luego lo suspendo",
            _ => "",
        };
        if (DateTime.UtcNow > _doneUntil)
            StatusText.Text = $"Listo para maxxear: {min} min con todo al 100 %{after}.";
        MainButtonSub.Text = _app.Settings.AfterAction == AfterAction.Nothing
            ? $"ventiladores al 100 % · {min} min"
            : $"{min} min al 100 % y {PowerActions.Describe(_app.Settings.AfterAction)}";
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
            _app.SetTrayTooltip($"FanMaxxxing · {st.Title}");
        }
        catch (Exception ex)
        {
            Log.ErrorThrottled("Error comprobando el motor", ex);
        }
    }

    private void RefreshRank()
    {
        var (rank, next) = Memes.RankFor(_app.Settings.Stats.TotalCooldowns);
        RankText.Text = rank.ToUpperInvariant();
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

    private void SpawnDoge()
    {
        if (_app.Settings.ReducedAnimations || !_renderHooked) return;
        double w = DogeCanvas.ActualWidth, h = DogeCanvas.ActualHeight;
        if (w < 100 || h < 100) return;

        var tb = _doge[_dogeNext];
        _dogeNext = (_dogeNext + 1) % _doge.Length;
        tb.Text = Memes.NextDoge();
        tb.FontSize = 22 + _rng.Next(16);
        tb.Foreground = DogeBrushes[_rng.Next(DogeBrushes.Length)];
        // Por la zona del escenario (izquierda), sin tapar el centro del ventilador
        double x = 40 + _rng.NextDouble() * Math.Max(50, w * 0.5 - 160);
        double y = 90 + _rng.NextDouble() * Math.Max(50, h - 260);
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        ((RotateTransform)tb.RenderTransform).Angle = _rng.Next(-18, 18);
        tb.BeginAnimation(OpacityProperty, DogeFade);
    }

    /// <summary>Aparece rápido, se mantiene y se desvanece. Congelada y compartida por todo el pool.</summary>
    private static AnimationTimeline MakeDogeFade()
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

    private static T Freeze<T>(T f) where T : Freezable
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
            ShowToast("⚠", "No puedo arrancar", ex.Message, error: true);
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

        // Fiesta
        _app.Sound.PlayTurbine();
        _shakeLeft = 0.8;
        Flash("MODO TURBINA ACTIVADO");
        Fan.SetMood(FanMood.Excited);
        Fan.TargetSpeed = 1500;
        Fan.Vibration = 1;
        _targetWind = 1;
        Particles.Mode = ParticleMode.Snow;
        Particles.Intensity = 1;
        ShowMeme(MemeMood.Cooling, force: true);
        if (_renderHooked) _dogeTimer.Start();

        var req = new CooldownRequest(_debugDuration ?? TimeSpan.FromMinutes(minutes), s.SmartStop, s.SmartStopCpuTemp, s.SmartStopGpuTemp,
            TimeSpan.FromSeconds(s.SmartStopMinSeconds));

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
                MainButtonText.Text = "DESPEGANDO…";
                MainButtonSub.Text = "hablando con los ventiladores";
                StatusText.Text = "Arrancando turbinas…";
                SetPillsEnabled(false);
                _app.SetTrayTooltip("FanMaxxxing · arrancando turbinas");
                break;
            case CooldownPhase.Cooling:
                MainButton.IsEnabled = true;
                MainButton.Background = CancelButtonBrush;
                MainButtonText.Text = "CANCELAR";
                MainButtonSub.Text = "y volver a automático";
                break;
            case CooldownPhase.Restoring:
                MainButton.IsEnabled = false;
                MainButton.Background = BusyButtonBrush;
                MainButtonText.Text = "ATERRIZANDO…";
                MainButtonSub.Text = "devolviendo ventiladores a automático";
                StatusText.Text = "Devolviendo los ventiladores a automático…";
                Fan.TargetSpeed = 220;
                Fan.Vibration = 0;
                _targetWind = 0;
                _dogeTimer.Stop();
                break;
            case CooldownPhase.Idle:
                MainButton.IsEnabled = true;
                MainButton.Background = IdleButtonBrush;
                MainButtonText.Text = "MAXXEAR";
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
        string status = "Ventiladores al 100 %. " + (temps.Length > 0 ? temps + "." : "");
        if (t.SmartStopPending) status += " Casi frío: si se mantiene, paro antes.";
        else if (_runAfter != AfterAction.Nothing) status += $" Al acabar: {PowerActions.Describe(_runAfter)}.";
        StatusText.Text = status;

        // Mientras enfría, la cara pasa de emocionada a feliz al final
        Fan.SetMood(t.Progress > 0.8 ? FanMood.Happy : FanMood.Excited);

        if (_app.Settings.TurnOffDisplay && !_displayOffDone && t.Elapsed.TotalSeconds > 8)
        {
            _displayOffDone = true;
            PowerActions.TurnOffDisplay(new WindowInteropHelper(this).EnsureHandle());
        }

        _app.SetTrayTooltip($"FanMaxxxing · enfriando, quedan {TimerText.Text}");
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
            ShowToast("⚠", "Ojo: no he podido volver a la config normal",
                r.RestoreError + " Abre FanControl y carga tu config a mano (lo reintentaré al volver a abrir FanMaxxxing).",
                TimeSpan.FromSeconds(15), error: true);
        }

        switch (r.End)
        {
            case CooldownEnd.Error:
                _lastRunFailed = true;
                _app.Sound.PlayBoop();
                Fan.SetMood(FanMood.Neutral);
                ShowMeme(MemeMood.Error, force: true);
                StatusText.Text = "No se pudo enfriar: " + r.Error;
                if (r.RestoreError == null) ShowToast("⚠", "Algo ha fallado", r.Error ?? "Error desconocido", error: true);
                return;

            case CooldownEnd.Cancelled:
                StatusText.Text = "Cancelado. Ventiladores de vuelta en automático. " + temps;
                _doneUntil = DateTime.UtcNow;
                ShowMeme(MemeMoodNow(), force: true);
                if (r.RestoreError == null) ShowToast("✋", "Cancelado", "Los ventiladores vuelven a su curva normal.", TimeSpan.FromSeconds(4));
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
        _app.Sound.PlayChime();
        StatusText.Text = (r.End == CooldownEnd.SmartStop ? "Ya estaba todo frío: he parado antes. " : "¡Hecho! ") + temps;

        var (title, sub) = Memes.Achievement(r);
        if (r.RestoreError == null)
            ShowToast("🏆", "LOGRO DESBLOQUEADO: " + title, sub, TimeSpan.FromSeconds(7));

        if (_runAfter != AfterAction.Nothing)
            StartPowerCountdown(_runAfter);
        else
            _app.ShowTrayBalloonIfHidden(this, "Enfriamiento terminado ❄", temps.Length > 0 ? temps : "Ventiladores de vuelta en automático.");
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
        string verb = action switch
        {
            AfterAction.Shutdown => "se apagará",
            AfterAction.Hibernate => "hibernará",
            _ => "se suspenderá",
        };
        PowerSub.Text = $"El equipo {verb} en";
        PowerNowText.Text = action switch
        {
            AfterAction.Shutdown => "Apagar ya",
            AfterAction.Hibernate => "Hibernar ya",
            _ => "Suspender ya",
        };
        var night = Memes.Next(MemeMood.Night);
        PowerTitle.Text = night.Top;
        PowerHint.Text = night.Bottom.ToLowerInvariant() + " · los ventiladores ya están en automático";
        PowerCount.Text = _powerLeft.ToString();
        PowerOverlay.Visibility = Visibility.Visible;
        Fan.SetMood(FanMood.Sleepy);
        ShowMeme(MemeMood.Night, force: true);

        // Que se vea aunque esté en la bandeja o detrás de otra ventana
        ShowFromTray();
        PowerCancelButton.Focus();
        _powerTimer.Start();
        _app.SetTrayTooltip($"FanMaxxxing · {verb} en {_powerLeft} s");
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
            ShowToast("🎭", $"(Simulación) Aquí el PC se {(_powerAction == AfterAction.Shutdown ? "apagaría" : _powerAction == AfterAction.Hibernate ? "hibernaría" : "suspendería")}",
                "En modo simulación no se toca nada de verdad.", TimeSpan.FromSeconds(6));
            Fan.SetMood(FanMood.Happy);
            ShowMeme(MemeMoodNow(), force: true);
            return;
        }

        var err = PowerActions.Execute(_powerAction, s.ForceCloseApps);
        if (err != null)
        {
            PowerOverlay.Visibility = Visibility.Collapsed;
            ShowToast("⚠", "Windows no me ha dejado", err, error: true);
            Fan.SetMood(FanMood.Neutral);
            return;
        }
        if (_powerAction == AfterAction.Sleep)
        {
            // Al volver de la suspensión, quitamos el overlay
            PowerOverlay.Visibility = Visibility.Collapsed;
            ShowMeme(MemeMoodNow(), force: true);
        }
        else
        {
            PowerCount.Text = "zZz";
            PowerSub.Text = "Apagando… dulces sueños";
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
        ShowToast("😎", "Vale, seguimos despiertos", "El PC ya está fresquito, puedes seguir a lo tuyo.", TimeSpan.FromSeconds(5));
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
        SmartStopText.Text = $"Parar antes si ya está todo frío (CPU ≤ {_app.Settings.SmartStopCpuTemp:0}° y GPU ≤ {_app.Settings.SmartStopGpuTemp:0}°)";
        SoundButton.Content = _app.Settings.SoundEnabled ? "" : "";
        _suppressPillEvents = false;
        UpdateIdleTexts();
    }

    private void SelectDuration(int minutes)
    {
        RadioButton? best = null;
        int bestDiff = int.MaxValue;
        foreach (var rb in DurationPanel.Children.OfType<RadioButton>())
        {
            int v = int.Parse((string)rb.Tag);
            int diff = Math.Abs(v - minutes);
            if (diff < bestDiff) { best = rb; bestDiff = diff; }
        }
        if (best != null)
        {
            // Si la duración no es una de las pastillas, la mostramos en la más cercana pero respetamos el valor
            if (bestDiff != 0) best.Content = $"{minutes} min";
            best.IsChecked = true;
            _app.Settings.DurationMinutes = minutes;
        }
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
        SmartStopCheck.IsEnabled = enabled;
        // "Al terminar" se puede cambiar en pleno enfriamiento
    }

    private void Duration_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || _suppressPillEvents) return;
        var text = ((string)rb.Content).Replace(" min", "");
        if (int.TryParse(text, out var m))
        {
            _app.Settings.DurationMinutes = m;
            _app.SaveSettings();
            UpdateIdleTexts();
        }
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
                ShowToast("🌙", $"Al terminar: {PowerActions.Describe(a)}",
                    $"Habrá una cuenta atrás de {_app.Settings.PowerCountdownSeconds} s para arrepentirte.", TimeSpan.FromSeconds(4));
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

    /// <summary>Para el menú de la bandeja.</summary>
    public void StartFromTray(AfterAction? after)
    {
        if (_engine.IsBusy) return;
        var a = after ?? _app.Settings.AfterAction;
        _suppressPillEvents = true;
        SelectAfter(a);
        _suppressPillEvents = false;
        _ = StartCooldownAsync(_app.Settings.DurationMinutes, a);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (IsPowerPending) { CancelPowerCountdown(); e.Handled = true; }
            else if (_engine.Phase == CooldownPhase.Cooling) { _engine.Cancel(); e.Handled = true; }
        }
    }

    private void SoundButton_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.SoundEnabled = !_app.Settings.SoundEnabled;
        _app.Sound.Enabled = _app.Settings.SoundEnabled;
        SoundButton.Content = _app.Settings.SoundEnabled ? "" : "";
        _app.SaveSettings();
        if (_app.Settings.SoundEnabled) _app.Sound.PlayBoop();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsBusy)
        {
            ShowToast("⏳", "Espera a que termine", "Los ajustes se pueden cambiar cuando no se está enfriando.", TimeSpan.FromSeconds(4));
            return;
        }
        var win = new SettingsWindow(_app) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _app.ApplyNewSettings(win.Result!);
            ApplySettingsToUi();
            RefreshBackendStatus();
            RefreshRank();
            SimBadge.Visibility = _app.ForceSimulation || _app.Settings.Backend == BackendKind.Simulation ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
