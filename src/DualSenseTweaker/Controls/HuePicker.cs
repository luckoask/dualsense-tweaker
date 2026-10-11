using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DualSenseTweaker.Controls;

/// <summary>
/// HUE color picker: a saturation × brightness square for the current hue and a rainbow hue bar
/// below it. Drag either; <see cref="Color"/> updates live (two-way bindable). The hue is kept
/// while the color is gray or black, so it does not jump back to red.
/// </summary>
public sealed class HuePicker : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(Color), typeof(HuePicker),
            new FrameworkPropertyMetadata(Colors.Red, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((HuePicker)d).OnColorChanged((Color)e.NewValue)));

    private const double BarHeight = 14;
    private const double Gap = 16;
    private const double Corner = 12;

    private static readonly Brush Rainbow = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(255, 0, 0), 0),
        new(Color.FromRgb(255, 255, 0), 1 / 6.0),
        new(Color.FromRgb(0, 255, 0), 2 / 6.0),
        new(Color.FromRgb(0, 255, 255), 3 / 6.0),
        new(Color.FromRgb(0, 0, 255), 4 / 6.0),
        new(Color.FromRgb(255, 0, 255), 5 / 6.0),
        new(Color.FromRgb(255, 0, 0), 1),
    }, 0));
    private static readonly Brush WhiteToClear = Freeze(new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0));
    private static readonly Brush ClearToBlack = Freeze(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90));
    private static readonly Pen Edge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen ThumbPen = Freeze(new Pen(Brushes.White, 2.5));
    private static readonly Pen ThumbShadow = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0))), 5));

    private enum DragTarget { None, Square, Bar }

    private double _hue, _saturation = 1, _value = 1;
    private DragTarget _drag;
    private bool _updating;

    public HuePicker()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, 170 + Gap + BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var (square, bar) = Layout();
        if (square.Width <= 0 || square.Height <= 0)
            return;

        var hueColor = FromHsv(_hue, 1, 1);
        dc.DrawRoundedRectangle(new SolidColorBrush(hueColor), null, square, Corner, Corner);
        dc.DrawRoundedRectangle(WhiteToClear, null, square, Corner, Corner);
        dc.DrawRoundedRectangle(ClearToBlack, Edge, square, Corner, Corner);

        dc.DrawRoundedRectangle(Rainbow, Edge, bar, BarHeight / 2, BarHeight / 2);

        // Thumbs: a ring in the square, a pill on the bar, both filled with what they select.
        var squareThumb = new Point(square.X + _saturation * square.Width, square.Y + (1 - _value) * square.Height);
        var color = FromHsv(_hue, _saturation, _value);
        dc.DrawEllipse(null, ThumbShadow, squareThumb, 9, 9);
        dc.DrawEllipse(new SolidColorBrush(color), ThumbPen, squareThumb, 9, 9);

        var barThumb = new Point(bar.X + _hue / 360 * bar.Width, bar.Y + bar.Height / 2);
        dc.DrawEllipse(null, ThumbShadow, barThumb, 10, 10);
        dc.DrawEllipse(new SolidColorBrush(hueColor), ThumbPen, barThumb, 10, 10);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var (square, bar) = Layout();
        var p = e.GetPosition(this);
        var barHit = bar;
        barHit.Inflate(0, 10);
        _drag = barHit.Contains(p) ? DragTarget.Bar : square.Contains(p) ? DragTarget.Square : DragTarget.None;
        if (_drag == DragTarget.None)
            return;
        CaptureMouse();
        Pick(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag != DragTarget.None && IsMouseCaptured)
            Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = DragTarget.None;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _drag = DragTarget.None;

    private (Rect Square, Rect Bar) Layout()
    {
        double w = ActualWidth, h = ActualHeight;
        double squareHeight = Math.Max(0, h - Gap - BarHeight);
        // Leave room for the thumbs at the edges.
        return (new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, squareHeight - 2)),
                new Rect(10, squareHeight + Gap, Math.Max(0, w - 20), BarHeight));
    }

    private void Pick(Point p)
    {
        var (square, bar) = Layout();
        if (_drag == DragTarget.Bar)
        {
            _hue = Math.Clamp((p.X - bar.X) / bar.Width, 0, 1) * 360;
            // Picking a hue on a gray/black color would show nothing; give it full color.
            if (_saturation < 0.05)
                _saturation = 1;
            if (_value < 0.05)
                _value = 1;
        }
        else
        {
            _saturation = Math.Clamp((p.X - square.X) / square.Width, 0, 1);
            _value = 1 - Math.Clamp((p.Y - square.Y) / square.Height, 0, 1);
        }

        _updating = true;
        try
        {
            Color = FromHsv(_hue, _saturation, _value);
        }
        finally
        {
            _updating = false;
        }
        InvalidateVisual();
    }

    private void OnColorChanged(Color color)
    {
        if (_updating)
            return;
        // Only adopt the hue of the new color when it has one (gray and black keep the current hue).
        var (h, s, v) = ToHsv(color);
        if (s > 0.01 && v > 0.01)
            _hue = h;
        _saturation = s;
        _value = v;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360 / 60;
        int sector = (int)Math.Floor(h) % 6;
        double f = h - Math.Floor(h);
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);
        return ((h + 360) % 360, max == 0 ? 0 : delta / max, max);
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
