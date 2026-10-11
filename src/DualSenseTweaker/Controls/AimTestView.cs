using System.Numerics;
using Vector = System.Windows.Vector;
using System.Windows;
using System.Windows.Media;
using DualSenseTweaker.Core.Motion;

namespace DualSenseTweaker.Controls;

/// <summary>Draws the gyro aim test: arena, target, crosshair with its trail and a flash on every shot.</summary>
public sealed class AimTestView : FrameworkElement
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(AimTestState), typeof(AimTestView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int FlashFrames = 8;

    private static readonly Brush Arena = Freeze(new LinearGradientBrush(Color.FromRgb(0x10, 0x14, 0x1E), Color.FromRgb(0x0B, 0x0E, 0x15), 90));
    private static readonly Pen ArenaEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x29, 0x38))), 1));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush TargetGlow = Freeze(new RadialGradientBrush(Color.FromArgb(0x70, 0xF2, 0x88, 0x4A), Color.FromArgb(0, 0xF2, 0x88, 0x4A)));
    private static readonly Pen TargetRing = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x88, 0x4A))), 2.5));
    private static readonly Pen TargetInner = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xF2, 0x88, 0x4A))), 1.5));
    private static readonly Brush TargetCenter = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xE6)));
    private static readonly Pen Cross = Freeze(new Pen(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Color TrailColor = Color.FromRgb(0x4F, 0x82, 0xF7);
    private static readonly Brush HitFlash = Freeze(new RadialGradientBrush(Color.FromArgb(0xA0, 0x2E, 0xD4, 0x7A), Color.FromArgb(0, 0x2E, 0xD4, 0x7A)));
    private static readonly Brush MissFlash = Freeze(new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0x5A, 0x6A), Color.FromArgb(0, 0xFF, 0x5A, 0x6A)));

    private int _seenShots = -1;
    private int _flash;

    public AimTestState? State
    {
        get => (AimTestState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40)
            return;
        var state = State;
        var arena = state?.Arena ?? new Vector2((float)w, (float)h);

        // Uniform scale so a circle stays a circle; the arena (where the crosshair can go) is centered.
        double scale = Math.Min((w - 1) / arena.X, (h - 1) / arena.Y);
        var offset = new Vector((w - arena.X * scale) / 2, (h - arena.Y * scale) / 2);
        Point Map(Vector2 p) => new Point(p.X * scale, p.Y * scale) + offset;

        var rect = new Rect(Map(Vector2.Zero), Map(arena));
        dc.DrawRoundedRectangle(ThemeDrawing.Brush(this, "ItemBrush", Arena), ThemeDrawing.Pen(this, "ItemBorderBrush", ArenaEdge), rect, 16, 16);
        for (double x = rect.Left + 44; x < rect.Right; x += 44)
            dc.DrawLine(ThemeDrawing.Pen(this, "ChartGridBrush", GridPen), new Point(x, rect.Top + 1), new Point(x, rect.Bottom - 1));
        for (double y = rect.Top + 44; y < rect.Bottom; y += 44)
            dc.DrawLine(ThemeDrawing.Pen(this, "ChartGridBrush", GridPen), new Point(rect.Left + 1, y), new Point(rect.Right - 1, y));
        if (state is null)
            return;

        dc.PushClip(new RectangleGeometry(rect, 16, 16));

        var target = Map(state.Target);
        double radius = state.TargetRadius * scale;
        dc.DrawEllipse(TargetGlow, null, target, radius * 2.4, radius * 2.4);
        dc.DrawEllipse(null, TargetRing, target, radius, radius);
        dc.DrawEllipse(null, TargetInner, target, radius * 0.55, radius * 0.55);
        dc.DrawEllipse(TargetCenter, null, target, radius * 0.18, radius * 0.18);

        // Fading trail of the crosshair.
        var trail = state.Trail;
        for (int i = 1; i < trail.Count; i++)
        {
            byte alpha = (byte)(200 * i / trail.Count);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, TrailColor.R, TrailColor.G, TrailColor.B)), 1 + 2.0 * i / trail.Count);
            dc.DrawLine(pen, Map(trail[i - 1]), Map(trail[i]));
        }

        var crosshair = Map(state.Crosshair);
        if (state.ShotCount != _seenShots)
        {
            if (_seenShots >= 0)
                _flash = FlashFrames;
            _seenShots = state.ShotCount;
        }
        if (_flash > 0)
        {
            double size = 18 + (FlashFrames - _flash) * 6;
            dc.PushOpacity(_flash / (double)FlashFrames);
            dc.DrawEllipse(state.LastShotHit ? HitFlash : MissFlash, null, crosshair, size, size);
            dc.Pop();
            _flash--;
        }

        const double gap = 5, arm = 11;
        var cross = new Pen(ThemeDrawing.Brush(this, "TextBrush", Cross.Brush), Cross.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(cross, crosshair + new Vector(-gap - arm, 0), crosshair + new Vector(-gap, 0));
        dc.DrawLine(cross, crosshair + new Vector(gap, 0), crosshair + new Vector(gap + arm, 0));
        dc.DrawLine(cross, crosshair + new Vector(0, -gap - arm), crosshair + new Vector(0, -gap));
        dc.DrawLine(cross, crosshair + new Vector(0, gap), crosshair + new Vector(0, gap + arm));
        dc.DrawEllipse(cross.Brush, null, crosshair, 1.6, 1.6);
        dc.Pop();
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
