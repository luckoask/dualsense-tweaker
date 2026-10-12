namespace DualSenseTweaker.MacroGamer.Core;

public sealed record OutputEvent(long Timestamp, string Kind, OutputControl? Control = null, int X = 0, int Y = 0);
public interface IInputOutputBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    ValueTask SetControlAsync(OutputControl control, bool pressed, CancellationToken cancellationToken);
    ValueTask MoveMouseAsync(int x, int y, CancellationToken cancellationToken);
    ValueTask WheelAsync(int delta, CancellationToken cancellationToken);
}

public sealed class DiagnosticOutputBackend : IInputOutputBackend
{
    private readonly Lock _gate = new();
    private readonly Queue<OutputEvent> _events = new();
    public string Name => "Diagnostic";
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public IReadOnlyList<OutputEvent> Events { get { lock (_gate) return _events.ToArray(); } }
    public void Clear() { lock (_gate) _events.Clear(); }
    private void Add(OutputEvent item) { lock (_gate) { _events.Enqueue(item); while (_events.Count > 4096) _events.Dequeue(); } }
    public ValueTask SetControlAsync(OutputControl control, bool pressed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); control.Validate();
        Add(new(System.Diagnostics.Stopwatch.GetTimestamp(), pressed ? "Down" : "Up", control));
        return ValueTask.CompletedTask;
    }
    public ValueTask MoveMouseAsync(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Add(new(System.Diagnostics.Stopwatch.GetTimestamp(), "Move", X: x, Y: y));
        return ValueTask.CompletedTask;
    }
    public ValueTask WheelAsync(int delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Add(new(System.Diagnostics.Stopwatch.GetTimestamp(), "Wheel", Y: delta));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Only this ledger owns synthetic holds. Failed releases remain owned and retryable.</summary>
public sealed class OutputLedger(IInputOutputBackend backend, DiagnosticLog diagnostics)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<OutputControl, HashSet<Guid>> _owners = [];
    public IInputOutputBackend Backend => backend;
    public async Task<IReadOnlyList<OutputControl>> HeldAsync()
    {
        await _gate.WaitAsync();
        try { return _owners.Keys.ToArray(); } finally { _gate.Release(); }
    }
    public async ValueTask PressAsync(Guid owner, OutputControl control, CancellationToken token)
    {
        control = control.Normalize(); control.Validate();
        await _gate.WaitAsync(token);
        try
        {
            if (!backend.IsAvailable) throw new InvalidOperationException(backend.UnavailableReason ?? "Output backend unavailable.");
            if (_owners.TryGetValue(control, out var owners)) { owners.Add(owner); return; }
            // Retain ownership even when a backend reports failure after a partial native operation.
            _owners[control] = [owner];
            await backend.SetControlAsync(control, true, token);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask ReleaseAsync(Guid owner, OutputControl control)
    {
        control = control.Normalize();
        await _gate.WaitAsync();
        try
        {
            if (!_owners.TryGetValue(control, out var owners) || !owners.Contains(owner)) return;
            if (owners.Count > 1) { owners.Remove(owner); return; }
            await backend.SetControlAsync(control, false, CancellationToken.None);
            _owners.Remove(control);
        }
        finally { _gate.Release(); }
    }
    public async Task ReleaseOwnerAsync(Guid owner)
    {
        OutputControl[] controls;
        await _gate.WaitAsync();
        try { controls = _owners.Where(x => x.Value.Contains(owner)).Select(x => x.Key).ToArray(); }
        finally { _gate.Release(); }
        var errors = new List<Exception>();
        foreach (var control in controls)
        {
            try { await ReleaseAsync(owner, control); }
            catch (Exception ex) { diagnostics.Write($"Release failed for {control}: {ex.Message}", "Error"); errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException("Synthetic controls remain held.", errors);
    }
    public async Task ReleaseAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var control in _owners.Keys.ToArray())
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { await backend.SetControlAsync(control, false, CancellationToken.None); _owners.Remove(control); break; }
                    catch (Exception ex) { diagnostics.Write($"Release retry {attempt + 1} for {control}: {ex.Message}", "Error"); }
                }
            }
            if (_owners.Count != 0) throw new InvalidOperationException("Output backend could not release all synthetic controls; retry Stop All.");
        }
        finally { _gate.Release(); }
    }
}
