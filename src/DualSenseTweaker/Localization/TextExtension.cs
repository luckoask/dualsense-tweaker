using System.Windows;
using System.Windows.Markup;

namespace DualSenseTweaker.Localization;

/// <summary>Dynamic resources also update the pages that have already been opened.</summary>
public sealed class TextExtension : DynamicResourceExtension
{
    public TextExtension(string source) : base("LocalizedText:" + source)
    {
        Source = source;
        if (Application.Current is { } app && !app.Resources.Contains(ResourceKey))
            app.Resources[ResourceKey] = L.Text(source);
    }

    [ConstructorArgument("source")]
    public string Source { get; }
}
