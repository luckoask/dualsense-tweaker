namespace DualSenseTweaker.Core.State;

public enum ReactiveLightbarMode
{
    /// <summary>The lightbar keeps its static color.</summary>
    Static,
    /// <summary>Static color, brightness follows loudness.</summary>
    Loudness,
    /// <summary>Color mixes the bass/mid/high colors by band energy, brightness follows loudness.</summary>
    Spectrum,
}

public enum RumbleTarget { Heavy, Soft, Both }

/// <summary>How system audio drives the controller (WASAPI loopback → FFT → mapping).</summary>
public sealed record AudioReactiveSettings
{
    public bool Enabled { get; init; }

    // Signal conditioning
    /// <summary>Pre-amplification in dB (-20 … +30).</summary>
    public double GainDb { get; init; }
    /// <summary>0–1; higher narrows the dB window mapped to 0–1, so quieter audio reacts more.</summary>
    public double Sensitivity { get; init; } = 0.6;
    /// <summary>0–1 noise gate on the normalized band levels.</summary>
    public double Threshold { get; init; } = 0.08;
    public int AttackMs { get; init; } = 8;
    public int ReleaseMs { get; init; } = 160;

    // Mapping
    public bool BassToRumble { get; init; } = true;
    public RumbleTarget RumbleTarget { get; init; } = RumbleTarget.Heavy;
    /// <summary>Rumble value at full bass (0–255).</summary>
    public byte RumbleMax { get; init; } = 200;
    public ReactiveLightbarMode Lightbar { get; init; } = ReactiveLightbarMode.Spectrum;
    public RgbColor BassColor { get; init; } = new(255, 0, 40);
    public RgbColor MidColor { get; init; } = new(0, 255, 120);
    public RgbColor HighColor { get; init; } = new(40, 90, 255);
    /// <summary>Lightbar brightness (percent) when the audio is silent.</summary>
    public byte MinBrightness { get; init; } = 4;
    public bool BeatFlash { get; init; } = true;
    public RgbColor FlashColor { get; init; } = RgbColor.White;
    public int FlashMs { get; init; } = 120;

    /// <summary>Starting audio restores light mapping after a manual color disabled both light effects.</summary>
    public AudioReactiveSettings WithEnabled(bool enabled) => this with
    {
        Enabled = enabled,
        Lightbar = enabled && Lightbar == ReactiveLightbarMode.Static && !BeatFlash
            ? ReactiveLightbarMode.Spectrum
            : Lightbar,
    };

    public AudioReactiveSettings Normalize() => this with
    {
        GainDb = Math.Clamp(GainDb, -20, 30),
        Sensitivity = Math.Clamp(Sensitivity, 0, 1),
        Threshold = Math.Clamp(Threshold, 0, 0.95),
        AttackMs = Math.Clamp(AttackMs, 1, 500),
        ReleaseMs = Math.Clamp(ReleaseMs, 10, 2000),
        RumbleTarget = Enum.IsDefined(RumbleTarget) ? RumbleTarget : RumbleTarget.Heavy,
        Lightbar = Enum.IsDefined(Lightbar) ? Lightbar : ReactiveLightbarMode.Static,
        MinBrightness = Math.Min(MinBrightness, (byte)100),
        FlashMs = Math.Clamp(FlashMs, 20, 1000),
    };
}

/// <summary>"Audio → haptics": low-passed system audio played on the controller's haptic channels (USB only).</summary>
public sealed record HapticsSettings
{
    public bool Enabled { get; init; }
    /// <summary>Linear gain (0–3).</summary>
    public double Strength { get; init; } = 1.0;
    public int CutoffHz { get; init; } = 200;

    public HapticsSettings Normalize() => this with
    {
        Strength = Math.Clamp(Strength, 0, 3),
        CutoffHz = Math.Clamp(CutoffHz, 30, 1000),
    };
}

/// <summary>Everything a profile stores.</summary>
public sealed record ProfileSettings
{
    public OutputSnapshot Output { get; init; } = new();
    public AudioReactiveSettings Reactive { get; init; } = new();
    public HapticsSettings Haptics { get; init; } = new();
    public GyroSettings Gyro { get; init; } = new();

    public ProfileSettings Normalize() => this with
    {
        Output = (Output ?? new OutputSnapshot()).Normalize(),
        Reactive = (Reactive ?? new AudioReactiveSettings()).Normalize(),
        Haptics = (Haptics ?? new HapticsSettings()).Normalize(),
        Gyro = (Gyro ?? new GyroSettings()).Normalize(),
    };

    public ProfileSettings WithoutRumble() => this with { Output = Output.WithoutRumble() };
}

/// <summary>Which output groups the app re-sends periodically in case a game or Steam Input overwrote them.</summary>
public sealed record RefreshOptions
{
    public bool Lightbar { get; init; } = true;
    public bool Triggers { get; init; } = true;
    public bool Leds { get; init; }
    public int IntervalMs { get; init; } = 1000;

    public RefreshOptions Normalize() => this with { IntervalMs = Math.Clamp(IntervalMs, 100, 10_000) };
}
