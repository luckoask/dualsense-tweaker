using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DualSenseTweaker.Controls;

/// <summary>Numbered card heading: "01  OVLÁDAČ" (number in the accent color, letter-spaced title).</summary>
public sealed class PanelTitle : StackPanel
{
    public static readonly DependencyProperty NumberProperty =
        DependencyProperty.Register(nameof(Number), typeof(string), typeof(PanelTitle), new PropertyMetadata(""));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PanelTitle), new PropertyMetadata(""));

    public PanelTitle()
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 0, 0, 22);

        var number = new TrackedText { FontSize = 13, Spacing = 2, Margin = new Thickness(0, 0, 16, 0) };
        number.SetResourceReference(TrackedText.ForegroundProperty, "NumberBrush");
        number.SetBinding(TrackedText.TextProperty, new Binding(nameof(Number)) { Source = this });

        var title = new TrackedText { FontSize = 13, Spacing = 4.2 };
        title.SetResourceReference(TrackedText.ForegroundProperty, "TextBrush");
        title.SetBinding(TrackedText.TextProperty, new Binding(nameof(Title)) { Source = this });

        Children.Add(number);
        Children.Add(title);
    }

    public string Number
    {
        get => (string)GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}
