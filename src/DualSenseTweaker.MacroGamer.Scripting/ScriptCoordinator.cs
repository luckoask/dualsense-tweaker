using System.Threading.Channels;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Scripting;

/// <summary>Parent-side script lifecycle. Physical triggers enter a bounded queue; only the macro engine emits approved outputs.</summary>
public sealed class ScriptCoordinator : IAsyncDisposable
{
    private sealed class ActiveScript(ScriptDefinition definition, ScriptWorkerClient worker, ScriptBinding[] bindings, Dictionary<string, double> variables)
    {
        public ScriptDefinition Definition { get; } = definition;
        public ScriptWorkerClient Worker { get; } = worker;
        public ScriptBinding[] Bindings { get; } = bindings;
        public Dictionary<string, double> Variables { get; } = variables;
        public Dictionary<int, TriggerMatcher> Matchers { get; } = bindings.ToDictionary(b => b.Index, _ => new TriggerMatcher());
        public CancellationTokenSource Stop { get; } = new();
    }
    private sealed record Invocation(ActiveScript Script, int Handler, PadFrame Frame);
    private readonly MacroEngine _engine;
    private readonly string _workerPath;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<Guid, ActiveScript> _scripts = [];
    private readonly InputTransitionDetector _transitions = new();
    private readonly Channel<Invocation> _queue = Channel.CreateBounded<Invocation>(new BoundedChannelOptions(64)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _consumer;
    private bool _disposed;
    public ScriptCoordinator(MacroEngine engine, string workerPath)
    { _engine = engine; _workerPath = workerPath; _consumer = Task.Run(ConsumeAsync); }
    public IReadOnlyList<string> RunningScripts { get { lock (_gate) return _scripts.Values.Where(s => !s.Stop.IsCancellationRequested).Select(s => s.Definition.Name).ToArray(); } }
    public async Task<ScriptResponse> ValidateAsync(ScriptDefinition definition, IReadOnlyDictionary<string, double> variables, CancellationToken token = default)
    {
        definition.Validate(); await using var worker = new ScriptWorkerClient(_workerPath, _engine.Diagnostics);
        return await worker.RequestAsync(new() { Operation = ScriptOperation.Validate, Source = definition.Source,
            Name = SourceName(definition.Name), Variables = new(variables), Input = _engine.CurrentInput }, token);
    }
    public async Task StartAsync(ScriptDefinition definition, IReadOnlyDictionary<string, double> variables, CancellationToken token = default)
    {
        definition.Validate();
        if (!definition.Enabled) throw new InvalidOperationException("Enable this script in the profile before running it.");
        if (!_engine.Snapshot.Enabled) throw new InvalidOperationException("Enable the engine before running scripts.");
        await _lifecycle.WaitAsync(token);
        ScriptWorkerClient? worker = null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_scripts.ContainsKey(definition.Id)) throw new InvalidOperationException("Stop the running script before replacing its immutable snapshot.");
                if (_scripts.Count >= 4) throw new InvalidOperationException("At most four scripts can run concurrently.");
            }
            worker = new(_workerPath, _engine.Diagnostics);
            var response = await worker.RequestAsync(new() { Source = definition.Source, Name = SourceName(definition.Name),
                Variables = new(variables), Input = _engine.CurrentInput }, token);
            if (!response.Success) throw new InvalidDataException(response.Error);
            var active = new ActiveScript(definition with { }, worker, response.Bindings, new(variables));
            lock (_gate)
            {
                if (!_engine.Snapshot.Enabled) throw new InvalidOperationException("Engine stopped while script was loading.");
                _scripts[definition.Id] = active;
                _transitions.Reset();
                if (_engine.CurrentInput is { } frame) _transitions.Update(frame);
            }
            worker = null;
            await ApplyResponseAsync(active, response);
            _engine.Diagnostics.Write($"Script registered: {definition.Name}");
        }
        catch (Exception ex)
        {
            _engine.Diagnostics.Write($"Script start failed: {ex.Message}", "Error");
            await _engine.StopAllAsync("Script start failed");
            throw;
        }
        finally { if (worker is not null) await worker.DisposeAsync(); _lifecycle.Release(); }
    }
    public void ProcessFrame(PadFrame frame)
    {
        bool overflow = false;
        lock (_gate)
        {
            if (_disposed || !_engine.Snapshot.Enabled) return;
            string? selected = _engine.Snapshot.ControllerId;
            if (selected is not null && !string.Equals(selected, frame.ControllerId, StringComparison.OrdinalIgnoreCase)) return;
            var changes = _transitions.Update(frame); if (changes is null) return;
            foreach (var script in _scripts.Values)
                foreach (var binding in script.Bindings)
                    if (script.Matchers[binding.Index].Match(new() { Kind = binding.Kind, Buttons = binding.Buttons }, changes) &&
                        !_queue.Writer.TryWrite(new(script, binding.Index, frame))) overflow = true;
        }
        if (overflow) _ = FaultAsync("Script trigger queue overflow; execution disarmed.");
    }
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var invocation in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                var script = invocation.Script;
                if (script.Stop.IsCancellationRequested || !_engine.Snapshot.Enabled) continue;
                try
                {
                    var response = await script.Worker.RequestAsync(new() { Operation = ScriptOperation.Invoke,
                        HandlerIndex = invocation.Handler, Input = invocation.Frame, Variables = script.Variables }, script.Stop.Token);
                    if (!response.Success) throw new InvalidDataException(response.Error);
                    await ApplyResponseAsync(script, response);
                }
                catch (OperationCanceledException) when (script.Stop.IsCancellationRequested) { }
                catch (Exception ex) { await FaultAsync($"{script.Definition.Name}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ApplyResponseAsync(ActiveScript script, ScriptResponse response)
    {
        if (script.Stop.IsCancellationRequested || !_engine.Snapshot.Enabled) return;
        foreach (string line in response.Logs) _engine.Diagnostics.Write($"{script.Definition.Name}: {line}");
        if (response.CancelRequested)
        {
            lock (_gate) foreach (var active in _scripts.Values) active.Stop.Cancel();
            await _engine.StopAllAsync("Script requested emergency stop"); return;
        }
        string runName = "Script: " + script.Definition.Name;
        if (response.Actions.Length > 0) await _engine.ExecuteSequenceAsync(runName[..Math.Min(runName.Length, 128)], response.Actions, script.Stop.Token);
    }
    private static string SourceName(string name) => name[..Math.Min(name.Length, 124)] + ".lua";
    private async Task FaultAsync(string message)
    {
        _engine.Diagnostics.Write(message, "Error");
        lock (_gate) foreach (var script in _scripts.Values) script.Stop.Cancel();
        try { await _engine.StopAllAsync("Script failure"); }
        catch (Exception ex) { _engine.Diagnostics.Write(ex.Message, "Error"); }
    }
    public async Task StopAllAsync()
    {
        // Cancel before acquiring lifecycle: script initialization may itself be awaiting a long sequence.
        lock (_gate) foreach (var script in _scripts.Values) script.Stop.Cancel();
        Exception? failure = null;
        try { await _engine.StopAllAsync("Scripts stopped"); }
        catch (Exception ex) { failure = ex; }
        await _lifecycle.WaitAsync();
        try
        {
            ActiveScript[] scripts;
            lock (_gate) { scripts = _scripts.Values.ToArray(); _scripts.Clear(); _transitions.Reset(); foreach (var script in scripts) script.Stop.Cancel(); }
            foreach (var script in scripts) await script.Worker.DisposeAsync();
        }
        finally { _lifecycle.Release(); }
        if (failure is not null) throw new InvalidOperationException("Scripts stopped, but output release failed.", failure);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        try { await StopAllAsync(); }
        finally { _stop.Cancel(); _queue.Writer.TryComplete(); await _consumer; _stop.Dispose(); }
    }
}
