using System.Runtime.InteropServices;
using DualSenseTweaker.MacroGamer.Core;
using DualSenseTweaker.MacroGamer.Output;

namespace DualSenseTweaker.MacroGamer.Tests;

public class OutputTests
{
    [Fact]
    public void NativePacketHasTheWindowsAbiSizeAndAlignment()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeInput>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Data)).ToInt32());
    }
    [Theory]
    [InlineData("E", 0x45, false)] [InlineData("F24", 0x87, false)] [InlineData("SPACE", 0x20, false)]
    [InlineData("RCTRL", 0xA3, true)] [InlineData("LEFT", 0x25, true)]
    public void KeyboardPacketsUseApprovedVirtualKeysAndReleaseFlags(string key, int expected, bool extended)
    {
        var down = NativeInputBuilder.Control(OutputControl.Key(key), true); var up = NativeInputBuilder.Control(OutputControl.Key(key), false);
        Assert.Equal(1u, down.Type); Assert.Equal(expected, down.Data.Keyboard.VirtualKey);
        Assert.Equal(extended ? 1u : 0u, down.Data.Keyboard.Flags); Assert.Equal(extended ? 3u : 2u, up.Data.Keyboard.Flags);
    }
    [Theory]
    [InlineData("LEFT", 2, 4, 0)] [InlineData("RIGHT", 8, 16, 0)] [InlineData("MIDDLE", 32, 64, 0)]
    [InlineData("X1", 128, 256, 1)] [InlineData("X2", 128, 256, 2)]
    public void MousePacketsHaveMatchingDownUpAndXButtonData(string button, int downFlag, int upFlag, int data)
    {
        var down = NativeInputBuilder.Control(OutputControl.Mouse(button), true); var up = NativeInputBuilder.Control(OutputControl.Mouse(button), false);
        Assert.Equal(0u, down.Type); Assert.Equal((uint)downFlag, down.Data.Mouse.Flags); Assert.Equal((uint)upFlag, up.Data.Mouse.Flags);
        Assert.Equal((uint)data, down.Data.Mouse.MouseData);
    }
    [Fact]
    public void WheelAndMovementPacketsPreserveSignedValues()
    {
        var move = NativeInputBuilder.Move(-10, 20); Assert.Equal(-10, move.Data.Mouse.X); Assert.Equal(20, move.Data.Mouse.Y); Assert.Equal(1u, move.Data.Mouse.Flags);
        var wheel = NativeInputBuilder.Wheel(-120); Assert.Equal(unchecked((uint)-120), wheel.Data.Mouse.MouseData); Assert.Equal(0x0800u, wheel.Data.Mouse.Flags);
    }
    [Fact]
    public async Task CompleteMacroPipelineProducesNativePacketsAndReleasesOnStop()
    {
        var sender = new CapturingSender(); var backend = new WindowsSendInputBackend(sender); await using var engine = new MacroEngine(backend);
        var macro = EngineTests.Macro(new() { Kind = ActionKind.KeyDown, Code = "E" }, new() { Kind = ActionKind.Delay, DurationMs = 60_000 });
        await engine.ConfigureAsync([macro], "pad"); engine.Enable(); engine.Start(macro.Id);
        await EngineTests.Eventually(() => sender.Count == 1); await engine.StopAllAsync();
        Assert.Equal(2, sender.Count); Assert.Equal(2u, sender.Packets[1].Data.Keyboard.Flags); Assert.Empty(await engine.Ledger.HeldAsync());
    }
    [Fact]
    public async Task NativeSenderFailureIsReportedAndCleanupUsesTheOwnedControl()
    {
        var sender = new CapturingSender { FailFirst = true }; await using var engine = new MacroEngine(new WindowsSendInputBackend(sender));
        var macro = EngineTests.Macro(new MacroAction { Kind = ActionKind.KeyTap }); await engine.ConfigureAsync([macro], null); engine.Enable(); engine.Start(macro.Id);
        await EngineTests.Eventually(() => engine.Snapshot.State == ExecutionState.Error);
        await engine.StopAllAsync(); Assert.False(engine.Snapshot.Enabled); Assert.NotNull(engine.Snapshot.LastError); Assert.Empty(await engine.Ledger.HeldAsync());
        Assert.Contains(sender.Packets, p => p.Data.Keyboard.Flags == 2);
    }
    [Fact]
    public void UnavailableVirtualOutputCannotEmitOrArm()
    {
        var backend = new UnavailableVirtualControllerBackend(); Assert.False(backend.IsAvailable); Assert.Contains("driver", backend.UnavailableReason);
        Assert.Throws<NotSupportedException>(() => backend.MoveMouseAsync(1, 1, default));
    }
    private sealed class CapturingSender : INativeInputSender
    {
        private readonly Lock _gate = new(); private readonly List<NativeInput> _packets = [];
        public bool FailFirst; public bool IsAvailable => true;
        public int Count { get { lock (_gate) return _packets.Count; } }
        public IReadOnlyList<NativeInput> Packets { get { lock (_gate) return _packets.ToArray(); } }
        public void Send(NativeInput input)
        {
            lock (_gate)
            {
                if (FailFirst) { FailFirst = false; throw new IOException("Injected native output failure"); }
                _packets.Add(input);
            }
        }
    }
}
