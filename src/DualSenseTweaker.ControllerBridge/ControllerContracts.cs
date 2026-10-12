namespace DualSenseTweaker.ControllerBridge;

// These bit positions deliberately match the established Core.Device.ControllerButtons contract.
[Flags]
public enum PadButtons : uint
{
    None = 0, DpadUp = 1, DpadRight = 2, DpadDown = 4, DpadLeft = 8,
    Square = 1 << 4, Cross = 1 << 5, Circle = 1 << 6, Triangle = 1 << 7,
    L1 = 1 << 8, R1 = 1 << 9, L2 = 1 << 10, R2 = 1 << 11,
    Create = 1 << 12, Options = 1 << 13, L3 = 1 << 14, R3 = 1 << 15,
    PS = 1 << 16, Touchpad = 1 << 17, Mute = 1 << 18,
    FnLeft = 1 << 19, FnRight = 1 << 20, PaddleLeft = 1 << 21, PaddleRight = 1 << 22,
}

public readonly record struct PadAnalog(byte LeftX, byte LeftY, byte RightX, byte RightY, byte L2, byte R2)
{
    public static PadAnalog Centered => new(128, 128, 128, 128, 0, 0);
}
public readonly record struct TouchContact(bool Active, byte Id, int X, int Y);
public sealed record ControllerDescriptor(string Id, string Model, string Transport, int? BatteryPercent);
public sealed record PadFrame(string ControllerId, Guid SessionId, long Sequence, long TimestampUs,
    PadButtons Buttons, PadAnalog Analog, TouchContact First = default, TouchContact Second = default, Guid? ConnectionId = null)
{
    public bool IsPressed(PadButtons buttons) => buttons != PadButtons.None && (Buttons & buttons) == buttons;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ControllerId) || ControllerId.Length > 1024 || SessionId == Guid.Empty ||
            Sequence < 0 || TimestampUs < 0 || ConnectionId == Guid.Empty || ((uint)Buttons & ~((1u << 23) - 1)) != 0 ||
            First.X is < 0 or > 1919 || First.Y is < 0 or > 1079 || Second.X is < 0 or > 1919 || Second.Y is < 0 or > 1079)
            throw new InvalidDataException("Invalid normalized controller frame.");
    }
}
