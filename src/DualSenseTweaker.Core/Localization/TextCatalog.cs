using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Localization;

/// <summary>Application text only. Saved profile names, enum values and device identifiers remain untouched.</summary>
public static class TextCatalog
{
    public sealed record Translation(string En, string Cs);
    private sealed record Template(Regex Pattern, string Source, int ArgumentCount);
    private static readonly IReadOnlyDictionary<string, Translation> Entries = Load();
    private static readonly Lazy<IReadOnlyList<Template>> Templates = new(BuildTemplates);
    private static int _language;

    public static AppLanguage Language
    {
        get => (AppLanguage)Volatile.Read(ref _language);
        set => Volatile.Write(ref _language, (int)(Enum.IsDefined(value) ? value : AppLanguage.Slovak));
    }

    public static IEnumerable<string> Sources => Entries.Keys;
    public static bool Contains(string source) => Entries.ContainsKey(source);
    public static CultureInfo Culture(AppLanguage language) => CultureInfo.GetCultureInfo(language switch
    {
        AppLanguage.English => "en-GB",
        AppLanguage.Czech => "cs-CZ",
        _ => "sk-SK",
    });

    public static string Text(string source) => Text(source, Language);

    public static string Text(string source, AppLanguage language)
    {
        if (source.Length == 0) return source;
        if (Entries.TryGetValue(source, out var translation))
            return RenderCase(source, Select(source, translation, language));
        // A status can outlive a language change. Translate its old text without touching its data.
        foreach (var entry in Entries)
            if (string.Equals(source, entry.Value.En, StringComparison.OrdinalIgnoreCase) || string.Equals(source, entry.Value.Cs, StringComparison.OrdinalIgnoreCase))
                return RenderCase(source, Select(entry.Key, entry.Value, language));
        foreach (var template in Templates.Value)
        {
            var match = template.Pattern.Match(source);
            if (!match.Success) continue;
            object[] arguments = Enumerable.Range(0, template.ArgumentCount).Select(i => (object)match.Groups[$"p{i}"].Value).ToArray();
            return string.Format(Culture(language), Select(template.Source, Entries[template.Source], language), arguments);
        }
        return source;
    }

    public static string Format(FormattableString source) => Format(source, Language);
    public static string Format(FormattableString source, AppLanguage language)
    {
        string template = Entries.TryGetValue(source.Format, out var translation)
            ? Select(source.Format, translation, language) : source.Format;
        return string.Format(Culture(language), template, source.GetArguments());
    }

    private static string Select(string source, Translation translation, AppLanguage language) => language switch
    {
        AppLanguage.English => translation.En,
        AppLanguage.Czech => translation.Cs,
        _ => source,
    };

    private static string RenderCase(string source, string translated)
    {
        if (!source.Any(char.IsLetter)) return translated;
        if (source == source.ToUpperInvariant()) return translated.ToUpperInvariant();
        if (source == source.ToLowerInvariant()) return translated.ToLowerInvariant();
        return translated;
    }

    private static IReadOnlyDictionary<string, Translation> Load()
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream("DualSenseTweaker.Core.Localization.strings.json")
                           ?? throw new InvalidOperationException("The application translation catalog is missing.");
        var data = JsonSerializer.Deserialize<Dictionary<string, Translation>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("The application translation catalog is invalid.");
        return new Dictionary<string, Translation>(data, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Template> BuildTemplates()
    {
        var templates = new List<Template>();
        var placeholder = new Regex(@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}", RegexOptions.CultureInvariant);
        foreach (var entry in Entries)
        {
            var fields = placeholder.Matches(entry.Key);
            if (fields.Count == 0) continue;
            int count = fields.Max(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
            foreach (string text in new[] { entry.Key, entry.Value.En, entry.Value.Cs }.Distinct())
            {
                var pattern = new System.Text.StringBuilder("\\A");
                int offset = 0;
                foreach (Match field in placeholder.Matches(text))
                {
                    pattern.Append(Regex.Escape(text[offset..field.Index]));
                    pattern.Append($"(?<p{field.Groups[1].Value}>.*?)");
                    offset = field.Index + field.Length;
                }
                pattern.Append(Regex.Escape(text[offset..])).Append("\\z");
                templates.Add(new Template(new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)), entry.Key, count));
            }
        }
        return templates;
    }
}
