using System.Text.Json;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;
using DualSenseTweaker.MacroGamer.Output;
using DualSenseTweaker.MacroGamer.Scripting;

namespace DualSenseTweaker.MacroGamer.Presentation;

public sealed class CompanionRuntime : IAsyncDisposable
{
    private readonly string _workerPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private Task? _inputTask;
    private MacroEngine _engine;
    private ScriptCoordinator _scripts;
    private ControllerDescriptor[] _controllers = [];
    private bool _sourceOnline;
    private PadFrame? _lastFrame;
    private InputRecording? _lastRecording;
    private string? _controllerId;
    private int _disposed;
    public DiagnosticLog Diagnostics { get; } = new();
    public MacroProfileStore Store { get; }
    public MacroProfile ActiveProfile { get; private set; }
    public CompanionSettings Settings { get; private set; }
    public InputRecorder Recorder { get; } = new();
    public InputRecording? LastRecording { get => Volatile.Read(ref _lastRecording); private set => Volatile.Write(ref _lastRecording, value); }
    public void AcknowledgeRecording(InputRecording recording) => Interlocked.CompareExchange(ref _lastRecording, null, recording);
    public string? SourceError { get; private set; }
    public MacroEngine Engine => _engine;
    public ScriptCoordinator Scripts => _scripts;
    public ControllerDescriptor[] Controllers => Volatile.Read(ref _controllers);
    public bool SourceOnline => Volatile.Read(ref _sourceOnline);
    public string? SelectedControllerId => _controllerId;
    public ControllerDescriptor? SelectedController => Controllers.FirstOrDefault(c => string.Equals(c.Id, _controllerId, StringComparison.OrdinalIgnoreCase));
    public event Action? Changed;
    public CompanionRuntime(string workerPath, string? dataRoot = null)
    {
        _workerPath = workerPath; Store = new(dataRoot ?? MacroProfileStore.DefaultRoot, Diagnostics);
        Settings = Store.LoadSettings(); var profiles = Store.LoadAll();
        ActiveProfile = profiles.FirstOrDefault(p => p.Id == Settings.SelectedProfileId) ?? profiles[0];
        _controllerId = ActiveProfile.ControllerId;
        _engine = CreateEngine(ActiveProfile.Backend); _scripts = new(_engine, _workerPath);
    }
    private MacroEngine CreateEngine(OutputBackendKind backend)
    {
        IInputOutputBackend output = backend switch
        { OutputBackendKind.KeyboardMouse => new WindowsSendInputBackend(), OutputBackendKind.VirtualController => new UnavailableVirtualControllerBackend(), _ => new DiagnosticOutputBackend() };
        var engine = new MacroEngine(output, Diagnostics); engine.Changed += OnEngineChanged; return engine;
    }
    private void OnEngineChanged(EngineSnapshot snapshot) => Notify();
    public async Task StartInputAsync(string? pipeName = null)
    {
        await _engine.ConfigureAsync(ActiveProfile.Macros, _controllerId);
        _inputTask = Task.Run(() => new InputPipeClient(pipeName).RunAsync(ReceiveAsync, SourceDisconnectedAsync, _stop.Token));
    }
    public async ValueTask ReceiveAsync(BridgeMessage message)
    {
        message.Validate();
        await _operations.WaitAsync();
        try
        {
        switch (message.Kind)
        {
            case BridgeMessageKind.Snapshot:
                Volatile.Write(ref _sourceOnline, true); SourceError = null; Volatile.Write(ref _controllers, message.Controllers);
                string? selected = ActiveProfile.ControllerId ?? message.Controllers.FirstOrDefault()?.Id;
                if (_controllerId != selected)
                {
                    await StopAllAsync("Controller selection changed"); _controllerId = selected; _lastFrame = null;
                    await _engine.ConfigureAsync(ActiveProfile.Macros, selected);
                }
                if (SelectedController is null && _lastFrame is not null) { await StopAllAsync("Selected controller disconnected"); _lastFrame = null; }
                break;
            case BridgeMessageKind.Frame when message.Frame is { } frame:
                if (!Controllers.Any(c => c.Id.Equals(frame.ControllerId, StringComparison.OrdinalIgnoreCase)) ||
                    !string.Equals(frame.ControllerId, _controllerId, StringComparison.OrdinalIgnoreCase)) return;
                if (_lastFrame is not null && (_lastFrame.SessionId != frame.SessionId || _lastFrame.ConnectionId != frame.ConnectionId))
                    await StopAllAsync("Controller connection generation changed");
                _lastFrame = frame;
                try { Recorder.Observe(frame); }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                {
                    Diagnostics.Write(ex.Message, "Error");
                    if (Recorder.Count > 0) LastRecording = Recorder.Stop();
                }
                _engine.ProcessFrame(frame); _scripts.ProcessFrame(frame);
                break;
            case BridgeMessageKind.Disconnected when message.ControllerId == _controllerId:
                if (_lastFrame is null || message.ConnectionId == _lastFrame.ConnectionId)
                { await StopAllAsync("Controller disconnected"); _lastFrame = null; Volatile.Write(ref _controllers, Controllers.Where(c => !c.Id.Equals(message.ControllerId, StringComparison.OrdinalIgnoreCase)).ToArray()); }
                break;
            case BridgeMessageKind.Gap:
                Diagnostics.Write($"Input queue dropped {message.DroppedFrames} frames. Enable explicitly after input stabilizes.", "Error");
                await StopAllAsync("Input gap"); break;
        }
        Notify();
        }
        finally { _operations.Release(); }
    }
    private async ValueTask SourceDisconnectedAsync(string reason)
    {
        Volatile.Write(ref _sourceOnline, false); Volatile.Write(ref _controllers, []); SourceError = reason; _lastFrame = null;
        await _operations.WaitAsync();
        try { await StopAllAsync(reason); }
        catch (Exception ex) { Diagnostics.Write(ex.Message, "Error"); }
        finally { _operations.Release(); }
        Notify();
    }
    public async Task ActivateAsync(MacroProfile profile)
    {
        profile.Validate(); await _operations.WaitAsync();
        try
        {
            await StopAllAsync("Profile changed");
            _engine.Changed -= OnEngineChanged; await _scripts.DisposeAsync(); await _engine.DisposeAsync();
            ActiveProfile = profile; LastRecording = null; _controllerId = profile.ControllerId ?? Controllers.FirstOrDefault()?.Id; _lastFrame = null;
            _engine = CreateEngine(profile.Backend); _scripts = new(_engine, _workerPath);
            await _engine.ConfigureAsync(profile.Macros, _controllerId);
            Settings = Settings with { SelectedProfileId = profile.Id }; await Task.Run(() => Store.SaveSettings(Settings));
        }
        finally { _operations.Release(); Notify(); }
    }
    public async Task SaveProfileAsync(MacroProfile profile)
    {
        profile.Validate();
        bool reconfigure = profile.Backend != ActiveProfile.Backend || profile.ControllerId != ActiveProfile.ControllerId ||
            JsonSerializer.Serialize(profile.Macros) != JsonSerializer.Serialize(ActiveProfile.Macros);
        await Task.Run(() => Store.Save(profile));
        if (reconfigure) await ActivateAsync(profile);
        else { ActiveProfile = profile; Notify(); }
    }
    public async Task EnableAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (ActiveProfile.Backend != OutputBackendKind.Diagnostic && (!SourceOnline || SelectedController is null || _lastFrame is null))
                throw new InvalidOperationException("Connect a real controller through Tweaker before enabling keyboard/mouse output. Diagnostic output can run without hardware.");
            await _scripts.StopAllAsync(); await _engine.ConfigureAsync(ActiveProfile.Macros, _controllerId);
            if (ActiveProfile.Backend != OutputBackendKind.Diagnostic && (!SourceOnline || SelectedController is null || _lastFrame is null))
                throw new InvalidOperationException("Controller disconnected while arming.");
            _engine.Enable();
        }
        finally { _operations.Release(); Notify(); }
    }
    public async Task StopAllAsync(string reason = "Emergency stop")
    {
        if (Recorder.IsRecording && Recorder.Count > 0) LastRecording = Recorder.Stop();
        else if (Recorder.IsRecording) { try { Recorder.Stop(); } catch (InvalidDataException) { } }
        try { await _engine.StopAllAsync(reason); }
        finally { await _scripts.StopAllAsync(); Notify(); }
    }
    public async Task StartRecordingAsync()
    {
        if (!SourceOnline || SelectedController is null) throw new InvalidOperationException("Select a connected controller before recording.");
        await StopAllAsync("Recording started"); LastRecording = null; Recorder.Start(SelectedController.Id); Notify();
    }
    public InputRecording StopRecording() { LastRecording = Recorder.Stop(); Notify(); return LastRecording; }
    public async Task SaveSettingsAsync(CompanionSettings settings)
    { settings.Validate(); await Task.Run(() => Store.SaveSettings(settings)); Settings = settings; Notify(); }
    private void Notify()
    {
        if (Changed is null) return;
        foreach (Action callback in Changed.GetInvocationList())
            try { callback(); } catch (Exception ex) { Diagnostics.Write($"Presentation observer: {ex.Message}", "Error"); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { await StopAllAsync("Application closing"); }
        finally
        {
            if (_inputTask is not null) await _inputTask;
            _engine.Changed -= OnEngineChanged; await _scripts.DisposeAsync(); await _engine.DisposeAsync(); _stop.Dispose();
        }
    }
}
