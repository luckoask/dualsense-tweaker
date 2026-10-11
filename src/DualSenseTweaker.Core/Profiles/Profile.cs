using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Profiles;

/// <summary>Custom is first so files without a kind (or from older versions) load as custom profiles.</summary>
public enum ProfileKind { Custom, Competitive, Adventure }

public sealed record Profile(string Name, ProfileKind Kind, ProfileSettings Settings)
{
    /// <summary>Competitive and Adventure always exist; they can be edited and reset, not deleted.</summary>
    public bool IsBuiltIn => Kind != ProfileKind.Custom;
    public string DisplayName => ProfilePresets.DisplayName(Name, Kind);
}

/// <summary>App-wide settings that do not belong to a profile.</summary>
public sealed record AppSettings
{
    public string? ActiveProfile { get; init; }
    public RefreshOptions Refresh { get; init; } = new();
    public AppPreferences Preferences { get; init; } = new();
}

public static class ProfilePresets
{
    public const string CompetitiveName = "Competitive";
    public const string AdventureName = "Adventure";
    public const string CustomName = "Custom";

    /// <summary>Localizes the default names for display while saved profile identifiers stay stable.</summary>
    public static string DisplayName(string name, ProfileKind kind) => (kind, name) switch
    {
        (ProfileKind.Competitive, CompetitiveName) => L.Text("Súťažný"),
        (ProfileKind.Adventure, AdventureName) => L.Text("Dobrodružný"),
        (ProfileKind.Custom, CustomName) => L.Text("Vlastný"),
        _ => name,
    };

    public static IReadOnlyList<Profile> Defaults =>
    [
        new(CompetitiveName, ProfileKind.Competitive, Competitive()),
        new(AdventureName, ProfileKind.Adventure, Adventure()),
        new(CustomName, ProfileKind.Custom, Custom()),
    ];

    public static ProfileSettings For(ProfileKind kind) => kind switch
    {
        ProfileKind.Competitive => Competitive(),
        ProfileKind.Adventure => Adventure(),
        _ => Custom(),
    };

    /// <summary>No distractions: lightbar and player LEDs off, no trigger resistance, no app rumble, game rumble weakened.</summary>
    public static ProfileSettings Competitive() => new ProfileSettings
    {
        Output = new OutputSnapshot
        {
            LightbarBrightness = 0,
            PlayerLeds = PlayerLedMode.Off,
            LedBrightness = LedBrightness.Low,
            MicLed = MicLedMode.Off,
            LeftTrigger = TriggerEffect.Off,
            RightTrigger = TriggerEffect.Off,
            RumbleAttenuation = OutputSnapshot.MaxAttenuation,
            TriggerAttenuation = OutputSnapshot.MaxAttenuation,
        },
        Reactive = new AudioReactiveSettings { Enabled = false },
        Haptics = new HapticsSettings { Enabled = false },
    }.Normalize();

    /// <summary>Immersion: resistance on both triggers, strong bass rumble and an audio-reactive lightbar.</summary>
    public static ProfileSettings Adventure() => new ProfileSettings
    {
        Output = new OutputSnapshot
        {
            LightbarColor = new RgbColor(255, 120, 0),
            LightbarBrightness = 100,
            PlayerLeds = PlayerLedMode.Player1,
            LeftTrigger = new TriggerEffect { Mode = TriggerMode.Resistance, StartPosition = 3, Strength = 4 },
            RightTrigger = new TriggerEffect { Mode = TriggerMode.Resistance, StartPosition = 2, Strength = 6 },
        },
        Reactive = new AudioReactiveSettings
        {
            Enabled = true,
            GainDb = 6,
            BassToRumble = true,
            RumbleTarget = RumbleTarget.Heavy,
            RumbleMax = 255,
            Lightbar = ReactiveLightbarMode.Spectrum,
            BeatFlash = true,
        },
        Haptics = new HapticsSettings { Enabled = false },
    }.Normalize();

    /// <summary>Neutral starting point: the app only sets the lightbar and LEDs, games keep triggers and rumble.</summary>
    public static ProfileSettings Custom() => new ProfileSettings().Normalize();
}
