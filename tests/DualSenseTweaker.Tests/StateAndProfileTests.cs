using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public class SettingsStateTests
{
    [Fact]
    public void ChangeBumpsVersionAndRaisesEvent()
    {
        var state = new SettingsState();
        int events = 0;
        state.Changed += (_, _) => events++;
        var (_, before) = state.Read();

        Assert.True(state.UpdateOutput(o => o with { RumbleHeavy = 10 }));

        Assert.Equal(1, events);
        Assert.Equal(before + 1, state.Read().Version);
    }

    [Fact]
    public void SameValueIsNotAChange()
    {
        var state = new SettingsState();
        int events = 0;
        state.Changed += (_, _) => events++;

        Assert.False(state.UpdateOutput(o => o with { PlayerLeds = o.PlayerLeds }));
        Assert.Equal(0, events);
    }

    [Fact]
    public void ValuesAreClampedToControllerRanges()
    {
        var state = new SettingsState(new ProfileSettings
        {
            Output = new OutputSnapshot
            {
                LightbarBrightness = 150,
                SpeakerVolume = 255,
                HeadphoneVolume = 255,
                RumbleAttenuation = 9,
                PlayerLeds = (PlayerLedMode)42,
                RightTrigger = new TriggerEffect { Mode = TriggerMode.Resistance, StartPosition = 12, Strength = 0 },
            },
            Reactive = new AudioReactiveSettings { Sensitivity = 3, AttackMs = 0 },
        });

        var output = state.Current.Output;
        Assert.Equal(100, output.LightbarBrightness);
        Assert.Equal(OutputSnapshot.MaxSpeakerVolume, output.SpeakerVolume);
        Assert.Equal(OutputSnapshot.MaxHeadphoneVolume, output.HeadphoneVolume);
        Assert.Equal(7, output.RumbleAttenuation);
        Assert.Equal(PlayerLedMode.Player1, output.PlayerLeds);
        Assert.Equal(9, output.RightTrigger.StartPosition);
        Assert.Equal(1, output.RightTrigger.Strength);
        Assert.Equal(1, state.Current.Reactive.Sensitivity);
        Assert.Equal(1, state.Current.Reactive.AttackMs);
    }
}

public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseTweakerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FirstRunSeedsCompetitiveAdventureAndCustom()
    {
        var profiles = new ProfileStore(_root).LoadAll();

        Assert.Equal(["Competitive", "Adventure", "Custom"], profiles.Select(p => p.Name));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "profiles"), "*.json").Length);
    }

    [Fact]
    public void CompetitivePresetSilencesTheController()
    {
        var output = ProfilePresets.Competitive().Output;

        Assert.Equal(0, output.LightbarBrightness);
        Assert.Equal(PlayerLedMode.Off, output.PlayerLeds);
        Assert.Equal(TriggerMode.Off, output.LeftTrigger.Mode);
        Assert.Equal(TriggerMode.Off, output.RightTrigger.Mode);
        Assert.Equal(0, output.RumbleHeavy + output.RumbleSoft);
        Assert.False(ProfilePresets.Competitive().Reactive.Enabled);
    }

    [Fact]
    public void AdventurePresetUsesResistanceRumbleAndReactiveLightbar()
    {
        var adventure = ProfilePresets.Adventure();

        Assert.Equal(TriggerMode.Resistance, adventure.Output.LeftTrigger.Mode);
        Assert.Equal(TriggerMode.Resistance, adventure.Output.RightTrigger.Mode);
        Assert.True(adventure.Reactive.Enabled);
        Assert.True(adventure.Reactive.BassToRumble);
        Assert.Equal(255, adventure.Reactive.RumbleMax);
        Assert.NotEqual(ReactiveLightbarMode.Static, adventure.Reactive.Lightbar);
    }

    [Fact]
    public void ProfilesRoundTripWithoutRumble()
    {
        var store = new ProfileStore(_root);
        var settings = new ProfileSettings
        {
            Output = new OutputSnapshot { LightbarColor = new RgbColor(1, 2, 3), RumbleHeavy = 255, LeftTrigger = TriggerEffect.Off },
            Reactive = new AudioReactiveSettings { Enabled = true, GainDb = 12, BassColor = new RgbColor(4, 5, 6) },
            Haptics = new HapticsSettings { Enabled = true, CutoffHz = 120 },
        }.Normalize();

        store.Save(new Profile("Večer: modrá/červená", ProfileKind.Custom, settings));
        var loaded = store.LoadAll().Single(p => p.Name == "Večer: modrá/červená");

        Assert.Equal(settings.WithoutRumble(), loaded.Settings);
    }

    [Fact]
    public void ExportThenImportGivesACustomProfile()
    {
        var file = Path.Combine(_root, "export.json");
        Directory.CreateDirectory(_root);
        var adventure = new Profile("Adventure", ProfileKind.Adventure, ProfilePresets.Adventure());

        ProfileStore.Export(adventure, file);
        var imported = ProfileStore.Import(file);

        Assert.Equal(ProfileKind.Custom, imported.Kind);
        Assert.Equal(adventure.Settings, imported.Settings);
    }

    [Fact]
    public void ImportRejectsGarbage()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "broken.json");
        File.WriteAllText(file, "{ not json");

        Assert.Throws<InvalidDataException>(() => ProfileStore.Import(file));
    }

    [Fact]
    public void ServiceAppliesActiveProfileAndAutoSavesEdits()
    {
        var store = new ProfileStore(_root);
        var settings = new SettingsState();
        using (var service = new ProfileService(store, settings, new RefreshState()))
        {
            service.Activate("Competitive");
            Assert.Equal(0, settings.Current.Output.LightbarBrightness);

            settings.UpdateOutput(o => o with { LightbarBrightness = 30, RumbleHeavy = 99 });
            service.Flush();
        }

        var saved = new ProfileStore(_root).LoadAll().Single(p => p.Name == "Competitive");
        Assert.Equal(30, saved.Settings.Output.LightbarBrightness);
        Assert.Equal(0, saved.Settings.Output.RumbleHeavy);
        Assert.Equal("Competitive", new ProfileStore(_root).LoadAppSettings().ActiveProfile);
    }

    [Fact]
    public void ServiceProtectsBuiltInsAndResetsToPreset()
    {
        var settings = new SettingsState();
        using var service = new ProfileService(new ProfileStore(_root), settings, new RefreshState());

        Assert.False(service.Delete("Adventure"));

        service.Activate("Adventure");
        settings.UpdateOutput(o => o with { LightbarColor = new RgbColor(1, 1, 1) });
        service.ResetActive();

        Assert.Equal(ProfilePresets.Adventure(), settings.Current);
    }

    [Fact]
    public void ServiceCreatesAndDeletesCustomProfiles()
    {
        var settings = new SettingsState();
        using var service = new ProfileService(new ProfileStore(_root), settings, new RefreshState());
        settings.UpdateOutput(o => o with { MicLed = MicLedMode.On });

        var created = service.CreateFromCurrent("Moje");

        Assert.Equal("Moje", service.Active.Name);
        Assert.Equal(MicLedMode.On, created.Settings.Output.MicLed);
        Assert.Throws<ArgumentException>(() => service.CreateFromCurrent("moje"));
        Assert.True(service.Delete("Moje"));
        Assert.DoesNotContain(service.Profiles, p => p.Name == "Moje");
    }

    [Fact]
    public void RefreshOptionsPersistInAppSettings()
    {
        var refresh = new RefreshState();
        using (new ProfileService(new ProfileStore(_root), new SettingsState(), refresh))
            refresh.Update(r => r with { Leds = true, IntervalMs = 500 });

        var loaded = new ProfileStore(_root).LoadAppSettings().Refresh;
        Assert.True(loaded.Leds);
        Assert.Equal(500, loaded.IntervalMs);
    }
}
