namespace DualSenseTweaker.Core.Device;

/// <summary>Coordinates LED takeover between the input and output threads.</summary>
internal sealed class LightbarInitialization(ConnectionType connection)
{
    // SDL waits for this controller timestamp before releasing the Bluetooth startup animation.
    // The sensor clock runs at 3 MHz, so this is about 3.4 seconds after power-on.
    internal const uint BluetoothReadyTimestamp = 10_200_000;

    private const int Waiting = 0, Ready = 1, Complete = 2;
    private int _state = connection == ConnectionType.Bluetooth ? Waiting : Ready;

    public bool NeedsReset => Volatile.Read(ref _state) == Ready;

    /// <returns>True once, when the writer should wake to take over the lightbar.</returns>
    public bool ObserveSensorTimestamp(uint timestamp) =>
        timestamp >= BluetoothReadyTimestamp && Interlocked.CompareExchange(ref _state, Ready, Waiting) == Waiting;

    public OutputGroups FilterGroups(OutputGroups groups) =>
        Volatile.Read(ref _state) == Complete ? groups : groups & ~OutputGroups.Lightbar;

    /// <summary>Called only after the takeover report was successfully written.</summary>
    public void MarkResetSent() => Interlocked.CompareExchange(ref _state, Complete, Ready);
}
