using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Device;

/// <summary>Independently updatable parts of the output report.</summary>
[Flags]
public enum OutputGroups
{
    None = 0,
    Lightbar = 1 << 0,
    /// <summary>Player LEDs and the shared player/mic LED brightness.</summary>
    PlayerLeds = 1 << 1,
    MicLed = 1 << 2,
    Rumble = 1 << 3,
    LeftTrigger = 1 << 4,
    RightTrigger = 1 << 5,
    MotorPower = 1 << 6,
    AudioRoute = 1 << 7,
    AudioVolume = 1 << 8,

    Leds = PlayerLeds | MicLed,
    Triggers = LeftTrigger | RightTrigger,
}

/// <summary>The values that end up on the wire, after layering live audio values over the profile.</summary>
internal sealed record ComposedOutput(
    RgbColor Lightbar,
    byte PlayerLedMask,
    LedBrightness LedBrightness,
    MicLedMode MicLed,
    byte RumbleHeavy,
    byte RumbleSoft,
    TriggerEffect LeftTrigger,
    TriggerEffect RightTrigger,
    byte RumbleAttenuation,
    byte TriggerAttenuation,
    AudioOutputRoute? AudioRoute,
    byte SpeakerVolume,
    byte HeadphoneVolume)
{
    public bool RumbleActive => RumbleHeavy > 0 || RumbleSoft > 0;
}

/// <summary>Pure decisions about what to send; the device writer only executes them.</summary>
internal static class OutputComposer
{
    public static ComposedOutput Compose(OutputSnapshot s, LiveOverlay live, bool headphonesConnected, TimeSpan effectTime = default)
    {
        // Audio haptics only work while no rumble-emulation bits are sent, so app rumble is muted then.
        bool rumbleAllowed = !live.AudioHapticsActive;
        return new ComposedOutput(
            Lightbar: LightbarEffects.Resolve(s, live.Lightbar, effectTime),
            PlayerLedMask: s.PlayerLeds.ToLedMask(),
            LedBrightness: s.LedBrightness,
            MicLed: s.MicLed,
            RumbleHeavy: rumbleAllowed ? Math.Max(s.RumbleHeavy, live.RumbleHeavy) : (byte)0,
            RumbleSoft: rumbleAllowed ? Math.Max(s.RumbleSoft, live.RumbleSoft) : (byte)0,
            LeftTrigger: s.LeftTrigger,
            RightTrigger: s.RightTrigger,
            RumbleAttenuation: s.RumbleAttenuation,
            TriggerAttenuation: s.TriggerAttenuation,
            AudioRoute: s.AudioControlEnabled ? OutputReportBuilder.ResolveRoute(s.AudioRoute, headphonesConnected) : null,
            SpeakerVolume: s.SpeakerVolume,
            HeadphoneVolume: s.HeadphoneVolume);
    }

    /// <summary>
    /// Groups whose values differ from the last sent report. With <paramref name="last"/> null
    /// (fresh connection) every group the app controls is included.
    /// </summary>
    public static OutputGroups Changes(ComposedOutput? last, ComposedOutput now)
    {
        var groups = OutputGroups.None;
        if (last is null)
        {
            groups = OutputGroups.Lightbar | OutputGroups.Leds;
            if (now.RumbleActive)
                groups |= OutputGroups.Rumble;
            if (now.LeftTrigger.Mode != TriggerMode.Game)
                groups |= OutputGroups.LeftTrigger;
            if (now.RightTrigger.Mode != TriggerMode.Game)
                groups |= OutputGroups.RightTrigger;
            if (now.RumbleAttenuation != 0 || now.TriggerAttenuation != 0)
                groups |= OutputGroups.MotorPower;
            if (now.AudioRoute is not null)
                groups |= OutputGroups.AudioRoute | OutputGroups.AudioVolume;
            return groups;
        }

        if (now.Lightbar != last.Lightbar)
            groups |= OutputGroups.Lightbar;
        if (now.PlayerLedMask != last.PlayerLedMask || now.LedBrightness != last.LedBrightness)
            groups |= OutputGroups.PlayerLeds;
        if (now.MicLed != last.MicLed)
            groups |= OutputGroups.MicLed;
        if (now.RumbleHeavy != last.RumbleHeavy || now.RumbleSoft != last.RumbleSoft)
            groups |= OutputGroups.Rumble;
        if (now.LeftTrigger != last.LeftTrigger)
            groups |= OutputGroups.LeftTrigger;
        if (now.RightTrigger != last.RightTrigger)
            groups |= OutputGroups.RightTrigger;
        if (now.RumbleAttenuation != last.RumbleAttenuation || now.TriggerAttenuation != last.TriggerAttenuation)
            groups |= OutputGroups.MotorPower;
        if (now.AudioRoute is not null && now.AudioRoute != last.AudioRoute)
            groups |= OutputGroups.AudioRoute;
        if (now.AudioRoute is not null && (last.AudioRoute is null
                || now.SpeakerVolume != last.SpeakerVolume || now.HeadphoneVolume != last.HeadphoneVolume))
            groups |= OutputGroups.AudioVolume;
        return groups;
    }

    /// <summary>Groups re-sent periodically so a game or Steam Input cannot permanently overwrite them.</summary>
    public static OutputGroups RefreshGroups(ComposedOutput now, RefreshOptions options)
    {
        var groups = OutputGroups.None;
        if (options.Lightbar)
            groups |= OutputGroups.Lightbar;
        if (options.Leds)
            groups |= OutputGroups.Leds;
        if (options.Triggers)
        {
            if (now.LeftTrigger.Mode != TriggerMode.Game)
                groups |= OutputGroups.LeftTrigger;
            if (now.RightTrigger.Mode != TriggerMode.Game)
                groups |= OutputGroups.RightTrigger;
        }
        if (now.RumbleActive)
            groups |= OutputGroups.Rumble;
        return groups;
    }

    /// <summary>
    /// A report without rumble bits ends rumble emulation, so while the app rumbles every report
    /// it sends has to carry the rumble values.
    /// </summary>
    public static OutputGroups KeepRumbleAlive(OutputGroups groups, ComposedOutput now) =>
        groups != OutputGroups.None && now.RumbleActive ? groups | OutputGroups.Rumble : groups;
}
