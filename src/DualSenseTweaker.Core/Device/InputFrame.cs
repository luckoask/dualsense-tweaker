namespace DualSenseTweaker.Core.Device;

/// <summary>One touchpad contact (0–1919 × 0–1079). <see cref="Id"/> changes with every new touch.</summary>
public readonly record struct TouchPoint(bool Active, byte Id, int X, int Y);

/// <summary>Digital buttons of one input report (D-pad decoded from the hat switch).</summary>
[Flags]
public enum ControllerButtons : uint
{
    None = 0,
    DpadUp = 1 << 0,
    DpadRight = 1 << 1,
    DpadDown = 1 << 2,
    DpadLeft = 1 << 3,
    Square = 1 << 4,
    Cross = 1 << 5,
    Circle = 1 << 6,
    Triangle = 1 << 7,
    L1 = 1 << 8,
    R1 = 1 << 9,
    L2 = 1 << 10,
    R2 = 1 << 11,
    Create = 1 << 12,
    Options = 1 << 13,
    L3 = 1 << 14,
    R3 = 1 << 15,
    PS = 1 << 16,
    Touchpad = 1 << 17,
    Mute = 1 << 18,
    /// <summary>DualSense Edge only.</summary>
    FnLeft = 1 << 19,
    FnRight = 1 << 20,
    PaddleLeft = 1 << 21,
    PaddleRight = 1 << 22,
}

/// <summary>Sticks (0–255, 128 = center, Y grows downwards) and analog triggers (0 = released).</summary>
public readonly record struct AnalogState(byte LeftX, byte LeftY, byte RightX, byte RightY, byte L2, byte R2)
{
    public static AnalogState Centered { get; } = new(128, 128, 128, 128, 0, 0);
}

/// <summary>
/// Raw IMU sample: gyroscope (pitch, yaw, roll) and accelerometer (x right, y up, z toward the
/// player), plus the sensor clock in 1/3 µs. Convert with <see cref="MotionCalibration"/>.
/// </summary>
public readonly record struct MotionSample(short GyroX, short GyroY, short GyroZ, short AccelX, short AccelY, short AccelZ, uint Timestamp);

/// <summary>Buttons, sticks, touchpad contacts and motion of one input report.</summary>
public readonly record struct InputFrame(bool PsButton, bool TouchpadClick, TouchPoint First, TouchPoint Second)
{
    public static InputFrame Neutral { get; } = new(false, false, default, default) { Analog = AnalogState.Centered };
    public const int TouchpadWidth = 1920;
    public const int TouchpadHeight = 1080;

    public ControllerButtons Buttons { get; init; }
    public AnalogState Analog { get; init; }
    public MotionSample Motion { get; init; }

    public int FingerCount => (First.Active ? 1 : 0) + (Second.Active ? 1 : 0);

    public bool IsPressed(ControllerButtons button) => (Buttons & button) == button && button != ControllerButtons.None;
}
