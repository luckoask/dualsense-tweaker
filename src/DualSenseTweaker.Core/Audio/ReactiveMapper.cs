using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Audio;

/// <summary>Turns analyzed audio into live lightbar/rumble values according to the profile mapping.</summary>
public sealed class ReactiveMapper
{
    private double _flash;
    private RgbColor _lastSpectrumColor;
    private bool _hasSpectrumColor;

    /// <param name="lightbarReactive">Whether the lightbar follows audio (the reactive mode is enabled).</param>
    /// <param name="rumbleMax">Rumble at full bass; 0 disables bass → rumble.</param>
    /// <param name="dt">Seconds since the previous call (drives the beat flash decay).</param>
    public LiveOverlay Map(AudioFeatures f, AudioReactiveSettings r, OutputSnapshot output,
        bool lightbarReactive, byte rumbleMax, bool audioHapticsActive, double dt)
    {
        RgbColor? lightbar = null;
        if (lightbarReactive)
        {
            lightbar = MapLightbar(f, r, output);

            if (r.BeatFlash && f.Beat)
                _flash = 1;
            else
                _flash = Math.Max(0, _flash - dt * 1000 / r.FlashMs);

            if (_flash > 0)
            {
                var baseColor = lightbar ?? output.EffectiveLightbar;
                lightbar = baseColor.Blend(r.FlashColor.Scale(Math.Max((int)output.LightbarBrightness, 1)), _flash);
            }
        }

        byte rumble = (byte)Math.Round(Math.Clamp(f.Bass, 0, 1) * rumbleMax);
        return new LiveOverlay
        {
            Lightbar = lightbar,
            RumbleHeavy = r.RumbleTarget is RumbleTarget.Heavy or RumbleTarget.Both ? rumble : (byte)0,
            RumbleSoft = r.RumbleTarget is RumbleTarget.Soft or RumbleTarget.Both ? rumble : (byte)0,
            AudioHapticsActive = audioHapticsActive,
        };
    }

    private RgbColor? MapLightbar(AudioFeatures f, AudioReactiveSettings r, OutputSnapshot output)
    {
        // The profile brightness is the ceiling; MinBrightness is shown during silence.
        int max = Math.Max((int)output.LightbarBrightness, r.MinBrightness);
        int Brightness(double level) => (int)Math.Round(r.MinBrightness + (max - r.MinBrightness) * Math.Clamp(level, 0, 1));

        switch (r.Lightbar)
        {
            case ReactiveLightbarMode.Loudness:
                return output.LightbarColor.Scale(Brightness(f.Loudness));

            case ReactiveLightbarMode.Spectrum:
                // Keep the last hue through silence instead of jumping to a fixed color.
                if (f.Bass + f.Mid + f.High > 0.02)
                {
                    _lastSpectrumColor = MixBands(f, r);
                    _hasSpectrumColor = true;
                }
                var color = _hasSpectrumColor ? _lastSpectrumColor : r.BassColor;
                return color.Scale(Brightness(Math.Max(f.Loudness, Math.Max(f.Bass, Math.Max(f.Mid, f.High)))));

            default:
                return null;
        }
    }

    /// <summary>Band colors weighted by band level, so the dominant band sets the hue.</summary>
    internal static RgbColor MixBands(AudioFeatures f, AudioReactiveSettings r)
    {
        double total = f.Bass + f.Mid + f.High;
        byte Channel(byte bass, byte mid, byte high) =>
            (byte)Math.Round((bass * f.Bass + mid * f.Mid + high * f.High) / total);
        return new RgbColor(
            Channel(r.BassColor.R, r.MidColor.R, r.HighColor.R),
            Channel(r.BassColor.G, r.MidColor.G, r.HighColor.G),
            Channel(r.BassColor.B, r.MidColor.B, r.HighColor.B));
    }
}
