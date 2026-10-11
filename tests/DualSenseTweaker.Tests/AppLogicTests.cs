using System.Buffers.Binary;
using DualSenseTweaker.Core.Audio;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public class RestoreReportTests
{
    [Fact]
    public void RestoreStopsMotorsReleasesTriggersAndResetsAttenuation()
    {
        var report = OutputReportBuilder.BuildRestore(ConnectionType.Usb, 0, useVibrationV2: false);

        Assert.Equal(0x01 | 0x02 | 0x04 | 0x08, report[1]);           // rumble v1 + haptics select + both triggers
        Assert.Equal(0, report[1 + 2] | report[1 + 3]);               // motors 0
        Assert.Equal(TriggerEffectFactory.Off(), report[(1 + 10)..(1 + 21)]);
        Assert.Equal(TriggerEffectFactory.Off(), report[(1 + 21)..(1 + 32)]);
        Assert.Equal(0x40, report[2]);                                // motor power flag, lightbar untouched
        Assert.Equal(0, report[1 + 36]);
    }

    [Fact]
    public void RestoreUsesVibrationV2FlagWhenSupported()
    {
        var report = OutputReportBuilder.BuildRestore(ConnectionType.Usb, 0, useVibrationV2: true);

        Assert.Equal(0x02 | 0x04 | 0x08, report[1]);
        Assert.Equal(0x04, report[1 + 38]);
    }

    [Fact]
    public void NeutralReportHasNoFlagsAndValidBluetoothCrc()
    {
        var report = OutputReportBuilder.BuildNeutral(ConnectionType.Bluetooth, 3);

        Assert.Equal(0x31, report[0]);
        Assert.All(report[3..(3 + 47)], b => Assert.Equal(0, b));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74));
        Assert.Equal(Crc32.Compute(0xA2, report.AsSpan(0, 74)), crc);
    }
}

public class PsButtonTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Presses (down then up) at the given times; returns the times at which the detector fired.</summary>
    private static List<int> Fire(PressSequenceDetector detector, params int[] pressTimes)
    {
        var fired = new List<int>();
        foreach (int t in pressTimes)
        {
            if (detector.Update(true, Ms(t)))
                fired.Add(t);
            detector.Update(true, Ms(t + 20));   // held over several reports
            detector.Update(false, Ms(t + 80));
        }
        return fired;
    }

    [Fact]
    public void FivequickPressesFireOnce() =>
        Assert.Equal([1600], Fire(new PressSequenceDetector(), 0, 400, 800, 1200, 1600));

    [Fact]
    public void SlowPressRestartsTheCount() =>
        Assert.Empty(Fire(new PressSequenceDetector(), 0, 400, 800, 2000, 2400, 2800));

    [Fact]
    public void CountStartsOverAfterFiring() =>
        Assert.Equal([1600, 3600], Fire(new PressSequenceDetector(), 0, 400, 800, 1200, 1600, 2000, 2400, 2800, 3200, 3600));

    [Fact]
    public void PsButtonIsReadFromButtons2Bit0()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1 + 9] = 0x01;

        InputReportParser.TryParse(report, ConnectionType.Usb, out _, out var frame);
        Assert.True(frame.PsButton);
        Assert.False(frame.TouchpadClick);

        report[1 + 9] = 0x02; // touchpad click, not PS
        InputReportParser.TryParse(report, ConnectionType.Usb, out _, out frame);
        Assert.False(frame.PsButton);
        Assert.True(frame.TouchpadClick);
    }
}

public class BatteryMonitorTests
{
    private static ControllerStatus Status(int percent, BatteryState state) => new(percent, state, false, false);

    [Fact]
    public void WarnsOnceAtLowAndOnceAtCritical()
    {
        var monitor = new BatteryMonitor();

        Assert.Equal(BatteryAlert.None, monitor.Update(Status(45, BatteryState.Discharging)));
        Assert.Equal(BatteryAlert.Low, monitor.Update(Status(15, BatteryState.Discharging)));
        Assert.Equal(BatteryAlert.None, monitor.Update(Status(15, BatteryState.Discharging)));
        Assert.Equal(BatteryAlert.Critical, monitor.Update(Status(5, BatteryState.Discharging)));
        Assert.Equal(BatteryAlert.None, monitor.Update(Status(5, BatteryState.Discharging)));
    }

    [Fact]
    public void ChargingRearms()
    {
        var monitor = new BatteryMonitor();
        monitor.Update(Status(15, BatteryState.Discharging));

        Assert.Equal(BatteryAlert.None, monitor.Update(Status(15, BatteryState.Charging)));
        Assert.Equal(BatteryAlert.Low, monitor.Update(Status(15, BatteryState.Discharging)));
    }

    [Fact]
    public void DisconnectDoesNotRearm()
    {
        var monitor = new BatteryMonitor();
        monitor.Update(Status(15, BatteryState.Discharging));

        Assert.Equal(BatteryAlert.None, monitor.Update(null));
        Assert.Equal(BatteryAlert.None, monitor.Update(Status(15, BatteryState.Discharging)));
    }
}

public sealed class GameProfileWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseTweakerTests", Guid.NewGuid().ToString("N"));
    private readonly PreferencesState _preferences = new();
    private readonly ProfileService _profiles;
    private readonly GameProfileWatcher _watcher;

    public GameProfileWatcherTests()
    {
        _profiles = new ProfileService(new ProfileStore(_root), new SettingsState(), new RefreshState(), _preferences);
        _profiles.Activate("Custom");
        _preferences.Update(p => p with { GameRules = [new GameRule("C:\\Games\\EldenRing.exe", "Adventure"), new GameRule("cs2.exe", "Competitive")] });
        _watcher = new GameProfileWatcher(_profiles, _preferences, () => new HashSet<string>());
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _profiles.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static HashSet<string> Running(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SwitchesWhileGameRunsAndBackAfterwards()
    {
        _watcher.Evaluate(Running("explorer", "eldenring"));
        Assert.Equal("Adventure", _profiles.Active.Name);

        _watcher.Evaluate(Running("explorer"));
        Assert.Equal("Custom", _profiles.Active.Name);
    }

    [Fact]
    public void ManualSwitchDuringGameIsRespected()
    {
        _watcher.Evaluate(Running("eldenring"));
        _profiles.Activate("Competitive");

        _watcher.Evaluate(Running("eldenring"));
        Assert.Equal("Competitive", _profiles.Active.Name);

        _watcher.Evaluate(Running());
        Assert.Equal("Competitive", _profiles.Active.Name); // user moved away, not switched back
    }

    [Fact]
    public void FirstRuleWinsAndDisabledDoesNothing()
    {
        _watcher.Evaluate(Running("cs2", "eldenring"));
        Assert.Equal("Adventure", _profiles.Active.Name);

        _watcher.Evaluate(Running());
        _preferences.Update(p => p with { AutoProfileEnabled = false });
        _watcher.Evaluate(Running("cs2"));
        Assert.Equal("Custom", _profiles.Active.Name);
    }

    [Theory]
    [InlineData("C:\\Games\\EldenRing.exe", "eldenring")]
    [InlineData("\"cs2.exe\"", "cs2")]
    [InlineData("Game", "game")]
    public void ExeNamesNormalizeToProcessNames(string exe, string key) =>
        Assert.Equal(key, GameRule.NormalizeExe(exe));
}

public sealed class PreferencesPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseTweakerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void PreferencesRoundTripThroughSettingsJson()
    {
        var preferences = new PreferencesState();
        using (new ProfileService(new ProfileStore(_root), new SettingsState(), new RefreshState(), preferences))
        {
            preferences.Update(p => p with
            {
                CloseToTray = false,
                HotkeyModifiers = HotkeyModifiers.CtrlShift,
                GameRules = [new GameRule("game.exe", "Adventure")],
            });
        }

        var loaded = new ProfileStore(_root).LoadAppSettings().Preferences;
        Assert.False(loaded.CloseToTray);
        Assert.Equal(HotkeyModifiers.CtrlShift, loaded.HotkeyModifiers);
        Assert.Equal([new GameRule("game.exe", "Adventure")], loaded.GameRules);
    }

    [Fact]
    public void UnchangedPreferencesAreNotAChange()
    {
        var state = new PreferencesState();
        Assert.False(state.Update(p => p with { CloseToTray = p.CloseToTray }));
    }
}

public class SpectrumTests
{
    [Fact]
    public void BandsCoverTheRangeInOrder()
    {
        var bins = AudioAnalyzer.SpectrumBins(48_000);

        Assert.Equal(AudioAnalyzer.SpectrumBands, bins.Length);
        Assert.All(bins, b => Assert.True(b.First >= 1 && b.Last >= b.First));
        for (int i = 1; i < bins.Length; i++)
            Assert.True(bins[i].First >= bins[i - 1].First);
        Assert.True(bins[^1].Last <= AudioAnalyzer.FftSize / 2);
    }

    [Fact]
    public void ToneShowsUpInItsBand()
    {
        var analyzer = new AudioAnalyzer();
        var settings = new AudioReactiveSettings { AttackMs = 1, Threshold = 0.05 };
        AudioFeatures features = AudioFeatures.Silent;
        for (int p = 0; p < 20; p++)
        {
            var packet = Enumerable.Range(p * 480, 480).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48_000.0))).ToArray();
            features = analyzer.Process(packet, 48_000, settings);
        }

        int loudest = features.Spectrum.Select((v, i) => (v, i)).MaxBy(x => x.v).i;
        var (first, last) = AudioAnalyzer.SpectrumBins(48_000)[loudest];
        double binHz = 48_000.0 / AudioAnalyzer.FftSize;
        Assert.InRange(1000, (first - 1) * binHz, (last + 1) * binHz);
        Assert.True(features.Spectrum[0] < 0.1);
    }
}
