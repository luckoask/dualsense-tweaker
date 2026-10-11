using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Localization;

public static class ApplicationAppearance
{
    private static AppTheme? _theme;
    public static AppTheme CurrentTheme => _theme ?? AppTheme.Dark;

    public static void Apply(Application application, AppPreferences preferences)
    {
        L.Language = preferences.Language;
        var culture = L.Culture(preferences.Language);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        foreach (string source in L.Sources)
            application.Resources["LocalizedText:" + source] = L.Text(source);
        // Keep the keys registered by markup extensions, including captions that use upper case.
        var keys = application.Resources.Keys.Cast<object>().OfType<string>().Where(k => k.StartsWith("LocalizedText:", StringComparison.Ordinal)).ToArray();
        foreach (string key in keys)
            application.Resources[key] = L.Text(key["LocalizedText:".Length..]);

        if (_theme != preferences.Theme)
        {
            _theme = preferences.Theme;
            application.ThemeMode = preferences.Theme == AppTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
            var palettes = application.Resources.MergedDictionaries;
            var palette = new ResourceDictionary { Source = new Uri($"Themes/{preferences.Theme}.xaml", UriKind.Relative) };
            int index = Enumerable.Range(0, palettes.Count).FirstOrDefault(i => palettes[i].Source?.OriginalString is "Themes/Dark.xaml" or "Themes/Light.xaml", -1);
            if (index >= 0) palettes[index] = palette;
            else palettes.Insert(0, palette);
        }
        if (application.MainWindow is MainWindow window)
        {
            window.Language = XmlLanguage.GetLanguage(culture.Name);
            window.UpdateAppearance();
        }
    }
}
