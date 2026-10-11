using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DualSenseTweaker.Controls;

/// <summary>
/// Text with letter spacing (WPF's TextBlock has none). A gradient Foreground spans the whole
/// word instead of restarting on every letter.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(TrackedText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Invalidate));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(TrackedText),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Invalidate));

    public static readonly DependencyProperty FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), FrameworkPropertyMetadataOptions.AffectsMeasure, Invalidate));

    public static readonly DependencyProperty FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure, Invalidate));

    public static readonly DependencyProperty FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(FontWeights.Bold, FrameworkPropertyMetadataOptions.AffectsMeasure, Invalidate));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(TrackedText),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    private bool _built;
    private GlyphRun? _run;
    private Geometry? _fallback;
    private double _height;
    private double _width;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Extra space between letters in device-independent pixels.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureBuilt();
        return new Size(_width, _height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureBuilt();
        if (_run is not null)
            dc.DrawGlyphRun(Foreground, _run);
        else if (_fallback is not null)
            dc.DrawGeometry(Foreground, null, _fallback);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _built = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static void Invalidate(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TrackedText)d)._built = false;

    private void EnsureBuilt()
    {
        if (_built)
            return;
        _built = true;
        _run = null;
        _fallback = null;
        var text = Text ?? "";
        double lineHeight = FontFamily.LineSpacing * FontSize;
        _height = lineHeight > 0 ? lineHeight : FontSize * 1.3;
        _width = 0;
        if (text.Length == 0)
            return;

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (typeface.TryGetGlyphTypeface(out var glyphs) && text.All(c => glyphs.CharacterToGlyphMap.ContainsKey(c)))
            _run = BuildGlyphRun(glyphs, text, pixelsPerDip);
        else
            _fallback = BuildGeometry(typeface, text, pixelsPerDip);
    }

    /// <summary>One glyph run with the spacing added to the advance widths: fast and drawn as real (ClearType) text.</summary>
    private GlyphRun BuildGlyphRun(GlyphTypeface glyphs, string text, double pixelsPerDip)
    {
        var indices = new ushort[text.Length];
        var advances = new double[text.Length];
        double x = 0;
        for (int i = 0; i < text.Length; i++)
        {
            indices[i] = glyphs.CharacterToGlyphMap[text[i]];
            advances[i] = glyphs.AdvanceWidths[indices[i]] * FontSize + (i < text.Length - 1 ? Spacing : 0);
            x += advances[i];
        }
        _width = Math.Max(0, x);
        var baseline = new Point(0, FontFamily.Baseline * FontSize);
        return new GlyphRun(glyphs, 0, false, FontSize, (float)pixelsPerDip, indices, baseline, advances,
            null, null, null, null, null, null);
    }

    /// <summary>Per-letter outlines; only for characters the font cannot map directly (font fallback).</summary>
    private Geometry BuildGeometry(Typeface typeface, string text, double pixelsPerDip)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        double x = 0;
        foreach (var ch in text)
        {
            var glyph = new FormattedText(ch.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, FontSize, Brushes.Black, pixelsPerDip);
            group.Children.Add(glyph.BuildGeometry(new Point(x, 0)));
            x += glyph.WidthIncludingTrailingWhitespace + Spacing;
            _height = Math.Max(_height, glyph.Height);
        }
        _width = Math.Max(0, x - Spacing);
        group.Freeze();
        return group;
    }
}
