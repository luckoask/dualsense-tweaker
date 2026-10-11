using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.ViewModels;

namespace DualSenseTweaker.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseLocalizationTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Competitive", ProfileKind.Competitive, "Súťažný")]
    [InlineData("Adventure", ProfileKind.Adventure, "Dobrodružný")]
    [InlineData("Custom", ProfileKind.Custom, "Vlastný")]
    [InlineData("My Rainbow", ProfileKind.Custom, "My Rainbow")]
    public void DisplayTranslationPreservesSavedIdentifiersAndUserNames(string name, ProfileKind kind, string display)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "profile.json");
        var profile = new Profile(name, kind, new ProfileSettings
        {
            Output = new OutputSnapshot { LightbarEffect = LightbarEffectMode.Rainbow, RainbowCycleSeconds = 9 },
        });
        Assert.Equal(display, profile.DisplayName);
        Assert.Equal(display.ToUpperInvariant(), new ProfileItem(name, kind, null).UpperName);
        ProfileStore.Export(profile, path);
        var imported = ProfileStore.Import(path);
        Assert.Equal(name, imported.Name);
        Assert.Equal(profile.Settings, imported.Settings);
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("displayName", json);
        Assert.Contains("\"Rainbow\"", json);
    }

    [Fact]
    public void TranslatedGameRuleChoicesStillSelectAndRemoveTheOriginalRules()
    {
        var settings = new SettingsState();
        var preferences = new PreferencesState();
        using var profiles = new ProfileService(new ProfileStore(_root), settings, new RefreshState(), preferences);
        preferences.Update(p => p with { GameRules = [new GameRule("existing.exe", ProfilePresets.AdventureName)] });
        using var watcher = new GameProfileWatcher(profiles, preferences, () => new HashSet<string>());
        var view = new AutoProfileViewModel(preferences, profiles, watcher, new NoDialogs(), _ => { });
        Assert.Equal("Dobrodružný", view.ProfileOptions.Single(p => p.Value == ProfilePresets.AdventureName).Label);
        Assert.Equal(ProfilePresets.AdventureName, view.NewProfile);
        view.NewExe = "game.exe";
        view.NewProfile = ProfilePresets.CompetitiveName;
        view.AddCommand.Execute(null);
        view.Sync();
        var rule = view.Rules.Single(r => r.Value.ExeName == "game.exe");
        Assert.Equal("Súťažný", rule.Label);
        Assert.Equal(ProfilePresets.CompetitiveName, rule.Value.ProfileName);
        view.RemoveCommand.Execute(rule);
        Assert.Single(preferences.Current.GameRules);
        Assert.Equal("existing.exe", preferences.Current.GameRules[0].ExeName);
        Assert.Equal(ProfilePresets.AdventureName, preferences.Current.GameRules[0].ProfileName);
    }

    public void Dispose()
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DualSenseLocalizationTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test cleanup target is outside its temporary directory.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class NoDialogs : IFileDialogs
    {
        public string? PickProfileToImport() => null;
        public string? PickExportPath(string suggestedName) => null;
        public string? PickExecutable() => null;
        public bool Confirm(string title, string message) => throw new InvalidOperationException("No destructive dialog is expected in this test.");
    }
}
