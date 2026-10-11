using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using DualSenseTweaker.Core.Motion;

namespace DualSenseTweaker.Controls;

public enum MotionGraphKind { Gyro, Accel }

/// <summary>Scrolling oscilloscope of the last seconds of gyro (°/s) or accelerometer (g) data, X/Y/Z as three lines.</summary>
public sealed class MotionGraph : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<MotionPoint>), typeof(MotionGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(MotionGraphKind), typeof(MotionGraph),
            new FrameworkPropertyMetadata(MotionGraphKind.Gyro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty =
        DependencyProperty.Register(nameof(WindowSeconds), typeof(double), typeof(MotionGraph),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>X = blue, Y = orange, Z = purple (LCKS palette).</summary>
    public static readonly Color[] AxisColors =
    [
        Color.FromRgb(0x4F, 0x82, 0xF7),
        Color.FromRgb(0xF2, 0x88, 0x4A),
        Color.FromRgb(0xB5, 0x7C, 0xFF),
    ];

    private static readonly double[] GyroScales = [25, 50, 100, 250, 500, 1000, 2000];
    private static readonly double[] AccelScales = [1.5, 2, 4];
    private static readonly Pen[] Lines = [.. AxisColors.Select(c => Freeze(new Pen(Freeze(new SolidColorBrush(c)), 1.6) { LineJoin = PenLineJoin.Round }))];
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen ZeroPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x66, 0x78)));
    private static readonly Typeface LabelFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private double _scale;

    public IReadOnlyList<MotionPoint>? Points
    {
        get => (IReadOnlyList<MotionPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public MotionGraphKind Kind
    {
        get => (MotionGraphKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double WindowSeconds
    {
        get => (double)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20)
            return;
        IReadOnlyList<MotionPoint> points = Points ?? [];
        bool gyro = Kind == MotionGraphKind.Gyro;
        UpdateScale(points, gyro);

        double mid = h / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        foreach (double fraction in new[] { -1, -0.5, 0.5, 1 })
            dc.DrawLine(ThemeDrawing.Pen(this, "ChartGridBrush", GridPen), new Point(0, mid - fraction * (mid - 2)), new Point(w, mid - fraction * (mid - 2)));
        dc.DrawLine(ThemeDrawing.Pen(this, "ChartZeroBrush", ZeroPen), new Point(0, mid), new Point(w, mid));

        string unit = gyro ? "°/s" : "g";
        DrawLabel(dc, $"+{_scale.ToString("0.#", CultureInfo.InvariantCulture)} {unit}", new Point(4, 2));
        DrawLabel(dc, $"−{_scale.ToString("0.#", CultureInfo.InvariantCulture)} {unit}", new Point(4, h - 15));

        if (points.Count < 2)
            return;
        double newest = points[points.Count - 1].Time;
        double window = Math.Max(WindowSeconds, 0.5);
        for (int axis = 0; axis < 3; axis++)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool started = false;
                foreach (var p in points)
                {
                    double age = newest - p.Time;
                    if (age > window)
                        continue;
                    var v = gyro ? p.Gyro : p.Accel;
                    double value = axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
                    var point = new Point(w * (1 - age / window), mid - Math.Clamp(value / _scale, -1.05, 1.05) * (mid - 2));
                    if (!started)
                    {
                        ctx.BeginFigure(point, false, false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(point, true, false);
                    }
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, Lines[axis], geometry);
        }
    }

    /// <summary>Grows immediately when the signal gets bigger, shrinks only when it stays much smaller.</summary>
    private void UpdateScale(IReadOnlyList<MotionPoint> points, bool gyro)
    {
        var scales = gyro ? GyroScales : AccelScales;
        if (_scale == 0 || Array.IndexOf(scales, _scale) < 0)
            _scale = scales[gyro ? 1 : 0];
        double peak = 0;
        foreach (var p in points)
        {
            var v = gyro ? p.Gyro : p.Accel;
            peak = Math.Max(peak, MaxAbs(v));
        }
        double needed = scales.FirstOrDefault(s => s >= peak * 1.1, scales[^1]);
        if (needed > _scale || peak < _scale / 4)
            _scale = Math.Max(needed, scales[gyro ? 1 : 0]);
    }

    private void DrawLabel(DrawingContext dc, string text, Point origin)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10.5, ThemeDrawing.Brush(this, "DimTextBrush", LabelBrush),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, origin);
    }

    private static double MaxAbs(Vector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
