using System.Runtime.InteropServices;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Input;

/// <summary>
/// Mouse events via SendInput. Windows blocks injection into windows of elevated (admin) apps
/// unless this app runs elevated too (UIPI); such calls fail silently and are logged once.
/// </summary>
public sealed class SendInputMouse : IMouseOutput
{
    private const uint InputMouse = 0;
    private const uint MoveFlag = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010,
        MiddleDown = 0x0020, MiddleUp = 0x0040, WheelFlag = 0x0800, HWheelFlag = 0x1000;

    private bool _failureLogged;

    public void Move(int dx, int dy) => Send(Mouse(dx, dy, 0, MoveFlag));

    public void Button(MouseButton button, bool down)
    {
        uint flags = button switch
        {
            MouseButton.Left => down ? LeftDown : LeftUp,
            MouseButton.Right => down ? RightDown : RightUp,
            MouseButton.Middle => down ? MiddleDown : MiddleUp,
            _ => 0,
        };
        if (flags != 0)
            Send(Mouse(0, 0, 0, flags));
    }

    public void Wheel(int vertical, int horizontal)
    {
        if (vertical != 0 && horizontal != 0)
            Send(Mouse(0, 0, vertical, WheelFlag), Mouse(0, 0, horizontal, HWheelFlag));
        else if (vertical != 0)
            Send(Mouse(0, 0, vertical, WheelFlag));
        else if (horizontal != 0)
            Send(Mouse(0, 0, horizontal, HWheelFlag));
    }

    private static Input Mouse(int dx, int dy, int data, uint flags) => new()
    {
        Type = InputMouse,
        Data = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags },
    };

    private void Send(params Input[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length || _failureLogged)
            return;
        _failureLogged = true;
        Log.Warn(L.Format($"SendInput zlyhal (chyba {Marshal.GetLastPInvokeError()}); okno v popredí je pravdepodobne spustené ako správca."));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Data; // largest member of the INPUT union, so the size matches
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
