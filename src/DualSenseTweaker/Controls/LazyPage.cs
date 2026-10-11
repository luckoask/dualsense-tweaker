using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace DualSenseTweaker.Controls;

/// <summary>
/// A section page that is built from <see cref="Template"/> the first time it is shown (measured),
/// so the window opens without creating every section's controls and bindings. Once built, the
/// page stays, keeping its scroll position.
/// </summary>
[ContentProperty(nameof(Template))]
public sealed class LazyPage : Decorator
{
    public static readonly DependencyProperty TemplateProperty =
        DependencyProperty.Register(nameof(Template), typeof(DataTemplate), typeof(LazyPage), new PropertyMetadata(null));

    public DataTemplate? Template
    {
        get => (DataTemplate?)GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Collapsed elements are never measured, so this runs only once the page is visible.
        if (Child is null && Template?.LoadContent() is UIElement page)
            Child = page;
        return base.MeasureOverride(constraint);
    }
}
