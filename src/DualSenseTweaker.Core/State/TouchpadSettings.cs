namespace DualSenseTweaker.Core.State;

public enum MouseButton { None, Left, Right, Middle }

/// <summary>DualSense touchpad as a laptop-style mouse (app-wide, saved in settings.json).</summary>
public sealed record TouchpadSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Button for a physical press with one finger on the pad.</summary>
    public MouseButton Click1 { get; init; } = MouseButton.Left;
    /// <summary>Button for a physical press with two fingers on the pad.</summary>
    public MouseButton Click2 { get; init; } = MouseButton.Right;
    /// <summary>A short touch without pressing clicks.</summary>
    public bool TapToClick { get; init; } = true;
    public MouseButton Tap1 { get; init; } = MouseButton.Left;
    public MouseButton Tap2 { get; init; } = MouseButton.Right;

    /// <summary>0–100 %.</summary>
    public int CursorSpeed { get; init; } = 31;
    /// <summary>Extra speed for fast swipes, 0–100 %.</summary>
    public int Acceleration { get; init; } = 54;

    /// <summary>10–300 %.</summary>
    public int ScrollSpeed { get; init; } = 100;
    /// <summary>Content follows the fingers, like on a laptop.</summary>
    public bool NaturalScrolling { get; init; } = true;
    /// <summary>A quick flick keeps scrolling and slows down smoothly.</summary>
    public bool Inertia { get; init; } = true;
    public bool HorizontalScrolling { get; init; } = true;

    public TouchpadSettings Normalize() => this with
    {
        Click1 = Valid(Click1, MouseButton.Left),
        Click2 = Valid(Click2, MouseButton.Right),
        Tap1 = Valid(Tap1, MouseButton.Left),
        Tap2 = Valid(Tap2, MouseButton.Right),
        CursorSpeed = Math.Clamp(CursorSpeed, 0, 100),
        Acceleration = Math.Clamp(Acceleration, 0, 100),
        ScrollSpeed = Math.Clamp(ScrollSpeed, 10, 300),
    };

    private static MouseButton Valid(MouseButton button, MouseButton fallback) => Enum.IsDefined(button) ? button : fallback;
}
