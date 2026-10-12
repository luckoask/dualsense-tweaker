using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;
using DualSenseTweaker.MacroGamer.Scripting;

namespace DualSenseTweaker.MacroGamer.Tests;

public class ScriptingTests
{
    private static ScriptResponse Load(string source) => new LuaSandbox().Process(new() { Source = source, Name = "test.lua" });
    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "script-worker", "MacroGamer.ScriptHost.dll");
    [Theory]
    [InlineData("os.execute('forbidden')")] [InlineData("io.open('forbidden', 'w')")]
    [InlineData("require('System')")] [InlineData("loadfile('forbidden.lua')")]
    [InlineData("clr.System.Diagnostics.Process.Start('forbidden')")] [InlineData("package.loadlib('forbidden', 'x')")]
    [InlineData("debug.getregistry()")] [InlineData("collectgarbage()")]
    public void DangerousApisAreAbsent(string code)
    {
        var response = Load(code); Assert.False(response.Success); Assert.NotEmpty(response.Error!); Assert.Empty(response.Actions);
    }
    [Fact]
    public void LuaSyntaxErrorReportsSourceAndLine()
    {
        var response = Load("local good = 1\nfunction broken(\nend");
        Assert.False(response.Success); Assert.Contains("test.lua", response.Error!); Assert.Contains(":", response.Error!);
    }
    [Theory]
    [InlineData("while true do end")] [InlineData("local function forever() return forever() end; forever()")]
    public void InfiniteLuaExecutionHitsInstructionQuota(string code)
    {
        var response = Load(code); Assert.False(response.Success); Assert.Contains("quota", response.Error!); Assert.True(response.InstructionSlices <= 129);
    }
    [Theory]
    [InlineData("key_tap('E', -1)")] [InlineData("key_tap('E', 1.5)")] [InlineData("key_tap('E', 60001)")]
    [InlineData("key_down('PROCESS')")] [InlineData("on_press('Unknown', function() end)")] [InlineData("mouse_down('Unknown')")]
    public void ApprovedApiValidatesActionsAndTriggers(string source)
    { var response = Load(source); Assert.False(response.Success); Assert.Empty(response.Actions); }
    [Theory]
    [InlineData("for i = 1, 3000 do key_down('E') end")]
    [InlineData("for i = 1, 100 do on_press('R1', function() end) end")]
    [InlineData("for i = 1, 200 do log('message') end")]
    [InlineData("delay(60000); delay(1)")]
    public void ActionsTriggersLogsAndDurationHaveQuotas(string source)
    { var response = Load(source); Assert.False(response.Success); Assert.Contains("quota", response.Error!); Assert.Empty(response.Actions); }
    [Fact]
    public void RegisteredCallbackBuildsActualSequenceAndReadsControlledInput()
    {
        var sandbox = new LuaSandbox();
        var loaded = sandbox.Process(new() { Source = "on_press('R1', function() if is_pressed('L1') and input().r2 >= 128 then key_tap('E', variable('tap_ms')) end end)" });
        Assert.True(loaded.Success); Assert.Single(loaded.Bindings);
        var response = sandbox.Process(new() { Operation = ScriptOperation.Invoke, HandlerIndex = 0,
            Input = EngineTests.Frame(1, PadButtons.R1 | PadButtons.L1, analog: PadAnalog.Centered with { R2 = 200 }), Variables = new() { ["tap_ms"] = 45 } });
        Assert.True(response.Success); var action = Assert.Single(response.Actions); Assert.Equal(ActionKind.KeyTap, action.Kind); Assert.Equal(45, action.DurationMs);
    }
    [Fact]
    public async Task EveryShippedExampleExecutesInTheRealWorker()
    {
        var paths = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.lua");
        Assert.True(paths.Length >= 5, "The sample library was not copied into the test output.");
        foreach (var path in paths)
        {
            await using var worker = new ScriptWorkerClient(WorkerPath);
            var loaded = await worker.RequestAsync(new() { Source = File.ReadAllText(path), Name = Path.GetFileName(path) });
            Assert.True(loaded.Success, loaded.Error); Assert.NotEmpty(loaded.Bindings);
            foreach (var binding in loaded.Bindings)
            {
                var response = await worker.RequestAsync(new() { Operation = ScriptOperation.Invoke, HandlerIndex = binding.Index,
                    Input = EngineTests.Frame(1, binding.Buttons | PadButtons.L1, analog: PadAnalog.Centered with { R2 = 200 }), Variables = new() { ["tap_ms"] = 45 } });
                Assert.True(response.Success, response.Error);
                if (Path.GetFileName(path) == "cancel.lua") Assert.True(response.CancelRequested);
                else Assert.NotEmpty(response.Actions);
            }
        }
    }
    [Fact]
    public async Task NativeCallbackLoopsCannotHangTheParent()
    {
        await using var worker = new ScriptWorkerClient(WorkerPath);
        // MoonSharp cannot preempt this nested native-library callback; the parent must terminate it.
        await Assert.ThrowsAsync<TimeoutException>(() => worker.RequestAsync(new()
        { Source = "string.gsub('x', '.', function() while true do end end)", Name = "native-loop.lua" }));
        Assert.False(worker.IsRunning);
    }
    [Fact]
    public async Task CancellationTerminatesUnresponsiveWorker()
    {
        await using var worker = new ScriptWorkerClient(WorkerPath); using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RequestAsync(new()
        { Source = "string.gsub('x', '.', function() while true do end end)" }, cancel.Token));
        Assert.False(worker.IsRunning);
    }
    [Fact]
    public async Task ValidationDoesNotEmitDesktopInputAndRunningSnapshotsAreImmutable()
    {
        await using var original = new ScriptWorkerClient(WorkerPath); await using var editor = new ScriptWorkerClient(WorkerPath);
        Assert.True((await original.RequestAsync(new() { Source = "on_press('R1', function() key_tap('E', 60) end)" })).Success);
        Assert.True((await editor.RequestAsync(new() { Operation = ScriptOperation.Validate, Source = "key_tap('Q', 50)" })).Success);
        var response = await original.RequestAsync(new() { Operation = ScriptOperation.Invoke, HandlerIndex = 0 });
        Assert.Equal("E", Assert.Single(response.Actions).Code);
        Assert.False((await original.RequestAsync(new() { Source = "key_tap('Q', 50)" })).Success);
    }
    [Fact]
    public async Task ProtocolLinesAreBoundedBeforeUnboundedAllocation()
    {
        using var reader = new StringReader(new string('x', ScriptWire.MaxLineCharacters + 1) + "\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => ScriptWire.ReadLineAsync(reader, default).AsTask());
    }
    [Fact]
    public async Task ActualWorkerRuntimeEnforcesManagedHeapLimit()
    {
        await using var worker = new ScriptWorkerClient(WorkerPath);
        var result = await worker.RequestAsync(new() { Source = "log('heap check')" });
        Assert.True(result.Success); Assert.Equal(134_217_728, result.ManagedHeapLimitBytes);
    }
    [Fact]
    public async Task NativeLibraryMemoryRequestFailsInsideIsolatedWorker()
    {
        await using var worker = new ScriptWorkerClient(WorkerPath);
        try
        {
            var result = await worker.RequestAsync(new() { Source = "local enormous = string.rep('x', 200000000)" });
            Assert.False(result.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        { Assert.False(worker.IsRunning); }
    }
    [Fact]
    public async Task PhysicalFrameLuaTriggerEngineAndOutputFormAWorkingPipeline()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        await engine.ConfigureAsync([], "pad"); engine.Enable();
        await using var scripts = new ScriptCoordinator(engine, WorkerPath);
        await scripts.StartAsync(new() { Enabled = true, Name = "Pipeline", Source = "on_press('R1', function() key_tap('E', 30) end)" }, new Dictionary<string, double>());
        engine.ProcessFrame(EngineTests.Frame(0)); scripts.ProcessFrame(EngineTests.Frame(0));
        engine.ProcessFrame(EngineTests.Frame(1, PadButtons.R1)); scripts.ProcessFrame(EngineTests.Frame(1, PadButtons.R1));
        await EngineTests.Eventually(() => output.Events.Count == 2);
        Assert.Equal(new[] { "Down", "Up" }, output.Events.Select(e => e.Kind));
        Assert.All(output.Events, e => Assert.Equal(OutputControl.Key("E"), e.Control));
        Assert.Empty(await engine.Ledger.HeldAsync());
    }
    [Fact]
    public async Task ScriptTimeoutDisarmsAndReleasesAnotherRunningMacro()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = EngineTests.Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        await engine.ConfigureAsync([macro], "pad"); engine.Enable();
        await using var scripts = new ScriptCoordinator(engine, WorkerPath);
        await scripts.StartAsync(new() { Enabled = true, Name = "Hung", Source = "on_press('Cross', function() string.gsub('x', '.', function() while true do end end) end)" }, new Dictionary<string, double>());
        engine.Start(macro.Id); await EngineTests.Eventually(() => output.Events.Count == 1);
        scripts.ProcessFrame(EngineTests.Frame(0)); scripts.ProcessFrame(EngineTests.Frame(1, PadButtons.Cross));
        await EngineTests.Eventually(() => !engine.Snapshot.Enabled, timeoutSeconds: 6);
        await EngineTests.Eventually(() => output.Events.Count == 2);
        Assert.Empty(await engine.Ledger.HeldAsync()); Assert.Contains(engine.Diagnostics.Entries, e => e.Message.Contains("two-second"));
    }
    [Fact]
    public async Task StopDuringScriptInitializationCancelsLongSequenceResponsively()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        await engine.ConfigureAsync([], "pad"); engine.Enable(); await using var scripts = new ScriptCoordinator(engine, WorkerPath);
        var started = scripts.StartAsync(new() { Enabled = true, Name = "Long init", Source = "key_down('E'); delay(59000)" }, new Dictionary<string, double>());
        await EngineTests.Eventually(() => output.Events.Count == 1);
        await scripts.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(2));
        try { await started; } catch (OperationCanceledException) { }
        Assert.Empty(await engine.Ledger.HeldAsync()); Assert.False(engine.Snapshot.Enabled); Assert.Empty(scripts.RunningScripts);
    }
}
