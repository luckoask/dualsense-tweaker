using System.Windows;
using System.Windows.Media;

namespace DualSenseTweaker.Controls;

/// <summary>Live spectrum bars (0–1 values, low → high frequencies) colored from the bass → mid → high colors.</summary>
public sealed class SpectrumView : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<float>), typeof(SpectrumView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowColorProperty = RegisterColor(nameof(LowColor), Color.FromRgb(0xFF, 0x00, 0x28));
    public static readonly DependencyProperty MidColorProperty = RegisterColor(nameof(MidColor), Color.FromRgb(0x00, 0xFF, 0x78));
    public static readonly DependencyProperty HighColorProperty = RegisterColor(nameof(HighColor), Color.FromRgb(0x28, 0x5A, 0xFF));

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x1A, 0x25)));
    private Brush? _barBrush;

    public IReadOnlyList<float>? Values
    {
        get => (IReadOnlyList<float>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color LowColor
    {
        get => (Color)GetValue(LowColorProperty);
        set => SetValue(LowColorProperty, value);
    }

    public Color MidColor
    {
        get => (Color)GetValue(MidColorProperty);
        set => SetValue(MidColorProperty, value);
    }

    public Color HighColor
    {
        get => (Color)GetValue(HighColorProperty);
        set => SetValue(HighColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        int count = values?.Count ?? 32;
        double width = ActualWidth, height = ActualHeight;
        if (count == 0 || width <= 0 || height <= 0)
            return;

        const double gap = 3;
        double barWidth = Math.Max(1, (width - gap * (count - 1)) / count);
        double radius = Math.Min(3, barWidth / 2);
        _barBrush ??= Freeze(new LinearGradientBrush(
            new GradientStopCollection
            {
                new(LowColor, 0),
                new(MidColor, 0.5),
                new(HighColor, 1),
            },
            new Point(0, 0), new Point(width, 0)) { MappingMode = BrushMappingMode.Absolute });

        for (int i = 0; i < count; i++)
        {
            double x = i * (barWidth + gap);
            dc.DrawRoundedRectangle(ThemeDrawing.Brush(this, "TrackBrush", TrackBrush), null, new Rect(x, 0, barWidth, height), radius, radius);
            double level = values is null ? 0 : Math.Clamp(values[i], 0, 1);
            double barHeight = Math.Max(2, level * height);
            dc.DrawRoundedRectangle(_barBrush, null, new Rect(x, height - barHeight, barWidth, barHeight), radius, radius);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        _barBrush = null; // gradient is mapped to the width
        base.OnRenderSizeChanged(sizeInfo);
    }

    private static DependencyProperty RegisterColor(string name, Color defaultColor) =>
        DependencyProperty.Register(name, typeof(Color), typeof(SpectrumView),
            new FrameworkPropertyMetadata(defaultColor, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((SpectrumView)d)._barBrush = null));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
