using System.Windows;
using System.Windows.Media;

namespace DualSenseTweaker.Controls;

internal static class ThemeDrawing
{
    public static Brush Brush(FrameworkElement owner, string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;
    public static Pen Pen(FrameworkElement owner, string key, Pen fallback) => new(Brush(owner, key, fallback.Brush), fallback.Thickness);
}
