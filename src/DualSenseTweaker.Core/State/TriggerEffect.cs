namespace DualSenseTweaker.Core.State;

public enum TriggerMode
{
    /// <summary>The app does not touch the trigger; games keep their own effects.</summary>
    Game,
    /// <summary>Actively switches any effect off (no resistance).</summary>
    Off,
    /// <summary>Continuous resistance from a start position ("feedback").</summary>
    Resistance,
    /// <summary>Resistance between two positions that snaps through like a trigger pull ("weapon").</summary>
    Section,
    /// <summary>Vibration from a start position.</summary>
    Vibration,
}

/// <summary>
/// One adaptive trigger setting. Positions are the 10 trigger zones used by the controller
/// (0 = released, 9 = fully pressed); strength and amplitude are 1–8.
/// </summary>
public sealed record TriggerEffect
{
    public const byte MaxZone = 9;
    public const byte MinSectionStart = 2;
    public const byte MaxSectionStart = 7;
    public const byte MaxSectionEnd = 8;
    public const byte MaxForce = 8;

    public TriggerMode Mode { get; init; } = TriggerMode.Game;
    public byte StartPosition { get; init; } = 2;
    public byte EndPosition { get; init; } = 6;
    public byte Strength { get; init; } = 5;
    public byte Amplitude { get; init; } = 4;
    /// <summary>Vibration frequency in Hz.</summary>
    public byte Frequency { get; init; } = 30;

    public static TriggerEffect Game { get; } = new();
    public static TriggerEffect Off { get; } = new() { Mode = TriggerMode.Off };

    /// <summary>Clamps parameters into the ranges the effect builders accept (see TriggerEffectFactory).</summary>
    public TriggerEffect Normalize()
    {
        byte start = Math.Min(StartPosition, MaxZone);
        byte sectionStart = Math.Clamp(StartPosition, MinSectionStart, MaxSectionStart);
        return this with
        {
            Mode = Enum.IsDefined(Mode) ? Mode : TriggerMode.Game,
            StartPosition = Mode == TriggerMode.Section ? sectionStart : start,
            EndPosition = Mode == TriggerMode.Section
                ? Math.Clamp(EndPosition, (byte)(sectionStart + 1), MaxSectionEnd)
                : Math.Min(EndPosition, MaxSectionEnd),
            Strength = Math.Clamp(Strength, (byte)1, MaxForce),
            Amplitude = Math.Clamp(Amplitude, (byte)1, MaxForce),
            Frequency = Math.Max(Frequency, (byte)1),
        };
    }
}
