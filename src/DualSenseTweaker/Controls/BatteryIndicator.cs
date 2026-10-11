using System.Windows;
using System.Windows.Media;

namespace DualSenseTweaker.Controls;

/// <summary>Battery glyph: fill by level (green / orange / red), lightning bolt while charging, outline only when unknown.</summary>
public sealed class BatteryIndicator : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(BatteryIndicator),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsChargingProperty =
        DependencyProperty.Register(nameof(IsCharging), typeof(bool), typeof(BatteryIndicator),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsKnownProperty =
        DependencyProperty.Register(nameof(IsKnown), typeof(bool), typeof(BatteryIndicator),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen Outline = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x66, 0x78))), 1.5));
    private static readonly Brush NubBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x66, 0x78)));
    private static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xD4, 0x7A)));
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x4A)));
    private static readonly Brush Low = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x6A)));
    private static readonly Brush Bolt = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x14)));
    private static readonly Pen BoltEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Colors.White)), 1));

    /// <summary>0–100 %.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    public bool IsKnown
    {
        get => (bool)GetValue(IsKnownProperty);
        set => SetValue(IsKnownProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(30, 15);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 6)
            return;
        double nub = Math.Max(2, w * 0.08);
        var body = new Rect(0.75, 0.75, w - nub - 1.5, h - 1.5);
        dc.DrawRoundedRectangle(null, ThemeDrawing.Pen(this, "DimTextBrush", Outline), body, h * 0.22, h * 0.22);
        dc.DrawRoundedRectangle(ThemeDrawing.Brush(this, "DimTextBrush", NubBrush), null, new Rect(w - nub, h * 0.3, nub, h * 0.4), 1, 1);
        if (!IsKnown)
            return;

        double level = Math.Clamp(Level, 0, 100);
        var inner = new Rect(body.X + 2.25, body.Y + 2.25, Math.Max(0, (body.Width - 4.5) * level / 100), body.Height - 4.5);
        var fill = level <= 15 ? Low : level <= 35 ? ThemeDrawing.Brush(this, "WarningBrush", Warn) : ThemeDrawing.Brush(this, "OnlineBrush", Good);
        dc.DrawRoundedRectangle(fill, null, inner, h * 0.12, h * 0.12);

        if (IsCharging)
        {
            double cx = body.X + body.Width / 2, cy = h / 2, s = h * 0.38;
            var bolt = new StreamGeometry();
            using (var ctx = bolt.Open())
            {
                ctx.BeginFigure(new Point(cx + s * 0.25, cy - s), true, true);
                ctx.LineTo(new Point(cx - s * 0.55, cy + s * 0.15), true, false);
                ctx.LineTo(new Point(cx - s * 0.02, cy + s * 0.15), true, false);
                ctx.LineTo(new Point(cx - s * 0.25, cy + s), true, false);
                ctx.LineTo(new Point(cx + s * 0.55, cy - s * 0.15), true, false);
                ctx.LineTo(new Point(cx + s * 0.02, cy - s * 0.15), true, false);
            }
            bolt.Freeze();
            dc.DrawGeometry(Bolt, BoltEdge, bolt);
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
