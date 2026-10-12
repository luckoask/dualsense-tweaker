using System.ComponentModel;
using System.Runtime.InteropServices;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Output;

[StructLayout(LayoutKind.Sequential)]
public struct NativeKeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
[StructLayout(LayoutKind.Sequential)]
public struct NativeMouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
[StructLayout(LayoutKind.Explicit)]
public struct NativeInputUnion
{
    [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    [FieldOffset(0)] public NativeMouseInput Mouse;
}
[StructLayout(LayoutKind.Sequential)]
public struct NativeInput { public uint Type; public NativeInputUnion Data; }

/// <summary>Native packet construction is testable without injecting inputs into the desktop.</summary>
public static class NativeInputBuilder
{
    private static readonly IReadOnlyDictionary<string, ushort> Keys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = 0x08, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["SHIFT"] = 0x10, ["CTRL"] = 0x11, ["ALT"] = 0x12,
        ["CAPSLOCK"] = 0x14, ["ESCAPE"] = 0x1B, ["SPACE"] = 0x20, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
        ["END"] = 0x23, ["HOME"] = 0x24, ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["INSERT"] = 0x2D, ["DELETE"] = 0x2E, ["LSHIFT"] = 0xA0, ["RSHIFT"] = 0xA1,
        ["LCTRL"] = 0xA2, ["RCTRL"] = 0xA3, ["LALT"] = 0xA4, ["RALT"] = 0xA5,
    };
    public static NativeInput Control(OutputControl control, bool pressed)
    {
        control = control.Normalize(); control.Validate(); string code = control.Code;
        if (control.Kind == ControlKind.Keyboard)
        {
            ushort key = code.Length == 1 ? code[0] : Keys.TryGetValue(code, out var value) ? value : (ushort)(0x70 + int.Parse(code.AsSpan(1)) - 1);
            uint flags = pressed ? 0u : 2u;
            if (code is "RCTRL" or "RALT" or "LEFT" or "UP" or "RIGHT" or "DOWN" or "HOME" or "END" or "INSERT" or "DELETE" or "PAGEUP" or "PAGEDOWN") flags |= 1;
            return new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = flags } } };
        }
        uint mouseFlags = code switch
        {
            "LEFT" => pressed ? 0x0002u : 0x0004u, "RIGHT" => pressed ? 0x0008u : 0x0010u,
            "MIDDLE" => pressed ? 0x0020u : 0x0040u, _ => pressed ? 0x0080u : 0x0100u,
        };
        return new() { Type = 0, Data = new() { Mouse = new() { Flags = mouseFlags, MouseData = code == "X1" ? 1u : code == "X2" ? 2u : 0u } } };
    }
    public static NativeInput Move(int x, int y) => new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Flags = 0x0001 } } };
    public static NativeInput Wheel(int delta) => new() { Type = 0, Data = new() { Mouse = new() { MouseData = unchecked((uint)delta), Flags = 0x0800 } } };
}

public interface INativeInputSender
{
    bool IsAvailable { get; }
    void Send(NativeInput input);
}
public sealed class Win32InputSender : INativeInputSender
{
    public bool IsAvailable => OperatingSystem.IsWindows();
    public void Send(NativeInput input)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("SendInput requires Windows.");
        uint sent = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
        if (sent != 1)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"SendInput delivered {sent}/1 events. Windows may block input across integrity levels or to the current desktop. Error {error}.");
        }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}

public sealed class WindowsSendInputBackend(INativeInputSender? sender = null) : IInputOutputBackend
{
    private readonly INativeInputSender _sender = sender ?? new Win32InputSender();
    public string Name => "Windows keyboard / mouse";
    public bool IsAvailable => _sender.IsAvailable;
    public string? UnavailableReason => IsAvailable ? null : "Keyboard/mouse output requires an interactive Windows desktop.";
    public ValueTask SetControlAsync(OutputControl control, bool pressed, CancellationToken token)
    { token.ThrowIfCancellationRequested(); _sender.Send(NativeInputBuilder.Control(control, pressed)); return ValueTask.CompletedTask; }
    public ValueTask MoveMouseAsync(int x, int y, CancellationToken token)
    { token.ThrowIfCancellationRequested(); _sender.Send(NativeInputBuilder.Move(x, y)); return ValueTask.CompletedTask; }
    public ValueTask WheelAsync(int delta, CancellationToken token)
    { token.ThrowIfCancellationRequested(); _sender.Send(NativeInputBuilder.Wheel(delta)); return ValueTask.CompletedTask; }
}

public sealed class UnavailableVirtualControllerBackend : IInputOutputBackend
{
    public string Name => "Virtual controller (unavailable)";
    public bool IsAvailable => false;
    public string UnavailableReason => "No maintained, signed, licensed virtual-controller driver has been verified for redistribution. ViGEmBus is retired; VirtualPad requires a commercial agreement. Use keyboard/mouse or diagnostics.";
    public ValueTask SetControlAsync(OutputControl control, bool pressed, CancellationToken token) => throw new NotSupportedException(UnavailableReason);
    public ValueTask MoveMouseAsync(int x, int y, CancellationToken token) => throw new NotSupportedException(UnavailableReason);
    public ValueTask WheelAsync(int delta, CancellationToken token) => throw new NotSupportedException(UnavailableReason);
}
