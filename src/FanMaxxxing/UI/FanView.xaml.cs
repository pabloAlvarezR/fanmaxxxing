using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanMaxxxing.UI;

public enum FanMood { Neutral, Happy, Hot, Excited, Sleepy }

/// <summary>
/// El protagonista: un ventilador con cara. Se anima desde fuera llamando a <see cref="Advance"/>
/// en cada frame (un único bucle de render para toda la ventana, nada de timers sueltos).
/// </summary>
public partial class FanView : UserControl
{
    private const int BladeCount = 7;
    private const double Cx = 200, Cy = 200;

    private static readonly Brush FaceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x3B)));
    private static readonly Brush TongueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x12, 0x3A)));

    private static readonly Geometry EyeNeutralL = E(46, 56, 7, 9);
    private static readonly Geometry EyeNeutralR = E(82, 56, 7, 9);
    private static readonly Geometry EyeBigL = E(46, 55, 9, 12);
    private static readonly Geometry EyeBigR = E(82, 55, 9, 12);
    private static readonly Geometry EyeHappyL = G("M 37,60 Q 46,46 55,60");
    private static readonly Geometry EyeHappyR = G("M 73,60 Q 82,46 91,60");
    private static readonly Geometry EyeHotL = G("M 38,49 L 52,56 L 38,63");
    private static readonly Geometry EyeHotR = G("M 90,49 L 76,56 L 90,63");
    private static readonly Geometry EyeSleepL = G("M 37,56 Q 46,65 55,56");
    private static readonly Geometry EyeSleepR = G("M 73,56 Q 82,65 91,56");

    private static readonly Geometry MouthNeutral = G("M 54,84 L 74,84");
    private static readonly Geometry MouthHappy = G("M 47,79 Q 64,96 81,79");
    private static readonly Geometry MouthHot = G("M 45,86 Q 51,79 57,86 Q 64,93 71,86 Q 77,79 83,86");
    private static readonly Geometry MouthExcited = G("M 45,77 Q 64,108 83,77 Z");
    private static readonly Geometry MouthSleepy = E(64, 86, 5, 6);

    private readonly LinearGradientBrush _bladeBrush;
    private readonly GradientStop _bladeA, _bladeB;
    private readonly Path[] _blades = new Path[BladeCount];

    private double _angle;
    private double _speed = 40;
    private double _time;
    private double _progressShown = -1;
    private FanMood _mood = (FanMood)(-1);
    private double _heat = -1;

    /// <summary>Velocidad objetivo en grados/segundo (la real acelera y frena con inercia).</summary>
    public double TargetSpeed { get; set; } = 40;

    /// <summary>0..1: cuánto "vibra" el ventilador (a tope = 1).</summary>
    public double Vibration { get; set; }

    public double CurrentSpeed => _speed;

    public FanView()
    {
        InitializeComponent();

        _bladeA = new GradientStop(Color.FromRgb(0x7D, 0xF9, 0xFF), 0);
        _bladeB = new GradientStop(Color.FromRgb(0x25, 0x63, 0xEB), 1);
        _bladeBrush = new LinearGradientBrush(new GradientStopCollection { _bladeA, _bladeB }, new Point(0.5, 0), new Point(0.5, 1));

        var blade = BuildBladeGeometry();
        var edge = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        for (int i = 0; i < BladeCount; i++)
        {
            var p = new Path
            {
                Data = blade,
                Fill = _bladeBrush,
                Stroke = edge,
                StrokeThickness = 1.5,
                RenderTransform = new RotateTransform(360.0 / BladeCount * i, Cx, Cy),
            };
            _blades[i] = p;
            BladeCanvas.Children.Add(p);
        }

        SetMood(FanMood.Neutral);
        SetHeat(0.5);
        SetProgress(0);
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Geometry E(double cx, double cy, double rx, double ry)
    {
        var g = new EllipseGeometry(new Point(cx, cy), rx, ry);
        g.Freeze();
        return g;
    }

    private static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// <summary>Aspa curvada generada en coordenadas polares.</summary>
    private static Geometry BuildBladeGeometry()
    {
        // Aspa de ventilador de PC: estrecha en el buje, ancha en la punta, con barrido curvo
        // y el borde exterior siguiendo el círculo de la carcasa.
        const double r0 = 60, r1 = 158, sweep = 26; // grados de barrido
        const int steps = 24;
        var lead = new List<Point>();
        var trail = new List<Point>();
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            double r = r0 + (r1 - r0) * t;
            double a = sweep * Math.Pow(t, 1.6);
            double width = 20 + 18 * Math.Pow(t, 0.8); // grados: 20° en el buje → 38° en la punta
            lead.Add(Polar(r, a));
            trail.Add(Polar(r, a + width));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(lead[0], true, true);
            ctx.PolyLineTo(lead.Skip(1).ToList(), true, true);
            // borde exterior: arco del propio círculo
            ctx.ArcTo(trail[^1], new Size(r1, r1), 0, false, SweepDirection.Clockwise, true, true);
            trail.Reverse();
            ctx.PolyLineTo(trail.Skip(1).ToList(), true, true);
            // de vuelta por el buje
            ctx.ArcTo(lead[0], new Size(r0, r0), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        geo.Freeze();
        return geo;

        static Point Polar(double r, double deg)
        {
            double rad = (deg - 90) * Math.PI / 180;
            return new Point(Cx + r * Math.Cos(rad), Cy + r * Math.Sin(rad));
        }
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
        BlurDisc.Opacity = blur * 0.9;
        double bladeOpacity = 1 - blur * 0.55;
        for (int i = 0; i < _blades.Length; i++) _blades[i].Opacity = bladeOpacity;

        // Respiración / vibración
        double breathe = 1 + 0.012 * Math.Sin(_time * 2 * Math.PI * 0.35);
        RootScale.ScaleX = RootScale.ScaleY = breathe;
        double v = Vibration;
        if (v > 0.01)
        {
            RootShake.X = Math.Sin(_time * 73) * 1.6 * v;
            RootShake.Y = Math.Cos(_time * 61) * 1.6 * v;
        }
        else if (RootShake.X != 0 || RootShake.Y != 0)
        {
            RootShake.X = RootShake.Y = 0;
        }

        // Gota de sudor (cara de calor)
        if (_mood == FanMood.Hot)
        {
            double c = (_time % 1.4) / 1.4;
            SweatPos.Y = 18 + c * 26;
            Sweat.Opacity = c < 0.15 ? c / 0.15 : 1 - (c - 0.15) / 0.85;
        }

        // Zzz (dormido)
        if (_mood == FanMood.Sleepy)
        {
            double c = (_time % 2.4) / 2.4;
            ZzzPos.Y = -80 - c * 50;
            ZzzPos.X = 90 + Math.Sin(c * Math.PI * 2) * 8;
            Zzz.Opacity = Math.Sin(c * Math.PI);
        }

        // Parpadeo ocasional en caras con ojos abiertos
        if (_mood is FanMood.Neutral or FanMood.Excited)
        {
            double blink = _time % 4.3;
            double sy = blink < 0.12 ? 0.15 : 1;
            if (EyeL.RenderTransform is ScaleTransform st) st.ScaleY = sy;
            if (EyeR.RenderTransform is ScaleTransform st2) st2.ScaleY = sy;
            ShineL.Opacity = ShineR.Opacity = sy < 1 ? 0 : 1;
        }
    }

    public void SetMood(FanMood mood)
    {
        if (mood == _mood) return;
        _mood = mood;

        Sweat.Opacity = 0;
        Zzz.Opacity = 0;
        BlushL.Opacity = BlushR.Opacity = 0;
        ShineL.Opacity = ShineR.Opacity = 0;
        EyeL.RenderTransform = Transform.Identity;
        EyeR.RenderTransform = Transform.Identity;

        switch (mood)
        {
            case FanMood.Happy:
                Eyes(EyeHappyL, EyeHappyR, filled: false);
                MouthShape(MouthHappy, filled: false);
                BlushL.Opacity = BlushR.Opacity = 0.8;
                break;
            case FanMood.Hot:
                Eyes(EyeHotL, EyeHotR, filled: false);
                MouthShape(MouthHot, filled: false);
                break;
            case FanMood.Excited:
                Eyes(EyeBigL, EyeBigR, filled: true);
                MouthShape(MouthExcited, filled: true);
                ShineL.Opacity = ShineR.Opacity = 1;
                EyeL.RenderTransform = new ScaleTransform(1, 1, 46, 56);
                EyeR.RenderTransform = new ScaleTransform(1, 1, 82, 56);
                BlushL.Opacity = BlushR.Opacity = 0.6;
                break;
            case FanMood.Sleepy:
                Eyes(EyeSleepL, EyeSleepR, filled: false);
                MouthShape(MouthSleepy, filled: false);
                break;
            default:
                Eyes(EyeNeutralL, EyeNeutralR, filled: true);
                MouthShape(MouthNeutral, filled: false);
                ShineL.Opacity = ShineR.Opacity = 1;
                EyeL.RenderTransform = new ScaleTransform(1, 1, 46, 56);
                EyeR.RenderTransform = new ScaleTransform(1, 1, 82, 56);
                break;
        }
    }

    private void Eyes(Geometry l, Geometry r, bool filled)
    {
        EyeL.Data = l;
        EyeR.Data = r;
        EyeL.Fill = EyeR.Fill = filled ? FaceBrush : null;
        EyeL.Stroke = EyeR.Stroke = filled ? null : FaceBrush;
    }

    private void MouthShape(Geometry g, bool filled)
    {
        Mouth.Data = g;
        Mouth.Fill = filled ? TongueBrush : null;
        Mouth.Stroke = FaceBrush;
    }

    /// <summary>0 = frío (azul), 1 = caliente (naranja/rojo).</summary>
    public void SetHeat(double heat)
    {
        heat = Math.Clamp(heat, 0, 1);
        if (Math.Abs(heat - _heat) < 0.004) return;
        _heat = heat;

        _bladeA.Color = Lerp(Color.FromRgb(0x7D, 0xF9, 0xFF), Color.FromRgb(0xFF, 0xC8, 0x3D), heat);
        _bladeB.Color = Lerp(Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0xFF, 0x3B, 0x3B), heat);
        var glow = Lerp(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xFF, 0x5A, 0x1F), heat);
        GlowInner.Color = Color.FromArgb(0x55, glow.R, glow.G, glow.B);
        GlowOuter.Color = Color.FromArgb(0x00, glow.R, glow.G, glow.B);
        var blur = _bladeB.Color;
        BlurInner.Color = Color.FromArgb(0xAA, blur.R, blur.G, blur.B);
        BlurMid.Color = Color.FromArgb(0x66, blur.R, blur.G, blur.B);
        HubDark.Color = Lerp(Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0xFF, 0x9F, 0x1C), heat);
        HubLight.Color = Lerp(Color.FromRgb(0xE0, 0xF7, 0xFF), Color.FromRgb(0xFF, 0xF3, 0xB0), heat);
    }

    /// <summary>Anillo de progreso 0..1.</summary>
    public void SetProgress(double p)
    {
        p = Math.Clamp(p, 0, 1);
        if (Math.Abs(p - _progressShown) < 0.0015) return;
        _progressShown = p;
        const double r = 189;
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
