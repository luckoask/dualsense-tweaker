using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Presentation;

public sealed class MainViewModel : ObservableModel, IAsyncDisposable
{
    private readonly CompanionRuntime _runtime;
    private readonly IUserInteraction _interaction;
    private readonly Action<Action> _post;
    private readonly Func<string, string> _text;
    private readonly List<AsyncCommand> _commands = [];
    private MacroEditor? _macro; private ScriptEditor? _script; private ActionEditor? _action;
    private MacroProfile? _profileCandidate; private ControllerDescriptor? _controllerCandidate;
    private InputRecording? _recording; private RecordingMapping? _mapping;
    private int _section; private string _status = "", _profileName = "", _game = "", _variables = "{}", _scriptError = "";
    private OutputBackendKind _backend; private string _language, _theme;
    private bool _autoGames, _disposed; private long _lastUiPost; private string? _lastAutomaticGame;
    public ObservableCollection<MacroEditor> Macros { get; } = [];
    public ObservableCollection<ScriptEditor> Scripts { get; } = [];
    public ObservableCollection<MacroProfile> Profiles { get; } = [];
    public ObservableCollection<ControllerDescriptor> Controllers { get; } = [];
    public ObservableCollection<InputRecording> Recordings { get; } = [];
    public ObservableCollection<RecordingMapping> RecordingMappings { get; } = [new("R1", "E"), new("Cross", "SPACE"), new("Circle", "Q"), new("Square", "R")];
    public ObservableCollection<DiagnosticEntry> Diagnostics { get; } = [];
    public ObservableCollection<OutputEvent> OutputEvents { get; } = [];
    public NavigationItem[] Navigation => new[] { "Prehľad", "Ovládače", "Makrá", "Editor makier", "Skripty", "Profily", "Diagnostika", "Nastavenia" }.Select((n, i) => new NavigationItem(i.ToString(), T(n))).ToArray();
    public Choice<ActionKind>[] ActionKinds => Enum.GetValues<ActionKind>().Select(k => new Choice<ActionKind>(k, k.ToString())).ToArray();
    public Choice<TriggerKind>[] TriggerKinds => Enum.GetValues<TriggerKind>().Where(k => k != TriggerKind.Disconnected).Select(k => new Choice<TriggerKind>(k, k.ToString())).ToArray();
    public Choice<RunMode>[] RunModes => Enum.GetValues<RunMode>().Select(k => new Choice<RunMode>(k, k.ToString())).ToArray();
    public Choice<OutputBackendKind>[] Backends => [new(OutputBackendKind.Diagnostic, T("Diagnostický výstup")), new(OutputBackendKind.KeyboardMouse, T("Klávesnica a myš")), new(OutputBackendKind.VirtualController, T("Virtuálny ovládač — nedostupný"), false)];
    public string[] Languages => ["SK", "EN", "CZ"];
    public string[] Themes => ["Dark", "Light"];
    public string[] AnalogTriggers => ["L2", "R2"];
    public string[] ButtonNames => Enum.GetNames<PadButtons>().Where(n => n != "None").ToArray();
    public string[] Samples => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "samples")) ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.lua").Select(Path.GetFileName).OfType<string>().ToArray() : [];
    public string? SelectedSample { get; set; }
    public int Section { get => _section; set => Set(ref _section, value); }
    public MacroEditor? SelectedMacro { get => _macro; set { Set(ref _macro, value); SelectedAction = value?.Actions.FirstOrDefault(); RefreshCommands(); } }
    public ScriptEditor? SelectedScript { get => _script; set { Set(ref _script, value); RefreshCommands(); } }
    public ActionEditor? SelectedAction { get => _action; set { Set(ref _action, value); RefreshCommands(); } }
    public MacroProfile? ProfileCandidate { get => _profileCandidate; set => Set(ref _profileCandidate, value); }
    public ControllerDescriptor? ControllerCandidate { get => _controllerCandidate; set => Set(ref _controllerCandidate, value); }
    public InputRecording? SelectedRecording { get => _recording; set { Set(ref _recording, value); Raise(nameof(RecordingSummary)); RefreshCommands(); } }
    public RecordingMapping? SelectedMapping { get => _mapping; set => Set(ref _mapping, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string GameExecutable { get => _game; set => Set(ref _game, value); }
    public string VariablesJson { get => _variables; set => Set(ref _variables, value); }
    public string ScriptError { get => _scriptError; private set => Set(ref _scriptError, value); }
    public OutputBackendKind Backend { get => _backend; set => Set(ref _backend, value); }
    public string Language { get => _language; set { if (Set(ref _language, value)) AppearanceRequested?.Invoke(value, Theme); } }
    public string Theme { get => _theme; set { if (Set(ref _theme, value)) AppearanceRequested?.Invoke(Language, value); } }
    public bool AutoGameProfiles { get => _autoGames; set => Set(ref _autoGames, value); }
    public bool EngineEnabled => _runtime.Engine.Snapshot.Enabled;
    public string EngineState => _runtime.Engine.Snapshot.State.ToString();
    public string ActiveProfileName => _runtime.ActiveProfile.Name;
    public string ActiveMacros => _runtime.Engine.Snapshot.ActiveMacros.Length == 0 ? T("Žiadne aktívne makro") : string.Join(", ", _runtime.Engine.Snapshot.ActiveMacros);
    public string ActiveScripts => _runtime.Scripts.RunningScripts.Count == 0 ? T("Žiadny aktívny skript") : string.Join(", ", _runtime.Scripts.RunningScripts);
    public string ConnectionText => _runtime.SelectedController is { } controller ? $"{controller.Model} · {controller.Transport}" : T("Ovládač nie je pripojený");
    public string BatteryText => _runtime.SelectedController?.BatteryPercent is { } battery ? $"{battery}%" : "—";
    public string SourceText => _runtime.SourceOnline ? T("Tweaker poskytuje vstup") : T("Spusti DualSense Tweaker v sekcii Ovládače");
    public string SourceError => _runtime.SourceError ?? "";
    public string ControllerAssociation => _runtime.ActiveProfile.ControllerId ?? T("Automaticky prvý ovládač");
    public string BackendStatus => _runtime.Engine.Ledger.Backend.IsAvailable ? _runtime.Engine.Ledger.Backend.Name : _runtime.Engine.Ledger.Backend.UnavailableReason ?? T("Výstup nie je dostupný");
    public string InputText => _runtime.Engine.CurrentInput is { } frame ? $"{frame.Buttons}\nL: {frame.Analog.LeftX}, {frame.Analog.LeftY}   R: {frame.Analog.RightX}, {frame.Analog.RightY}\nL2: {frame.Analog.L2}   R2: {frame.Analog.R2}\nTouch: {frame.First.Active}, {frame.Second.Active}\n#{frame.Sequence} · {frame.TimestampUs} µs" : T("Čakám na skutočné vstupné dáta");
    public bool IsRecording => _runtime.Recorder.IsRecording;
    public string RecordingState => IsRecording ? T("Nahrávam vstupy") + $" · {_runtime.Recorder.Count}" : T("Nahrávanie je zastavené");
    public string RecordingSummary => SelectedRecording is { } recording ? $"{recording.Points.Length} · {recording.Points[^1].OffsetUs / 1000} ms · {recording.ControllerId}" : T("Zatiaľ žiadny záznam");
    public string HeldOutputs { get; private set; } = "—";
    public string DataDirectory => _runtime.Store.Root;
    public bool EmergencyAvailable { get; set; }
    public string EmergencyStatus { get; set; } = "Ctrl+Shift+F10";
    public event Action<string, string>? AppearanceRequested;
    public AsyncCommand EnableCommand { get; }
    public AsyncCommand StopAllCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand NewMacroCommand { get; }
    public AsyncCommand DeleteMacroCommand { get; }
    public AsyncCommand RunMacroCommand { get; }
    public AsyncCommand EditMacroCommand { get; }
    public AsyncCommand AddActionCommand { get; }
    public AsyncCommand RemoveActionCommand { get; }
    public AsyncCommand UpActionCommand { get; }
    public AsyncCommand DownActionCommand { get; }
    public AsyncCommand ImportMacroCommand { get; }
    public AsyncCommand ExportMacroCommand { get; }
    public AsyncCommand RecordCommand { get; }
    public AsyncCommand StopRecordCommand { get; }
    public AsyncCommand RecordingToMacroCommand { get; }
    public AsyncCommand DeleteRecordingCommand { get; }
    public AsyncCommand AddMappingCommand { get; }
    public AsyncCommand RemoveMappingCommand { get; }
    public AsyncCommand NewScriptCommand { get; }
    public AsyncCommand DeleteScriptCommand { get; }
    public AsyncCommand ValidateScriptCommand { get; }
    public AsyncCommand RunScriptCommand { get; }
    public AsyncCommand StopScriptsCommand { get; }
    public AsyncCommand LoadScriptCommand { get; }
    public AsyncCommand ExportScriptCommand { get; }
    public AsyncCommand LoadSampleCommand { get; }
    public AsyncCommand NewProfileCommand { get; }
    public AsyncCommand ActivateProfileCommand { get; }
    public AsyncCommand DuplicateProfileCommand { get; }
    public AsyncCommand DeleteProfileCommand { get; }
    public AsyncCommand ImportProfileCommand { get; }
    public AsyncCommand ExportProfileCommand { get; }
    public AsyncCommand PickGameCommand { get; }
    public AsyncCommand ApplyControllerCommand { get; }
    public AsyncCommand AutomaticControllerCommand { get; }
    public AsyncCommand LaunchTweakerCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public AsyncCommand OpenDataCommand { get; }
    public AsyncCommand ExportDiagnosticsCommand { get; }
    public AsyncCommand ClearDiagnosticsCommand { get; }
    private const string JsonFilter = "JSON (*.json)|*.json";
    private const string LuaFilter = "Lua (*.lua)|*.lua";
    public MainViewModel(CompanionRuntime runtime, IUserInteraction interaction, Action<Action> post, Func<string, string> text)
    {
        _runtime = runtime; _interaction = interaction; _post = post; _text = text;
        _language = runtime.Settings.Language; _theme = runtime.Settings.Theme; _autoGames = runtime.Settings.AutoGameProfiles;
        EnableCommand = Command(async () => { if (!EmergencyAvailable) throw new InvalidOperationException(T("Núdzová klávesová skratka nie je dostupná")); await SaveAsync(); await runtime.EnableAsync(); Status = T("Engine je zapnutý"); }, () => !EngineEnabled);
        StopAllCommand = Command(async () => { await runtime.StopAllAsync(); Status = T("Všetky vstupy boli zastavené"); });
        SaveCommand = Command(SaveAsync);
        NewMacroCommand = Command(() => { if (Macros.Count >= 256) throw new InvalidDataException("Macro limit reached."); var macro = new MacroEditor(); Macros.Add(macro); SelectedMacro = macro; Section = 3; return Task.CompletedTask; });
        DeleteMacroCommand = Command(async () => { if (SelectedMacro is null || !interaction.Confirm(T("Odstrániť vybrané makro?"))) return; Macros.Remove(SelectedMacro); SelectedMacro = Macros.FirstOrDefault(); await SaveAsync(); }, () => SelectedMacro is not null);
        RunMacroCommand = Command(async () => { var id = SelectedMacro?.Id ?? throw new InvalidOperationException("Select a macro."); await SaveAsync(); if (!runtime.Engine.Start(id)) throw new InvalidOperationException(T("Zapni engine a povoľ vybrané makro")); }, () => SelectedMacro is not null && EngineEnabled);
        EditMacroCommand = Command(() => { Section = 3; return Task.CompletedTask; }, () => SelectedMacro is not null);
        AddActionCommand = Command(() => { var action = new ActionEditor(new() { Kind = ActionKind.Delay, DurationMs = 100 }); SelectedMacro!.Actions.Add(action); SelectedAction = action; return Task.CompletedTask; }, () => SelectedMacro is not null);
        RemoveActionCommand = Command(() => { SelectedMacro!.Actions.Remove(SelectedAction!); SelectedAction = SelectedMacro.Actions.FirstOrDefault(); return Task.CompletedTask; }, () => SelectedMacro is not null && SelectedAction is not null);
        UpActionCommand = Command(() => MoveAction(-1), () => SelectedAction is not null && SelectedMacro?.Actions.IndexOf(SelectedAction) > 0);
        DownActionCommand = Command(() => MoveAction(1), () => SelectedAction is not null && SelectedMacro is not null && SelectedMacro.Actions.IndexOf(SelectedAction) < SelectedMacro.Actions.Count - 1);
        ImportMacroCommand = Command(async () => { string? path = interaction.OpenFile(JsonFilter); if (path is null) return; var macro = await Task.Run(() => MacroProfileStore.ImportMacro(path)); var editor = new MacroEditor(macro with { Id = Guid.NewGuid(), Enabled = false }); Macros.Add(editor); SelectedMacro = editor; await SaveAsync(); });
        ExportMacroCommand = Command(async () => { var macro = SelectedMacro!.ToDefinition(); string? path = interaction.SaveFile("macro.json", JsonFilter); if (path is not null) await Task.Run(() => MacroProfileStore.ExportMacro(macro, path)); }, () => SelectedMacro is not null);
        RecordCommand = Command(runtime.StartRecordingAsync, () => !IsRecording);
        StopRecordCommand = Command(async () => { var recording = runtime.StopRecording(); if (Recordings.Count >= 16) throw new InvalidDataException("Delete a recording first (limit 16)."); Recordings.Add(recording); SelectedRecording = recording; await SaveAsync(); }, () => IsRecording);
        RecordingToMacroCommand = Command(async () =>
        {
            var mappings = new Dictionary<PadButtons, OutputControl>();
            foreach (var map in RecordingMappings) { var button = MacroEditor.ParseButtons(map.Button); var control = OutputControl.Key(map.Key); control.Validate(); if (!mappings.TryAdd(button, control)) throw new InvalidDataException("Duplicate recording mapping."); }
            var macro = SelectedRecording!.ToKeyboardMacro(T("Nahrané makro"), mappings); var editor = new MacroEditor(macro); Macros.Add(editor); SelectedMacro = editor; Section = 3; await SaveAsync();
        }, () => SelectedRecording is not null);
        DeleteRecordingCommand = Command(async () => { Recordings.Remove(SelectedRecording!); SelectedRecording = Recordings.FirstOrDefault(); await SaveAsync(); }, () => SelectedRecording is not null);
        AddMappingCommand = Command(() => { var mapping = new RecordingMapping(); RecordingMappings.Add(mapping); SelectedMapping = mapping; return Task.CompletedTask; });
        RemoveMappingCommand = Command(() => { if (SelectedMapping is not null) RecordingMappings.Remove(SelectedMapping); return Task.CompletedTask; });
        NewScriptCommand = Command(() => { if (Scripts.Count >= 64) throw new InvalidDataException("Script limit reached."); var script = new ScriptEditor(); Scripts.Add(script); SelectedScript = script; Section = 4; return Task.CompletedTask; });
        DeleteScriptCommand = Command(async () => { if (!interaction.Confirm(T("Odstrániť vybraný skript?"))) return; await runtime.Scripts.StopAllAsync(); Scripts.Remove(SelectedScript!); SelectedScript = Scripts.FirstOrDefault(); await SaveAsync(); }, () => SelectedScript is not null);
        ValidateScriptCommand = Command(async () => { var result = await runtime.Scripts.ValidateAsync(SelectedScript!.ToDefinition(), ParseVariables()); ScriptError = result.Success ? T("Skript je platný") : result.Error ?? "Validation failed."; }, () => SelectedScript is not null);
        RunScriptCommand = Command(async () => { await SaveAsync(); await runtime.Scripts.StartAsync(SelectedScript!.ToDefinition(), ParseVariables()); ScriptError = ""; }, () => SelectedScript is not null && EngineEnabled);
        StopScriptsCommand = Command(async () => { await runtime.Scripts.StopAllAsync(); Status = T("Skripty boli zastavené"); });
        LoadScriptCommand = Command(async () => { string? path = interaction.OpenFile(LuaFilter); if (path is null) return; var info = new FileInfo(path); if (info.Length > 256_000) throw new InvalidDataException("Script file too large."); var source = await File.ReadAllTextAsync(path); var script = new ScriptEditor(new() { Name = Path.GetFileNameWithoutExtension(path), Source = source }); script.ToDefinition(); Scripts.Add(script); SelectedScript = script; });
        ExportScriptCommand = Command(async () => { var script = SelectedScript!.ToDefinition(); string? path = interaction.SaveFile("script.lua", LuaFilter); if (path is not null) await File.WriteAllTextAsync(path, script.Source); }, () => SelectedScript is not null);
        LoadSampleCommand = Command(async () => { if (SelectedSample is null || !Samples.Contains(SelectedSample)) return; var script = new ScriptEditor(new() { Name = Path.GetFileNameWithoutExtension(SelectedSample), Source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", SelectedSample)) }); Scripts.Add(script); SelectedScript = script; });
        NewProfileCommand = Command(async () => { await SaveAsync(); var profile = new MacroProfile { Name = T("Nový profil") }; runtime.Store.Save(profile); await runtime.ActivateAsync(profile); LoadEditors(); });
        ActivateProfileCommand = Command(async () => { if (ProfileCandidate is null) return; var id = ProfileCandidate.Id; await SaveAsync(); var profile = runtime.Store.LoadAll().First(p => p.Id == id); await runtime.ActivateAsync(profile); LoadEditors(); });
        DuplicateProfileCommand = Command(async () => { await SaveAsync(); var profile = runtime.ActiveProfile.Duplicate(ProfileName + " (copy)"); runtime.Store.Save(profile); await runtime.ActivateAsync(profile); LoadEditors(); });
        DeleteProfileCommand = Command(async () => { if (Profiles.Count <= 1) throw new InvalidOperationException(T("Posledný profil nemožno odstrániť")); if (!interaction.Confirm(T("Odstrániť aktívny profil?"))) return; var id = runtime.ActiveProfile.Id; await runtime.StopAllAsync(); runtime.Store.Delete(id); await runtime.ActivateAsync(runtime.Store.LoadAll()[0]); LoadEditors(); });
        ImportProfileCommand = Command(async () => { string? path = interaction.OpenFile(JsonFilter); if (path is null) return; await SaveAsync(); var profile = await Task.Run(() => MacroProfileStore.Import(path)); profile = profile.Duplicate(profile.Name) with { Macros = profile.Macros.Select(m => m with { Id = Guid.NewGuid(), Enabled = false }).ToArray(), Scripts = profile.Scripts.Select(s => s with { Id = Guid.NewGuid(), Enabled = false }).ToArray() }; runtime.Store.Save(profile); await runtime.ActivateAsync(profile); LoadEditors(); });
        ExportProfileCommand = Command(async () => { await SaveAsync(); string? path = interaction.SaveFile("profile.json", JsonFilter); if (path is not null) await Task.Run(() => MacroProfileStore.Export(runtime.ActiveProfile, path)); });
        PickGameCommand = Command(() => { string? path = interaction.OpenFile("Windows executable (*.exe)|*.exe"); if (path is not null) GameExecutable = path; return Task.CompletedTask; });
        ApplyControllerCommand = Command(async () => { if (ControllerCandidate is null) throw new InvalidOperationException(T("Vyber pripojený ovládač")); await SaveAsync(); await runtime.SaveProfileAsync(runtime.ActiveProfile with { ControllerId = ControllerCandidate.Id }); RefreshStatus(); });
        AutomaticControllerCommand = Command(async () => { await SaveAsync(); await runtime.SaveProfileAsync(runtime.ActiveProfile with { ControllerId = null }); RefreshStatus(); });
        LaunchTweakerCommand = Command(() => { interaction.LaunchTweaker(); return Task.CompletedTask; });
        SaveSettingsCommand = Command(async () => { await runtime.SaveSettingsAsync(runtime.Settings with { Language = Language, Theme = Theme, AutoGameProfiles = AutoGameProfiles }); Status = T("Nastavenia boli uložené"); });
        OpenDataCommand = Command(() => { interaction.OpenFolder(DataDirectory); return Task.CompletedTask; });
        ExportDiagnosticsCommand = Command(async () => { string? path = interaction.SaveFile("macro-gamer-diagnostics.txt", "Text (*.txt)|*.txt"); if (path is not null) await File.WriteAllLinesAsync(path, runtime.Diagnostics.Entries.Select(e => $"{e.At:O} [{e.Level}] {e.Message}")); });
        ClearDiagnosticsCommand = Command(() => { runtime.Diagnostics.Clear(); Diagnostics.Clear(); if (runtime.Engine.Ledger.Backend is DiagnosticOutputBackend output) output.Clear(); OutputEvents.Clear(); return Task.CompletedTask; });
        runtime.Changed += OnRuntimeChanged; LoadEditors(); RestoreDraft(); RefreshStatus();
    }
    private string T(string source) => _text(source);
    private AsyncCommand Command(Func<Task> action, Func<bool>? enabled = null) { var command = new AsyncCommand(action, Error, enabled); _commands.Add(command); return command; }
    public void Error(Exception exception) { Status = exception.Message; ScriptError = exception.Message; _runtime.Diagnostics.Write(exception.Message, "Error"); RefreshCommands(); }
    private Task MoveAction(int amount) { var items = SelectedMacro!.Actions; int index = items.IndexOf(SelectedAction!); items.Move(index, index + amount); RefreshCommands(); return Task.CompletedTask; }
    private Dictionary<string, double> ParseVariables() => JsonSerializer.Deserialize<Dictionary<string, double>>(VariablesJson) ?? throw new InvalidDataException("Profile variables must be a JSON object.");
    private MacroProfile BuildProfile() => _runtime.ActiveProfile with
    { Name = ProfileName, GameExecutable = string.IsNullOrWhiteSpace(GameExecutable) ? null : GameExecutable, Backend = Backend, Variables = ParseVariables(), RecordingKeyMappings = RecordingMappings.ToDictionary(m => m.Button, m => m.Key, StringComparer.OrdinalIgnoreCase), Macros = Macros.Select(m => m.ToDefinition()).ToArray(), Scripts = Scripts.Select(s => s.ToDefinition()).ToArray(), Recordings = Recordings.ToArray() };
    public async Task SaveAsync()
    {
        await _runtime.SaveProfileAsync(BuildProfile());
        ReloadProfiles(); DeleteDraft(); Status = T("Profil bol uložený"); RefreshStatus();
    }
    private void LoadEditors()
    {
        var profile = _runtime.ActiveProfile; ProfileName = profile.Name; GameExecutable = profile.GameExecutable ?? ""; Backend = profile.Backend;
        VariablesJson = JsonSerializer.Serialize(profile.Variables, new JsonSerializerOptions { WriteIndented = true });
        Macros.Clear(); foreach (var macro in profile.Macros) Macros.Add(new(macro));
        Scripts.Clear(); foreach (var script in profile.Scripts) Scripts.Add(new(script));
        Recordings.Clear(); foreach (var recording in profile.Recordings) Recordings.Add(recording);
        RecordingMappings.Clear(); foreach (var (button, key) in profile.RecordingKeyMappings) RecordingMappings.Add(new(button, key));
        SelectedMacro = Macros.FirstOrDefault(); SelectedScript = Scripts.FirstOrDefault(); SelectedRecording = Recordings.FirstOrDefault(); ReloadProfiles(); RefreshStatus();
    }
    private void ReloadProfiles() { Profiles.Clear(); foreach (var profile in _runtime.Store.LoadAll()) Profiles.Add(profile); ProfileCandidate = Profiles.FirstOrDefault(p => p.Id == _runtime.ActiveProfile.Id); }
    private void OnRuntimeChanged()
    {
        long now = Stopwatch.GetTimestamp(); long previous = Interlocked.Read(ref _lastUiPost);
        if (Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(80)) return;
        Interlocked.Exchange(ref _lastUiPost, now); _post(RefreshStatus);
    }
    public void RefreshStatus()
    {
        if (_disposed) return;
        var controllers = _runtime.Controllers;
        if (!Controllers.SequenceEqual(controllers)) { Controllers.Clear(); foreach (var controller in controllers) Controllers.Add(controller); ControllerCandidate = Controllers.FirstOrDefault(c => c.Id == _runtime.SelectedControllerId); }
        var entries = _runtime.Diagnostics.Entries.TakeLast(200).ToArray(); Diagnostics.Clear(); foreach (var entry in entries) Diagnostics.Add(entry);
        if (_runtime.Engine.Ledger.Backend is DiagnosticOutputBackend output) { OutputEvents.Clear(); foreach (var item in output.Events.TakeLast(100)) OutputEvents.Add(item); }
        _ = UpdateHeldAsync(); RefreshAll(); RefreshCommands();
    }
    private async Task UpdateHeldAsync()
    { try { var held = await _runtime.Engine.Ledger.HeldAsync(); HeldOutputs = held.Count == 0 ? "—" : string.Join(", ", held); Raise(nameof(HeldOutputs)); } catch (Exception ex) { _runtime.Diagnostics.Write(ex.Message, "Error"); } }
    public void RefreshCommands() { foreach (var command in _commands) command.Refresh(); }
    public async Task EvaluateGamesAsync(string? foregroundExecutable)
    {
        if (!AutoGameProfiles || string.IsNullOrWhiteSpace(foregroundExecutable) || _lastAutomaticGame == foregroundExecutable) return;
        _lastAutomaticGame = foregroundExecutable;
        var match = Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.GameExecutable) && string.Equals(Path.GetFileName(p.GameExecutable), Path.GetFileName(foregroundExecutable), StringComparison.OrdinalIgnoreCase));
        if (match is null || match.Id == _runtime.ActiveProfile.Id) return;
        await SaveAsync(); await _runtime.ActivateAsync(match); LoadEditors(); Status = T("Automatický profil bol vybraný; engine zostáva vypnutý");
    }
    private string DraftPath => Path.Combine(DataDirectory, "editor-draft.json");
    // Raw editor buffers preserve invalid/incomplete edits on close without treating them as runnable profiles.
    private void SaveDraft()
    {
        Directory.CreateDirectory(DataDirectory);
        var draft = new EditorDraft { ProfileId = _runtime.ActiveProfile.Id, ProfileName = ProfileName, GameExecutable = GameExecutable, VariablesJson = VariablesJson, Backend = Backend,
            Macros = Macros.Select(m => new MacroDraft { Id = m.Id, Name = m.Name, Enabled = m.Enabled, Trigger = m.Trigger, Buttons = m.Buttons, RequiredButtons = m.RequiredButtons, AnalogTrigger = m.AnalogTrigger, HeldMs = m.HeldMs, Threshold = m.Threshold, Mode = m.Mode, RepeatCount = m.RepeatCount, RepeatWhileActive = m.RepeatWhileActive, MaxRuntimeMs = m.MaxRuntimeMs, Actions = m.Actions.Select(a => new ActionDraft(a.Kind, a.Code, a.Duration, a.X, a.Y)).ToArray() }).ToArray(),
            Scripts = Scripts.Select(s => new ScriptDraft(s.Id, s.Name, s.Enabled, s.Source)).ToArray(), Recordings = Recordings.ToArray(), Mappings = RecordingMappings.Select(m => new MappingDraft(m.Button, m.Key)).ToArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft); if (bytes.Length > MacroProfileStore.MaxFileBytes) throw new InvalidDataException("Editor draft exceeds storage limit.");
        string temporary = DraftPath + ".tmp"; File.WriteAllBytes(temporary, bytes); File.Move(temporary, DraftPath, true);
    }
    private void RestoreDraft()
    {
        if (!File.Exists(DraftPath)) return;
        try
        {
            if (new FileInfo(DraftPath).Length > MacroProfileStore.MaxFileBytes) throw new InvalidDataException("Oversized editor draft.");
            var draft = JsonSerializer.Deserialize<EditorDraft>(File.ReadAllText(DraftPath), new JsonSerializerOptions { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Invalid editor draft.");
            ValidateDraftStructure(draft);
            if (draft.ProfileId != _runtime.ActiveProfile.Id) return;
            ProfileName = draft.ProfileName; GameExecutable = draft.GameExecutable; VariablesJson = draft.VariablesJson; Backend = draft.Backend;
            Macros.Clear(); foreach (var m in draft.Macros) { if (m.Actions is null || m.Actions.Length > 2048) throw new InvalidDataException("Invalid draft actions."); var editor = new MacroEditor { Id = m.Id, Name = m.Name, Enabled = m.Enabled, Trigger = m.Trigger, Buttons = m.Buttons, RequiredButtons = m.RequiredButtons, AnalogTrigger = m.AnalogTrigger, HeldMs = m.HeldMs, Threshold = m.Threshold, Mode = m.Mode, RepeatCount = m.RepeatCount, RepeatWhileActive = m.RepeatWhileActive, MaxRuntimeMs = m.MaxRuntimeMs }; editor.Actions.Clear(); foreach (var action in m.Actions) editor.Actions.Add(new() { Kind = action.Kind, Code = action.Code, Duration = action.Duration, X = action.X, Y = action.Y }); Macros.Add(editor); }
            Scripts.Clear(); foreach (var s in draft.Scripts) Scripts.Add(new() { Id = s.Id, Name = s.Name, Enabled = s.Enabled, Source = s.Source });
            if (draft.Mappings is not null && draft.Mappings.Length <= 23) { RecordingMappings.Clear(); foreach (var m in draft.Mappings) RecordingMappings.Add(new(m.Button, m.Key)); }
            if (draft.Recordings is not null && draft.Recordings.Length <= 16) { foreach (var recording in draft.Recordings) recording.Validate(); Recordings.Clear(); foreach (var recording in draft.Recordings) Recordings.Add(recording); }
            SelectedMacro = Macros.FirstOrDefault(); SelectedScript = Scripts.FirstOrDefault(); Status = T("Rozpracované úpravy boli obnovené; engine je vypnutý");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { _runtime.Diagnostics.Write($"Editor draft rejected: {ex.Message}", "Error"); }
    }
    private static void ValidateDraftStructure(EditorDraft draft)
    {
        static bool Text(string? value, int maximum) => value is not null && value.Length <= maximum;
        if (draft.Format != 1 || draft.ProfileId == Guid.Empty || !Enum.IsDefined(draft.Backend) ||
            !Text(draft.ProfileName, 128) || !Text(draft.GameExecutable, 260) || !Text(draft.VariablesJson, 8192) ||
            draft.Macros is not { Length: <= 256 } || draft.Scripts is not { Length: <= 64 } ||
            draft.Mappings is not { Length: <= 23 } || draft.Recordings is not { Length: <= 16 })
            throw new InvalidDataException("Invalid editor draft structure.");
        foreach (var m in draft.Macros)
        {
            if (m is null || m.Id == Guid.Empty || !Text(m.Name, 128) || !Text(m.Buttons, 256) || !Text(m.RequiredButtons, 256) ||
                !Text(m.AnalogTrigger, 16) || !Text(m.HeldMs, 32) || !Text(m.Threshold, 32) || !Text(m.RepeatCount, 32) || !Text(m.MaxRuntimeMs, 32) ||
                !Enum.IsDefined(m.Trigger) || !Enum.IsDefined(m.Mode) || m.Actions is not { Length: <= 2048 })
                throw new InvalidDataException("Invalid macro draft structure.");
            foreach (var a in m.Actions)
                if (a is null || !Enum.IsDefined(a.Kind) || !Text(a.Code, 32) || !Text(a.Duration, 32) || !Text(a.X, 32) || !Text(a.Y, 32))
                    throw new InvalidDataException("Invalid action draft structure.");
        }
        if (draft.Macros.Select(m => m.Id).Distinct().Count() != draft.Macros.Length) throw new InvalidDataException("Duplicate macro draft identity.");
        foreach (var s in draft.Scripts)
            if (s is null || s.Id == Guid.Empty || !Text(s.Name, 128) || !Text(s.Source, 64_000)) throw new InvalidDataException("Invalid script draft structure.");
        if (draft.Scripts.Select(s => s.Id).Distinct().Count() != draft.Scripts.Length) throw new InvalidDataException("Duplicate script draft identity.");
        foreach (var mapping in draft.Mappings)
            if (mapping is null || !Text(mapping.Button, 128) || !Text(mapping.Key, 32)) throw new InvalidDataException("Invalid mapping draft structure.");
        foreach (var recording in draft.Recordings) (recording ?? throw new InvalidDataException("Null recording draft.")).Validate();
    }
    private void DeleteDraft() { if (File.Exists(DraftPath)) File.Delete(DraftPath); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Exception? stopFailure = null;
        try { await _runtime.StopAllAsync("Application closing"); }
        catch (Exception ex) { stopFailure = ex; _runtime.Diagnostics.Write(ex.Message, "Error"); }
        if (_runtime.LastRecording is { } recording && !Recordings.Contains(recording))
        {
            if (Recordings.Count >= 16) throw new InvalidDataException("Save or export the completed recording before closing (recording limit 16).");
            Recordings.Add(recording);
        }
        try { await SaveAsync(); } catch (Exception ex) { _runtime.Diagnostics.Write($"Profile not valid yet; preserving editor draft: {ex.Message}", "Warning"); await Task.Run(SaveDraft); }
        await _runtime.SaveSettingsAsync(_runtime.Settings with { Language = Language, Theme = Theme, AutoGameProfiles = AutoGameProfiles });
        if (stopFailure is not null) throw new InvalidOperationException("Edits were preserved, but output release failed. Retry Stop All before closing.", stopFailure);
        _disposed = true; _runtime.Changed -= OnRuntimeChanged; await _runtime.DisposeAsync();
    }
    public sealed record EditorDraft
    {
        public int Format { get; init; } = 1; public Guid ProfileId { get; init; } public string ProfileName { get; init; } = "";
        public string GameExecutable { get; init; } = ""; public string VariablesJson { get; init; } = "{}"; public OutputBackendKind Backend { get; init; }
        public MacroDraft[] Macros { get; init; } = []; public ScriptDraft[] Scripts { get; init; } = [];
        public MappingDraft[] Mappings { get; init; } = []; public InputRecording[] Recordings { get; init; } = [];
    }
    public sealed record MacroDraft
    {
        public Guid Id { get; init; } public string Name { get; init; } = ""; public bool Enabled { get; init; } public TriggerKind Trigger { get; init; }
        public string Buttons { get; init; } = ""; public string RequiredButtons { get; init; } = ""; public string AnalogTrigger { get; init; } = "R2";
        public string HeldMs { get; init; } = "250"; public string Threshold { get; init; } = "128"; public RunMode Mode { get; init; }
        public string RepeatCount { get; init; } = "1"; public bool RepeatWhileActive { get; init; } public string MaxRuntimeMs { get; init; } = "30000"; public ActionDraft[] Actions { get; init; } = [];
    }
    public sealed record ActionDraft(ActionKind Kind, string Code, string Duration, string X, string Y);
    public sealed record ScriptDraft(Guid Id, string Name, bool Enabled, string Source);
    public sealed record MappingDraft(string Button, string Key);
}
