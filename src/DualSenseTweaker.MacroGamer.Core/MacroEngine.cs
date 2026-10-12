using DualSenseTweaker.ControllerBridge;

namespace DualSenseTweaker.MacroGamer.Core;

public interface IExecutionClock
{
    long Timestamp { get; }
    TimeSpan Elapsed(long start);
    ValueTask DelayAsync(TimeSpan delay, CancellationToken token);
}
public sealed class StopwatchExecutionClock : IExecutionClock
{
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    public TimeSpan Elapsed(long start) => System.Diagnostics.Stopwatch.GetElapsedTime(start);
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken token) => await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, token);
}

public sealed class MacroEngine : IAsyncDisposable
{
    private sealed class Run(MacroDefinition macro)
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public MacroDefinition Macro { get; } = macro;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private readonly Lock _gate = new();
    private readonly InputTransitionDetector _transitions = new();
    private readonly Dictionary<Guid, TriggerMatcher> _matchers = [];
    private readonly Dictionary<Guid, Run> _runs = [];
    private readonly IExecutionClock _clock;
    private MacroDefinition[] _macros = [];
    private string? _controllerId;
    private PadFrame? _current;
    private bool _enabled, _stopping, _disposed;
    private Task? _stopTask;
    private ExecutionState _state = ExecutionState.Idle;
    private string? _lastError;
    public MacroEngine(IInputOutputBackend backend, DiagnosticLog? diagnostics = null, IExecutionClock? clock = null)
    {
        Diagnostics = diagnostics ?? new(); Ledger = new(backend, Diagnostics); _clock = clock ?? new StopwatchExecutionClock();
    }
    public DiagnosticLog Diagnostics { get; }
    public OutputLedger Ledger { get; }
    public event Action<EngineSnapshot>? Changed;
    public EngineSnapshot Snapshot { get { lock (_gate) return Capture(); } }
    public PadFrame? CurrentInput { get { lock (_gate) return _current; } }
    public async Task ConfigureAsync(IEnumerable<MacroDefinition> macros, string? controllerId)
    {
        var copies = macros.Select(m => { m.Validate(); return m.Snapshot(); }).ToArray();
        if (copies.Length > 256 || copies.Select(m => m.Id).Distinct().Count() != copies.Length)
            throw new InvalidDataException("Too many macros or duplicate macro identities.");
        await StopAllAsync("Profile changed");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _macros = copies; _controllerId = controllerId; _current = null; _transitions.Reset(); _matchers.Clear();
            foreach (var macro in copies) _matchers[macro.Id] = new();
            _state = ExecutionState.Idle; _lastError = null;
        }
        Publish();
    }
    public void Enable()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping) throw new InvalidOperationException("Wait for Stop All to complete.");
            if (_lastError is not null) throw new InvalidOperationException("Resolve the error and configure the profile before arming.");
            if (!Ledger.Backend.IsAvailable) throw new InvalidOperationException(Ledger.Backend.UnavailableReason);
            _enabled = true; _state = ExecutionState.Armed;
            _transitions.Reset(); _matchers.Clear();
            if (_current is not null) _transitions.Update(_current);
            foreach (var macro in _macros) _matchers[macro.Id] = new();
        }
        Diagnostics.Write("Engine explicitly armed."); Publish();
    }
    public bool Start(Guid macroId)
    {
        bool started;
        lock (_gate)
        {
            var macro = _macros.FirstOrDefault(m => m.Id == macroId) ?? throw new ArgumentException("Macro does not exist.");
            started = StartLocked(macro);
        }
        Publish(); return started;
    }
    public void ProcessFrame(PadFrame frame)
    {
        bool sessionChanged;
        lock (_gate)
        {
            if (_disposed || _stopping || _controllerId is not null && !string.Equals(_controllerId, frame.ControllerId, StringComparison.OrdinalIgnoreCase)) return;
            _controllerId ??= frame.ControllerId;
            sessionChanged = _current is not null && _current.SessionId != frame.SessionId;
            if (sessionChanged)
            {
                _enabled = false;
                foreach (var run in _runs.Values) run.Cancellation.Cancel();
                _transitions.Reset(); _matchers.Clear();
                foreach (var macro in _macros) _matchers[macro.Id] = new();
            }
            var changes = _transitions.Update(frame);
            if (changes is null) return;
            _current = frame;
            foreach (var macro in _macros.Where(m => _enabled && m.Enabled))
            {
                bool matches = _matchers[macro.Id].Match(macro.Trigger, changes);
                if (_runs.TryGetValue(macro.Id, out var active))
                {
                    if (macro.Mode == RunMode.Hold && !TriggerMatcher.IsActive(macro.Trigger, frame) || macro.Mode == RunMode.Toggle && matches)
                        active.Cancellation.Cancel();
                    continue;
                }
                if (matches) StartLocked(macro);
            }
        }
        if (sessionChanged) _ = ObserveStopAsync("Controller session changed");
        Publish();
    }
    private async Task ObserveStopAsync(string reason)
    {
        try { await StopAllAsync(reason); }
        catch (Exception ex) { Diagnostics.Write(ex.Message, "Error"); }
    }
    public Task DisconnectAsync(string controllerId)
    {
        lock (_gate)
        {
            _transitions.Disconnect(controllerId);
            if (_controllerId is not null && !string.Equals(_controllerId, controllerId, StringComparison.OrdinalIgnoreCase) ||
                _controllerId is null && _current is not null && !string.Equals(_current.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
            _current = null;
        }
        return StopAllAsync("Controller disconnected");
    }
    public Task StopAllAsync(string reason = "Emergency stop")
    {
        Task result;
        lock (_gate)
        {
            if (_stopping) return _stopTask!;
            _enabled = false; _stopping = true; _state = ExecutionState.Stopping;
            var runs = _runs.Values.ToArray();
            foreach (var run in runs) run.Cancellation.Cancel();
            _stopTask = Task.Run(() => FinishStopAsync(runs, reason)); result = _stopTask;
        }
        Publish(); return result;
    }
    private async Task FinishStopAsync(Run[] runs, string reason)
    {
        try
        {
            await Task.WhenAll(runs.Select(r => r.Completion));
            await Ledger.ReleaseAllAsync();
            lock (_gate) _state = _lastError is null ? ExecutionState.Stopped : ExecutionState.Error;
            Diagnostics.Write($"Stopped: {reason}");
        }
        catch (Exception ex) { lock (_gate) { _lastError = ex.Message; _state = ExecutionState.Error; } Diagnostics.Write(ex.Message, "Error"); throw; }
        finally { lock (_gate) _stopping = false; Publish(); }
    }
    private bool StartLocked(MacroDefinition macro)
    {
        if (!_enabled || _stopping || _disposed || !macro.Enabled || _runs.ContainsKey(macro.Id)) return false;
        if (_runs.Count >= 8) { Diagnostics.Write("Concurrent run limit reached.", "Warning"); return false; }
        var run = new Run(macro.Snapshot());
        _runs[macro.Id] = run; _state = ExecutionState.Running;
        run.Completion = Task.Run(() => ExecuteAsync(run));
        return true;
    }
    private async Task ExecuteAsync(Run run)
    {
        var macro = run.Macro;
        var token = run.Cancellation.Token;
        using var deadline = new CancellationTokenSource(macro.MaxRuntimeMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var cancellation = linked.Token;
        long start = _clock.Timestamp;
        TimeSpan due = TimeSpan.Zero;
        int actions = 0;
        try
        {
            int repetitions = macro.RepeatWhileActive ? 1000 : macro.RepeatCount;
            for (int repeat = 0; repeat < repetitions; repeat++)
            {
                foreach (var action in macro.Actions)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++actions > 100_000 || _clock.Elapsed(start).TotalMilliseconds >= macro.MaxRuntimeMs)
                        throw new InvalidOperationException("Macro resource/runtime quota exceeded.");
                    await WaitUntilAsync(start, due, cancellation);
                    switch (action.Kind)
                    {
                        case ActionKind.Delay: due += TimeSpan.FromMilliseconds(action.DurationMs); break;
                        case ActionKind.KeyDown:
                        case ActionKind.MouseDown: await Ledger.PressAsync(run.Owner, action.Control, cancellation); due = AfterOutput(start, due, 4); break;
                        case ActionKind.KeyUp:
                        case ActionKind.MouseUp: await Ledger.ReleaseAsync(run.Owner, action.Control); due = AfterOutput(start, due, 4); break;
                        case ActionKind.KeyTap:
                        case ActionKind.MouseTap:
                            await Ledger.PressAsync(run.Owner, action.Control, cancellation);
                            due = AfterOutput(start, due, Math.Max(4, action.DurationMs));
                            await WaitUntilAsync(start, due, cancellation);
                            await Ledger.ReleaseAsync(run.Owner, action.Control); due = AfterOutput(start, due, 4); break;
                        case ActionKind.MouseMove: await Ledger.Backend.MoveMouseAsync(action.X, action.Y, cancellation); due = AfterOutput(start, due, 4); break;
                        case ActionKind.MouseWheel: await Ledger.Backend.WheelAsync(action.Y, cancellation); due = AfterOutput(start, due, 4); break;
                    }
                }
                await WaitUntilAsync(start, due, cancellation);
                // Even an empty-duration sequence cannot become an unbounded busy loop.
                if (macro.RepeatWhileActive) { due += TimeSpan.FromMilliseconds(4); await WaitUntilAsync(start, due, cancellation); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (deadline.IsCancellationRequested && !token.IsCancellationRequested) Diagnostics.Write($"Runtime limit stopped {macro.Name}.", "Warning");
        }
        catch (Exception ex) { RecordError(ex); Diagnostics.Write($"{macro.Name}: {ex.Message}", "Error"); }
        finally
        {
            try { await Ledger.ReleaseOwnerAsync(run.Owner); }
            catch (Exception ex) { RecordError(ex); Diagnostics.Write(ex.Message, "Error"); }
            lock (_gate)
            {
                _runs.Remove(macro.Id);
                if (_state != ExecutionState.Error && !_stopping) _state = _runs.Count > 0 ? ExecutionState.Running : _enabled ? ExecutionState.Armed : ExecutionState.Stopped;
            }
            run.Cancellation.Dispose(); Publish();
        }
    }
    private TimeSpan AfterOutput(long start, TimeSpan due, int delayMs) =>
        (_clock.Elapsed(start) > due ? _clock.Elapsed(start) : due) + TimeSpan.FromMilliseconds(delayMs);
    private void RecordError(Exception ex)
    {
        lock (_gate)
        {
            _lastError = ex.Message; _enabled = false; _state = ExecutionState.Error;
            foreach (var run in _runs.Values) run.Cancellation.Cancel();
        }
    }
    private async ValueTask WaitUntilAsync(long start, TimeSpan due, CancellationToken token)
    {
        var remaining = due - _clock.Elapsed(start);
        if (remaining > TimeSpan.Zero) await _clock.DelayAsync(remaining, token);
        var late = _clock.Elapsed(start) - due;
        if (late > TimeSpan.FromMilliseconds(15)) Diagnostics.Write($"Scheduling delay: {late.TotalMilliseconds:F1} ms.", "Warning");
    }
    private EngineSnapshot Capture() => new(_state, _enabled, _controllerId, _runs.Values.Select(r => r.Macro.Name).ToArray(), _lastError);
    private void Publish()
    {
        var snapshot = Snapshot;
        if (Changed is null) return;
        foreach (Action<EngineSnapshot> subscriber in Changed.GetInvocationList())
            try { subscriber(snapshot); } catch (Exception ex) { Diagnostics.Write($"State observer failed: {ex.Message}", "Error"); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        await StopAllAsync("Application closing");
    }
}
