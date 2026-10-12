using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Tests;

public class BridgeTests
{
    [Fact]
    public async Task PartialReadsRoundTripFramingAndMultipleControllerIdentities()
    {
        var frame = EngineTests.Frame(1, PadButtons.PaddleLeft | PadButtons.Mute) with { ConnectionId = Guid.NewGuid() };
        var snapshot = new BridgeMessage { SessionId = frame.SessionId, Kind = BridgeMessageKind.Snapshot,
            Controllers = [new("pad", "DualSense Edge", "Bluetooth", 75), new("second", "DualSense", "USB", null)] };
        using var stream = new MemoryStream(); await BridgeProtocol.WriteAsync(stream, snapshot, default);
        await BridgeProtocol.WriteAsync(stream, new() { SessionId = frame.SessionId, Kind = BridgeMessageKind.Frame, Frame = frame }, default);
        using var fragmented = new FragmentedStream(stream.ToArray());
        Assert.Equal(snapshot.Controllers, (await BridgeProtocol.ReadAsync(fragmented, default))!.Controllers);
        Assert.Equal(frame, (await BridgeProtocol.ReadAsync(fragmented, default))!.Frame);
        Assert.Null(await BridgeProtocol.ReadAsync(fragmented, default));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(65_537)] [InlineData(int.MaxValue)]
    public async Task InvalidLengthsAreRejectedBeforeAllocation(int length)
    {
        byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => BridgeProtocol.ReadAsync(stream, default).AsTask());
    }
    [Fact]
    public async Task TruncatedPayloadAndMalformedJsonAreRejected()
    {
        using var truncated = new MemoryStream(new byte[] { 10, 0, 0, 0, 1 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => BridgeProtocol.ReadAsync(truncated, default).AsTask());
        byte[] json = Encoding.UTF8.GetBytes("{bad}"); byte[] payload = new byte[4 + json.Length]; BinaryPrimitives.WriteInt32LittleEndian(payload, json.Length); json.CopyTo(payload, 4);
        using var malformed = new MemoryStream(payload);
        await Assert.ThrowsAsync<InvalidDataException>(() => BridgeProtocol.ReadAsync(malformed, default).AsTask());
    }
    [Fact]
    public void MismatchedSessionsUnknownVersionsAndDuplicateIdentitiesAreRejected()
    {
        var frame = EngineTests.Frame(0);
        Assert.Throws<InvalidDataException>(() => new BridgeMessage { SessionId = Guid.NewGuid(), Kind = BridgeMessageKind.Frame, Frame = frame }.Validate());
        Assert.Throws<InvalidDataException>(() => new BridgeMessage { SessionId = Guid.NewGuid(), FormatVersion = 99 }.Validate());
        Assert.Throws<InvalidDataException>(() => new BridgeMessage { SessionId = Guid.NewGuid(), Controllers = [new("same", "DualSense", "USB", 50), new("SAME", "DualSense", "Bluetooth", 50)] }.Validate());
    }
    [Fact]
    public async Task ActualNamedPipeUsesRestrictedReadOnlyTransportAndStopsOnClose()
    {
        string name = "MacroGamer-Test-" + Guid.NewGuid().ToString("N");
        var publisher = new InputPipePublisher(() => [new("pad", "DualSense", "USB", 50)], pipeName: name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.In, InputPipeIdentity.RestrictedOptions);
        await client.ConnectAsync(5000, deadline.Token);
        Assert.False(client.CanWrite); Assert.True((InputPipeIdentity.RestrictedOptions & PipeOptions.CurrentUserOnly) != 0);
        var snapshot = await BridgeProtocol.ReadAsync(client, deadline.Token); Assert.Single(snapshot!.Controllers);
        var frame = EngineTests.Frame(1, PadButtons.R1) with { SessionId = publisher.SessionId, ConnectionId = Guid.NewGuid() };
        publisher.Publish(frame); Assert.Equal(frame, (await BridgeProtocol.ReadAsync(client, deadline.Token))!.Frame);
        publisher.PublishDisconnected("pad", frame.ConnectionId!.Value);
        Assert.Equal(BridgeMessageKind.Disconnected, (await BridgeProtocol.ReadAsync(client, deadline.Token))!.Kind);
        await publisher.DisposeAsync();
        // Canceling a Windows pipe write can leave a truncated final heartbeat. Both paths mean source loss.
        try { while (await BridgeProtocol.ReadAsync(client, deadline.Token) is not null) { } }
        catch (EndOfStreamException) { }
    }
    [Fact]
    public async Task SlowConsumerDoesNotBlockProducerAndReceivesObservableGap()
    {
        string name = "MacroGamer-Overflow-" + Guid.NewGuid().ToString("N");
        await using var publisher = new InputPipePublisher(() => [new("pad", "DualSense", "USB", null)], pipeName: name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.In, InputPipeIdentity.RestrictedOptions);
        await client.ConnectAsync(5000, deadline.Token); await BridgeProtocol.ReadAsync(client, deadline.Token);
        var frame = EngineTests.Frame(0) with { SessionId = publisher.SessionId };
        for (int i = 0; i < 50_000; i++) publisher.Publish(frame with { Sequence = i, TimestampUs = i });
        Assert.True(publisher.DroppedFrames > 0);
        bool gap = false;
        for (int i = 0; i < 512; i++)
        {
            var message = await BridgeProtocol.ReadAsync(client, deadline.Token);
            if (message!.Kind == BridgeMessageKind.Gap) { gap = true; Assert.True(message.DroppedFrames > 0); break; }
        }
        Assert.True(gap);
    }
    [Fact]
    public async Task ReopeningPhysicalIdentityInSameSourceSessionDisarmsEngine()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = EngineTests.Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        var connection = Guid.NewGuid(); await engine.ConfigureAsync([macro], "pad"); engine.Enable();
        engine.ProcessFrame(EngineTests.Frame(0) with { ConnectionId = connection });
        engine.ProcessFrame(EngineTests.Frame(1, PadButtons.R1) with { ConnectionId = connection });
        await EngineTests.Eventually(() => output.Events.Count == 1);
        engine.ProcessFrame(EngineTests.Frame(2, PadButtons.R1) with { ConnectionId = Guid.NewGuid() });
        await EngineTests.Eventually(() => engine.Snapshot.ActiveMacros.Length == 0);
        Assert.False(engine.Snapshot.Enabled); Assert.Empty(await engine.Ledger.HeldAsync());
    }
    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
