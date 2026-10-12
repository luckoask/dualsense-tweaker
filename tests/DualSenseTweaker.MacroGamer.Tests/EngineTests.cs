using System.Collections.Concurrent;
using System.Diagnostics;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Tests;

public class EngineTests
{
    private static readonly Guid Session = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static PadFrame Frame(long sequence, PadButtons buttons = PadButtons.None, string id = "pad", long? time = null, Guid? session = null, PadAnalog? analog = null) =>
        new(id, session ?? Session, sequence, time ?? sequence * 10_000, buttons, analog ?? PadAnalog.Centered);
    internal static MacroDefinition Macro(params MacroAction[] actions) => new() { Enabled = true, Name = "Test macro", Actions = actions };
    internal static async Task Eventually(Func<bool> predicate, double timeoutSeconds = 3)
    {
        long start = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException("Expected state was not observed.");
            await Task.Delay(2);
        }
    }
    private static async Task Completion(MacroEngine engine) => await Eventually(() => engine.Snapshot.ActiveMacros.Length == 0);

    [Fact]
    public void TransitionsArePhysicalEdgesAndIgnoreDuplicateOrStaleFrames()
    {
        var detector = new InputTransitionDetector();
        Assert.True(detector.Update(Frame(0))!.Connected);
        var down = detector.Update(Frame(1, PadButtons.R1 | PadButtons.Cross))!;
        Assert.Equal(PadButtons.R1 | PadButtons.Cross, down.Pressed);
        Assert.Equal(PadButtons.None, detector.Update(Frame(2, down.Current.Buttons))!.Pressed);
        Assert.Null(detector.Update(Frame(2, PadButtons.None)));
        Assert.Null(detector.Update(Frame(3, PadButtons.None, time: 1)));
        Assert.Equal(PadButtons.R1 | PadButtons.Cross, detector.Update(Frame(4))!.Released);
    }
    [Fact]
    public void FirstSampleAndNewSessionDoNotFabricatePresses()
    {
        var detector = new InputTransitionDetector();
        Assert.Equal(PadButtons.None, detector.Update(Frame(0, PadButtons.R1))!.Pressed);
        var reconnect = detector.Update(Frame(0, PadButtons.Cross, session: Guid.NewGuid()))!;
        Assert.True(reconnect.Connected); Assert.Null(reconnect.Previous); Assert.Equal(PadButtons.None, reconnect.Released);
    }
    [Fact]
    public void ControllerIdentitiesNeverFormCombinedEdges()
    {
        var detector = new InputTransitionDetector();
        detector.Update(Frame(0, id: "one")); detector.Update(Frame(0, id: "two"));
        detector.Update(Frame(1, PadButtons.L1, id: "one"));
        var two = detector.Update(Frame(1, PadButtons.R1, id: "two"))!;
        Assert.Equal(PadButtons.None, two.Previous!.Buttons);
        Assert.False(new TriggerMatcher().Match(new() { Kind = TriggerKind.Combination, Buttons = PadButtons.L1 | PadButtons.R1 }, two));
    }
    [Fact]
    public void CombinationMatchesWhenFinalButtonIsPressedAndReleaseMatchesOnce()
    {
        var detector = new InputTransitionDetector(); var matcher = new TriggerMatcher();
        var trigger = new MacroTrigger { Kind = TriggerKind.Combination, Buttons = PadButtons.L1 | PadButtons.R1 };
        detector.Update(Frame(0));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(1, PadButtons.L1))!));
        Assert.True(matcher.Match(trigger, detector.Update(Frame(2, trigger.Buttons))!));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(3, trigger.Buttons))!));
        var up = detector.Update(Frame(4))!;
        Assert.True(matcher.Match(trigger with { Kind = TriggerKind.Release }, up));
    }
    [Fact]
    public void HeldTriggerFiresOnceAtMonotonicThresholdAndRearmsOnRelease()
    {
        var detector = new InputTransitionDetector(); var matcher = new TriggerMatcher();
        var trigger = new MacroTrigger { Kind = TriggerKind.Held, Buttons = PadButtons.R1, HeldMs = 100 };
        detector.Update(Frame(0));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(1, PadButtons.R1, time: 1_000))!));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(2, PadButtons.R1, time: 100_999))!));
        Assert.True(matcher.Match(trigger, detector.Update(Frame(3, PadButtons.R1, time: 101_000))!));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(4, PadButtons.R1, time: 200_000))!));
        matcher.Match(trigger, detector.Update(Frame(5, time: 210_000))!);
        matcher.Match(trigger, detector.Update(Frame(6, PadButtons.R1, time: 220_000))!);
        Assert.True(matcher.Match(trigger, detector.Update(Frame(7, PadButtons.R1, time: 320_000))!));
    }
    [Fact]
    public void HeldBaselineCannotTriggerWithoutAnActualPress()
    {
        var changes = new InputTransitionDetector().Update(Frame(0, PadButtons.R1))!;
        Assert.False(new TriggerMatcher().Match(new() { Kind = TriggerKind.Held }, changes));
    }
    [Fact]
    public void AnalogThresholdMatchesOnlyAnUpwardCrossingWithRequiredButtons()
    {
        var detector = new InputTransitionDetector(); var matcher = new TriggerMatcher();
        var trigger = new MacroTrigger { Kind = TriggerKind.TriggerThreshold, RequiredButtons = PadButtons.L1 };
        detector.Update(Frame(0));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(1, analog: PadAnalog.Centered with { R2 = 200 }))!));
        detector.Update(Frame(2, PadButtons.L1));
        Assert.True(matcher.Match(trigger, detector.Update(Frame(3, PadButtons.L1, analog: PadAnalog.Centered with { R2 = 128 }))!));
        Assert.False(matcher.Match(trigger, detector.Update(Frame(4, PadButtons.L1, analog: PadAnalog.Centered with { R2 = 255 }))!));
    }
    [Fact]
    public async Task EngineAndDefaultsStartDisabled()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new MacroAction { Kind = ActionKind.KeyTap }); await engine.ConfigureAsync([macro], "pad");
        Assert.False(engine.Start(macro.Id)); Assert.Empty(output.Events); Assert.False(engine.Snapshot.Enabled);
        Assert.All(MacroProfile.Default().Macros, m => Assert.False(m.Enabled));
        Assert.All(MacroProfile.Default().Scripts, s => Assert.False(s.Enabled));
    }
    [Fact]
    public async Task TimedSequenceAndRepetitionsUseMonotonicDeadline()
    {
        var clock = new VirtualClock(); var output = new ClockBackend(clock); await using var engine = new MacroEngine(output, clock: clock);
        var macro = Macro(new MacroAction { Kind = ActionKind.KeyTap, Code = "E", DurationMs = 20 }) with { RepeatCount = 2 };
        await engine.ConfigureAsync([macro], "pad"); engine.Enable(); Assert.True(engine.Start(macro.Id)); await Completion(engine);
        Assert.Equal(new[] { 0d, 20d, 24d, 44d }, output.Times.Select(t => t.TotalMilliseconds));
        Assert.Equal(new[] { "Down", "Up", "Down", "Up" }, output.Events.Select(e => e.Kind));
        Assert.Empty(await engine.Ledger.HeldAsync()); Assert.Equal(ExecutionState.Armed, engine.Snapshot.State);
    }
    [Fact]
    public async Task CancellationAndEmergencyStopReleaseLongHeldKey()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        await engine.ConfigureAsync([macro], "pad"); engine.Enable(); engine.Start(macro.Id);
        await Eventually(() => output.Events.Count > 0); await engine.StopAllAsync();
        Assert.Equal(new[] { "Down", "Up" }, output.Events.Select(e => e.Kind));
        Assert.Empty(await engine.Ledger.HeldAsync()); Assert.False(engine.Snapshot.Enabled); Assert.Equal(ExecutionState.Stopped, engine.Snapshot.State);
    }
    [Theory]
    [InlineData(RunMode.Hold)]
    [InlineData(RunMode.Toggle)]
    public async Task HoldAndToggleStopAtTheirPhysicalCondition(RunMode mode)
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 }) with { Mode = mode };
        await engine.ConfigureAsync([macro], "pad"); engine.Enable();
        engine.ProcessFrame(Frame(0)); engine.ProcessFrame(Frame(1, PadButtons.R1));
        await Eventually(() => output.Events.Count == 1);
        engine.ProcessFrame(Frame(2, PadButtons.R1)); Assert.Single(output.Events);
        engine.ProcessFrame(Frame(3));
        if (mode == RunMode.Toggle) { Assert.Single(output.Events); engine.ProcessFrame(Frame(4, PadButtons.R1)); }
        await Completion(engine); Assert.Equal(new[] { "Down", "Up" }, output.Events.Select(e => e.Kind));
        Assert.True(engine.Snapshot.Enabled);
    }
    [Fact]
    public async Task RuntimeLimitReleasesHolds()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 }) with { MaxRuntimeMs = 30 };
        await engine.ConfigureAsync([macro], null); engine.Enable(); engine.Start(macro.Id); await Completion(engine);
        Assert.Empty(await engine.Ledger.HeldAsync()); Assert.Equal(new[] { "Down", "Up" }, output.Events.Select(e => e.Kind));
        Assert.Contains(engine.Diagnostics.Entries, e => e.Message.Contains("Runtime limit"));
    }
    [Fact]
    public async Task DisconnectStopsAndReconnectRequiresExplicitArming()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        await engine.ConfigureAsync([macro], "pad"); engine.Enable(); engine.ProcessFrame(Frame(0)); engine.ProcessFrame(Frame(1, PadButtons.R1));
        await Eventually(() => output.Events.Count == 1); await engine.DisconnectAsync("pad");
        engine.ProcessFrame(Frame(0, session: Guid.NewGuid()));
        Assert.False(engine.Snapshot.Enabled); Assert.Empty(await engine.Ledger.HeldAsync()); Assert.Equal(2, output.Events.Count);
    }
    [Fact]
    public async Task SessionChangeWithoutExplicitDisconnectDisarmsAndReleases()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        await engine.ConfigureAsync([macro], "pad"); engine.Enable(); engine.ProcessFrame(Frame(0)); engine.ProcessFrame(Frame(1, PadButtons.R1));
        await Eventually(() => output.Events.Count == 1); engine.ProcessFrame(Frame(0, PadButtons.R1, session: Guid.NewGuid()));
        await Completion(engine); Assert.False(engine.Snapshot.Enabled); Assert.Empty(await engine.Ledger.HeldAsync());
    }
    [Fact]
    public async Task UnselectedControllerCannotTriggerOrStopSelectedController()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macro = Macro(new() { Kind = ActionKind.KeyDown }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        await engine.ConfigureAsync([macro], "pad"); engine.Enable(); engine.ProcessFrame(Frame(0, id: "other")); engine.ProcessFrame(Frame(1, PadButtons.R1, id: "other"));
        Assert.Empty(output.Events); await engine.DisconnectAsync("other"); Assert.True(engine.Snapshot.Enabled);
    }
    [Fact]
    public async Task ConcurrentStartsAndStopsCannotLeaveOutputsOwned()
    {
        var output = new DiagnosticOutputBackend(); await using var engine = new MacroEngine(output);
        var macros = Enumerable.Range(0, 8).Select(i => Macro(new() { Kind = ActionKind.KeyDown, Code = ((char)('A' + i)).ToString() }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 })).ToArray();
        await engine.ConfigureAsync(macros, "pad"); engine.Enable();
        await Task.WhenAll(macros.Select(m => Task.Run(() => engine.Start(m.Id))));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => engine.StopAllAsync())));
        Assert.False(engine.Snapshot.Enabled); Assert.Empty(await engine.Ledger.HeldAsync()); Assert.Empty(engine.Snapshot.ActiveMacros);
        Assert.False(engine.Start(macros[0].Id));
    }
    [Fact]
    public async Task RepeatingZeroDelaySequenceHasABoundedRate()
    {
        var clock = new VirtualClock(); var output = new ClockBackend(clock); await using var engine = new MacroEngine(output, clock: clock);
        var macro = Macro(new MacroAction { Kind = ActionKind.MouseMove, X = 1, DurationMs = 0 }) with { RepeatCount = 20 };
        await engine.ConfigureAsync([macro], null); engine.Enable(); engine.Start(macro.Id); await Completion(engine);
        Assert.Equal(20, output.Times.Count);
        Assert.All(output.Times.Zip(output.Times.Skip(1)), pair => Assert.True((pair.Second - pair.First).TotalMilliseconds >= 4));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void RepetitionLimitsAreRejected(int repeats) => Assert.Throws<InvalidDataException>(() => (Macro(new MacroAction()) with { RepeatCount = repeats }).Validate());
    [Fact]
    public void MalformedFramesAndUnknownControlsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Frame(0, (PadButtons)(1u << 31)).Validate());
        Assert.Throws<InvalidDataException>(() => OutputControl.Key("launch_process").Validate());
        Assert.Throws<InvalidDataException>(() => (Macro(new MacroAction()) with { Trigger = new() { Kind = TriggerKind.Disconnected } }).Validate());
    }
    internal sealed class VirtualClock : IExecutionClock
    {
        private long _ticks;
        public long Timestamp => Interlocked.Read(ref _ticks);
        public TimeSpan Elapsed(long start) => TimeSpan.FromTicks(Timestamp - start);
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Interlocked.Add(ref _ticks, delay.Ticks); return ValueTask.CompletedTask; }
    }
    private sealed class ClockBackend(VirtualClock clock) : IInputOutputBackend
    {
        private readonly DiagnosticOutputBackend _output = new();
        private readonly ConcurrentQueue<TimeSpan> _times = new();
        public string Name => "Clock test"; public bool IsAvailable => true; public string? UnavailableReason => null;
        public IReadOnlyList<OutputEvent> Events => _output.Events; public IReadOnlyList<TimeSpan> Times => _times.ToArray();
        public ValueTask SetControlAsync(OutputControl control, bool pressed, CancellationToken token) { _times.Enqueue(clock.Elapsed(0)); return _output.SetControlAsync(control, pressed, token); }
        public ValueTask MoveMouseAsync(int x, int y, CancellationToken token) { _times.Enqueue(clock.Elapsed(0)); return _output.MoveMouseAsync(x, y, token); }
        public ValueTask WheelAsync(int delta, CancellationToken token) { _times.Enqueue(clock.Elapsed(0)); return _output.WheelAsync(delta, token); }
    }
}

public class LedgerTests
{
    [Fact]
    public async Task OverlappingOwnersGenerateOneDownAndOneFinalUp()
    {
        var output = new DiagnosticOutputBackend(); var ledger = new OutputLedger(output, new());
        var one = Guid.NewGuid(); var two = Guid.NewGuid(); var key = OutputControl.Key("E");
        await ledger.PressAsync(one, key, default); await ledger.PressAsync(two, key, default);
        await ledger.ReleaseOwnerAsync(one); Assert.Single(output.Events); Assert.Single(await ledger.HeldAsync());
        await ledger.ReleaseOwnerAsync(two); Assert.Equal(new[] { "Down", "Up" }, output.Events.Select(e => e.Kind)); Assert.Empty(await ledger.HeldAsync());
    }
    [Fact]
    public async Task FailedReleaseRetainsOwnershipAndStopRetries()
    {
        var output = new FlakyBackend { RemainingFailedUps = 1 }; var ledger = new OutputLedger(output, new());
        var owner = Guid.NewGuid(); var key = OutputControl.Key("E");
        await ledger.PressAsync(owner, key, default);
        await Assert.ThrowsAsync<IOException>(() => ledger.ReleaseAsync(owner, key).AsTask()); Assert.Single(await ledger.HeldAsync());
        await ledger.ReleaseAllAsync(); Assert.Empty(await ledger.HeldAsync()); Assert.Equal(2, output.Output.Events.Count);
    }
    [Fact]
    public async Task PersistentBackendReleaseFailureIsExplicitAndRetryable()
    {
        var output = new FlakyBackend { RemainingFailedUps = 20 }; var ledger = new OutputLedger(output, new());
        await ledger.PressAsync(Guid.NewGuid(), OutputControl.Mouse("LEFT"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReleaseAllAsync()); Assert.Single(await ledger.HeldAsync());
        output.RemainingFailedUps = 0; await ledger.ReleaseAllAsync(); Assert.Empty(await ledger.HeldAsync());
    }
    private sealed class FlakyBackend : IInputOutputBackend
    {
        public int RemainingFailedUps; public DiagnosticOutputBackend Output { get; } = new();
        public string Name => "Fault injection"; public bool IsAvailable => true; public string? UnavailableReason => null;
        public ValueTask SetControlAsync(OutputControl control, bool pressed, CancellationToken token)
        { if (!pressed && RemainingFailedUps-- > 0) throw new IOException("Injected release failure"); return Output.SetControlAsync(control, pressed, token); }
        public ValueTask MoveMouseAsync(int x, int y, CancellationToken token) => Output.MoveMouseAsync(x, y, token);
        public ValueTask WheelAsync(int delta, CancellationToken token) => Output.WheelAsync(delta, token);
    }
}
