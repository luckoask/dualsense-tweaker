using System.Diagnostics;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.Core.Diagnostics;

namespace DualSenseTweaker.Core.Device;

/// <summary>Passive, read-only input sharing. No companion/output assemblies are referenced by the main application.</summary>
public sealed class ControllerInputBridge : IAsyncDisposable
{
    private readonly DualSenseManager _manager;
    private readonly InputPipePublisher _publisher;
    private long _sequence;
    private int _disposed;
    public ControllerInputBridge(DualSenseManager manager)
    {
        _manager = manager;
        _publisher = new(() => manager.Devices.Select(d => new ControllerDescriptor(d.Info.Key, d.Info.ModelName, d.Info.ConnectionName,
            d.Status is { BatteryState: not BatteryState.Unknown } status ? status.BatteryPercent : null)).ToArray(), Log.Warn);
        manager.AllInputReceived += OnInput; manager.DeviceDisconnected += OnDisconnected;
    }
    private void OnInput(ControllerInfo info, Guid connectionId, InputFrame frame, TimeSpan deviceTime)
    {
        if (_publisher.ConnectedClients == 0 || Volatile.Read(ref _disposed) != 0) return;
        long timestamp = (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
        _publisher.Publish(new(info.Key, _publisher.SessionId, Interlocked.Increment(ref _sequence), timestamp, (PadButtons)(uint)frame.Buttons,
            new(frame.Analog.LeftX, frame.Analog.LeftY, frame.Analog.RightX, frame.Analog.RightY, frame.Analog.L2, frame.Analog.R2),
            new(frame.First.Active, frame.First.Id, frame.First.X, frame.First.Y),
            new(frame.Second.Active, frame.Second.Id, frame.Second.X, frame.Second.Y), connectionId));
    }
    private void OnDisconnected(ControllerInfo info, Guid connectionId) => _publisher.PublishDisconnected(info.Key, connectionId);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manager.AllInputReceived -= OnInput; _manager.DeviceDisconnected -= OnDisconnected;
        await _publisher.DisposeAsync().ConfigureAwait(false);
    }
}
