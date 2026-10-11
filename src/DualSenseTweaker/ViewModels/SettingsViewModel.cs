using System.Windows.Media;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>Base for view models whose properties are pass-throughs to <see cref="SettingsState"/>.</summary>
public abstract class SettingsViewModel(SettingsState state) : ObservableObject
{
    protected SettingsState State { get; } = state;

    protected ProfileSettings Current => State.Current;

    /// <summary>Re-reads every property after the settings changed (profile switch, another view, …).</summary>
    public virtual void Refresh() => OnPropertyChanged(string.Empty);

    protected static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

/// <summary>Colors offered by the swatch pickers.</summary>
public static class Palette
{
    public static IReadOnlyList<ColorSwatch> Swatches { get; } =
    [
        .. new[]
        {
            "#0050FF", "#285AFF", "#00B4FF", "#00FF78", "#3DDC84", "#FFD400",
            "#FF7800", "#FF0028", "#FF2EC4", "#9B4DFF", "#FFFFFF",
        }.Select(hex => new ColorSwatch(hex)),
    ];

    public static SolidColorBrush ToBrush(RgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

public sealed class ColorSwatch
{
    public ColorSwatch(string hex)
    {
        RgbColor.TryParse(hex, out var color);
        Hex = color.ToHex();
        Brush = Palette.ToBrush(color);
    }

    public string Hex { get; }
    public SolidColorBrush Brush { get; }
}
