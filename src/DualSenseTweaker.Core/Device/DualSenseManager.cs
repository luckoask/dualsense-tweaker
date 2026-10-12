using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;
using HidSharp;

namespace DualSenseTweaker.Core.Device;

/// <summary>An open controller as the controller list shows it.</summary>
public sealed record DeviceSnapshot(ControllerInfo Info, ControllerStatus? Status, FirmwareInfo? Firmware);

/// <summary>
/// Finds DualSense controllers and keeps every controlled one connected at the same time, each
/// with its own output sources. One of them is the selected controller: the UI edits it, and its
/// input (touchpad, gyro) and connection details are published. All device opening and closing
/// happens on the manager's own thread.
/// </summary>
public sealed class DualSenseManager : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const string BluetoothHidServiceGuid = "00001124-0000-1000-8000-00805f9b34fb";

    private readonly Func<ControllerInfo, OutputSources?> _sourcesFor;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _macByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFactoryReset = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HidDevice> _hidByPath = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ControllerInfo> _controllers = [];
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];
    private ConnectionSnapshot _connection = ConnectionSnapshot.Disconnected(L.Text("Hľadám ovládač…"));
    private DualSenseDevice? _selected;
    private string? _selectedKey;
    private string? _lastFailure;
    private volatile bool _listDirty = true;
    private volatile bool _managedDirty;
    private volatile bool _disposed;
    private bool _started;

    private sealed record OpenDevice(DualSenseDevice Device, Action<InputFrame, TimeSpan> InputHandler, Guid ConnectionId);

    /// <summary>Every controller shares the same sources (single-profile setups, tools, tests).</summary>
    public DualSenseManager(OutputSources sources) : this(_ => sources)
    {
    }

    /// <param name="sourcesFor">
    /// What to send to a controller; null leaves it alone (not controlled, e.g. after a factory reset).
    /// Must return the same instance for the same controller. Called on the manager thread.
    /// </param>
    public DualSenseManager(Func<ControllerInfo, OutputSources?> sourcesFor)
    {
        _sourcesFor = sourcesFor;
        _thread = new Thread(Run) { IsBackground = true, Name = "DualSense manager" };
    }

    public event EventHandler? ControllersChanged;

    /// <summary>Raised when <see cref="Connection"/> (the selected controller) changes; status updates come through <see cref="StatusChanged"/>.</summary>
    public event EventHandler? ConnectionChanged;

    /// <summary>Raised when battery or jack state of the selected controller changes; read <see cref="Connection"/>.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>A controller was opened or closed, or its battery changed; read <see cref="Devices"/>.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Every full input report of the selected controller (input thread, ~250×/s).</summary>
    public event Action<InputFrame, TimeSpan>? InputReceived;
    /// <summary>Identity-bearing input for passive consumers. Subscribers must only enqueue; raised on HID reader threads.</summary>
    public event Action<ControllerInfo, Guid, InputFrame, TimeSpan>? AllInputReceived;
    /// <summary>Identity/generation closed by the manager; passive consumers must release their synthetic outputs.</summary>
    public event Action<ControllerInfo, Guid>? DeviceDisconnected;

    /// <summary>The PS button of a connected controller was pressed 5× quickly (raised on its input thread).</summary>
    public event EventHandler<ControllerInfo>? PsButtonSequence;

    /// <summary>All DualSense controllers Windows currently sees.</summary>
    public IReadOnlyList<ControllerInfo> Controllers => Volatile.Read(ref _controllers);

    /// <summary>The controllers that are open and controlled right now.</summary>
    public IReadOnlyList<DeviceSnapshot> Devices => Volatile.Read(ref _snapshots);

    /// <summary>The selected controller.</summary>
    public ConnectionSnapshot Connection => Volatile.Read(ref _connection);

    public DeviceStatistics? Statistics => Volatile.Read(ref _selected)?.Statistics;

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        DeviceList.Local.Changed += OnDeviceListChanged;
        _thread.Start();
    }

    /// <summary>Makes this controller the selected one (the one the UI edits).</summary>
    public void Select(ControllerInfo controller) => Select(controller.Key);

    /// <summary>Selects by key (MAC address); takes effect once that controller is connected.</summary>
    public void Select(string key)
    {
        lock (_gate)
            _selectedKey = key;
        _signal.Set();
    }

    /// <summary>Asks <c>sourcesFor</c> again for every controller: opens newly controlled ones, closes released ones.</summary>
    public void RefreshManaged()
    {
        _managedDirty = true;
        _signal.Set();
    }

    /// <summary>Puts the controller back to its factory look and closes it (it stays closed while sourcesFor returns null).</summary>
    public void FactoryReset(string key)
    {
        lock (_gate)
            _pendingFactoryReset.Add(key);
        _managedDirty = true;
        _signal.Set();
    }

    /// <summary>Re-sends the given groups to every connected controller right away (e.g. "Otestovať").</summary>
    /// <returns>False when no controller is connected.</returns>
    public bool RequestResend(OutputGroups groups)
    {
        List<DualSenseDevice> devices;
        lock (_gate)
            devices = [.. _devices.Values.Select(d => d.Device)];
        foreach (var device in devices)
            device.RequestResend(groups);
        return devices.Count > 0;
    }

    public void Rescan()
    {
        _listDirty = true;
        lock (_gate)
            _retryAt.Clear();
        _signal.Set();
    }

    /// <summary>Stops the motors of every controller, closes them and stops the manager thread.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_started)
            return;
        DeviceList.Local.Changed -= OnDeviceListChanged;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        _listDirty = true;
        _signal.Set();
    }

    private void Run()
    {
        while (!_disposed)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error(L.Text("Chyba pri správe ovládačov"), ex);
            }
            _signal.WaitOne(PollInterval);
        }
        foreach (var key in OpenKeys())
            Close(key, factoryReset: false);
        PublishDevices();
    }

    private void Tick()
    {
        bool changed = false;

        foreach (var (key, device) in OpenEntries().Where(e => e.Device.HasFailed).ToList())
        {
            _lastFailure = L.Format($"{device.FailureReason} Čakám na opätovné pripojenie…");
            Close(key, factoryReset: false);
            _listDirty = true;
            changed = true;
        }

        if (_listDirty || OpenKeys().Count == 0)
        {
            _listDirty = false;
            RefreshControllers();
        }
        var controllers = Controllers;

        // One entry per controller: a controller plugged in by cable can briefly show up twice (USB first).
        var present = controllers.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        List<string> factoryResets;
        lock (_gate)
        {
            factoryResets = [.. _pendingFactoryReset];
            _pendingFactoryReset.Clear();
        }
        foreach (var key in factoryResets.Where(k => OpenKeys().Contains(k, StringComparer.OrdinalIgnoreCase)))
        {
            Close(key, factoryReset: true);
            changed = true;
        }

        foreach (var (key, device) in OpenEntries().ToList())
        {
            if (!present.Any(c => SamePath(c, device.Info)))
            {
                Close(key, factoryReset: false);
                _lastFailure = L.Text("Ovládač bol odpojený. Čakám na opätovné pripojenie…");
                changed = true;
            }
        }

        bool recheck = _managedDirty;
        _managedDirty = false;
        foreach (var info in present)
        {
            bool open = OpenKeys().Contains(info.Key, StringComparer.OrdinalIgnoreCase);
            if (open && !recheck)
                continue;
            var sources = _sourcesFor(info);
            if (open)
            {
                if (sources is null)
                {
                    Close(info.Key, factoryReset: false); // released by the user
                    changed = true;
                }
                continue;
            }
            if (sources is null || DateTime.UtcNow < RetryAt(info.Key))
                continue;
            changed |= Connect(info, sources);
        }

        changed |= UpdateSelection(present);
        if (changed)
            PublishDevices();
    }

    private bool Connect(ControllerInfo target, OutputSources sources)
    {
        if (!_hidByPath.TryGetValue(target.DevicePath, out var hid))
            return false;
        try
        {
            var device = DualSenseDevice.Open(target, hid, sources);
            var connectionId = Guid.NewGuid();
            Action<InputFrame, TimeSpan> handler = (frame, time) =>
            {
                AllInputReceived?.Invoke(target, connectionId, frame, time);
                if (ReferenceEquals(Volatile.Read(ref _selected), device))
                    InputReceived?.Invoke(frame, time);
            };
            device.Failed += OnDeviceFailed;
            device.StatusChanged += OnDeviceStatusChanged;
            device.PsButtonSequence += OnDevicePsButtonSequence;
            device.InputReceived += handler;
            lock (_gate)
            {
                _devices[target.Key] = new OpenDevice(device, handler, connectionId);
                _retryAt.Remove(target.Key);
            }
            _lastFailure = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            Log.Warn($"Pripojenie k {target.DisplayName} zlyhalo: {ex.Message}");
            lock (_gate)
                _retryAt[target.Key] = DateTime.UtcNow + RetryDelay;
            _lastFailure = L.Format($"Pripojenie k {target.DisplayName} zlyhalo: {ex.Message} Skúšam znova…");
            return false;
        }
    }

    /// <summary>Keeps the user's choice while that controller is connected, otherwise picks the first connected one.</summary>
    private bool UpdateSelection(IReadOnlyList<ControllerInfo> present)
    {
        DualSenseDevice? selected;
        lock (_gate)
        {
            if (_selectedKey is null || !_devices.ContainsKey(_selectedKey))
            {
                var first = present.FirstOrDefault(c => _devices.ContainsKey(c.Key));
                if (first is not null)
                    _selectedKey = first.Key;
            }
            selected = _selectedKey is not null && _devices.TryGetValue(_selectedKey, out var open) ? open.Device : null;
        }

        var previous = Volatile.Read(ref _selected);
        Volatile.Write(ref _selected, selected);

        ConnectionSnapshot snapshot;
        if (selected is not null)
        {
            int others = OpenKeys().Count - 1;
            snapshot = new ConnectionSnapshot(selected.Info, selected.Firmware, selected.UsesVibrationV2, selected.Status,
                L.Format($"Pripojené: {selected.Info.DisplayName}{(others > 0 ? L.Format($" (+ ďalšie: {others})") : "")}"))
            {
                Calibration = selected.Calibration,
            };
        }
        else
        {
            var controllers = Controllers;
            snapshot = ConnectionSnapshot.Disconnected(
                controllers.Count == 0 ? L.Text("Ovládač DualSense sa nenašiel. Pripoj ho cez USB alebo Bluetooth.")
                : _lastFailure ?? L.Text("Ovládač je v pôvodnom stave – priraď mu profil v sekcii Zariadenie."));
        }
        SetConnection(snapshot);
        return !ReferenceEquals(previous, selected);
    }

    private void OnDeviceFailed(object? sender, EventArgs e) => _signal.Set();

    private void OnDevicePsButtonSequence(object? sender, EventArgs e)
    {
        if (sender is DualSenseDevice device)
            PsButtonSequence?.Invoke(this, device.Info);
    }

    private void OnDeviceStatusChanged(object? sender, ControllerStatus status)
    {
        if (ReferenceEquals(sender, Volatile.Read(ref _selected)))
        {
            bool updated = false;
            lock (_gate)
            {
                if (_connection.IsConnected && ReferenceEquals(sender, _selected))
                {
                    _connection = _connection with { Status = status };
                    updated = true;
                }
            }
            if (updated)
                StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        PublishDevices();
    }

    private void Close(string key, bool factoryReset)
    {
        OpenDevice? entry;
        lock (_gate)
        {
            if (!_devices.Remove(key, out entry))
                return;
            if (ReferenceEquals(_selected, entry.Device))
                _selected = null;
        }
        var device = entry.Device;
        DeviceDisconnected?.Invoke(device.Info, entry.ConnectionId);
        device.StatusChanged -= OnDeviceStatusChanged;
        device.PsButtonSequence -= OnDevicePsButtonSequence;
        device.InputReceived -= entry.InputHandler;
        device.Failed -= OnDeviceFailed;
        if (factoryReset)
            device.ResetToFactoryAndClose();
        else
            device.Dispose();
        Log.Info(L.Format($"Zatvorené: {device.Info.DisplayName}"));
    }

    private void PublishDevices()
    {
        List<DeviceSnapshot> snapshots;
        lock (_gate)
            snapshots = [.. _devices.Values.Select(d => new DeviceSnapshot(d.Device.Info, d.Device.Status, d.Device.Firmware))];
        Volatile.Write(ref _snapshots, snapshots);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetConnection(ConnectionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot == _connection)
                return;
            _connection = snapshot;
        }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<string> OpenKeys()
    {
        lock (_gate)
            return [.. _devices.Keys];
    }

    private List<(string Key, DualSenseDevice Device)> OpenEntries()
    {
        lock (_gate)
            return [.. _devices.Select(d => (d.Key, d.Value.Device))];
    }

    private DateTime RetryAt(string key)
    {
        lock (_gate)
            return _retryAt.TryGetValue(key, out var at) ? at : DateTime.MinValue;
    }

    private void RefreshControllers()
    {
        var found = new List<(ControllerInfo Info, HidDevice Hid)>();
        foreach (var hid in DeviceList.Local.GetHidDevices(DualSenseProtocol.SonyVendorId))
        {
            if (hid.ProductID is not (DualSenseProtocol.DualSenseProductId or DualSenseProtocol.DualSenseEdgeProductId))
                continue;
            if (Describe(hid) is { } info)
                found.Add((info, hid));
        }

        var presentPaths = found.Select(f => f.Info.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _macByPath.Keys.Where(path => !presentPaths.Contains(path)).ToList())
            _macByPath.Remove(stale);
        _hidByPath = found.ToDictionary(f => f.Info.DevicePath, f => f.Hid, StringComparer.OrdinalIgnoreCase);

        var controllers = found
            .Select(f => f.Info)
            .OrderBy(c => c.Connection)
            .ThenBy(c => c.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (controllers.SequenceEqual(Controllers))
            return;

        Volatile.Write(ref _controllers, controllers);
        Log.Info(controllers.Count == 0
            ? L.Text("Nenašiel sa žiadny ovládač.")
            : L.Text("Nájdené ovládače: ") + string.Join(", ", controllers.Select(c => c.DisplayName)));
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private ControllerInfo? Describe(HidDevice hid)
    {
        try
        {
            int maxInput = hid.GetMaxInputReportLength();
            if (maxInput <= 0 || hid.GetMaxOutputReportLength() <= 0)
                return null;

            var connection = hid.DevicePath.Contains(BluetoothHidServiceGuid, StringComparison.OrdinalIgnoreCase)
                             || maxInput >= DualSenseProtocol.InputReportBtSize
                ? ConnectionType.Bluetooth
                : ConnectionType.Usb;

            if (!_macByPath.TryGetValue(hid.DevicePath, out var mac) && ProbeMacAddress(hid) is { } probed)
                _macByPath[hid.DevicePath] = mac = probed;

            return new ControllerInfo(hid.DevicePath, hid.ProductID, connection, mac);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Log.Warn(L.Format($"Zariadenie {hid.DevicePath} sa nepodarilo načítať: {ex.Message}"));
            return null;
        }
    }

    /// <summary>Reads the MAC from the pairing-info feature report; falls back to the HID serial number.</summary>
    private static string? ProbeMacAddress(HidDevice hid)
    {
        if (hid.TryOpen(out HidStream stream))
        {
            using (stream)
            {
                var report = FeatureReports.TryGet(stream, hid.GetMaxFeatureReportLength(),
                    DualSenseProtocol.FeaturePairingInfo, DualSenseProtocol.FeaturePairingInfoSize);
                if (report is not null && FeatureReports.ParseMacAddress(report) is { } mac)
                    return mac;
            }
        }

        try
        {
            var serial = hid.GetSerialNumber()?.Replace(":", "").Replace("-", "");
            if (serial is { Length: 12 } && serial.All(Uri.IsHexDigit))
                return string.Join(':', serial.ToUpperInvariant().Chunk(2).Select(pair => new string(pair)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // No serial number available.
        }
        return null;
    }

    private static bool SamePath(ControllerInfo a, ControllerInfo b) =>
        string.Equals(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase);
}
