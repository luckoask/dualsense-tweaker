using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

namespace DualSenseTweaker.ControllerBridge;

public static class InputPipeIdentity
{
    public static string CurrentUserPipeName
    {
        get
        {
            string identity;
            if (OperatingSystem.IsWindows()) { using var user = WindowsIdentity.GetCurrent(); identity = user.User?.Value ?? throw new InvalidOperationException("Current Windows user SID is unavailable."); }
            else identity = Environment.UserName;
            return "LCKS.DualSenseInput.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        }
    }
    public const PipeOptions RestrictedOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
}

/// <summary>Read-only fanout. Publish only enqueues; it never waits on consumers or performs HID/native output.</summary>
public sealed class InputPipePublisher : IAsyncDisposable
{
    private sealed class Subscriber(NamedPipeServerStream pipe)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public Channel<BridgeMessage> Queue { get; } = Channel.CreateBounded<BridgeMessage>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        public long Dropped;
    }
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, Task> _writers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ControllerDescriptor[]> _controllers;
    private readonly Action<string>? _diagnostics;
    private readonly string _pipeName;
    private readonly Task _accept;
    private long _totalDropped;
    private int _disposed;
    public Guid SessionId { get; } = Guid.NewGuid();
    public long DroppedFrames => Interlocked.Read(ref _totalDropped);
    public int ConnectedClients => _subscribers.Count;
    public InputPipePublisher(Func<ControllerDescriptor[]> controllers, Action<string>? diagnostics = null, string? pipeName = null)
    {
        _controllers = controllers; _diagnostics = diagnostics; _pipeName = pipeName ?? InputPipeIdentity.CurrentUserPipeName;
        _accept = Task.Run(AcceptAsync);
    }
    public void Publish(PadFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var message = new BridgeMessage { SessionId = SessionId, Kind = BridgeMessageKind.Frame, Frame = frame };
        Enqueue(message);
    }
    public void PublishDisconnected(string controllerId, Guid connectionId) => Enqueue(new()
    {
        SessionId = SessionId, Kind = BridgeMessageKind.Disconnected, ControllerId = controllerId, ConnectionId = connectionId,
    });
    private void Enqueue(BridgeMessage message)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (subscriber.Queue.Writer.TryWrite(message)) continue;
            Interlocked.Increment(ref subscriber.Dropped); Interlocked.Increment(ref _totalDropped);
        }
    }
    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new(_pipeName, PipeDirection.Out, 3, PipeTransmissionMode.Byte, InputPipeIdentity.RestrictedOptions);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var id = Guid.NewGuid(); var subscriber = new Subscriber(pipe); pipe = null;
                _subscribers[id] = subscriber;
                var task = Task.Run(() => WriteClientAsync(id, subscriber)); _writers[id] = task;
                _ = RemoveWriterAsync(id, task);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _diagnostics?.Invoke($"Input pipe listener: {ex.Message}"); await Task.Delay(500, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            finally { pipe?.Dispose(); }
        }
    }
    private async Task RemoveWriterAsync(Guid id, Task task)
    {
        try { await task; }
        finally { _writers.TryRemove(id, out _); }
    }
    private async Task WriteClientAsync(Guid id, Subscriber subscriber)
    {
        long sentDropped = 0;
        try
        {
            await SendSnapshotAsync(subscriber.Pipe, _stop.Token);
            var heartbeat = System.Diagnostics.Stopwatch.StartNew();
            while (!_stop.IsCancellationRequested)
            {
                if (heartbeat.Elapsed >= TimeSpan.FromSeconds(1)) { await SendSnapshotAsync(subscriber.Pipe, _stop.Token); heartbeat.Restart(); }
                long dropped = Interlocked.Read(ref subscriber.Dropped);
                if (dropped != sentDropped)
                {
                    await WriteWithTimeoutAsync(subscriber.Pipe, new() { SessionId = SessionId, Kind = BridgeMessageKind.Gap, DroppedFrames = dropped }, _stop.Token);
                    sentDropped = dropped;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); wait.CancelAfter(1000);
                bool ready;
                try { ready = await subscriber.Queue.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { continue; }
                if (!ready) break;
                // Check for a gap again before delivering a frame that may otherwise start new outputs.
                if (Interlocked.Read(ref subscriber.Dropped) != sentDropped) continue;
                if (subscriber.Queue.Reader.TryRead(out var message)) await WriteWithTimeoutAsync(subscriber.Pipe, message, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
        { _diagnostics?.Invoke($"Input pipe client closed: {ex.Message}"); }
        finally { _subscribers.TryRemove(id, out _); subscriber.Queue.Writer.TryComplete(); await subscriber.Pipe.DisposeAsync(); }
    }
    private ValueTask SendSnapshotAsync(Stream pipe, CancellationToken token) => WriteWithTimeoutAsync(pipe,
        new() { SessionId = SessionId, Kind = BridgeMessageKind.Snapshot, Controllers = _controllers() }, token);
    private static async ValueTask WriteWithTimeoutAsync(Stream pipe, BridgeMessage message, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(2000);
        await BridgeProtocol.WriteAsync(pipe, message, deadline.Token);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        foreach (var subscriber in _subscribers.Values) subscriber.Queue.Writer.TryComplete();
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_writers.Values).ConfigureAwait(false);
        _stop.Dispose();
    }
}

public sealed class InputPipeClient(string? pipeName = null)
{
    public async Task RunAsync(Func<BridgeMessage, ValueTask> receive, Func<string, ValueTask> disconnected, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName ?? InputPipeIdentity.CurrentUserPipeName,
                    PipeDirection.In, InputPipeIdentity.RestrictedOptions);
                await pipe.ConnectAsync(1000, token);
                Guid? session = null;
                while (!token.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token); heartbeat.CancelAfter(3000);
                    var message = await BridgeProtocol.ReadAsync(pipe, heartbeat.Token);
                    if (message is null) throw new EndOfStreamException("Tweaker input source closed.");
                    if (session is not null && message.SessionId != session) throw new InvalidDataException("Input source session changed unexpectedly.");
                    session = message.SessionId;
                    await receive(message);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
            { await disconnected(ex is OperationCanceledException ? "Input heartbeat timed out." : ex.Message); }
            await Task.Delay(1000, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        await disconnected("Input client stopped.");
    }
}
