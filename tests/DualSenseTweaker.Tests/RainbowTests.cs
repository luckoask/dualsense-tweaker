using System.Buffers.Binary;
using System.Windows.Media;
using DualSenseTweaker.Core.Audio;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.ViewModels;

namespace DualSenseTweaker.Tests;

public class RainbowTests
{
    private static readonly OutputSnapshot Rainbow = new()
    {
        LightbarEffect = LightbarEffectMode.Rainbow,
        RainbowCycleSeconds = 6,
    };

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(1, 255, 255, 0)]
    [InlineData(2, 0, 255, 0)]
    [InlineData(3, 0, 255, 255)]
    [InlineData(4, 0, 0, 255)]
    [InlineData(5, 255, 0, 255)]
    [InlineData(6, 255, 0, 0)]
    public void FullCycleVisitsEveryHueAndLoops(int seconds, byte red, byte green, byte blue) =>
        Assert.Equal(new RgbColor(red, green, blue), LightbarEffects.Resolve(Rainbow, null, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void IntermediateColorsAreSmoothAcrossTheLoopAndLimitedToFiftyFramesPerSecond()
    {
        var previous = LightbarEffects.Resolve(Rainbow, null, TimeSpan.Zero);
        for (int milliseconds = 20; milliseconds <= 6020; milliseconds += 20)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            var next = LightbarEffects.Resolve(Rainbow, null, time);
            Assert.InRange(Math.Abs(next.R - previous.R), 0, 6);
            Assert.InRange(Math.Abs(next.G - previous.G), 0, 6);
            Assert.InRange(Math.Abs(next.B - previous.B), 0, 6);
            Assert.Equal(next, LightbarEffects.Resolve(Rainbow, null, time + TimeSpan.FromMilliseconds(19)));
            previous = next;
        }
        Assert.Equal(new RgbColor(255, 128, 0), LightbarEffects.Resolve(Rainbow, null, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void SpeedBrightnessAndDisabledEffectHaveTheExpectedOutput()
    {
        Assert.Equal(new RgbColor(0, 0, 255), LightbarEffects.Resolve(Rainbow with { RainbowCycleSeconds = 3 }, null, TimeSpan.FromSeconds(2)));
        Assert.Equal(new RgbColor(64, 64, 0), LightbarEffects.Resolve(Rainbow with { LightbarBrightness = 25 }, null, TimeSpan.FromSeconds(1)));
        Assert.Equal(RgbColor.Black, LightbarEffects.Resolve(Rainbow with { LightbarBrightness = 0 }, null, TimeSpan.FromSeconds(1)));
        var output = Rainbow with { LightbarEffect = LightbarEffectMode.Static, LightbarColor = new RgbColor(20, 40, 60) };
        Assert.Equal(output.EffectiveLightbar, LightbarEffects.Resolve(output, null, TimeSpan.FromSeconds(1)));
        Assert.Equal(RgbColor.White, LightbarEffects.Resolve(output, RgbColor.White, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ImportedEffectAndSpeedAreNormalized()
    {
        var output = new OutputSnapshot { LightbarEffect = (LightbarEffectMode)99, RainbowCycleSeconds = 0 }.Normalize();
        Assert.Equal(LightbarEffectMode.Static, output.LightbarEffect);
        Assert.Equal(OutputSnapshot.DefaultRainbowCycleSeconds, output.RainbowCycleSeconds);
        Assert.Equal(1, (output with { RainbowCycleSeconds = -1 }).Normalize().RainbowCycleSeconds);
        Assert.Equal(60, (output with { RainbowCycleSeconds = int.MaxValue }).Normalize().RainbowCycleSeconds);
    }

    [Theory]
    [InlineData(ConnectionType.Usb, 1)]
    [InlineData(ConnectionType.Bluetooth, 3)]
    public void RainbowReachesRealReportBytesWithoutChangingOtherOutputs(ConnectionType connection, int offset)
    {
        var output = Rainbow with { PlayerLeds = PlayerLedMode.Player3, MicLed = MicLedMode.On, LeftTrigger = TriggerEffect.Off };
        var live = new LiveOverlay { Lightbar = RgbColor.White, RumbleHeavy = 91 };
        var first = OutputComposer.Compose(output, live, false, TimeSpan.Zero);
        var next = OutputComposer.Compose(output, live, false, TimeSpan.FromSeconds(2));
        var groups = OutputComposer.Changes(first, next);
        Assert.Equal(OutputGroups.Lightbar, groups);
        Assert.Equal(first with { Lightbar = new RgbColor(0, 255, 0) }, next);
        groups = OutputComposer.KeepRumbleAlive(groups, next);
        var report = OutputReportBuilder.Build(next, groups, connection, 0, false);
        Assert.Equal(new byte[] { 0, 255, 0 }, report.AsSpan(offset + DualSenseProtocol.Common.LightbarRed, 3).ToArray());
        Assert.Equal((byte)91, report[offset + DualSenseProtocol.Common.MotorLeft]);
        Assert.Equal((byte)DualSenseProtocol.Flag1.LightbarControl, report[offset + DualSenseProtocol.Common.ValidFlag1]);
        if (connection == ConnectionType.Bluetooth)
            Assert.Equal(Crc32.Compute(DualSenseProtocol.CrcSeedOutput, report.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74)));
    }

    [Fact]
    public void PreviewMatchesHidColorWithoutModifyingOrSavingTheProfile()
    {
        var state = new SettingsState(new ProfileSettings { Output = Rainbow });
        var viewModel = new OutputViewModel(state);
        var original = state.Read();
        int events = 0;
        state.Changed += (_, _) => events++;
        for (int second = 0; second < 8; second++)
        {
            var time = TimeSpan.FromSeconds(second);
            viewModel.UpdateLive(RgbColor.White, time);
            var brush = Assert.IsType<SolidColorBrush>(viewModel.LiveLightbar);
            var onWire = OutputComposer.Compose(state.Current.Output, new LiveOverlay { Lightbar = RgbColor.White }, false, time).Lightbar;
            Assert.Equal(Color.FromRgb(onWire.R, onWire.G, onWire.B), brush.Color);
        }
        Assert.Equal(original, state.Read());
        Assert.Equal(0, events);
        Assert.False(viewModel.IsLightbarReactive);
    }

    [Fact]
    public void SelectingRainbowKeepsAudioRumbleAndStoredManualColor()
    {
        var state = new SettingsState(new ProfileSettings
        {
            Reactive = new AudioReactiveSettings { Enabled = true, BassToRumble = true, BeatFlash = true },
        });
        var stored = state.Current.Output.LightbarColor;
        var viewModel = new OutputViewModel(state) { LightbarEffect = LightbarEffectMode.Rainbow };
        Assert.True(viewModel.IsRainbow);
        Assert.True(state.Current.Reactive.Enabled);
        Assert.True(state.Current.Reactive.BassToRumble);
        Assert.Equal(ReactiveLightbarMode.Static, state.Current.Reactive.Lightbar);
        Assert.False(state.Current.Reactive.BeatFlash);
        Assert.Equal(stored, state.Current.Output.LightbarColor);
        viewModel.LightbarBrightness = 42;
        viewModel.RainbowCycleSeconds = 12;
        Assert.True(viewModel.IsRainbow);
        Assert.Equal(42, state.Current.Output.LightbarBrightness);
        Assert.Equal(12, state.Current.Output.RainbowCycleSeconds);
        viewModel.LightbarHex = "#112233";
        Assert.False(viewModel.IsRainbow);
        Assert.Equal(new RgbColor(17, 34, 51), state.Current.Output.LightbarColor);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("spectrum")]
    [InlineData("beat")]
    public void AudioLightControlsExplicitlyTakeOverFromRainbow(string action)
    {
        var state = new SettingsState(new ProfileSettings
        {
            Output = Rainbow,
            Reactive = new AudioReactiveSettings { Enabled = action != "start", Lightbar = ReactiveLightbarMode.Static, BeatFlash = false },
        });
        var live = new LiveOutput();
        using var manager = new DualSenseManager(new OutputSources(state, live, new RefreshState()));
        using var engine = new AudioEngine(state, live, manager); // never started: no hardware or audio capture
        var audio = new AudioViewModel(state, engine);
        switch (action)
        {
            case "start": audio.Enabled = true; Assert.Equal(ReactiveLightbarMode.Spectrum, state.Current.Reactive.Lightbar); break;
            case "spectrum": audio.LightbarMode = ReactiveLightbarMode.Spectrum; break;
            case "beat": audio.BeatFlash = true; break;
        }
        Assert.Equal(LightbarEffectMode.Static, state.Current.Output.LightbarEffect);
        Assert.True(state.Current.Reactive.Enabled);
        Assert.True(state.Current.Reactive.BassToRumble);
    }

    [Fact]
    public void ProfileExportImportPreservesRainbowWhileOldProfilesDefaultToStatic()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DualSenseRainbowTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "profile.json");
        try
        {
            var settings = new ProfileSettings { Output = Rainbow with { RainbowCycleSeconds = 13, LightbarBrightness = 71 } };
            ProfileStore.Export(new Profile("Rainbow", ProfileKind.Custom, settings), path);
            Assert.Equal(settings, ProfileStore.Import(path).Settings);
            File.WriteAllText(path, """{"format":2,"name":"Old","kind":"Custom","settings":{"output":{"lightbarColor":{"r":12,"g":34,"b":56}}}}""");
            var old = ProfileStore.Import(path).Settings.Output;
            Assert.Equal(LightbarEffectMode.Static, old.LightbarEffect);
            Assert.Equal(8, old.RainbowCycleSeconds);
            Assert.Equal(new RgbColor(12, 34, 56), old.LightbarColor);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
