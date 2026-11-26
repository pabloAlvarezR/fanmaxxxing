using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FanMaxxxing.UI;

/// <summary>Texto estilo meme clásico: Impact blanco con borde negro. La geometría se cachea.</summary>
public sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((OutlinedText)d)._geometry = null));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(34.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((OutlinedText)d)._geometry = null));

    private static readonly Typeface Face = new(new FontFamily("Impact, Arial Black, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush FillBrush = Frozen(Brushes.White);
    private static readonly Pen OutlinePen = MakePen();

    private Geometry? _geometry;
    private double _geometryWidth = -1;

    private static Brush Frozen(Brush b) => b.IsFrozen ? b : (Brush)b.GetCurrentValueAsFrozen();

    private static Pen MakePen()
    {
        var p = new Pen(Brushes.Black, 5) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    public OutlinedText()
    {
        IsHitTestVisible = false;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private FormattedText Build(double maxWidth)
    {
        var ft = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, FontSize,
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center,
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth > 10 && !double.IsInfinity(maxWidth)) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        var ft = Build(w - 8);
        // Holgura extra para que al pintar (ActualWidth - 8) quepa en las mismas líneas que al medir
        return new Size(Math.Min(w, Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 14), ft.Height + 8);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 0) return;
        if (_geometry == null || Math.Abs(_geometryWidth - w) > 0.5)
        {
            var ft = Build(w - 8);
            // Con TextAlignment.Center y MaxTextWidth, el origen x es el borde izquierdo de la caja
            var geo = ft.BuildGeometry(new Point(4, 4));
            geo.Freeze();
            _geometry = geo;
            _geometryWidth = w;
        }
        dc.DrawGeometry(null, OutlinePen, _geometry);
        dc.DrawGeometry(FillBrush, null, _geometry);
    }
}
