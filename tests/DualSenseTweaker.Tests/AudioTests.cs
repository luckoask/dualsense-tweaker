using DualSenseTweaker.Core.Audio;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public class SpectrumAnalyzerTests
{
    [Fact]
    public void FullScaleSineAtBinCenterReadsAboutOne()
    {
        const int size = 1024;
        var spectrum = new SpectrumAnalyzer(size);
        var samples = Enumerable.Range(0, size).Select(i => (float)Math.Sin(2 * Math.PI * 32 * i / size)).ToArray();
        var magnitudes = new float[size / 2 + 1];

        spectrum.Compute(samples, magnitudes);

        Assert.InRange(magnitudes[32], 0.95f, 1.05f);
        Assert.True(magnitudes[100] < 0.01f);
    }
}

public class AudioAnalyzerTests
{
    private const int Rate = 48_000;
    private static readonly AudioReactiveSettings Settings = new() { AttackMs = 1, ReleaseMs = 50, Threshold = 0.05 };

    private static float[] Sine(double hz, double amplitude, int samples, int offset = 0) =>
        [.. Enumerable.Range(offset, samples).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate)))];

    private static AudioFeatures Feed(AudioAnalyzer analyzer, double hz, double amplitude, int packets)
    {
        AudioFeatures features = AudioFeatures.Silent;
        for (int p = 0; p < packets; p++)
            features = analyzer.Process(Sine(hz, amplitude, 480, p * 480), Rate, Settings);
        return features;
    }

    [Fact]
    public void BassToneLightsUpBassBand()
    {
        var features = Feed(new AudioAnalyzer(), 80, 0.5, 20);

        Assert.True(features.Bass > 0.6, $"bass {features.Bass}");
        Assert.True(features.High < 0.2, $"high {features.High}");
    }

    [Fact]
    public void HighToneLightsUpHighBand()
    {
        var features = Feed(new AudioAnalyzer(), 6000, 0.5, 20);

        Assert.True(features.High > 0.6, $"high {features.High}");
        Assert.True(features.Bass < 0.2, $"bass {features.Bass}");
    }

    [Fact]
    public void GainRaisesQuietSignals()
    {
        var quiet = Feed(new AudioAnalyzer(), 80, 0.01, 20);
        var analyzer = new AudioAnalyzer();
        AudioFeatures boosted = AudioFeatures.Silent;
        for (int p = 0; p < 20; p++)
            boosted = analyzer.Process(Sine(80, 0.01, 480, p * 480), Rate, Settings with { GainDb = 20 });

        Assert.True(boosted.Bass > quiet.Bass + 0.2, $"{boosted.Bass} vs {quiet.Bass}");
    }

    [Fact]
    public void SilenceReleasesLevels()
    {
        var analyzer = new AudioAnalyzer();
        Feed(analyzer, 80, 0.5, 20);

        var features = analyzer.ProcessSilence(0.5, Rate, Settings);

        Assert.True(features.Bass < 0.01, $"bass {features.Bass}");
    }

    [Fact]
    public void KickAfterQuietIsABeat()
    {
        var analyzer = new AudioAnalyzer();
        Feed(analyzer, 80, 0.02, 100); // ~1 s of quiet bass establishes the average

        bool beat = false;
        for (int p = 0; p < 3 && !beat; p++)
            beat = analyzer.Process(Sine(80, 0.8, 480, p * 480), Rate, Settings).Beat;

        Assert.True(beat);
    }

    [Fact]
    public void SteadyToneIsNotABeat()
    {
        var analyzer = new AudioAnalyzer();
        Feed(analyzer, 80, 0.5, 100);

        Assert.False(Feed(analyzer, 80, 0.5, 10).Beat);
    }

    [Theory]
    [InlineData(0.0, 0.0)]   // -inf dB
    [InlineData(1.0, 1.0)]   // 0 dBFS is the top of the window
    public void NormalizeMapsTheDbWindow(double level, double expected) =>
        Assert.Equal(expected, AudioAnalyzer.Normalize(level, new AudioReactiveSettings { Threshold = 0 }), 3);
}

public class ReactiveMapperTests
{
    private static readonly OutputSnapshot Output = new() { LightbarColor = new RgbColor(0, 0, 255), LightbarBrightness = 100 };

    [Theory]
    [InlineData(ConnectionType.Usb, 1)]
    [InlineData(ConnectionType.Bluetooth, 3)]
    public void StartingAudioAfterCustomColorSendsChangingLightbarColors(ConnectionType connection, int offset)
    {
        var staticColor = new RgbColor(5, 55, 158);
        var output = Output with { LightbarColor = staticColor };
        var manuallySelected = new AudioReactiveSettings
        {
            Enabled = false, Lightbar = ReactiveLightbarMode.Static, BeatFlash = false,
            BassColor = new RgbColor(255, 0, 0), MidColor = new RgbColor(0, 255, 0), MinBrightness = 0,
        };
        var reactive = manuallySelected.WithEnabled(true);
        var mapper = new ReactiveMapper();
        var bass = mapper.Map(new AudioFeatures(1, 0, 0, 1, false), reactive, output, reactive.Enabled, 0, false, 0.01);
        var mid = mapper.Map(new AudioFeatures(0, 1, 0, 1, false), reactive, output, reactive.Enabled, 0, false, 0.01);
        var first = OutputComposer.Compose(output, bass, false);
        var second = OutputComposer.Compose(output, mid, false);
        var report = OutputReportBuilder.Build(second, OutputComposer.Changes(first, second), connection, 0, false);

        Assert.Equal(ReactiveLightbarMode.Spectrum, reactive.Lightbar);
        Assert.Equal(new RgbColor(255, 0, 0), first.Lightbar);
        Assert.Equal(new RgbColor(0, 255, 0), second.Lightbar);
        Assert.Equal(0x04, report[offset + 1]);
        Assert.Equal(new byte[] { 0, 255, 0 }, report.AsSpan(offset + 44, 3).ToArray());
        Assert.Equal(staticColor, output.LightbarColor); // the custom color remains available when audio stops
    }

    [Fact]
    public void AudioStartKeepsExistingLoudnessAndBeatOnlyMappings()
    {
        var loudness = new AudioReactiveSettings { Lightbar = ReactiveLightbarMode.Loudness, BeatFlash = false };
        var beatOnly = new AudioReactiveSettings { Lightbar = ReactiveLightbarMode.Static, BeatFlash = true };

        Assert.Equal(ReactiveLightbarMode.Loudness, loudness.WithEnabled(true).Lightbar);
        Assert.Equal(ReactiveLightbarMode.Static, beatOnly.WithEnabled(true).Lightbar);
        Assert.True(beatOnly.WithEnabled(true).BeatFlash);
        Assert.Equal(loudness with { Enabled = false }, loudness.WithEnabled(false));
    }

    [Fact]
    public void SpectrumColorFollowsDominantBand()
    {
        var reactive = new AudioReactiveSettings { BassColor = new RgbColor(255, 0, 0), MidColor = new RgbColor(0, 255, 0), HighColor = new RgbColor(0, 0, 255) };

        var color = ReactiveMapper.MixBands(new AudioFeatures(1, 0, 0, 1, false), reactive);
        var mixed = ReactiveMapper.MixBands(new AudioFeatures(0.5, 0.5, 0, 1, false), reactive);

        Assert.Equal(new RgbColor(255, 0, 0), color);
        Assert.Equal(new RgbColor(128, 128, 0), mixed);
    }

    [Fact]
    public void LoudnessModeScalesStaticColorBetweenMinAndProfileBrightness()
    {
        var reactive = new AudioReactiveSettings { Lightbar = ReactiveLightbarMode.Loudness, MinBrightness = 10, BeatFlash = false };
        var mapper = new ReactiveMapper();

        var silent = mapper.Map(AudioFeatures.Silent, reactive, Output, true, 0, false, 0.01);
        var loud = mapper.Map(new AudioFeatures(0, 0, 0, 1, false), reactive, Output, true, 0, false, 0.01);

        Assert.Equal(new RgbColor(0, 0, 26), silent.Lightbar);
        Assert.Equal(new RgbColor(0, 0, 255), loud.Lightbar);
    }

    [Fact]
    public void BassDrivesTheChosenMotor()
    {
        var mapper = new ReactiveMapper();
        var features = new AudioFeatures(0.5, 0, 0, 0, false);

        var heavy = mapper.Map(features, new AudioReactiveSettings { RumbleTarget = RumbleTarget.Heavy }, Output, false, 200, false, 0.01);
        var both = mapper.Map(features, new AudioReactiveSettings { RumbleTarget = RumbleTarget.Both }, Output, false, 200, false, 0.01);

        Assert.Equal((100, 0), ((int)heavy.RumbleHeavy, (int)heavy.RumbleSoft));
        Assert.Equal((100, 100), ((int)both.RumbleHeavy, (int)both.RumbleSoft));
        Assert.Null(heavy.Lightbar); // lightbar not reactive
    }

    [Fact]
    public void BeatFlashesAndDecays()
    {
        var reactive = new AudioReactiveSettings { Lightbar = ReactiveLightbarMode.Static, BeatFlash = true, FlashColor = RgbColor.White, FlashMs = 100 };
        var mapper = new ReactiveMapper();

        var flash = mapper.Map(new AudioFeatures(1, 0, 0, 1, true), reactive, Output, true, 0, false, 0.01);
        var later = mapper.Map(new AudioFeatures(1, 0, 0, 1, false), reactive, Output, true, 0, false, 0.2);

        Assert.Equal(RgbColor.White, flash.Lightbar);
        Assert.Null(later.Lightbar); // flash over: back to the profile color
    }
}

public class HapticsBufferTests
{
    [Fact]
    public void PassesLowFrequenciesAndBlocksHighOnes()
    {
        var buffer = new HapticsBuffer(targetLatencyMs: 5, maxLatencyMs: 500);
        const int rate = 48_000;
        float[] Tone(double hz) => [.. Enumerable.Range(0, rate / 10).SelectMany(i => Enumerable.Repeat((float)Math.Sin(2 * Math.PI * hz * i / rate), 2))];

        double Rms(double hz)
        {
            var fresh = new HapticsBuffer(5, 500);
            fresh.Write(Tone(hz), 2, rate, gain: 1, cutoffHz: 150);
            var left = new float[rate / 20];
            var right = new float[rate / 20];
            fresh.Read(left, right, rate);
            return Math.Sqrt(left.Skip(left.Length / 2).Average(v => v * v));
        }

        Assert.True(Rms(60) > 0.5, $"60 Hz rms {Rms(60)}");
        Assert.True(Rms(2000) < 0.02, $"2 kHz rms {Rms(2000)}");
        Assert.Equal(0, buffer.BufferedFrames);
    }

    [Fact]
    public void LatencyStaysBounded()
    {
        var buffer = new HapticsBuffer(targetLatencyMs: 15, maxLatencyMs: 60);
        var second = new float[48_000 * 2];

        buffer.Write(second, 2, 48_000, 1, 200);

        Assert.InRange(buffer.BufferedFrames, 0, 48_000 * 60 / 1000);
    }

    [Fact]
    public void UnderrunPlaysSilence()
    {
        var buffer = new HapticsBuffer();
        var left = new float[64];
        var right = new float[64];
        Array.Fill(left, 1f);

        buffer.Read(left, right, 48_000);

        Assert.All(left, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ResamplesToTheOutputRate()
    {
        var buffer = new HapticsBuffer(targetLatencyMs: 1, maxLatencyMs: 1000);
        buffer.Write(new float[44_100 / 10 * 2], 2, 44_100, 1, 200);  // 100 ms at 44.1 kHz
        int before = buffer.BufferedFrames;

        buffer.Read(new float[480], new float[480], 48_000);            // 10 ms at 48 kHz

        Assert.InRange(before - buffer.BufferedFrames, 439, 443);         // ≈ 441 source frames consumed
    }
}
