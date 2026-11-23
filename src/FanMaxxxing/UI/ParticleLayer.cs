using System.Windows;
using System.Windows.Media;

namespace FanMaxxxing.UI;

public enum ParticleMode { None, Embers, Snow }

/// <summary>
/// Sistema de partículas con un array de tamaño fijo (cero crecimiento de memoria).
/// Dibuja con DrawingContext usando pinceles congelados precreados; no crea objetos WPF por frame.
/// </summary>
public sealed class ParticleLayer : FrameworkElement
{
    private enum Kind : byte { Ember, Snow, Wind, Confetti }

    private struct P
    {
        public bool Alive;
        public Kind Kind;
        public double X, Y, Vx, Vy, Life, MaxLife, Size, Rot, VRot, Phase;
        public int Color;
    }

    private const int Capacity = 260;
    private readonly P[] _p = new P[Capacity];
    private readonly Random _rng = new();
    private double _emberAcc, _snowAcc, _windAcc;

    private static readonly SolidColorBrush[] EmberBrushes =
    {
        F(Color.FromRgb(0xFF, 0xD6, 0x0A)), F(Color.FromRgb(0xFF, 0x9F, 0x1C)),
        F(Color.FromRgb(0xFF, 0x6B, 0x35)), F(Color.FromRgb(0xFF, 0x3B, 0x3B)),
    };

    private static readonly Pen[] SnowPens =
    {
        P2(Color.FromRgb(0xE0, 0xF7, 0xFF), 1.6), P2(Color.FromRgb(0x7D, 0xF9, 0xFF), 1.6), P2(Color.FromRgb(0xC7, 0xD2, 0xFE), 1.4),
    };

    private static readonly Pen WindPen = P2(Color.FromArgb(0x70, 0xBF, 0xF4, 0xFF), 2);

    private static readonly Pen[] ConfettiPens =
    {
        P2(Color.FromRgb(0x22, 0xD3, 0xEE), 5), P2(Color.FromRgb(0xFF, 0x2E, 0x88), 5), P2(Color.FromRgb(0xFF, 0xD6, 0x0A), 5),
        P2(Color.FromRgb(0xA3, 0xFF, 0x12), 5), P2(Color.FromRgb(0x7C, 0x5C, 0xFF), 5), P2(Color.FromRgb(0xFF, 0x6B, 0x35), 5),
    };

    private static SolidColorBrush F(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen P2(Color c, double w)
    {
        var p = new Pen(F(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    public ParticleLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public ParticleMode Mode { get; set; } = ParticleMode.Embers;

    /// <summary>0..1 cantidad de partículas ambientales.</summary>
    public double Intensity { get; set; } = 0.5;

    /// <summary>0..1 rachas de viento (ventilador a tope).</summary>
    public double Wind { get; set; }

    public bool Reduced { get; set; }

    public bool HasLiveParticles { get; private set; }

    public void Confetti(int count)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        for (int i = 0; i < count; i++)
        {
            Spawn(new P
            {
                Kind = Kind.Confetti,
                X = w * (0.2 + _rng.NextDouble() * 0.6),
                Y = h * 0.45 + _rng.NextDouble() * 40,
                Vx = (_rng.NextDouble() - 0.5) * 900,
                Vy = -350 - _rng.NextDouble() * 650,
                MaxLife = 2.4 + _rng.NextDouble() * 1.4,
                Size = 6 + _rng.NextDouble() * 6,
                Rot = _rng.NextDouble() * Math.PI * 2,
                VRot = (_rng.NextDouble() - 0.5) * 18,
                Color = _rng.Next(ConfettiPens.Length),
            });
        }
    }

    private void Spawn(P p)
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (_p[i].Alive) continue;
            p.Alive = true;
            p.Life = 0;
            _p[i] = p;
            return;
        }
        // lleno: se ignora (límite duro de memoria)
    }

    public void Update(double dt)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double factor = Reduced ? 0.35 : 1;

        // Generación ambiental
        if (Mode == ParticleMode.Embers)
        {
            _emberAcc += dt * 26 * Intensity * factor;
            while (_emberAcc >= 1)
            {
                _emberAcc--;
                Spawn(new P
                {
                    Kind = Kind.Ember,
                    X = _rng.NextDouble() * w,
                    Y = h + 10,
                    Vx = (_rng.NextDouble() - 0.5) * 30,
                    Vy = -40 - _rng.NextDouble() * 70,
                    MaxLife = 3 + _rng.NextDouble() * 4,
                    Size = 1.5 + _rng.NextDouble() * 3,
                    Phase = _rng.NextDouble() * 10,
                    Color = _rng.Next(EmberBrushes.Length),
                });
            }
        }
        else if (Mode == ParticleMode.Snow)
        {
            _snowAcc += dt * 34 * Intensity * factor;
            while (_snowAcc >= 1)
            {
                _snowAcc--;
                Spawn(new P
                {
                    Kind = Kind.Snow,
                    X = _rng.NextDouble() * (w + 200) - 100,
                    Y = -12,
                    Vx = 20 + Wind * 160 + (_rng.NextDouble() - 0.5) * 30,
                    Vy = 45 + _rng.NextDouble() * 70,
                    MaxLife = 14,
                    Size = 3 + _rng.NextDouble() * 5,
                    Rot = _rng.NextDouble() * Math.PI,
                    VRot = (_rng.NextDouble() - 0.5) * 2,
                    Phase = _rng.NextDouble() * 10,
                    Color = _rng.Next(SnowPens.Length),
                });
            }
        }

        if (Wind > 0.05)
        {
            _windAcc += dt * 14 * Wind * factor;
            while (_windAcc >= 1)
            {
                _windAcc--;
                Spawn(new P
                {
                    Kind = Kind.Wind,
                    X = -150,
                    Y = _rng.NextDouble() * h,
                    Vx = 900 + _rng.NextDouble() * 700,
                    Vy = 0,
                    MaxLife = 3,
                    Size = 50 + _rng.NextDouble() * 110,
                    Phase = _rng.NextDouble() * 10,
                });
            }
        }

        bool any = false;
        for (int i = 0; i < Capacity; i++)
        {
            ref var p = ref _p[i];
            if (!p.Alive) continue;
            p.Life += dt;
            switch (p.Kind)
            {
                case Kind.Ember:
                    p.X += (p.Vx + Math.Sin((p.Life + p.Phase) * 2.2) * 18) * dt;
                    p.Y += p.Vy * dt;
                    break;
                case Kind.Snow:
                    p.Vx += ((20 + Wind * 160) - p.Vx) * Math.Min(1, dt * 0.8);
                    p.X += (p.Vx + Math.Sin((p.Life + p.Phase) * 1.4) * 22) * dt;
                    p.Y += p.Vy * dt;
                    p.Rot += p.VRot * dt;
                    break;
                case Kind.Wind:
                    p.X += p.Vx * dt;
                    p.Y += Math.Sin((p.Life + p.Phase) * 3) * 12 * dt;
                    break;
                case Kind.Confetti:
                    p.Vy += 900 * dt;
                    p.Vx *= Math.Pow(0.35, dt);
                    p.X += p.Vx * dt;
                    p.Y += p.Vy * dt;
                    p.Rot += p.VRot * dt;
                    break;
            }
            if (p.Life >= p.MaxLife || p.Y > h + 40 || p.Y < -60 || p.X > w + 200)
            {
                p.Alive = false;
                continue;
            }
            any = true;
        }
        // Solo repintamos si hay (o había) algo que dibujar: con la capa vacía no gastamos nada.
        if (any || HasLiveParticles) InvalidateVisual();
        HasLiveParticles = any;
    }

    public void Clear()
    {
        for (int i = 0; i < Capacity; i++) _p[i].Alive = false;
        HasLiveParticles = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        for (int i = 0; i < Capacity; i++)
        {
            ref var p = ref _p[i];
            if (!p.Alive) continue;
            double t = p.Life / p.MaxLife;
            switch (p.Kind)
            {
                case Kind.Ember:
                {
                    double a = Math.Sin(t * Math.PI) * (0.55 + 0.45 * Math.Sin((p.Life + p.Phase) * 9));
                    dc.PushOpacity(Math.Clamp(a, 0, 1));
                    dc.DrawEllipse(EmberBrushes[p.Color], null, new Point(p.X, p.Y), p.Size, p.Size);
                    dc.Pop();
                    break;
                }
                case Kind.Snow:
                {
                    double a = Math.Min(1, p.Life * 2) * 0.85;
                    dc.PushOpacity(a);
                    var pen = SnowPens[p.Color];
                    for (int k = 0; k < 3; k++)
                    {
                        double ang = p.Rot + k * Math.PI / 3;
                        double dx = Math.Cos(ang) * p.Size, dy = Math.Sin(ang) * p.Size;
                        dc.DrawLine(pen, new Point(p.X - dx, p.Y - dy), new Point(p.X + dx, p.Y + dy));
                    }
                    dc.Pop();
                    break;
                }
                case Kind.Wind:
                {
                    dc.PushOpacity(Math.Sin(Math.Min(1, t * 1.5) * Math.PI) * 0.8);
                    dc.DrawLine(WindPen, new Point(p.X, p.Y), new Point(p.X + p.Size, p.Y));
                    dc.Pop();
                    break;
                }
                case Kind.Confetti:
                {
                    double a = t > 0.75 ? (1 - t) / 0.25 : 1;
                    dc.PushOpacity(a);
                    double len = p.Size * (0.4 + 0.6 * Math.Abs(Math.Cos(p.Rot * 1.7))); // "giro" 3D falso
                    double dx = Math.Cos(p.Rot) * len / 2, dy = Math.Sin(p.Rot) * len / 2;
                    dc.DrawLine(ConfettiPens[p.Color], new Point(p.X - dx, p.Y - dy), new Point(p.X + dx, p.Y + dy));
                    dc.Pop();
                    break;
                }
            }
        }
    }
}
