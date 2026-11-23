using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanMaxxxing.UI;

/// <summary>Tarjeta de temperatura: valor grande con color según calor, delta y mini-gráfica.</summary>
public sealed class TempCard : Border
{
    private const int HistoryLength = 120; // 2 minutos a 1 Hz
    private readonly double[] _history = new double[HistoryLength];
    private int _count, _head;

    private readonly TextBlock _title = new();
    private readonly TextBlock _value = new();
    private readonly TextBlock _unit = new();
    private readonly TextBlock _delta = new();
    private readonly TextBlock _sub = new();
    private readonly Path _spark = new();
    private readonly SolidColorBrush _valueBrush = new(Colors.White);
    private readonly SolidColorBrush _sparkBrush = new(Colors.White);

    public TempCard()
    {
        Background = (Brush)Application.Current.Resources["Card"];
        BorderBrush = (Brush)Application.Current.Resources["CardBorder"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(16, 12, 16, 10);

        var num = (FontFamily)Application.Current.Resources["NumFont"];
        var dim = (Brush)Application.Current.Resources["TextDim"];

        _title.FontFamily = num;
        _title.FontSize = 13;
        _title.Foreground = dim;

        _value.FontFamily = num;
        _value.FontSize = 46;
        _value.FontWeight = FontWeights.SemiBold;
        _value.Foreground = _valueBrush;
        _value.Text = "--";

        _unit.FontFamily = num;
        _unit.FontSize = 20;
        _unit.Foreground = _valueBrush;
        _unit.Text = " °C";
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(0, 0, 0, 9);

        _delta.FontFamily = num;
        _delta.FontSize = 15;
        _delta.Foreground = (Brush)Application.Current.Resources["Cyan"];
        _delta.VerticalAlignment = VerticalAlignment.Bottom;
        _delta.HorizontalAlignment = HorizontalAlignment.Right;
        _delta.Margin = new Thickness(0, 0, 0, 10);

        _sub.FontSize = 11;
        _sub.Foreground = (Brush)Application.Current.Resources["TextFaint"];
        _sub.TextTrimming = TextTrimming.CharacterEllipsis;

        _spark.Stroke = _sparkBrush;
        _spark.StrokeThickness = 2;
        _spark.StrokeLineJoin = PenLineJoin.Round;
        _spark.Height = 30;
        _spark.Stretch = Stretch.None;
        _spark.Margin = new Thickness(0, 4, 0, 2);
        _spark.Opacity = 0.85;

        var valueRow = new Grid();
        var vp = new StackPanel { Orientation = Orientation.Horizontal };
        vp.Children.Add(_value);
        vp.Children.Add(_unit);
        valueRow.Children.Add(vp);
        valueRow.Children.Add(_delta);

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(valueRow);
        stack.Children.Add(_spark);
        stack.Children.Add(_sub);
        Child = stack;

        SizeChanged += (_, _) => RedrawSpark();
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public void SetSubtitle(string? text) => _sub.Text = text ?? "";

    public void Push(double? value)
    {
        if (value is double v)
        {
            _history[_head] = v;
            _head = (_head + 1) % HistoryLength;
            if (_count < HistoryLength) _count++;
            _value.Text = v.ToString("0");
            var c = ColorFor(v);
            _valueBrush.Color = c;
            _sparkBrush.Color = c;
            ToolTip = null;
        }
        else
        {
            _value.Text = "--";
            _valueBrush.Color = Color.FromRgb(0x8D, 0x97, 0xC0);
            ToolTip = "Sin lectura. Las temperaturas de CPU necesitan permisos de administrador y el driver PawnIO (lo instala FanControl).";
        }
        RedrawSpark();
    }

    public void SetDelta(double? delta)
    {
        if (delta is double d && Math.Abs(d) >= 0.5)
        {
            _delta.Text = d < 0 ? $"▼ {Math.Abs(d):0}°" : $"▲ {d:0}°";
            _delta.Foreground = d < 0
                ? (Brush)Application.Current.Resources["Cyan"]
                : (Brush)Application.Current.Resources["Orange"];
        }
        else
        {
            _delta.Text = "";
        }
    }

    public static Color ColorFor(double t)
    {
        // 35 °C cian → 55 amarillo → 75+ rojo
        var cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
        var yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
        var red = Color.FromRgb(0xFF, 0x3B, 0x3B);
        if (t <= 35) return cyan;
        if (t <= 55) return FanView.Lerp(cyan, yellow, (t - 35) / 20);
        if (t <= 75) return FanView.Lerp(yellow, red, (t - 55) / 20);
        return red;
    }

    private void RedrawSpark()
    {
        double w = ActualWidth - Padding.Left - Padding.Right - 2;
        if (_count < 2 || w <= 10)
        {
            _spark.Data = null;
            return;
        }
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < _count; i++)
        {
            double v = _history[(_head - _count + i + HistoryLength) % HistoryLength];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        if (max - min < 4) { double mid = (max + min) / 2; min = mid - 2; max = mid + 2; }
        const double h = 28;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < _count; i++)
            {
                double v = _history[(_head - _count + i + HistoryLength) % HistoryLength];
                var pt = new Point(w * i / (HistoryLength - 1), 1 + h - (v - min) / (max - min) * h);
                if (i == 0) ctx.BeginFigure(pt, false, false);
                else ctx.LineTo(pt, true, true);
            }
        }
        geo.Freeze();
        _spark.Data = geo;
    }
}
