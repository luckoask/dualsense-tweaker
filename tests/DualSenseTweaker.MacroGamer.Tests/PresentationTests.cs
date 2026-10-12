using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;
using DualSenseTweaker.MacroGamer.Presentation;

namespace DualSenseTweaker.MacroGamer.Tests;

public class PresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MacroGamerUi-" + Guid.NewGuid().ToString("N"));
    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "script-worker", "MacroGamer.ScriptHost.dll");
    private MainViewModel Create(out CompanionRuntime runtime, FakeInteraction? interaction = null)
    {
        runtime = new(WorkerPath, _root);
        return new(runtime, interaction ?? new(), _ => { }, text => text) { EmergencyAvailable = true };
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public async Task UiCanSaveArmRunStopAndReopenARealDiagnosticMacro()
    {
        var view = Create(out var runtime);
        Assert.Equal(8, view.Navigation.Length); Assert.False(view.EngineEnabled);
        view.SelectedMacro!.Enabled = true; await view.SaveAsync(); await view.EnableCommand.ExecuteAsync();
        Assert.True(view.EngineEnabled); await view.RunMacroCommand.ExecuteAsync();
        var output = Assert.IsType<DiagnosticOutputBackend>(runtime.Engine.Ledger.Backend);
        await EngineTests.Eventually(() => output.Events.Count == 2);
        await view.StopAllCommand.ExecuteAsync(); Assert.False(view.EngineEnabled); Assert.Empty(await runtime.Engine.Ledger.HeldAsync());
        await view.DisposeAsync();
        await using var reopened = Create(out var next); Assert.False(reopened.EngineEnabled); Assert.True(reopened.SelectedMacro!.Enabled);
        Assert.Equal("E", reopened.SelectedMacro.Actions[0].Code);
    }
    [Fact]
    public async Task MacroEditsDisarmButScriptOnlyEditsPreserveRunningSnapshot()
    {
        await using var view = Create(out var runtime); view.SelectedScript!.Enabled = true; await view.SaveAsync();
        await view.EnableCommand.ExecuteAsync(); await view.RunScriptCommand.ExecuteAsync();
        Assert.Single(runtime.Scripts.RunningScripts);
        view.SelectedScript.Source = "on_press('R1', function() key_tap('Q', 20) end)"; await view.SaveAsync();
        Assert.True(view.EngineEnabled); Assert.Single(runtime.Scripts.RunningScripts);
        view.SelectedMacro!.Actions[0].Code = "Q"; await view.SaveAsync();
        Assert.False(view.EngineEnabled); Assert.Empty(runtime.Scripts.RunningScripts);
    }
    [Fact]
    public async Task InvalidNumericEditIsPreservedAsDraftAndRestoredDisabled()
    {
        var view = Create(out _); view.SelectedMacro!.RepeatCount = "unfinished";
        await view.DisposeAsync(); Assert.True(File.Exists(Path.Combine(_root, "editor-draft.json")));
        await using var reopened = Create(out _); Assert.Equal("unfinished", reopened.SelectedMacro!.RepeatCount); Assert.False(reopened.EngineEnabled);
        reopened.SelectedMacro.RepeatCount = "2"; await reopened.SaveAsync(); Assert.False(File.Exists(Path.Combine(_root, "editor-draft.json")));
    }
    [Fact]
    public async Task ProfileCreateRenameDuplicateDeleteAndImportExportUseRealFiles()
    {
        var interaction = new FakeInteraction(); await using var view = Create(out var runtime, interaction);
        await view.NewProfileCommand.ExecuteAsync(); Assert.Equal(2, view.Profiles.Count);
        view.ProfileName = "Game profile"; view.GameExecutable = "game.exe"; await view.SaveAsync();
        await view.DuplicateProfileCommand.ExecuteAsync(); Assert.Equal(3, view.Profiles.Count);
        string exported = Path.Combine(_root, "profile-export.json"); interaction.SavePath = exported;
        await view.ExportProfileCommand.ExecuteAsync(); Assert.True(File.Exists(exported));
        interaction.OpenPath = exported; await view.ImportProfileCommand.ExecuteAsync(); Assert.Equal(4, view.Profiles.Count);
        await view.DeleteProfileCommand.ExecuteAsync(); Assert.Equal(3, view.Profiles.Count); Assert.False(view.EngineEnabled);
        Assert.Contains(view.Profiles, profile => profile.GameExecutable == "game.exe");
    }
    [Fact]
    public async Task ActionAddReorderRemoveAndMacroImportExportAreFunctional()
    {
        var interaction = new FakeInteraction(); await using var view = Create(out _, interaction);
        await view.AddActionCommand.ExecuteAsync(); Assert.Equal(2, view.SelectedMacro!.Actions.Count);
        await view.UpActionCommand.ExecuteAsync(); Assert.Equal(ActionKind.Delay, view.SelectedMacro.Actions[0].Kind);
        await view.DownActionCommand.ExecuteAsync(); Assert.Equal(ActionKind.Delay, view.SelectedMacro.Actions[1].Kind);
        await view.RemoveActionCommand.ExecuteAsync(); Assert.Single(view.SelectedMacro.Actions);
        interaction.SavePath = Path.Combine(_root, "macro.json"); await view.ExportMacroCommand.ExecuteAsync();
        interaction.OpenPath = interaction.SavePath; await view.ImportMacroCommand.ExecuteAsync(); Assert.Equal(2, view.Macros.Count);
        Assert.False(view.SelectedMacro!.Enabled);
    }
    [Fact]
    public async Task RecorderButtonsCreateAPersistedPlayableMacroAndKeepMappings()
    {
        await using var view = Create(out var runtime);
        Guid session = Guid.NewGuid(); var descriptor = new ControllerDescriptor("pad", "DualSense Edge", "Bluetooth", 65);
        await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Snapshot, Controllers = [descriptor] });
        await view.RecordCommand.ExecuteAsync(); Assert.True(view.IsRecording);
        foreach (var frame in new[] { EngineTests.Frame(0), EngineTests.Frame(1, PadButtons.R1), EngineTests.Frame(2) })
            await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Frame, Frame = frame with { SessionId = session } });
        await view.StopRecordCommand.ExecuteAsync(); Assert.False(view.IsRecording); Assert.Single(view.Recordings);
        view.RecordingMappings[0].Key = "Q"; await view.RecordingToMacroCommand.ExecuteAsync();
        Assert.Contains(view.SelectedMacro!.Actions, a => a.Kind == ActionKind.KeyDown && a.Code == "Q");
        Assert.Equal("Q", runtime.Store.LoadAll().Single().RecordingKeyMappings["R1"]);
        view.SelectedMacro.Enabled = true; await view.SaveAsync(); await view.EnableCommand.ExecuteAsync(); await view.RunMacroCommand.ExecuteAsync();
        var output = Assert.IsType<DiagnosticOutputBackend>(runtime.Engine.Ledger.Backend); await EngineTests.Eventually(() => output.Events.Count == 2);
    }
    [Fact]
    public async Task ForegroundGameSelectsAssociatedProfileAndKeepsExecutionDisabled()
    {
        await using var view = Create(out var runtime);
        view.GameExecutable = "test-game.exe"; await view.SaveAsync(); Guid associated = runtime.ActiveProfile.Id;
        await view.NewProfileCommand.ExecuteAsync(); view.AutoGameProfiles = true;
        await view.EnableCommand.ExecuteAsync(); Assert.True(view.EngineEnabled);
        await view.EvaluateGamesAsync("test-game.exe"); Assert.Equal(associated, runtime.ActiveProfile.Id); Assert.False(view.EngineEnabled);
    }
    [Fact]
    public async Task InputGapStopsExecutionAndMissingAssociatedControllerDoesNotHideOthers()
    {
        await using var view = Create(out var runtime); Guid session = Guid.NewGuid();
        await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Snapshot, Controllers = [new("pad", "DualSense", "USB", 50), new("other", "DualSense", "Bluetooth", 80)] });
        await view.EnableCommand.ExecuteAsync();
        await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Gap, DroppedFrames = 1 }); Assert.False(view.EngineEnabled);
        await runtime.SaveProfileAsync(runtime.ActiveProfile with { ControllerId = "pad" });
        await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Snapshot, Controllers = [new("other", "DualSense", "Bluetooth", 80)] });
        Assert.True(runtime.SourceOnline); Assert.Single(runtime.Controllers); Assert.Null(runtime.SelectedController);
    }
    [Theory]
    [InlineData("{\"Macros\":[null]}")]
    [InlineData("{\"Scripts\":[null]}")]
    [InlineData("{\"Mappings\":[null]}")]
    [InlineData("{\"Recordings\":[null]}")]
    [InlineData("{\"ProfileName\":null}")]
    public async Task MaliciousDraftIsRejectedBeforeChangingAnyEditors(string corruptedFields)
    {
        var original = Create(out var first); Guid id = first.ActiveProfile.Id; int macroCount = original.Macros.Count;
        await original.DisposeAsync();
        var node = System.Text.Json.Nodes.JsonNode.Parse(corruptedFields)!.AsObject();
        node["Format"] = 1; node["ProfileId"] = id;
        File.WriteAllText(Path.Combine(_root, "editor-draft.json"), node.ToJsonString());
        await using var view = Create(out var runtime); Assert.Equal(macroCount, view.Macros.Count); Assert.False(view.EngineEnabled);
        Assert.Contains(runtime.Diagnostics.Entries, entry => entry.Message.StartsWith("Editor draft rejected:"));
    }
    [Fact]
    public async Task ClosingWhileRecordingPreservesActualCapturedFrames()
    {
        var view = Create(out var runtime); Guid session = Guid.NewGuid();
        await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Snapshot, Controllers = [new("pad", "DualSense", "USB", 50)] });
        await view.RecordCommand.ExecuteAsync();
        foreach (var frame in new[] { EngineTests.Frame(0), EngineTests.Frame(1, PadButtons.R1), EngineTests.Frame(2) })
            await runtime.ReceiveAsync(new() { SessionId = session, Kind = BridgeMessageKind.Frame, Frame = frame with { SessionId = session } });
        await view.DisposeAsync();
        await using var reopened = Create(out _); Assert.Single(reopened.Recordings); Assert.Equal(3, reopened.Recordings[0].Points.Length);
    }
    private sealed class FakeInteraction : IUserInteraction
    {
        public string? OpenPath, SavePath;
        public string? OpenFile(string filter) => OpenPath;
        public string? SaveFile(string name, string filter) => SavePath;
        public bool Confirm(string message) => true;
        public void OpenFolder(string path) { }
        public void LaunchTweaker() { }
    }
}
