using System.Windows;
using System.Windows.Media;
using DualSenseTweaker.Core.Device;

namespace DualSenseTweaker.Controls;

/// <summary>Live picture of the touchpad: the pad, up to two finger contacts and the pressed state.</summary>
public sealed class TouchpadView : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty =
        DependencyProperty.Register(nameof(Frame), typeof(InputFrame), typeof(TouchpadView),
            new FrameworkPropertyMetadata(default(InputFrame), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(TouchpadView),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Pad = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x16, 0x1B, 0x27), Color.FromRgb(0x0E, 0x12, 0x1A), 90));
    private static readonly Pen Edge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x2D, 0x3D))), 1.5));
    private static readonly Pen PressedEdge = Freeze(new Pen(Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x4F, 0x82, 0xF7), 0),
            new(Color.FromRgb(0x8E, 0x6C, 0xF0), 0.5),
            new(Color.FromRgb(0xF2, 0x88, 0x4A), 1),
        }, 0)), 2.5));
    private static readonly Brush Finger = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xFA)));
    // First finger blue, second orange – the PLAYER 1 / PLAYER 2 colors of the LCKS design.
    private static readonly Brush BlueGlow = Freeze(new RadialGradientBrush(Color.FromArgb(0xA0, 0x4F, 0x82, 0xF7), Color.FromArgb(0x00, 0x4F, 0x82, 0xF7)));
    private static readonly Brush OrangeGlow = Freeze(new RadialGradientBrush(Color.FromArgb(0xA0, 0xF2, 0x88, 0x4A), Color.FromArgb(0x00, 0xF2, 0x88, 0x4A)));

    public InputFrame Frame
    {
        get => (InputFrame)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>When false (mouse off) the contacts are drawn dimmed.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20)
            return;
        var frame = Frame;
        var pad = new Rect(1, 1, w - 2, h - 2);
        dc.DrawRoundedRectangle(ThemeDrawing.Brush(this, "TouchpadBackgroundBrush", Pad), frame.TouchpadClick ? PressedEdge : ThemeDrawing.Pen(this, "ItemBorderBrush", Edge), pad, 22, 22);

        dc.PushOpacity(IsActive ? 1 : 0.35);
        DrawFinger(dc, frame.First, pad, BlueGlow);
        DrawFinger(dc, frame.Second, pad, OrangeGlow);
        dc.Pop();
    }

    private void DrawFinger(DrawingContext dc, TouchPoint point, Rect pad, Brush glow)
    {
        if (!point.Active)
            return;
        var center = new Point(
            pad.X + pad.Width * point.X / (InputFrame.TouchpadWidth - 1),
            pad.Y + pad.Height * point.Y / (InputFrame.TouchpadHeight - 1));
        dc.DrawEllipse(glow, null, center, 32, 32);
        dc.DrawEllipse(ThemeDrawing.Brush(this, "LedOnBrush", Finger), null, center, 9, 9);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
