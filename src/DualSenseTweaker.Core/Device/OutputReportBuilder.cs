using System.Buffers.Binary;
using DualSenseTweaker.Core.State;
using static DualSenseTweaker.Core.Device.DualSenseProtocol;

namespace DualSenseTweaker.Core.Device;

/// <summary>Pure translation of composed output values into raw output report bytes.</summary>
internal static class OutputReportBuilder
{
    /// <param name="groups">Only these parts get their valid-flags set; everything else is left untouched on the controller.</param>
    /// <param name="sequence">Bluetooth sequence number (0–15); ignored for USB.</param>
    public static byte[] Build(ComposedOutput o, OutputGroups groups, ConnectionType connection, byte sequence, bool useVibrationV2)
    {
        byte[] report = CreateReport(connection, sequence, out int offset);
        Span<byte> c = report.AsSpan(offset, Common.Size);

        // Zero rumble deliberately sets no bits: leaving them off stops rumble emulation and gives
        // the actuators back to audio haptics (SDL's PS5 driver relies on the same behavior).
        if (groups.HasFlag(OutputGroups.Rumble) && o.RumbleActive)
        {
            c[Common.ValidFlag0] |= Flag0.HapticsSelect;
            if (useVibrationV2)
                c[Common.ValidFlag2] |= Flag2.CompatibleVibration2;
            else
                c[Common.ValidFlag0] |= Flag0.CompatibleVibration;
            c[Common.MotorLeft] = o.RumbleHeavy;
            c[Common.MotorRight] = o.RumbleSoft;
        }

        if (groups.HasFlag(OutputGroups.Lightbar))
        {
            c[Common.ValidFlag1] |= Flag1.LightbarControl;
            c[Common.LightbarRed] = o.Lightbar.R;
            c[Common.LightbarGreen] = o.Lightbar.G;
            c[Common.LightbarBlue] = o.Lightbar.B;
        }

        if (groups.HasFlag(OutputGroups.PlayerLeds))
        {
            c[Common.ValidFlag1] |= Flag1.PlayerIndicator;
            c[Common.PlayerLeds] = o.PlayerLedMask;
            // Applies to both the player LEDs and the mic LED.
            c[Common.ValidFlag2] |= Flag2.LedBrightness;
            c[Common.LedBrightness] = (byte)o.LedBrightness;
        }

        if (groups.HasFlag(OutputGroups.MicLed))
        {
            // Only the LED; the microphone itself stays enabled.
            c[Common.ValidFlag1] |= Flag1.MicMuteLed;
            c[Common.MuteButtonLed] = (byte)o.MicLed;
        }

        if (groups.HasFlag(OutputGroups.RightTrigger))
            WriteTrigger(c, Common.RightTrigger, Flag0.RightTrigger, o.RightTrigger);
        if (groups.HasFlag(OutputGroups.LeftTrigger))
            WriteTrigger(c, Common.LeftTrigger, Flag0.LeftTrigger, o.LeftTrigger);

        if (groups.HasFlag(OutputGroups.MotorPower))
        {
            c[Common.ValidFlag1] |= Flag1.MotorPower;
            c[Common.MotorPower] = (byte)((o.RumbleAttenuation & 0x07) | ((o.TriggerAttenuation & 0x07) << 4));
        }

        if (o.AudioRoute is { } route)
            WriteAudio(c, o, route, groups);

        Seal(report, connection);
        return report;
    }

    /// <summary>
    /// Releases the firmware's LED state before applying our color (SDL's LED reset command).
    /// On Bluetooth this must wait until the connection animation ends. Send the requested
    /// lightbar and LED values in a separate report afterwards.
    /// </summary>
    public static byte[] BuildLightbarReset(ConnectionType connection, byte sequence)
    {
        byte[] report = CreateReport(connection, sequence, out int offset);
        report[offset + Common.ValidFlag1] = Flag1.ReleaseLeds;
        Seal(report, connection);
        return report;
    }

    /// <summary>
    /// Leaves the controller the way games expect it: motors explicitly at 0 (Linux-style, with the
    /// rumble flags), both triggers released and no motor attenuation. The lightbar keeps its color.
    /// </summary>
    public static byte[] BuildRestore(ConnectionType connection, byte sequence, bool useVibrationV2)
    {
        byte[] report = CreateReport(connection, sequence, out int offset);
        WriteRestore(report.AsSpan(offset, Common.Size), useVibrationV2);
        Seal(report, connection);
        return report;
    }

    /// <summary>
    /// "Factory reset": everything of <see cref="BuildRestore"/> plus the look of a fresh controller —
    /// default blue lightbar (the color the Linux driver starts with), player LEDs and mic LED off.
    /// </summary>
    public static byte[] BuildFactoryReset(ConnectionType connection, byte sequence, bool useVibrationV2)
    {
        byte[] report = CreateReport(connection, sequence, out int offset);
        Span<byte> c = report.AsSpan(offset, Common.Size);
        WriteRestore(c, useVibrationV2);

        c[Common.ValidFlag1] |= Flag1.LightbarControl | Flag1.PlayerIndicator | Flag1.MicMuteLed;
        c[Common.LightbarRed] = FactoryLightbar.R;
        c[Common.LightbarGreen] = FactoryLightbar.G;
        c[Common.LightbarBlue] = FactoryLightbar.B;
        c[Common.PlayerLeds] = 0;
        c[Common.MuteButtonLed] = 0;
        c[Common.ValidFlag2] |= Flag2.LedBrightness;
        c[Common.LedBrightness] = 0;

        Seal(report, connection);
        return report;
    }

    /// <summary>Lightbar color of a factory-fresh controller on a PC.</summary>
    public static readonly RgbColor FactoryLightbar = new(0, 0, 128);

    private static void WriteRestore(Span<byte> c, bool useVibrationV2)
    {
        c[Common.ValidFlag0] |= Flag0.HapticsSelect | Flag0.RightTrigger | Flag0.LeftTrigger;
        if (useVibrationV2)
            c[Common.ValidFlag2] |= Flag2.CompatibleVibration2;
        else
            c[Common.ValidFlag0] |= Flag0.CompatibleVibration;

        var off = TriggerEffectFactory.Off();
        off.CopyTo(c[Common.RightTrigger..]);
        off.CopyTo(c[Common.LeftTrigger..]);

        c[Common.ValidFlag1] |= Flag1.MotorPower;
        c[Common.MotorPower] = 0;
    }

    /// <summary>A report without any valid flags: changes nothing except ending rumble emulation, which hands the actuators back to audio haptics.</summary>
    public static byte[] BuildNeutral(ConnectionType connection, byte sequence)
    {
        byte[] report = CreateReport(connection, sequence, out _);
        Seal(report, connection);
        return report;
    }

    public static AudioOutputRoute ResolveRoute(AudioOutputRoute route, bool headphonesConnected) =>
        route == AudioOutputRoute.Auto
            ? headphonesConnected ? AudioOutputRoute.Headphones : AudioOutputRoute.Speaker
            : route;

    /// <summary>Switching a trigger back to "game" sends Off once, so the app's last effect does not linger.</summary>
    private static void WriteTrigger(Span<byte> c, int offset, byte flag, TriggerEffect effect)
    {
        var block = TriggerEffectFactory.Build(effect) ?? TriggerEffectFactory.Off();
        c[Common.ValidFlag0] |= flag;
        block.CopyTo(c[offset..]);
    }

    private static void WriteAudio(Span<byte> c, ComposedOutput o, AudioOutputRoute route, OutputGroups groups)
    {
        if (groups.HasFlag(OutputGroups.AudioVolume))
        {
            c[Common.ValidFlag0] |= Flag0.HeadphoneVolume | Flag0.SpeakerVolume;
            c[Common.HeadphoneVolume] = Math.Min(o.HeadphoneVolume, OutputSnapshot.MaxHeadphoneVolume);
            c[Common.SpeakerVolume] = Math.Min(o.SpeakerVolume, OutputSnapshot.MaxSpeakerVolume);
        }

        if (groups.HasFlag(OutputGroups.AudioRoute))
        {
            // Output path: 0 = headphones L/R, 2 = headphones L + speaker R, 3 = speaker only (right channel).
            byte path = route switch
            {
                AudioOutputRoute.Speaker => 3,
                AudioOutputRoute.HeadphonesAndSpeaker => 2,
                _ => 0,
            };
            c[Common.ValidFlag0] |= Flag0.AudioControl;
            c[Common.AudioControl] = (byte)(path << AudioOutputPathShift);

            if (route != AudioOutputRoute.Headphones)
            {
                c[Common.ValidFlag1] |= Flag1.AudioControl2;
                c[Common.AudioControl2] = SpeakerPreampGain6Db;
            }
        }
    }

    private static byte[] CreateReport(ConnectionType connection, byte sequence, out int commonOffset)
    {
        if (connection == ConnectionType.Bluetooth)
        {
            var bt = new byte[OutputReportBtSize];
            bt[0] = OutputReportBt;
            bt[1] = (byte)((sequence & 0x0F) << 4); // high nibble: sequence number, low nibble: tag 0
            bt[2] = OutputTag;
            commonOffset = 3;
            return bt;
        }

        var usb = new byte[OutputReportUsbSize];
        usb[0] = OutputReportUsb;
        commonOffset = 1;
        return usb;
    }

    private static void Seal(byte[] report, ConnectionType connection)
    {
        if (connection != ConnectionType.Bluetooth)
            return;
        int crcOffset = report.Length - 4;
        uint crc = Crc32.Compute(CrcSeedOutput, report.AsSpan(0, crcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(crcOffset), crc);
    }
}
