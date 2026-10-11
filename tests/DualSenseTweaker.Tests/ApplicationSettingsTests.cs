using System.Runtime.CompilerServices;
using System.Text;
using DualSenseTweaker.Core.Localization;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public sealed class ApplicationSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseAppSettingsTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(AppLanguage.Slovak, AppTheme.Dark)]
    [InlineData(AppLanguage.Slovak, AppTheme.Light)]
    [InlineData(AppLanguage.English, AppTheme.Dark)]
    [InlineData(AppLanguage.English, AppTheme.Light)]
    [InlineData(AppLanguage.Czech, AppTheme.Dark)]
    [InlineData(AppLanguage.Czech, AppTheme.Light)]
    public void AppearanceSurvivesSavingWithoutChangingAssignments(AppLanguage language, AppTheme theme)
    {
        var store = new ProfileStore(_root);
        var preferences = new AppPreferences
        {
            Language = language, Theme = theme, CloseToTray = false,
            GameRules = [new("game.exe", ProfilePresets.AdventureName)],
            Controllers = [new("AA:BB:CC:DD:EE:FF", "My Rainbow", ProfilePresets.CustomName)],
        };
        store.SaveAppSettings(new AppSettings { ActiveProfile = ProfilePresets.CustomName, Preferences = preferences });
        var loaded = store.LoadAppSettings();
        Assert.Equal(language, loaded.Preferences.Language);
        Assert.Equal(theme, loaded.Preferences.Theme);
        Assert.False(loaded.Preferences.CloseToTray);
        Assert.Equal(ProfilePresets.CustomName, loaded.ActiveProfile);
        Assert.Equal(preferences.GameRules, loaded.Preferences.GameRules);
        Assert.Equal(preferences.Controllers, loaded.Preferences.Controllers);
    }

    [Fact]
    public void OlderSettingsKeepTheExistingSlovakDarkAppearance()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"activeProfile":"Custom","preferences":{"closeToTray":false,"hotkeysEnabled":false}}""");
        var preferences = new ProfileStore(_root).LoadAppSettings().Preferences;
        Assert.Equal(AppLanguage.Slovak, preferences.Language);
        Assert.Equal(AppTheme.Dark, preferences.Theme);
        Assert.False(preferences.CloseToTray);
        Assert.False(preferences.HotkeysEnabled);
    }

    [Fact]
    public void UnknownAppearanceValuesFallBackToSupportedOptions()
    {
        var normalized = new AppPreferences { Language = (AppLanguage)999, Theme = (AppTheme)(-1) }.Normalize();
        Assert.Equal(AppLanguage.Slovak, normalized.Language);
        Assert.Equal(AppTheme.Dark, normalized.Theme);
    }

    [Fact]
    public void ApplicationAppearanceDoesNotMutateTheActiveControllerProfile()
    {
        var store = new ProfileStore(_root);
        var settings = new SettingsState();
        var preferences = new PreferencesState();
        using (var profiles = new ProfileService(store, settings, new RefreshState(), preferences))
        {
            settings.UpdateOutput(o => o with { LightbarEffect = LightbarEffectMode.Rainbow, RainbowCycleSeconds = 9 });
            var before = settings.Read();
            preferences.Update(p => p with { Language = AppLanguage.Czech, Theme = AppTheme.Light });
            Assert.Equal(before, settings.Read());
            Assert.Equal(ProfilePresets.CustomName, profiles.Active.Name);
        }
        Assert.Equal(AppLanguage.Czech, store.LoadAppSettings().Preferences.Language);
        Assert.Equal(AppTheme.Light, store.LoadAppSettings().Preferences.Theme);
        Assert.Contains(store.LoadAll(), p => p.Name == ProfilePresets.CustomName && p.Settings.Output.LightbarEffect == LightbarEffectMode.Rainbow);
    }

    [Theory]
    [InlineData("Nastavenia", "Settings", "Nastavení")]
    [InlineData("Dúha", "Rainbow", "Duha")]
    [InlineData("SVETELNÝ PÁS", "LIGHTBAR", "SVĚTELNÝ PÁS")]
    public void NavigationAndControlsUseTheSelectedLanguage(string source, string en, string cs)
    {
        Assert.Equal(en, TextCatalog.Text(source, AppLanguage.English));
        Assert.Equal(cs, TextCatalog.Text(source, AppLanguage.Czech));
        Assert.Equal(source, TextCatalog.Text(source, AppLanguage.Slovak));
    }

    [Fact]
    public void FormattingPreservesUserNamesAndTranslatesAnExistingStatus()
    {
        const string name = "Guest / Dúha {blue}";
        var message = FormattableStringFactory.Create("Profil „{0}“ bol načítaný.", name);
        string en = TextCatalog.Format(message, AppLanguage.English);
        Assert.Equal($"Profile “{name}” imported.", en);
        Assert.Equal($"Profil „{name}“ byl načten.", TextCatalog.Text(en, AppLanguage.Czech));
        Assert.Equal($"Profil „{name}“ bol načítaný.", TextCatalog.Text(en, AppLanguage.Slovak));
        Assert.Equal(name, TextCatalog.Text(name, AppLanguage.English));
    }

    [Fact]
    public void EveryTranslationPreservesItsFormatArguments()
    {
        foreach (string source in TextCatalog.Sources)
        {
            int expected = CompositeFormat.Parse(source).MinimumArgumentCount;
            foreach (AppLanguage language in Enum.GetValues<AppLanguage>())
            {
                string translated = TextCatalog.Text(source, language);
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.Equal(expected, CompositeFormat.Parse(translated).MinimumArgumentCount);
            }
        }
    }

    public void Dispose()
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DualSenseAppSettingsTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid temporary cleanup path.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
