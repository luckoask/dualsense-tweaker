using System.Buffers.Binary;
using System.Text;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public class Crc32Tests
{
    [Fact]
    public void MatchesStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void SeedIsHashedAsLeadingByte()
    {
        byte[] data = [0x31, 0x10, 0x20, 0x30];
        Assert.Equal(Crc32.Compute([0xA2, .. data]), Crc32.Compute(0xA2, data));
    }

    [Fact]
    public void DifferentSeedsGiveDifferentCrc() =>
        Assert.NotEqual(Crc32.Compute(0xA1, [1, 2, 3]), Crc32.Compute(0xA2, [1, 2, 3]));
}

public class TriggerEffectFactoryTests
{
    [Fact]
    public void OffIsModeFiveWithZeroParameters() =>
        Assert.Equal(new byte[] { 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, TriggerEffectFactory.Off());

    [Fact]
    public void ResistancePacksActiveZonesAndThreeBitForces()
    {
        // Zones 3–9 active (0x03F8); force 5 → value 4 (0b100) in each zone's 3 bits → 0x24924800.
        Assert.Equal(
            new byte[] { 0x21, 0xF8, 0x03, 0x00, 0x48, 0x92, 0x24, 0, 0, 0, 0 },
            TriggerEffectFactory.Resistance(3, 5));
    }

    [Fact]
    public void FullResistanceSetsAllThirtyForceBits() =>
        Assert.Equal(
            new byte[] { 0x21, 0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 0, 0 },
            TriggerEffectFactory.Resistance(0, 8));

    [Fact]
    public void SectionMarksStartAndEndZoneAndStoresStrengthMinusOne() =>
        Assert.Equal(
            new byte[] { 0x25, 0x44, 0x00, 0x04, 0, 0, 0, 0, 0, 0, 0 },
            TriggerEffectFactory.Section(2, 6, 5));

    [Fact]
    public void SectionEndZoneEightUsesHighByte() =>
        Assert.Equal(new byte[] { 0x25, 0x80, 0x01, 0x07, 0, 0, 0, 0, 0, 0, 0 }, TriggerEffectFactory.Section(7, 8, 8));

    [Fact]
    public void VibrationPutsFrequencyAtIndexNine() =>
        Assert.Equal(
            new byte[] { 0x26, 0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 40, 0 },
            TriggerEffectFactory.Vibration(0, 8, 40));

    [Fact]
    public void ZeroStrengthFallsBackToOff()
    {
        Assert.Equal(TriggerEffectFactory.Off(), TriggerEffectFactory.Resistance(4, 0));
        Assert.Equal(TriggerEffectFactory.Off(), TriggerEffectFactory.Section(3, 5, 0));
        Assert.Equal(TriggerEffectFactory.Off(), TriggerEffectFactory.Vibration(4, 0, 30));
        Assert.Equal(TriggerEffectFactory.Off(), TriggerEffectFactory.Vibration(4, 3, 0));
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 9)]
    public void ResistanceRejectsOutOfRange(int position, int strength) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffectFactory.Resistance(position, strength));

    [Theory]
    [InlineData(1, 5)]
    [InlineData(8, 9)]
    [InlineData(5, 5)]
    [InlineData(5, 9)]
    public void SectionRejectsInvalidZones(int start, int end) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffectFactory.Section(start, end, 4));

    [Fact]
    public void GameModeBuildsNothing() => Assert.Null(TriggerEffectFactory.Build(TriggerEffect.Game));

    [Fact]
    public void BuildClampsSettingsIntoValidRanges()
    {
        var effect = new TriggerEffect { Mode = TriggerMode.Section, StartPosition = 0, EndPosition = 0, Strength = 20 };
        // Start clamps to 2, end to start+1, strength to 8.
        Assert.Equal(TriggerEffectFactory.Section(2, 3, 8), TriggerEffectFactory.Build(effect));
    }
}

public class OutputReportBuilderTests
{
    private static readonly OutputSnapshot Sample = new()
    {
        LightbarColor = new RgbColor(10, 20, 30),
        PlayerLeds = PlayerLedMode.Player3,
        LedBrightness = LedBrightness.Low,
        MicLed = MicLedMode.Blink,
        RumbleHeavy = 200,
        RumbleSoft = 50,
        RightTrigger = new TriggerEffect { Mode = TriggerMode.Resistance, StartPosition = 3, Strength = 5 },
        LeftTrigger = new TriggerEffect { Mode = TriggerMode.Vibration, StartPosition = 0, Amplitude = 8, Frequency = 40 },
        RumbleAttenuation = 3,
        TriggerAttenuation = 5,
    };

    private static ComposedOutput Compose(OutputSnapshot s, LiveOverlay? live = null, bool headphones = false) =>
        OutputComposer.Compose(s.Normalize(), live ?? LiveOverlay.None, headphones);

    private static byte[] Usb(OutputSnapshot s, OutputGroups groups, bool v2 = false) =>
        OutputReportBuilder.Build(Compose(s), groups, ConnectionType.Usb, 0, v2);

    [Fact]
    public void UsbReportHasIdAndCommonBlockAtOffset1()
    {
        var report = Usb(Sample, OutputGroups.Lightbar | OutputGroups.Leds | OutputGroups.Rumble);

        Assert.Equal(48, report.Length);
        Assert.Equal(0x02, report[0]);
        Assert.Equal(0x01 | 0x02, report[1]);              // valid_flag0: compatible vibration + haptics select
        Assert.Equal(0x01 | 0x04 | 0x10, report[2]);       // valid_flag1: mic LED, lightbar, player LEDs
        Assert.Equal(50, report[1 + 2]);                   // motor_right = soft
        Assert.Equal(200, report[1 + 3]);                  // motor_left = heavy
        Assert.Equal(2, report[1 + 8]);                    // mute_button_led = pulse
        Assert.Equal(0x01, report[1 + 38]);                // valid_flag2: LED brightness
        Assert.Equal(2, report[1 + 42]);                   // led_brightness = low
        Assert.Equal(0b10101, report[1 + 43]);             // player 3
        Assert.Equal(new byte[] { 10, 20, 30 }, report[(1 + 44)..(1 + 47)]);
    }

    [Fact]
    public void OnlyRequestedGroupsGetFlags()
    {
        var report = Usb(Sample, OutputGroups.Lightbar);

        Assert.Equal(0, report[1]);
        Assert.Equal(0x04, report[2]);
        Assert.Equal(0, report[1 + 38]);
        Assert.Equal(0, report[1 + 3]);                    // no motor values either
        Assert.Equal(0, report[1 + 43]);
    }

    [Fact]
    public void VibrationV2UsesFlag2InsteadOfFlag0()
    {
        var report = Usb(Sample, OutputGroups.Rumble, v2: true);

        Assert.Equal(0x02, report[1]);                     // haptics select only
        Assert.Equal(0x04, report[1 + 38]);                // compatible vibration 2
    }

    [Fact]
    public void ZeroRumbleSetsNoRumbleBitsSoAudioHapticsWork()
    {
        var report = Usb(Sample with { RumbleHeavy = 0, RumbleSoft = 0 }, OutputGroups.Rumble, v2: true);

        Assert.Equal(0, report[1] & 0x03);
        Assert.Equal(0, report[1 + 38] & 0x04);
    }

    [Fact]
    public void TriggerBlocksLandAtTheirOffsetsWithFlags()
    {
        var report = Usb(Sample, OutputGroups.Triggers);

        Assert.Equal(0x04 | 0x08, report[1]);              // right + left trigger enable
        Assert.Equal(TriggerEffectFactory.Resistance(3, 5), report[(1 + 10)..(1 + 21)]);
        Assert.Equal(TriggerEffectFactory.Vibration(0, 8, 40), report[(1 + 21)..(1 + 32)]);
    }

    [Fact]
    public void SwitchingTriggerBackToGameSendsOff()
    {
        var report = Usb(Sample with { RightTrigger = TriggerEffect.Game }, OutputGroups.RightTrigger);

        Assert.Equal(0x04, report[1]);
        Assert.Equal(TriggerEffectFactory.Off(), report[(1 + 10)..(1 + 21)]);
    }

    [Fact]
    public void MotorPowerPacksRumbleLowAndTriggerHighNibble()
    {
        var report = Usb(Sample, OutputGroups.MotorPower);

        Assert.Equal(0x40, report[2]);                     // valid_flag1 bit 6
        Assert.Equal(0x53, report[1 + 36]);
    }

    [Fact]
    public void BluetoothReportHasSequenceTagAndCrc()
    {
        var report = OutputReportBuilder.Build(Compose(Sample), OutputGroups.Lightbar | OutputGroups.Triggers, ConnectionType.Bluetooth, 7, false);

        Assert.Equal(78, report.Length);
        Assert.Equal(0x31, report[0]);
        Assert.Equal(0x70, report[1]);                     // sequence in the high nibble
        Assert.Equal(0x10, report[2]);                     // tag
        Assert.Equal(0x04 | 0x08, report[3]);              // common block starts at 3
        Assert.Equal(0x21, report[3 + 10]);
        Assert.Equal(10, report[3 + 44]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74));
        Assert.Equal(Crc32.Compute(0xA2, report.AsSpan(0, 74)), crc);
    }

    [Fact]
    public void LightbarBrightnessScalesColor()
    {
        var snapshot = Sample with { LightbarColor = new RgbColor(200, 255, 0), LightbarBrightness = 50 };
        var report = Usb(snapshot, OutputGroups.Lightbar);

        Assert.Equal(new byte[] { 100, 128, 0 }, report[(1 + 44)..(1 + 47)]);
    }

    [Theory]
    [InlineData(PlayerLedMode.Off, 0b00000)]
    [InlineData(PlayerLedMode.Player1, 0b00100)]
    [InlineData(PlayerLedMode.Player2, 0b01010)]
    [InlineData(PlayerLedMode.Player3, 0b10101)]
    [InlineData(PlayerLedMode.Player4, 0b11011)]
    [InlineData(PlayerLedMode.All, 0b11111)]
    public void PlayerLedMasksMatchLinuxDriver(PlayerLedMode mode, byte mask) =>
        Assert.Equal(mask, mode.ToLedMask());

    [Fact]
    public void AutoRouteUsesSpeakerWithoutHeadphones()
    {
        var snapshot = Sample with { AudioControlEnabled = true, SpeakerVolume = 80, HeadphoneVolume = 90 };
        var report = OutputReportBuilder.Build(Compose(snapshot), OutputGroups.AudioRoute | OutputGroups.AudioVolume, ConnectionType.Usb, 0, false);

        Assert.Equal(0x10 | 0x20 | 0x80, report[1] & 0xF0); // headphone + speaker volume + audio control
        Assert.Equal(90, report[1 + 4]);
        Assert.Equal(80, report[1 + 5]);
        Assert.Equal(3 << 4, report[1 + 7]);                 // path 3 = speaker
        Assert.Equal(0x80, report[2] & 0x80);                // audio_control2 enabled
        Assert.Equal(0x02, report[1 + 37]);                  // +6 dB pre-amp
    }

    [Fact]
    public void AutoRouteUsesHeadphonesWhenPlugged()
    {
        var snapshot = Sample with { AudioControlEnabled = true };
        var report = OutputReportBuilder.Build(Compose(snapshot, headphones: true), OutputGroups.AudioRoute, ConnectionType.Usb, 0, false);

        Assert.Equal(0x80, report[1] & 0xF0);
        Assert.Equal(0, report[1 + 7]);
        Assert.Equal(0, report[2] & 0x80);
    }

    [Theory]
    [InlineData(ConnectionType.Usb, 1, 48)]
    [InlineData(ConnectionType.Bluetooth, 3, 78)]
    public void LightbarResetReleasesFirmwareLedControlWithoutFadingOutTheNextColor(ConnectionType connection, int offset, int size)
    {
        var report = OutputReportBuilder.BuildLightbarReset(connection, 7);

        Assert.Equal(size, report.Length);
        Assert.Equal(0x08, report[offset + 1]); // release LED state, as in SDL's PS5 driver
        Assert.Equal(0, report[offset]);
        Assert.Equal(0, report[offset + 38]);
        Assert.Equal(0, report[offset + 41]);
        Assert.Equal(0, report[offset + 43]); // no player or mic LED setting in the reset
        Assert.Equal(0, report[offset + 8]);
        if (connection == ConnectionType.Bluetooth)
        {
            Assert.Equal(0x70, report[1]);
            Assert.Equal(Crc32.Compute(0xA2, report.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74)));
        }
    }
}

public class LightbarInitializationTests
{
    [Theory]
    [InlineData(ConnectionType.Usb, 1)]
    [InlineData(ConnectionType.Bluetooth, 3)]
    public void TakeoverIsFollowedByTheLatestCustomColorAndExistingLedSettings(ConnectionType connection, int offset)
    {
        var initialization = new LightbarInitialization(connection);
        var settings = new OutputSnapshot { LightbarColor = new RgbColor(0, 0, 255) };
        var groups = OutputGroups.Lightbar | OutputGroups.Leds;

        // The user can edit the color while the controller is still connecting.
        settings = settings with { LightbarColor = new RgbColor(255, 40, 90), PlayerLeds = PlayerLedMode.Player3, MicLed = MicLedMode.On };
        initialization.ObserveSensorTimestamp(10_200_000);
        Assert.True(initialization.NeedsReset);
        var reset = OutputReportBuilder.BuildLightbarReset(connection, 0);
        Assert.Equal(0x08, reset[offset + 1]);
        initialization.MarkResetSent();
        var composed = OutputComposer.Compose(settings, LiveOverlay.None, false);
        var report = OutputReportBuilder.Build(composed, initialization.FilterGroups(groups), connection, 1, false);

        Assert.Equal(new byte[] { 255, 40, 90 }, report.AsSpan(offset + 44, 3).ToArray());
        Assert.Equal(0b10101, report[offset + 43]);
        Assert.Equal(1, report[offset + 8]);
        Assert.Equal(0x04 | 0x10 | 0x01, report[offset + 1]);
    }

    [Fact]
    public void BluetoothDefersLightbarButKeepsPlayerAndMicLedsWorking()
    {
        var initialization = new LightbarInitialization(ConnectionType.Bluetooth);
        var requested = OutputGroups.Lightbar | OutputGroups.Leds | OutputGroups.Rumble | OutputGroups.Triggers;

        Assert.False(initialization.NeedsReset);
        Assert.False(initialization.ObserveSensorTimestamp(10_199_999));
        Assert.Equal(requested & ~OutputGroups.Lightbar, initialization.FilterGroups(requested));

        Assert.True(initialization.ObserveSensorTimestamp(10_200_000));
        Assert.True(initialization.NeedsReset);
        Assert.Equal(requested & ~OutputGroups.Lightbar, initialization.FilterGroups(requested));
        initialization.MarkResetSent();
        Assert.False(initialization.NeedsReset);
        Assert.Equal(requested, initialization.FilterGroups(requested));
    }

    [Fact]
    public void FailedResetStaysPendingUntilAWriteSucceeds()
    {
        var initialization = new LightbarInitialization(ConnectionType.Bluetooth);
        initialization.ObserveSensorTimestamp(20_000_000);

        // A timed-out write must not call MarkResetSent, even when more input reports arrive.
        Assert.False(initialization.ObserveSensorTimestamp(20_012_000));
        Assert.True(initialization.NeedsReset);
        Assert.Equal(OutputGroups.None, initialization.FilterGroups(OutputGroups.Lightbar));

        initialization.MarkResetSent();
        Assert.Equal(OutputGroups.Lightbar, initialization.FilterGroups(OutputGroups.Lightbar));
    }

    [Fact]
    public void SensorTimestampWrapDoesNotUndoSuccessfulTakeover()
    {
        var initialization = new LightbarInitialization(ConnectionType.Bluetooth);
        initialization.ObserveSensorTimestamp(uint.MaxValue);
        initialization.MarkResetSent();

        Assert.False(initialization.ObserveSensorTimestamp(0));
        Assert.False(initialization.NeedsReset);
        Assert.Equal(OutputGroups.Lightbar, initialization.FilterGroups(OutputGroups.Lightbar));
    }

    [Fact]
    public void UsbTakesOverImmediatelyWithoutWaitingForSensorInput()
    {
        var initialization = new LightbarInitialization(ConnectionType.Usb);

        Assert.True(initialization.NeedsReset);
        initialization.MarkResetSent();
        Assert.Equal(OutputGroups.Lightbar | OutputGroups.Leds,
            initialization.FilterGroups(OutputGroups.Lightbar | OutputGroups.Leds));
    }
}

public class OutputComposerTests
{
    private static ComposedOutput Compose(OutputSnapshot s, LiveOverlay? live = null) =>
        OutputComposer.Compose(s.Normalize(), live ?? LiveOverlay.None, false);

    [Fact]
    public void FreshConnectionSendsOnlyWhatTheAppControls()
    {
        var groups = OutputComposer.Changes(null, Compose(new OutputSnapshot()));

        Assert.Equal(OutputGroups.Lightbar | OutputGroups.Leds, groups);
    }

    [Fact]
    public void FreshConnectionIncludesActiveTriggersRumbleAndAudio()
    {
        var snapshot = new OutputSnapshot
        {
            RumbleHeavy = 10,
            LeftTrigger = TriggerEffect.Off,
            RumbleAttenuation = 2,
            AudioControlEnabled = true,
        };
        var groups = OutputComposer.Changes(null, Compose(snapshot));

        Assert.True(groups.HasFlag(OutputGroups.Rumble));
        Assert.True(groups.HasFlag(OutputGroups.LeftTrigger));
        Assert.False(groups.HasFlag(OutputGroups.RightTrigger));
        Assert.True(groups.HasFlag(OutputGroups.MotorPower));
        Assert.True(groups.HasFlag(OutputGroups.AudioRoute | OutputGroups.AudioVolume));
    }

    [Fact]
    public void OnlyChangedGroupsAreSent()
    {
        var before = Compose(new OutputSnapshot());
        var after = Compose(new OutputSnapshot { LightbarColor = new RgbColor(1, 2, 3) });

        Assert.Equal(OutputGroups.Lightbar, OutputComposer.Changes(before, after));
        Assert.Equal(OutputGroups.None, OutputComposer.Changes(after, after));
    }

    [Fact]
    public void LiveLightbarOverridesProfileColor()
    {
        var composed = Compose(new OutputSnapshot(), new LiveOverlay { Lightbar = new RgbColor(9, 8, 7) });

        Assert.Equal(new RgbColor(9, 8, 7), composed.Lightbar);
    }

    [Fact]
    public void LiveRumbleTakesTheStrongerValue()
    {
        var composed = Compose(new OutputSnapshot { RumbleHeavy = 50, RumbleSoft = 100 }, new LiveOverlay { RumbleHeavy = 120, RumbleSoft = 10 });

        Assert.Equal((byte)120, composed.RumbleHeavy);
        Assert.Equal((byte)100, composed.RumbleSoft);
    }

    [Fact]
    public void AudioHapticsMuteAppRumble()
    {
        var composed = Compose(new OutputSnapshot { RumbleHeavy = 50 }, new LiveOverlay { RumbleHeavy = 200, AudioHapticsActive = true });

        Assert.False(composed.RumbleActive);
    }

    [Fact]
    public void RefreshFollowsOptionsAndSkipsGameTriggers()
    {
        var now = Compose(new OutputSnapshot { RightTrigger = TriggerEffect.Off });

        Assert.Equal(OutputGroups.Lightbar | OutputGroups.RightTrigger,
            OutputComposer.RefreshGroups(now, new RefreshOptions { Lightbar = true, Triggers = true, Leds = false }));
        Assert.Equal(OutputGroups.Leds,
            OutputComposer.RefreshGroups(now, new RefreshOptions { Lightbar = false, Triggers = false, Leds = true }));
    }

    [Fact]
    public void ActiveRumbleRidesAlongWithEveryReport()
    {
        var rumbling = Compose(new OutputSnapshot { RumbleSoft = 1 });
        var still = Compose(new OutputSnapshot());

        Assert.Equal(OutputGroups.Lightbar | OutputGroups.Rumble, OutputComposer.KeepRumbleAlive(OutputGroups.Lightbar, rumbling));
        Assert.Equal(OutputGroups.Lightbar, OutputComposer.KeepRumbleAlive(OutputGroups.Lightbar, still));
        Assert.Equal(OutputGroups.None, OutputComposer.KeepRumbleAlive(OutputGroups.None, rumbling));
    }
}

public class InputReportParserTests
{
    [Fact]
    public void ParsesUsbStatus()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1 + 52] = 0x15;                             // charging, level 5
        report[1 + 53] = 0x03;                             // headphones + mic

        Assert.Equal(InputParseResult.Ok, InputReportParser.TryParse(report, ConnectionType.Usb, out var status));
        Assert.Equal(new ControllerStatus(55, BatteryState.Charging, true, true), status);
    }

    [Fact]
    public void ParsesBluetoothReportWithValidCrc()
    {
        var report = BluetoothReport(status0: 0x08, status1: 0x01);

        Assert.Equal(InputParseResult.Ok, InputReportParser.TryParse(report, ConnectionType.Bluetooth, out var status));
        Assert.Equal(new ControllerStatus(85, BatteryState.Discharging, true, false), status);
    }

    [Fact]
    public void RejectsBluetoothReportWithBadCrc()
    {
        var report = BluetoothReport(status0: 0x08, status1: 0);
        report[10] ^= 0xFF;

        Assert.Equal(InputParseResult.CrcMismatch, InputReportParser.TryParse(report, ConnectionType.Bluetooth, out _));
    }

    [Fact]
    public void DetectsReducedBluetoothReport()
    {
        var report = new byte[78];
        report[0] = 0x01;

        Assert.Equal(InputParseResult.SimpleBluetoothReport, InputReportParser.TryParse(report, ConnectionType.Bluetooth, out _));
    }

    [Theory]
    [InlineData(0x0A, 100, BatteryState.Discharging)]
    [InlineData(0x00, 5, BatteryState.Discharging)]
    [InlineData(0x23, 100, BatteryState.Full)]
    [InlineData(0xB4, 0, BatteryState.NotCharging)]
    [InlineData(0xF4, 0, BatteryState.Unknown)]
    public void DecodesBatteryLikeLinux(byte status0, int percent, BatteryState state) =>
        Assert.Equal((percent, state), InputReportParser.DecodeBattery(status0));

    private static byte[] BluetoothReport(byte status0, byte status1)
    {
        var report = new byte[78];
        report[0] = 0x31;
        report[2 + 52] = status0;
        report[2 + 53] = status1;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc32.Compute(0xA1, report.AsSpan(0, 74)));
        return report;
    }
}

public class FeatureReportTests
{
    [Fact]
    public void ParsesMacAddressInDisplayOrder()
    {
        byte[] report = [0x09, 0x5A, 0x9A, 0xA3, 0x56, 0x27, 0x0C, 0, 0, 0];
        Assert.Equal("0C:27:56:A3:9A:5A", FeatureReports.ParseMacAddress(report));
    }

    [Fact]
    public void ParsesFirmwareInfo()
    {
        var report = new byte[64];
        report[0] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(28), 0x0110002A);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(44), 0x0224);

        var info = FirmwareInfo.Parse(report);

        Assert.NotNull(info);
        Assert.Equal("0x0110002A", info.FirmwareText);
        Assert.Equal("2.36", info.UpdateVersionText);
    }
}
