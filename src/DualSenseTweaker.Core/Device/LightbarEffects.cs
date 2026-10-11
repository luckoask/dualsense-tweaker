using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Device;

/// <summary>Time-based lightbar colors shared by HID output and the UI, without changing saved settings.</summary>
public static class LightbarEffects
{
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    // A shared monotonic clock keeps the preview and every connected controller in phase.
    public static TimeSpan CurrentTime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static RgbColor Resolve(OutputSnapshot output, RgbColor? audioColor, TimeSpan time)
    {
        if (output.LightbarEffect != LightbarEffectMode.Rainbow)
            return audioColor ?? output.EffectiveLightbar;

        int cycle = Math.Clamp(output.RainbowCycleSeconds, 1, 60);
        double seconds = Math.Max(0, time.Ticks / FrameInterval.Ticks * FrameInterval.TotalSeconds);
        double hue = seconds % cycle / cycle * 6;
        int segment = (int)hue;
        byte rising = (byte)Math.Round((hue - segment) * 255);
        byte falling = (byte)(255 - rising);
        var color = segment switch
        {
            0 => new RgbColor(255, rising, 0),
            1 => new RgbColor(falling, 255, 0),
            2 => new RgbColor(0, 255, rising),
            3 => new RgbColor(0, falling, 255),
            4 => new RgbColor(rising, 0, 255),
            _ => new RgbColor(255, 0, falling),
        };
        return color.Scale(output.LightbarBrightness);
    }
}
