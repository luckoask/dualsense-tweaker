using System.Buffers.Binary;
using System.Numerics;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Motion;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public class MotionParsingTests
{
    [Fact]
    public void ParsesButtonsSticksTriggersAndMotion()
    {
        var report = new byte[64];
        report[0] = 0x01;
        // Common block starts at 1 on USB.
        report[1] = 10; report[2] = 20; report[3] = 30; report[4] = 40; // sticks
        report[5] = 200; report[6] = 50;                                  // L2, R2
        report[8] = 0x20 | 2;                                             // ✕ + hat "right"
        report[9] = 0x02 | 0x40;                                          // R1 + L3
        report[10] = 0x01 | 0x10;                                         // PS + Edge Fn left
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(16), 100);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(18), -200);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(20), 300);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(22), 1);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(24), 8192);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(26), -3);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(28), 0x01020304);

        Assert.Equal(InputParseResult.Ok, InputReportParser.TryParse(report, ConnectionType.Usb, out _, out var frame));

        Assert.Equal(ControllerButtons.DpadRight | ControllerButtons.Cross | ControllerButtons.R1 | ControllerButtons.L3
                     | ControllerButtons.PS | ControllerButtons.FnLeft, frame.Buttons);
        Assert.True(frame.PsButton);
        Assert.Equal(new AnalogState(10, 20, 30, 40, 200, 50), frame.Analog);
        Assert.Equal(new MotionSample(100, -200, 300, 1, 8192, -3, 0x01020304), frame.Motion);
    }

    [Theory]
    [InlineData(0, ControllerButtons.DpadUp)]
    [InlineData(1, ControllerButtons.DpadUp | ControllerButtons.DpadRight)]
    [InlineData(3, ControllerButtons.DpadDown | ControllerButtons.DpadRight)]
    [InlineData(5, ControllerButtons.DpadDown | ControllerButtons.DpadLeft)]
    [InlineData(7, ControllerButtons.DpadUp | ControllerButtons.DpadLeft)]
    [InlineData(8, ControllerButtons.None)]
    public void DecodesHatSwitch(byte hat, ControllerButtons expected) =>
        Assert.Equal(expected, InputReportParser.ParseButtons(hat, 0, 0));

    [Fact]
    public void FaceButtonsAndShoulders()
    {
        var buttons = InputReportParser.ParseButtons(0xF8, 0xFF, 0x04);
        Assert.Equal(ControllerButtons.Square | ControllerButtons.Cross | ControllerButtons.Circle | ControllerButtons.Triangle
                     | ControllerButtons.L1 | ControllerButtons.R1 | ControllerButtons.L2 | ControllerButtons.R2
                     | ControllerButtons.Create | ControllerButtons.Options | ControllerButtons.L3 | ControllerButtons.R3
                     | ControllerButtons.Mute, buttons);
    }
}

public class MotionCalibrationTests
{
    /// <summary>Feature report 0x05 as read from a real DualSense (BT, firmware 0x010D0002).</summary>
    private static readonly short[] RealCalibration =
        [-15, -2, 8, 8670, -8712, 8856, -8859, 8846, -8831, 540, 540, 8338, -8208, 8102, -8373, 8147, -8239];

    internal static byte[] Report(short[] values)
    {
        var report = new byte[41];
        report[0] = 0x05;
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(1 + i * 2), values[i]);
        return report;
    }

    [Fact]
    public void RealControllerAtRestReadsOneGAndNoRotation()
    {
        var calibration = MotionCalibration.Parse(Report(RealCalibration));
        Assert.NotNull(calibration);
        Assert.True(calibration.IsFactory);
        Assert.InRange(calibration.GyroScale.X, 0.060f, 0.064f); // ≈ 1/16 °/s per unit

        // Averages measured with that controller lying on a desk.
        var rest = new MotionSample(-13, 1, 6, -83, 7997, 1291, 0);
        Assert.InRange(calibration.Accel(rest).Length(), 0.99f, 1.01f);
        Assert.InRange(calibration.Accel(rest).Y, 0.95f, 1.0f);
        Assert.True(calibration.Gyro(rest).Length() < 0.3f);
    }

    [Fact]
    public void RejectsMissingOrBrokenCalibration()
    {
        Assert.Null(MotionCalibration.Parse(Report(new short[17])));
        Assert.Null(MotionCalibration.Parse(new byte[10]));
    }

    [Fact]
    public void DefaultScaleIsOneSixteenthDegree()
    {
        var sample = new MotionSample(160, 0, 0, 0, 8192, 0, 0);
        Assert.Equal(10f, MotionCalibration.Default.Gyro(sample).X, 3);
        Assert.Equal(1f, MotionCalibration.Default.Accel(sample).Y, 3);
    }
}

public class OrientationTests
{
    private const float Dt = 0.004f;

    [Fact]
    public void IntegratesYaw()
    {
        var filter = new OrientationFilter();
        for (int i = 0; i < 250; i++)
            filter.Update(new Vector3(0, 90, 0), Vector3.UnitY, Dt);
        var (pitch, roll, yaw) = OrientationFilter.Angles(filter.Orientation);
        Assert.InRange(yaw, 89f, 91f);
        Assert.InRange(pitch, -0.5f, 0.5f);
        Assert.InRange(roll, -0.5f, 0.5f);
    }

    [Fact]
    public void GravityPullsPitchToTheMeasuredTilt()
    {
        // Nose up by 30°: in controller space "up" leans toward the front (−Z).
        var up = new Vector3(0, MathF.Cos(MathF.PI / 6), -MathF.Sin(MathF.PI / 6));
        var filter = new OrientationFilter();
        for (int i = 0; i < 750; i++)
            filter.Update(Vector3.Zero, up, Dt);
        Assert.InRange(OrientationFilter.Angles(filter.Orientation).Pitch, 29f, 31f);
    }

    [Fact]
    public void RollFollowsTiltToTheLeft()
    {
        // Rolled left by 20°: "up" leans toward +X in controller space.
        var up = new Vector3(MathF.Sin(MathF.PI / 9), MathF.Cos(MathF.PI / 9), 0);
        var filter = new OrientationFilter();
        for (int i = 0; i < 750; i++)
            filter.Update(Vector3.Zero, up, Dt);
        Assert.InRange(OrientationFilter.Angles(filter.Orientation).RollLeft, 19f, 21f);
    }

    [Fact]
    public void RecenterKeepsTiltAndClearsYaw()
    {
        var up = new Vector3(0, MathF.Cos(MathF.PI / 6), -MathF.Sin(MathF.PI / 6));
        var filter = new OrientationFilter();
        for (int i = 0; i < 750; i++)
            filter.Update(Vector3.Zero, up, Dt);
        for (int i = 0; i < 250; i++)
            filter.Update(up * 60, up, Dt); // turning around the real vertical axis
        filter.Recenter();
        var (pitch, _, yaw) = OrientationFilter.Angles(filter.Orientation);
        Assert.InRange(yaw, -0.5f, 0.5f);
        Assert.InRange(pitch, 27f, 33f);
    }
}

public class MotionProcessorTests
{
    private const uint TicksPer4Ms = 12_000; // sensor clock: 3 MHz

    /// <summary>A sample in physical units with the default calibration (1/16 °/s, 1/8192 g).</summary>
    private static MotionSample Sample(Vector3 dps, Vector3 g, uint timestamp) => new(
        (short)(dps.X * 16), (short)(dps.Y * 16), (short)(dps.Z * 16),
        (short)(g.X * 8192), (short)(g.Y * 8192), (short)(g.Z * 8192), timestamp);

    private static MotionReading Feed(MotionProcessor processor, Vector3 dps, int count, ref uint timestamp, ref TimeSpan time)
    {
        MotionReading reading = default;
        for (int i = 0; i < count; i++)
        {
            reading = processor.Process(Sample(dps, Vector3.UnitY, timestamp), time);
            timestamp = unchecked(timestamp + TicksPer4Ms);
            time += TimeSpan.FromMilliseconds(4);
        }
        return reading;
    }

    [Fact]
    public void DtComesFromTheSensorClockAndSurvivesWrapAround()
    {
        var processor = new MotionProcessor();
        processor.Process(Sample(Vector3.Zero, Vector3.UnitY, uint.MaxValue - 5999), TimeSpan.Zero);
        // The wall clock says 30 ms (a late Bluetooth packet); the sensor says 4 ms.
        var reading = processor.Process(Sample(Vector3.Zero, Vector3.UnitY, 6000), TimeSpan.FromMilliseconds(30));
        Assert.Equal(0.004f, reading.Dt, 5);
    }

    [Fact]
    public void AutoCalibrationRemovesAConstantOffsetAtRest()
    {
        var processor = new MotionProcessor { AutoCalibration = true };
        uint ts = 0;
        var time = TimeSpan.Zero;
        var offset = new Vector3(1.5f, -1f, 0.5f);
        var reading = Feed(processor, offset, 3000, ref ts, ref time); // 12 s

        Assert.True(reading.IsStill);
        Assert.True(Vector3.Distance(processor.Bias, offset) < 0.1f);
        Assert.True(reading.Gyro.Length() < 0.1f);
    }

    [Fact]
    public void WithoutAutoCalibrationTheOffsetStays()
    {
        var processor = new MotionProcessor { AutoCalibration = false };
        uint ts = 0;
        var time = TimeSpan.Zero;
        Feed(processor, new Vector3(1.5f, 0, 0), 2000, ref ts, ref time);
        Assert.Equal(Vector3.Zero, processor.Bias);
    }

    [Fact]
    public void ManualCalibrationMeasuresTheOffsetAndRestartsOnMovement()
    {
        var processor = new MotionProcessor { AutoCalibration = false };
        Vector3? completed = null;
        processor.CalibrationCompleted += bias => completed = bias;
        uint ts = 0;
        var time = TimeSpan.Zero;
        var offset = new Vector3(2f, -1f, 0.5f);
        Feed(processor, offset, 10, ref ts, ref time);

        processor.StartCalibration();
        Feed(processor, offset, 250, ref ts, ref time);              // 1 s at rest
        Assert.Equal(CalibrationPhase.Running, processor.Calibrating.Phase);
        Feed(processor, new Vector3(0, 120, 0), 5, ref ts, ref time); // someone bumps the table
        Assert.True(processor.Calibrating.Moving || processor.Calibrating.Fraction < 0.1);
        Feed(processor, offset, 800, ref ts, ref time);              // settles (~0.5 s), then 2 s at rest

        Assert.Equal(CalibrationPhase.Done, processor.Calibrating.Phase);
        Assert.NotNull(completed);
        Assert.True(Vector3.Distance(completed.Value, offset) < 0.1f);
        Assert.Equal(completed.Value, processor.Bias);
    }
}

public class GyroMouseTests
{
    private static readonly GyroSettings Plain = new() { Sensitivity = 12, Tightening = 0, Smoothing = 0, Acceleration = 0, Axis = GyroAxisMode.Yaw };

    private static MotionReading Reading(Vector3 gyro, Vector3? up = null) =>
        new(TimeSpan.Zero, 0.01f, gyro, up ?? Vector3.UnitY, up ?? Vector3.UnitY, Quaternion.Identity, false);

    [Fact]
    public void TurningLeftMovesLeftAndNoseUpMovesUp()
    {
        var engine = new GyroMouseEngine();
        var left = engine.Compute(Reading(new Vector3(0, 10, 0)), Plain);
        Assert.Equal(-1.2f, left.X, 4); // 10 °/s × 0.01 s × 12 counts/°
        Assert.Equal(0f, left.Y, 4);

        var up = engine.Compute(Reading(new Vector3(10, 0, 0)), Plain);
        Assert.Equal(-1.2f, up.Y, 4);
    }

    [Fact]
    public void VerticalRatioAndInversion()
    {
        var engine = new GyroMouseEngine();
        var s = Plain with { VerticalPercent = 50, InvertX = true };
        var counts = engine.Compute(Reading(new Vector3(10, 10, 0)), s);
        Assert.Equal(1.2f, counts.X, 4);
        Assert.Equal(-0.6f, counts.Y, 4);
    }

    [Fact]
    public void TighteningSoftensSlowMovementOnly()
    {
        var engine = new GyroMouseEngine();
        var s = Plain with { Tightening = 4 };
        Assert.Equal(-0.12f, engine.Compute(Reading(new Vector3(0, 2, 0)), s).X, 4);  // 2 °/s → scaled by 2/4
        Assert.Equal(-6f, engine.Compute(Reading(new Vector3(0, 50, 0)), s).X, 4);    // fast: untouched
    }

    [Fact]
    public void SmoothingLeavesFastMovementDirect()
    {
        var engine = new GyroMouseEngine();
        var s = Plain with { Smoothing = 10 };
        Assert.Equal(-6f, engine.Compute(Reading(new Vector3(0, 50, 0)), s).X, 4);
        var slow = engine.Compute(Reading(new Vector3(0, 2, 0)), s).X;
        Assert.True(slow > -0.24f && slow < 0); // delayed, not lost
    }

    [Fact]
    public void AccelerationBoostsFastTurns()
    {
        var engine = new GyroMouseEngine();
        var s = Plain with { Acceleration = 100 };
        Assert.Equal(-96f, engine.Compute(Reading(new Vector3(0, 200, 0)), s).X, 3); // 4× above 160 °/s
        Assert.Equal(-1.2f, engine.Compute(Reading(new Vector3(0, 10, 0)), s).X, 4); // slow: 1×
    }

    [Fact]
    public void PlayerSpaceTurnsAroundTheRealVerticalAxis()
    {
        var engine = new GyroMouseEngine();
        var s = Plain with { Axis = GyroAxisMode.PlayerSpace };
        // Held flat: same as yaw.
        Assert.Equal(-1.2f, engine.Compute(Reading(new Vector3(0, 10, 0)), s).X, 4);
        // Held upright (nose to the sky): turning left is a rotation around the controller's −Z.
        var upright = new Vector3(0, 0, -1);
        Assert.Equal(-1.2f, engine.Compute(Reading(new Vector3(0, 0, -10), upright), s).X, 4);
        Assert.Equal(0f, engine.Compute(Reading(new Vector3(0, 10, 0), upright), s).X, 4);
    }

    [Fact]
    public void FractionsCarryOver()
    {
        var engine = new GyroMouseEngine();
        var step = new Vector2(0.4f, -0.4f);
        Assert.Equal((0, 0), engine.ToCounts(step));
        Assert.Equal((0, 0), engine.ToCounts(step));
        Assert.Equal((1, -1), engine.ToCounts(step));
    }

    [Fact]
    public void ActivationConditions()
    {
        var idle = new InputFrame(false, false, default, default);
        var aiming = idle with { Analog = AnalogState.Centered with { L2 = 120 } };
        var touching = new InputFrame(false, false, new TouchPoint(true, 1, 10, 10), default);
        var l1 = idle with { Buttons = ControllerButtons.L1 };

        Assert.True(GyroMouseEngine.IsActive(GyroActivation.Always, idle));
        Assert.False(GyroMouseEngine.IsActive(GyroActivation.HoldL2, idle));
        Assert.True(GyroMouseEngine.IsActive(GyroActivation.HoldL2, aiming));
        Assert.True(GyroMouseEngine.IsActive(GyroActivation.TouchpadTouch, touching));
        Assert.True(GyroMouseEngine.IsActive(GyroActivation.HoldL1, l1));
        Assert.False(GyroMouseEngine.IsActive(GyroActivation.OffWhileL1, l1));
        Assert.True(GyroMouseEngine.IsActive(GyroActivation.OffWhileL1, idle));
    }
}

public class AimTrainerTests
{
    [Fact]
    public void ShootingOnTheTargetScoresAndSpawnsANewOne()
    {
        var trainer = new AimTrainer(seed: 7);
        var first = trainer.State;
        trainer.Update(first.Target - first.Crosshair, 1, 0.5);

        var after = trainer.State;
        Assert.Equal(1, after.Hits);
        Assert.Equal(0, after.Misses);
        Assert.Equal(0.5, after.AverageHitSeconds, 3);
        Assert.NotEqual(first.Target, after.Target);
        Assert.True(after.LastShotHit);

        trainer.Update(Vector2.Zero, 1, 0.1); // new target is far away
        Assert.Equal(1, trainer.State.Misses);
    }

    [Fact]
    public void CrosshairStaysInsideTheArena()
    {
        var trainer = new AimTrainer(seed: 1);
        trainer.Update(new Vector2(-10_000, 10_000), 0, 0.1);
        Assert.Equal(new Vector2(0, AimTrainer.ArenaSize.Y), trainer.State.Crosshair);
        trainer.Recenter();
        Assert.Equal(AimTrainer.ArenaSize / 2, trainer.State.Crosshair);
    }
}

public sealed class GyroSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseTweakerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void NormalizeClampsEverything()
    {
        var s = new GyroSettings { Sensitivity = 500, VerticalPercent = 0, Acceleration = 300, Tightening = double.NaN, Smoothing = -3, Activation = (GyroActivation)42 }.Normalize();
        Assert.Equal(GyroSettings.MaxSensitivity, s.Sensitivity);
        Assert.Equal(10, s.VerticalPercent);
        Assert.Equal(100, s.Acceleration);
        Assert.Equal(1, s.Tightening);
        Assert.Equal(0, s.Smoothing);
        Assert.Equal(GyroActivation.Always, s.Activation);
    }

    [Fact]
    public void CalibrationEntriesAreReplacedPerController()
    {
        var prefs = new GyroPreferences()
            .WithCalibration(new GyroCalibrationEntry("AA:BB", 1, 2, 3))
            .WithCalibration(new GyroCalibrationEntry("CC:DD", 4, 5, 6))
            .WithCalibration(new GyroCalibrationEntry("aa:bb", 7, 8, 9));
        Assert.Equal(2, prefs.Calibrations.Count);
        Assert.Equal(7, prefs.Find("AA:BB")!.BiasX);
        Assert.Null(prefs.WithoutCalibration("CC:DD").Find("cc:dd"));
    }

    [Fact]
    public void GyroSettingsAndCalibrationsSurviveSaveAndLoad()
    {
        var store = new ProfileStore(_root);
        var settings = new ProfileSettings
        {
            Gyro = new GyroSettings { MouseEnabled = true, Activation = GyroActivation.HoldL2, Axis = GyroAxisMode.Roll, Sensitivity = 17.5, InvertY = true },
        }.Normalize();
        store.Save(new Profile("Gyro test", ProfileKind.Custom, settings));
        Assert.Equal(settings.Gyro, store.LoadAll().Single(p => p.Name == "Gyro test").Settings.Gyro);

        var app = new AppSettings
        {
            Preferences = new AppPreferences { Gyro = new GyroPreferences { AutoCalibration = false }.WithCalibration(new GyroCalibrationEntry("AA:BB", 0.5f, -0.25f, 1)) },
        };
        store.SaveAppSettings(app);
        var loaded = store.LoadAppSettings().Preferences.Gyro;
        Assert.False(loaded.AutoCalibration);
        Assert.Equal(new GyroCalibrationEntry("AA:BB", 0.5f, -0.25f, 1), loaded.Find("AA:BB"));
    }
}
