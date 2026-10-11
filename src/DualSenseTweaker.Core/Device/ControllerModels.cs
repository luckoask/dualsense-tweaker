using System.Buffers.Binary;

namespace DualSenseTweaker.Core.Device;

public enum ConnectionType { Usb, Bluetooth }

/// <summary>A DualSense found during enumeration (not necessarily connected).</summary>
public sealed record ControllerInfo(string DevicePath, int ProductId, ConnectionType Connection, string? MacAddress)
{
    public bool IsEdge => ProductId == DualSenseProtocol.DualSenseEdgeProductId;
    public string ModelName => IsEdge ? "DualSense Edge" : "DualSense";
    public string ConnectionName => Connection == ConnectionType.Bluetooth ? "Bluetooth" : "USB";

    /// <summary>Stable identity across USB/Bluetooth switches when the MAC address is known.</summary>
    public string Key => MacAddress ?? DevicePath;

    public string DisplayName => MacAddress is null
        ? $"{ModelName} · {ConnectionName}"
        : $"{ModelName} · {ConnectionName} · {MacAddress}";
}

/// <summary>Contents of feature report 0x20.</summary>
public sealed record FirmwareInfo(uint HardwareVersion, uint FirmwareVersion, ushort UpdateVersion)
{
    public string FirmwareText => $"0x{FirmwareVersion:X8}";
    public string UpdateVersionText => $"{UpdateVersion >> 8}.{UpdateVersion & 0xFF:D2}";

    internal static FirmwareInfo? Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 46 || report[0] != DualSenseProtocol.FeatureFirmwareInfo)
            return null;
        return new FirmwareInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(report[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(report[28..]),
            BinaryPrimitives.ReadUInt16LittleEndian(report[44..]));
    }
}

public enum BatteryState { Unknown, Discharging, Charging, Full, NotCharging }

/// <summary>The parts of the input report the app cares about.</summary>
public sealed record ControllerStatus(
    int BatteryPercent,
    BatteryState BatteryState,
    bool HeadphonesConnected,
    bool MicrophoneConnected);

public readonly record struct DeviceStatistics(long InputReports, long OutputReports);

/// <summary>What the manager currently knows about the active connection.</summary>
public sealed record ConnectionSnapshot(
    ControllerInfo? Controller,
    FirmwareInfo? Firmware,
    bool UsesVibrationV2,
    ControllerStatus? Status,
    string Message)
{
    public bool IsConnected => Controller is not null;

    /// <summary>IMU scaling of the connected controller (factory data or the generic fallback).</summary>
    public MotionCalibration Calibration { get; init; } = MotionCalibration.Default;

    public static ConnectionSnapshot Disconnected(string message) => new(null, null, false, null, message);
}
