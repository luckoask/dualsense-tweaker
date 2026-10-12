using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Tests;

public class PersistenceAndRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MacroGamerTests-" + Guid.NewGuid().ToString("N"));
    public PersistenceAndRecorderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    [Fact]
    public void ShippedMacroPresetsImportAndRoundTripAsRealDefinitions()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.json"); Assert.Equal(3, files.Length);
        foreach (string path in files)
        {
            var macro = MacroProfileStore.ImportMacro(path); Assert.False(macro.Enabled);
            string exported = Path.Combine(_root, Path.GetFileName(path)); MacroProfileStore.ExportMacro(macro, exported);
            var restored = MacroProfileStore.ImportMacro(exported); Assert.Equal(macro.Actions, restored.Actions); Assert.Equal(macro.Trigger, restored.Trigger);
        }
    }
    [Fact]
    public void ProfilesAndSettingsRoundTripIndependently()
    {
        var store = new MacroProfileStore(_root); var profile = MacroProfile.Default() with { Name = "Game", ControllerId = "pad", GameExecutable = "game.exe", Variables = new() { ["duration"] = 60 } };
        store.Save(profile); store.SaveSettings(new() { SelectedProfileId = profile.Id, Language = "CZ", Theme = "Light" });
        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(profile.Id, loaded.Id); Assert.Equal(profile.Macros[0].Actions, loaded.Macros[0].Actions); Assert.Equal(profile.Scripts, loaded.Scripts);
        Assert.Equal(60, loaded.Variables["duration"]); Assert.Equal("CZ", store.LoadSettings().Language);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }
    [Fact]
    public void ProfileAndMacroExportImportPreserveDefinitions()
    {
        var profile = MacroProfile.Default(); string path = Path.Combine(_root, "export.json");
        MacroProfileStore.Export(profile, path); Assert.Equal(profile.Id, MacroProfileStore.Import(path).Id);
        MacroProfileStore.ExportMacro(profile.Macros[0], path); var macro = MacroProfileStore.ImportMacro(path);
        Assert.Equal(profile.Macros[0].Actions, macro.Actions); Assert.Equal(profile.Macros[0].Trigger, macro.Trigger);
    }
    [Fact]
    public void DuplicateHasIndependentMacroAndScriptIdentitiesAndVariables()
    {
        var profile = MacroProfile.Default() with { Variables = new() { ["x"] = 1 } }; var copy = profile.Duplicate("Copy");
        Assert.NotEqual(profile.Id, copy.Id); Assert.NotEqual(profile.Macros[0].Id, copy.Macros[0].Id); Assert.NotEqual(profile.Scripts[0].Id, copy.Scripts[0].Id);
        copy.Variables["x"] = 2; Assert.Equal(1, profile.Variables["x"]);
    }
    [Fact]
    public void CorruptedFilesAreReportedAndPreserved()
    {
        string folder = Path.Combine(_root, "profiles"); Directory.CreateDirectory(folder); string path = Path.Combine(folder, "broken.json"); File.WriteAllText(path, "{ broken");
        var log = new DiagnosticLog(); var store = new MacroProfileStore(_root, log);
        Assert.Single(store.LoadAll()); Assert.Equal("{ broken", File.ReadAllText(path)); Assert.Contains(log.Entries, e => e.Level == "Error");
    }
    [Theory]
    [InlineData("{}")] [InlineData("null")] [InlineData("{\"formatVersion\":999}")] [InlineData("{\"formatVersion\":1,\"unexpected\":true}")]
    public void MalformedOrUnsupportedImportsAreRejected(string json)
    {
        string path = Path.Combine(_root, "bad.json"); File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => MacroProfileStore.Import(path));
    }
    [Fact]
    public void OversizedImportIsRejectedBeforeDeserialization()
    {
        string path = Path.Combine(_root, "huge.json"); using (var stream = File.Create(path)) stream.SetLength(MacroProfileStore.MaxFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => MacroProfileStore.Import(path));
    }
    [Fact]
    public void DisplayNamesAreNeverUsedAsFilesystemPaths()
    {
        var profile = MacroProfile.Default() with { Name = "../outside" }; new MacroProfileStore(_root).Save(profile);
        Assert.True(File.Exists(Path.Combine(_root, "profiles", profile.Id.ToString("N") + ".json")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.json")));
    }
    [Fact]
    public void RecorderOmitsUnchangedFramesAndCapturesDigitalEdges()
    {
        var recorder = new InputRecorder(); recorder.Start("pad"); recorder.Observe(EngineTests.Frame(0));
        for (int i = 1; i < 1000; i++) recorder.Observe(EngineTests.Frame(i));
        Assert.Equal(1, recorder.Count); recorder.Observe(EngineTests.Frame(1000, PadButtons.R1)); recorder.Observe(EngineTests.Frame(1001));
        var recording = recorder.Stop(); Assert.Equal(3, recording.Points.Length); Assert.False(recorder.IsRecording);
        var macro = recording.ToKeyboardMacro("Recorded", new Dictionary<PadButtons, OutputControl> { [PadButtons.R1] = OutputControl.Key("E") });
        Assert.Contains(macro.Actions, a => a.Kind == ActionKind.KeyDown && a.Code == "E"); Assert.Contains(macro.Actions, a => a.Kind == ActionKind.KeyUp && a.Code == "E");
    }
    [Fact]
    public void AnalogToleranceAndSamplingLimitAreApplied()
    {
        var recorder = new InputRecorder(); recorder.Start("pad"); recorder.Observe(EngineTests.Frame(0));
        recorder.Observe(EngineTests.Frame(1, time: 20_000, analog: PadAnalog.Centered with { RightX = 129 })); Assert.Equal(1, recorder.Count);
        recorder.Observe(EngineTests.Frame(2, time: 21_000, analog: PadAnalog.Centered with { RightX = 140 })); Assert.Equal(2, recorder.Count);
        recorder.Observe(EngineTests.Frame(3, time: 22_000, analog: PadAnalog.Centered with { RightX = 160 })); Assert.Equal(2, recorder.Count);
        recorder.Observe(EngineTests.Frame(4, time: 40_000, analog: PadAnalog.Centered with { RightX = 160 })); Assert.Equal(3, recorder.Count);
    }
    [Fact]
    public void RecorderRejectsSessionChangeAndStopsCleanly()
    {
        var recorder = new InputRecorder(); recorder.Start("pad"); recorder.Observe(EngineTests.Frame(0));
        Assert.Throws<InvalidOperationException>(() => recorder.Observe(EngineTests.Frame(0, session: Guid.NewGuid())));
        Assert.False(recorder.IsRecording); Assert.Single(recorder.Stop().Points);
    }
    [Fact]
    public void RecordedSharedMappingsDoNotReleaseAStillMappedButton()
    {
        var recorder = new InputRecorder(); recorder.Start("pad"); recorder.Observe(EngineTests.Frame(0));
        recorder.Observe(EngineTests.Frame(1, PadButtons.R1)); recorder.Observe(EngineTests.Frame(2, PadButtons.R1 | PadButtons.Cross));
        recorder.Observe(EngineTests.Frame(3, PadButtons.Cross)); recorder.Observe(EngineTests.Frame(4));
        var macro = recorder.Stop().ToKeyboardMacro("Shared", new Dictionary<PadButtons, OutputControl> { [PadButtons.R1] = OutputControl.Key("E"), [PadButtons.Cross] = OutputControl.Key("E") });
        Assert.Single(macro.Actions, a => a.Kind == ActionKind.KeyDown); Assert.Single(macro.Actions, a => a.Kind == ActionKind.KeyUp);
    }
    [Fact]
    public void RecordingIsPreservedInProfileAndEmptyMappingHasAnExplicitError()
    {
        var recorder = new InputRecorder(); recorder.Start("pad"); recorder.Observe(EngineTests.Frame(0));
        var recording = recorder.Stop(); var profile = MacroProfile.Default() with { Recordings = [recording] };
        string path = Path.Combine(_root, "recorded.json"); MacroProfileStore.Export(profile, path);
        Assert.Equal(recording.Points, MacroProfileStore.Import(path).Recordings[0].Points);
        Assert.Throws<InvalidDataException>(() => recording.ToKeyboardMacro("Empty", new Dictionary<PadButtons, OutputControl>()));
    }
    [Fact]
    public void NullOutputControlInAnImportedMacroIsRejectedWithoutNullReferenceCrash()
    {
        string path = Path.Combine(_root, "bad-macro.json");
        File.WriteAllText(path, "{\"name\":\"Bad\",\"actions\":[{\"kind\":\"KeyDown\",\"code\":null}]}");
        Assert.Throws<InvalidDataException>(() => MacroProfileStore.ImportMacro(path));
    }
}
