using System.Globalization;

namespace DualSenseTweaker.Core.State;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => default;
    public static RgbColor White => new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Scales all channels by <paramref name="percent"/> (0–100).</summary>
    public RgbColor Scale(int percent) => new(Scale(R, percent), Scale(G, percent), Scale(B, percent));

    /// <summary>Linear blend towards <paramref name="other"/>; <paramref name="amount"/> 0 = this, 1 = other.</summary>
    public RgbColor Blend(RgbColor other, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return new RgbColor(Lerp(R, other.R, t), Lerp(G, other.G, t), Lerp(B, other.B, t));
    }

    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = new RgbColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static byte Scale(byte value, int percent) => (byte)((value * Math.Clamp(percent, 0, 100) + 50) / 100);

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}

public enum PlayerLedMode { Off, Player1, Player2, Player3, Player4, All }

/// <summary>Brightness of the player and mic LEDs (the lightbar has its own color scaling).</summary>
public enum LedBrightness : byte { High = 0, Medium = 1, Low = 2 }

public enum MicLedMode : byte { Off = 0, On = 1, Blink = 2 }

public enum LightbarEffectMode { Static, Rainbow }

/// <summary>Where the controller routes the USB audio stream.</summary>
public enum AudioOutputRoute { Auto, Headphones, Speaker, HeadphonesAndSpeaker }

/// <summary>
/// Static description of what the controller should show and do, as edited in the UI and stored
/// in profiles. Audio-reactive values are layered on top at runtime (see <see cref="LiveOutput"/>).
/// </summary>
public sealed record OutputSnapshot
{
    public const byte MaxLightbarBrightness = 100;
    public const int DefaultRainbowCycleSeconds = 8;
    public const byte MaxHeadphoneVolume = 0x7F;
    /// <summary>The controller ignores speaker volumes above 0x64 (same limit the PS5 uses).</summary>
    public const byte MaxSpeakerVolume = 0x64;
    public const byte MaxAttenuation = 7;

    public RgbColor LightbarColor { get; init; } = new(0, 80, 255);
    public LightbarEffectMode LightbarEffect { get; init; } = LightbarEffectMode.Static;
    /// <summary>Seconds for a complete rainbow cycle (red → yellow → green → cyan → blue → magenta).</summary>
    public int RainbowCycleSeconds { get; init; } = DefaultRainbowCycleSeconds;
    /// <summary>Lightbar brightness in percent, applied by scaling the color.</summary>
    public byte LightbarBrightness { get; init; } = MaxLightbarBrightness;
    public PlayerLedMode PlayerLeds { get; init; } = PlayerLedMode.Player1;
    public LedBrightness LedBrightness { get; init; } = LedBrightness.High;
    public MicLedMode MicLed { get; init; } = MicLedMode.Off;
    /// <summary>Left, strong low-frequency motor.</summary>
    public byte RumbleHeavy { get; init; }
    /// <summary>Right, weak high-frequency motor.</summary>
    public byte RumbleSoft { get; init; }
    /// <summary>Controller-side power reduction of the rumble motors (0 = none, 7 = most), applies to games too.</summary>
    public byte RumbleAttenuation { get; init; }
    /// <summary>Controller-side power reduction of trigger effects (0 = none, 7 = most).</summary>
    public byte TriggerAttenuation { get; init; }
    public TriggerEffect LeftTrigger { get; init; } = TriggerEffect.Game;
    public TriggerEffect RightTrigger { get; init; } = TriggerEffect.Game;
    /// <summary>When false, the controller's audio routing and volumes are left untouched.</summary>
    public bool AudioControlEnabled { get; init; }
    public AudioOutputRoute AudioRoute { get; init; } = AudioOutputRoute.Auto;
    public byte SpeakerVolume { get; init; } = MaxSpeakerVolume;
    public byte HeadphoneVolume { get; init; } = 100;

    /// <summary>Clamps values coming from the UI or from profile files into the ranges the controller accepts.</summary>
    public OutputSnapshot Normalize() => this with
    {
        LightbarBrightness = Math.Min(LightbarBrightness, MaxLightbarBrightness),
        LightbarEffect = Enum.IsDefined(LightbarEffect) ? LightbarEffect : LightbarEffectMode.Static,
        // Older profiles deserialize the absent numeric field as zero with source-generated JSON metadata.
        RainbowCycleSeconds = RainbowCycleSeconds == 0 ? DefaultRainbowCycleSeconds : Math.Clamp(RainbowCycleSeconds, 1, 60),
        PlayerLeds = Enum.IsDefined(PlayerLeds) ? PlayerLeds : PlayerLedMode.Player1,
        LedBrightness = Enum.IsDefined(LedBrightness) ? LedBrightness : LedBrightness.High,
        MicLed = Enum.IsDefined(MicLed) ? MicLed : MicLedMode.Off,
        RumbleAttenuation = Math.Min(RumbleAttenuation, MaxAttenuation),
        TriggerAttenuation = Math.Min(TriggerAttenuation, MaxAttenuation),
        LeftTrigger = (LeftTrigger ?? TriggerEffect.Game).Normalize(),
        RightTrigger = (RightTrigger ?? TriggerEffect.Game).Normalize(),
        AudioRoute = Enum.IsDefined(AudioRoute) ? AudioRoute : AudioOutputRoute.Auto,
        SpeakerVolume = Math.Min(SpeakerVolume, MaxSpeakerVolume),
        HeadphoneVolume = Math.Min(HeadphoneVolume, MaxHeadphoneVolume),
    };

    /// <summary>The settings worth persisting: manual rumble is a momentary action, not a preference.</summary>
    public OutputSnapshot WithoutRumble() => this with { RumbleHeavy = 0, RumbleSoft = 0 };

    public RgbColor EffectiveLightbar => LightbarColor.Scale(LightbarBrightness);
}

public static class PlayerLedModeExtensions
{
    /// <summary>
    /// Bitmask of the 5 player LEDs, centered like on the PS5 ("--x--" for player 1);
    /// same table as the Linux driver.
    /// </summary>
    public static byte ToLedMask(this PlayerLedMode mode) => mode switch
    {
        PlayerLedMode.Player1 => 0b00100,
        PlayerLedMode.Player2 => 0b01010,
        PlayerLedMode.Player3 => 0b10101,
        PlayerLedMode.Player4 => 0b11011,
        PlayerLedMode.All => 0b11111,
        _ => 0,
    };
}
