using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DualSenseTweaker.ControllerBridge;

public enum BridgeMessageKind { Snapshot, Frame, Heartbeat, Gap, Disconnected }
public sealed record BridgeMessage
{
    public int FormatVersion { get; init; } = 1;
    public Guid SessionId { get; init; }
    public BridgeMessageKind Kind { get; init; }
    public ControllerDescriptor[] Controllers { get; init; } = [];
    public PadFrame? Frame { get; init; }
    public long DroppedFrames { get; init; }
    public string? ControllerId { get; init; }
    public Guid? ConnectionId { get; init; }
    public void Validate()
    {
        if (FormatVersion != 1 || SessionId == Guid.Empty || !Enum.IsDefined(Kind) || DroppedFrames < 0 ||
            Controllers is null || Controllers.Length > 32 || Controllers.Select(c => c?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Controllers.Length)
            throw new InvalidDataException("Invalid controller IPC envelope.");
        foreach (var controller in Controllers)
            if (controller is null || string.IsNullOrWhiteSpace(controller.Id) || controller.Id.Length > 1024 ||
                string.IsNullOrWhiteSpace(controller.Model) || controller.Model.Length > 64 ||
                controller.Transport is not ("USB" or "Bluetooth") || controller.BatteryPercent is < 0 or > 100)
                throw new InvalidDataException("Invalid controller IPC identity/status.");
        if (Kind == BridgeMessageKind.Frame)
        {
            if (Frame is null || Frame.SessionId != SessionId) throw new InvalidDataException("Mismatched controller session.");
            Frame.Validate();
        }
        else if (Frame is not null) throw new InvalidDataException("Unexpected frame payload.");
        if (Kind == BridgeMessageKind.Disconnected && (string.IsNullOrWhiteSpace(ControllerId) || ControllerId.Length > 1024 || ConnectionId is null || ConnectionId == Guid.Empty))
            throw new InvalidDataException("Invalid disconnect notification.");
    }
}

public static class BridgeProtocol
{
    public const int MaxMessageBytes = 65_536;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    public static async ValueTask WriteAsync(Stream stream, BridgeMessage message, CancellationToken token)
    {
        message.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Controller IPC message exceeds size limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async ValueTask<BridgeMessage?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 4), token);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(first), token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("Controller IPC length exceeds limit.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(bytes, Options) ?? throw new InvalidDataException("Null IPC message.");
            message.Validate(); return message;
        }
        catch (JsonException ex) { throw new InvalidDataException("Malformed controller IPC JSON.", ex); }
    }
}
