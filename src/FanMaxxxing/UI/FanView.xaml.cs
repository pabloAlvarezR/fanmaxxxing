using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanMaxxxing.UI;

/// <summary>
/// Estado visual del ventilador. Ya no hay caras: el estado se expresa con el anillo RGB.
/// Neutral = fijo · Hot = late en rojo · Excited = el color recorre el anillo · Happy = fijo y brillante · Sleepy = respira tenue.
/// </summary>
public enum FanMood { Neutral, Happy, Hot, Excited, Sleepy }

/// <summary>
/// Ventilador de PC de 120 mm dibujado con vectores: marco, tornillos, brazos del motor, 9 aspas curvadas,
/// brillo especular, anillo RGB y display en el buje. Se anima desde fuera llamando a <see cref="Advance"/>
/// en cada frame (un único bucle de render para toda la ventana).
/// </summary>
public partial class FanView : UserControl
{
    private const int BladeCount = 9;
    private const double Cx = 200, Cy = 200;

    private readonly GradientStop _bladeA, _bladeB;
    private readonly Path[] _blades = new Path[BladeCount];

    private double _angle;
    private double _speed = 40;
    private double _time;
    private double _progressShown = -1;
    private FanMood _mood = (FanMood)(-1);
    private double _heat = -1;
    private string _hubMain = "", _hubSub = "";

    /// <summary>Velocidad objetivo en grados/segundo (la real acelera y frena con inercia).</summary>
    public double TargetSpeed { get; set; } = 40;

    /// <summary>0..1: cuánto vibra el ventilador (a tope = 1).</summary>
    public double Vibration { get; set; }

    public double CurrentSpeed => _speed;

    public FanView()
    {
        InitializeComponent();

        // Aspas de plástico ahumado: claras en el borde de ataque, oscuras en la raíz
        _bladeA = new GradientStop(Color.FromRgb(0x4B, 0x54, 0x68), 0);
        _bladeB = new GradientStop(Color.FromRgb(0x14, 0x17, 0x20), 1);
        var bladeBrush = new LinearGradientBrush(new GradientStopCollection { _bladeA, _bladeB }, new Point(0, 0), new Point(1, 1));
        var edge = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

        var blade = BuildBladeGeometry();
        for (int i = 0; i < BladeCount; i++)
        {
            var p = new Path
            {
                Data = blade,
                Fill = bladeBrush,
                Stroke = edge,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = new RotateTransform(360.0 / BladeCount * i, Cx, Cy),
            };
            _blades[i] = p;
            BladeCanvas.Children.Add(p);
        }

        Struts.Data = BuildStruts();

        SetMood(FanMood.Neutral);
        SetHeat(0.5);
        SetProgress(0);
        SetHub("AUTO", "");
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Point Polar(double r, double deg)
    {
        double rad = (deg - 90) * Math.PI / 180;
        return new Point(Cx + r * Math.Cos(rad), Cy + r * Math.Sin(rad));
    }

    /// <summary>Aspa fina y muy curvada (estilo ventilador de alta presión estática).</summary>
    private static Geometry BuildBladeGeometry()
    {
        const double r0 = 58, r1 = 170;
        const int steps = 28;
        var lead = new List<Point>();
        var trail = new List<Point>();
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            double r = r0 + (r1 - r0) * t;
            double leadAngle = 38 * Math.Pow(t, 1.5);
            double trailAngle = 20 + 46 * Math.Pow(t, 1.25); // 20° en la raíz → 66° en la punta (≈28° de ancho)
            lead.Add(Polar(r, leadAngle));
            trail.Add(Polar(r, trailAngle));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(lead[0], true, true);
            ctx.PolyLineTo(lead.Skip(1).ToList(), true, true);
            ctx.ArcTo(trail[^1], new Size(r1, r1), 0, false, SweepDirection.Clockwise, true, true);
            trail.Reverse();
            ctx.PolyLineTo(trail.Skip(1).ToList(), true, true);
            ctx.ArcTo(lead[0], new Size(r0, r0), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        geo.Freeze();
        return geo;
    }

    private static Geometry BuildStruts()
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var a in new[] { 45.0, 135.0, 225.0, 315.0 })
            {
                ctx.BeginFigure(Polar(56, a), false, false);
                ctx.LineTo(Polar(178, a + 6), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>Avanza la animación. <paramref name="dt"/> en segundos.</summary>
    public void Advance(double dt)
    {
        _time += dt;

        // Inercia: acelera rápido, frena más despacio (como un ventilador real)
        double tau = TargetSpeed > _speed ? 1.1 : 2.6;
        _speed += (TargetSpeed - _speed) * (1 - Math.Exp(-dt / tau));
        _angle = (_angle + _speed * dt) % 360;
        BladeRotate.Angle = _angle;

        // Desenfoque según velocidad
        double blur = Math.Clamp((_speed - 250) / 900, 0, 1);
        BlurDisc.Opacity = blur;
        double bladeOpacity = 1 - blur * 0.6;
        for (int i = 0; i < _blades.Length; i++) _blades[i].Opacity = bladeOpacity;

        // Vibración a máxima velocidad (muy sutil)
        double v = Vibration;
        if (v > 0.01)
        {
            RootShake.X = Math.Sin(_time * 73) * 1.1 * v;
            RootShake.Y = Math.Cos(_time * 61) * 1.1 * v;
        }
        else if (RootShake.X != 0 || RootShake.Y != 0)
        {
            RootShake.X = RootShake.Y = 0;
        }

        // Anillo RGB según el estado
        double intensity;
        switch (_mood)
        {
            case FanMood.Excited:
                RingSpin.Angle = RingGlowSpin.Angle = (RingSpin.Angle + 150 * dt) % 360;
                intensity = 1;
                break;
            case FanMood.Hot:
                intensity = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_time * 2 * Math.PI * 1.2));
                break;
            case FanMood.Sleepy:
                intensity = 0.12 + 0.3 * (0.5 + 0.5 * Math.Sin(_time * 2 * Math.PI * 0.22));
                break;
            case FanMood.Happy:
                intensity = 0.95;
                break;
            default:
                intensity = 0.7;
                break;
        }
        RingGroup.Opacity = intensity;
    }

    public void SetMood(FanMood mood)
    {
        if (mood == _mood) return;
        _mood = mood;
        if (mood != FanMood.Excited) RingSpin.Angle = RingGlowSpin.Angle = 0;
    }

    /// <summary>Texto del display del buje (p. ej. "62°" / "AUTO", "MAX" / "2950 RPM").</summary>
    public void SetHub(string main, string sub)
    {
        if (main != _hubMain) { _hubMain = main; HubMain.Text = main; }
        if (sub != _hubSub) { _hubSub = sub; HubSub.Text = sub; }
    }

    /// <summary>0 = frío (cian/violeta), 1 = caliente (naranja/ámbar).</summary>
    public void SetHeat(double heat)
    {
        heat = Math.Clamp(heat, 0, 1);
        if (Math.Abs(heat - _heat) < 0.004) return;
        _heat = heat;

        var a = Lerp(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xFF, 0x4D, 0x1F), heat);
        var b = Lerp(Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0xFF, 0xC8, 0x3D), heat);
        RingA.Color = RingGlowA.Color = a;
        RingB.Color = RingGlowB.Color = b;
        HubRingBrush.Color = a;
        GlowInner.Color = Color.FromArgb(0x30, a.R, a.G, a.B);
        GlowOuter.Color = Color.FromArgb(0x00, a.R, a.G, a.B);

        // Las aspas reflejan un poco la luz del anillo
        _bladeA.Color = Lerp(Color.FromRgb(0x4B, 0x54, 0x68), a, 0.16);
        var blurTint = Lerp(Color.FromRgb(0x30, 0x38, 0x48), a, 0.12);
        BlurInner.Color = Color.FromArgb(0x7A, blurTint.R, blurTint.G, blurTint.B);
        BlurMid.Color = Color.FromArgb(0x66, blurTint.R, blurTint.G, blurTint.B);
    }

    /// <summary>Arco de progreso 0..1 alrededor de la abertura.</summary>
    public void SetProgress(double p)
    {
        p = Math.Clamp(p, 0, 1);
        if (Math.Abs(p - _progressShown) < 0.0015) return;
        _progressShown = p;
        const double r = 187;
        if (p <= 0.0005)
        {
            ProgressArc.Data = null;
            return;
        }
        if (p >= 0.9995)
        {
            ProgressArc.Data = new EllipseGeometry(new Point(Cx, Cy), r, r);
            return;
        }
        double ang = p * 2 * Math.PI;
        var start = new Point(Cx, Cy - r);
        var end = new Point(Cx + r * Math.Sin(ang), Cy - r * Math.Cos(ang));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        ProgressArc.Data = geo;
    }

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
