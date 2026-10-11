using System.Windows;
using System.Windows.Controls;

namespace DualSenseTweaker.Controls;

/// <summary>Title + optional hint on the left, on/off switch on the right.</summary>
public partial class SwitchRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SwitchRow), new PropertyMetadata(""));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SwitchRow),
            new PropertyMetadata("", (d, e) => ((SwitchRow)d).HintText.Visibility =
                string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(SwitchRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public SwitchRow()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }
}
