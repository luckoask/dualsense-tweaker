using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace DualSenseTweaker.MacroGamer.Localization;

public static class TextCatalog
{
    public sealed record Translation(string En, string Cs);
    private static readonly Dictionary<string, Translation> Entries = Load();
    public static string Language { get; set; } = "SK";
    public static string Text(string source) => Entries.TryGetValue(source, out var entry) ? Language switch { "EN" => entry.En, "CZ" => entry.Cs, _ => source } : source;
    private static Dictionary<string, Translation> Load()
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream("MacroGamer.Localization.strings.json")
            ?? throw new InvalidOperationException("Macro Gamer translations are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Translation>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Invalid translations.");
    }
    public static void Apply(Application application, string language, string theme)
    {
        Language = language;
        foreach (var source in Entries.Keys) application.Resources["MacroText:" + source] = Text(source);
        application.ThemeMode = theme == "Light" ? ThemeMode.Light : ThemeMode.Dark;
        application.Resources.MergedDictionaries[0] = new() { Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative) };
    }
}
public sealed class TextExtension(string source) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        string key = "MacroText:" + source;
        if (Application.Current is { } app && !app.Resources.Contains(key)) app.Resources[key] = TextCatalog.Text(source);
        return new DynamicResourceExtension(key).ProvideValue(serviceProvider);
    }
}
