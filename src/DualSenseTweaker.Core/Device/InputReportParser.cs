using System.Buffers.Binary;

namespace DualSenseTweaker.Core.Device;

internal enum InputParseResult
{
    Ok,
    /// <summary>Bluetooth controller still sends the reduced 0x01 report (feature 0x05 not read yet).</summary>
    SimpleBluetoothReport,
    CrcMismatch,
    Ignored,
}

internal static class InputReportParser
{
    public static InputParseResult TryParse(ReadOnlySpan<byte> report, ConnectionType connection, out ControllerStatus? status) =>
        TryParse(report, connection, out status, out _);

    /// <param name="frame">Buttons and touchpad contacts of this report.</param>
    public static InputParseResult TryParse(ReadOnlySpan<byte> report, ConnectionType connection, out ControllerStatus? status, out InputFrame frame)
    {
        status = null;
        frame = default;
        if (report.IsEmpty)
            return InputParseResult.Ignored;

        int offset;
        if (connection == ConnectionType.Usb)
        {
            if (report[0] != DualSenseProtocol.InputReportUsb || report.Length < DualSenseProtocol.InputReportUsbSize)
                return InputParseResult.Ignored;
            offset = 1;
        }
        else
        {
            if (report[0] == DualSenseProtocol.InputReportUsb)
                return InputParseResult.SimpleBluetoothReport;
            if (report[0] != DualSenseProtocol.InputReportBt || report.Length < DualSenseProtocol.InputReportBtSize)
                return InputParseResult.Ignored;

            const int crcOffset = DualSenseProtocol.InputReportBtSize - 4;
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(report[crcOffset..]);
            if (Crc32.Compute(DualSenseProtocol.CrcSeedInput, report[..crcOffset]) != expected)
                return InputParseResult.CrcMismatch;
            offset = 2;
        }

        var common = report[offset..];
        byte buttons2 = common[DualSenseProtocol.InputButtons2Offset];
        int touch = DualSenseProtocol.InputTouchOffset;
        frame = new InputFrame(
            (buttons2 & DualSenseProtocol.Buttons2PsHome) != 0,
            (buttons2 & DualSenseProtocol.Buttons2TouchpadClick) != 0,
            ParseTouch(common.Slice(touch, 4)),
            ParseTouch(common.Slice(touch + 4, 4)))
        {
            Buttons = ParseButtons(common[DualSenseProtocol.InputButtons0Offset], common[DualSenseProtocol.InputButtons1Offset], buttons2),
            Analog = new AnalogState(
                common[DualSenseProtocol.InputSticksOffset], common[DualSenseProtocol.InputSticksOffset + 1],
                common[DualSenseProtocol.InputSticksOffset + 2], common[DualSenseProtocol.InputSticksOffset + 3],
                common[DualSenseProtocol.InputTriggersOffset], common[DualSenseProtocol.InputTriggersOffset + 1]),
            Motion = ParseMotion(common),
        };

        byte status0 = report[offset + DualSenseProtocol.InputStatusOffset];
        byte status1 = report[offset + DualSenseProtocol.InputStatusOffset + 1];
        var (percent, state) = DecodeBattery(status0);
        status = new ControllerStatus(
            percent,
            state,
            (status1 & DualSenseProtocol.Status1HeadphonesDetect) != 0,
            (status1 & DualSenseProtocol.Status1MicrophoneDetect) != 0);
        return InputParseResult.Ok;
    }

    /// <summary>buttons[0..2] of the common report → flags (hat switch: 0 = up, clockwise, 8 = released).</summary>
    internal static ControllerButtons ParseButtons(byte buttons0, byte buttons1, byte buttons2)
    {
        var result = (buttons0 & 0x0F) switch
        {
            0 => ControllerButtons.DpadUp,
            1 => ControllerButtons.DpadUp | ControllerButtons.DpadRight,
            2 => ControllerButtons.DpadRight,
            3 => ControllerButtons.DpadDown | ControllerButtons.DpadRight,
            4 => ControllerButtons.DpadDown,
            5 => ControllerButtons.DpadDown | ControllerButtons.DpadLeft,
            6 => ControllerButtons.DpadLeft,
            7 => ControllerButtons.DpadUp | ControllerButtons.DpadLeft,
            _ => ControllerButtons.None,
        };
        // □ ✕ ○ △ are bits 4–7 of buttons[0] and flags 4–7; L1 … R3 are buttons[1] and flags 8–15.
        result |= (ControllerButtons)((uint)(buttons0 & 0xF0));
        result |= (ControllerButtons)((uint)buttons1 << 8);
        if ((buttons2 & DualSenseProtocol.Buttons2PsHome) != 0)
            result |= ControllerButtons.PS;
        if ((buttons2 & DualSenseProtocol.Buttons2TouchpadClick) != 0)
            result |= ControllerButtons.Touchpad;
        if ((buttons2 & DualSenseProtocol.Buttons2Mute) != 0)
            result |= ControllerButtons.Mute;
        // Edge: Fn left, Fn right, left paddle, right paddle → flags 19–22.
        result |= (ControllerButtons)((uint)(buttons2 >> DualSenseProtocol.Buttons2EdgeShift) << 19);
        return result;
    }

    internal static MotionSample ParseMotion(ReadOnlySpan<byte> common)
    {
        var gyro = common[DualSenseProtocol.InputGyroOffset..];
        var accel = common[DualSenseProtocol.InputAccelOffset..];
        return new MotionSample(
            BinaryPrimitives.ReadInt16LittleEndian(gyro),
            BinaryPrimitives.ReadInt16LittleEndian(gyro[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(gyro[4..]),
            BinaryPrimitives.ReadInt16LittleEndian(accel),
            BinaryPrimitives.ReadInt16LittleEndian(accel[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(accel[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(common[DualSenseProtocol.InputSensorTimestampOffset..]));
    }

    /// <summary>contact, x_lo, x_hi:4 | y_lo:4, y_hi → 12-bit X and Y (Linux DS_TOUCH_POINT_X/Y).</summary>
    internal static TouchPoint ParseTouch(ReadOnlySpan<byte> point) => new(
        (point[0] & DualSenseProtocol.TouchInactive) == 0,
        (byte)(point[0] & 0x7F),
        point[1] | ((point[2] & 0x0F) << 8),
        (point[2] >> 4) | (point[3] << 4));

    /// <summary>Low nibble = capacity in tens of percent, high nibble = charging state (same mapping as Linux).</summary>
    internal static (int Percent, BatteryState State) DecodeBattery(byte status0)
    {
        int level = status0 & 0x0F;
        int capacity = Math.Min(level * 10 + 5, 100);
        return (status0 >> 4) switch
        {
            0x0 => (capacity, BatteryState.Discharging),
            0x1 => (capacity, BatteryState.Charging),
            0x2 => (100, BatteryState.Full),
            0xA or 0xB => (0, BatteryState.NotCharging), // voltage/temperature out of range
            _ => (0, BatteryState.Unknown),              // 0xF = charging error
        };
    }
}
