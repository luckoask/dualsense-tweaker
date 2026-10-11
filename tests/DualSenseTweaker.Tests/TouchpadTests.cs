using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Input;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public class TouchParsingTests
{
    [Fact]
    public void ParsesTwelveBitCoordinatesAndActiveFlag()
    {
        // x = 0x5A3 (1443), y = 0x2C7 (711): x_lo A3, x_hi 5 | y_lo 7 << 4, y_hi 2C
        var point = InputReportParser.ParseTouch([0x05, 0xA3, 0x75, 0x2C]);

        Assert.Equal(new TouchPoint(true, 5, 1443, 711), point);
        Assert.False(InputReportParser.ParseTouch([0x85, 0, 0, 0]).Active);
    }

    [Fact]
    public void TouchPointsComeFromOffset32()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1 + 32] = 0x03;
        report[1 + 33] = 0x10;           // first finger at x = 16
        report[1 + 36] = 0x80;           // second slot inactive

        InputReportParser.TryParse(report, ConnectionType.Usb, out _, out var frame);

        Assert.Equal(1, frame.FingerCount);
        Assert.Equal(16, frame.First.X);
    }
}

public class TouchpadGestureTests
{
    private sealed class RecordingMouse : IMouseOutput
    {
        public List<string> Events { get; } = [];
        public int MoveX, MoveY, WheelV, WheelH;

        public void Move(int dx, int dy)
        {
            MoveX += dx;
            MoveY += dy;
        }

        public void Button(MouseButton button, bool down) => Events.Add($"{button}{(down ? "↓" : "↑")}");

        public void Wheel(int vertical, int horizontal)
        {
            WheelV += vertical;
            WheelH += horizontal;
        }
    }

    private static readonly TouchpadSettings Settings = new();
    private readonly TouchpadGestureEngine _engine = new();
    private readonly RecordingMouse _mouse = new();
    private int _ms;

    private static TouchPoint Finger(byte id, int x, int y) => new(true, id, x, y);

    /// <summary>Feeds one report 4 ms after the previous one.</summary>
    private void Frame(TouchPoint first = default, TouchPoint second = default, bool pressed = false, TouchpadSettings? settings = null)
    {
        _engine.Process(new InputFrame(false, pressed, first, second), TimeSpan.FromMilliseconds(_ms), settings ?? Settings, _mouse);
        _ms += 4;
    }

    private void Idle(int reports = 1, TouchpadSettings? settings = null)
    {
        for (int i = 0; i < reports; i++)
            Frame(settings: settings);
    }

    [Fact]
    public void OneFingerMovesTheCursor()
    {
        for (int i = 0; i < 20; i++)
            Frame(Finger(1, 500 + i * 10, 500 + i * 5));
        Idle();

        Assert.True(_mouse.MoveX > 0 && _mouse.MoveY > 0 && _mouse.MoveX > _mouse.MoveY);
        Assert.Empty(_mouse.Events); // long move is not a tap
    }

    [Fact]
    public void FasterSwipeMovesFurtherWithAcceleration()
    {
        for (int i = 0; i < 20; i++)
            Frame(Finger(1, 100 + i * 4, 500));
        int slow = _mouse.MoveX;
        Idle(60);

        _mouse.MoveX = 0;
        for (int i = 0; i < 5; i++)
            Frame(Finger(2, 100 + i * 16, 500)); // same 80 units, 4× faster

        Assert.True(_mouse.MoveX > slow, $"fast {_mouse.MoveX} vs slow {slow}");
    }

    [Fact]
    public void OneFingerPressIsLeftAndTwoFingerPressIsRight()
    {
        Frame(Finger(1, 900, 500));
        Frame(Finger(1, 900, 500), pressed: true);
        Frame(Finger(1, 900, 500));
        Idle(80);
        Frame(Finger(2, 800, 500), Finger(3, 1100, 500));
        Frame(Finger(2, 800, 500), Finger(3, 1100, 500), pressed: true);
        Frame(Finger(2, 800, 500), Finger(3, 1100, 500));
        Idle();

        Assert.Equal(["Left↓", "Left↑", "Right↓", "Right↑"], _mouse.Events);
    }

    [Fact]
    public void ShortTouchTapsAndCanBeDisabled()
    {
        Frame(Finger(1, 900, 500));
        Frame(Finger(1, 902, 501));
        Idle();
        Assert.Equal(["Left↓", "Left↑"], _mouse.Events);

        _mouse.Events.Clear();
        var noTap = Settings with { TapToClick = false };
        Frame(Finger(2, 900, 500), settings: noTap);
        Idle(settings: noTap);
        Assert.Empty(_mouse.Events);
    }

    [Fact]
    public void TwoFingerTapIsRightClick()
    {
        Frame(Finger(1, 800, 500), Finger(2, 1100, 500));
        Frame(Finger(1, 801, 500), Finger(2, 1100, 501));
        Idle();

        Assert.Equal(["Right↓", "Right↑"], _mouse.Events);
    }

    [Fact]
    public void TwoFingersDownScrollUpWithNaturalDirection()
    {
        for (int i = 0; i < 20; i++)
            Frame(Finger(1, 800, 300 + i * 10), Finger(2, 1100, 300 + i * 10));

        Assert.True(_mouse.WheelV > 0, $"wheel {_mouse.WheelV}");
        Assert.Equal(0, _mouse.WheelH); // vertical swipe locks to the vertical axis
        Assert.Equal(0, _mouse.MoveX + _mouse.MoveY);
    }

    [Fact]
    public void TraditionalDirectionInverts()
    {
        var traditional = Settings with { NaturalScrolling = false };
        for (int i = 0; i < 20; i++)
            Frame(Finger(1, 800, 300 + i * 10), Finger(2, 1100, 300 + i * 10), settings: traditional);

        Assert.True(_mouse.WheelV < 0);
    }

    [Fact]
    public void SidewaysSwipeScrollsHorizontally()
    {
        for (int i = 0; i < 20; i++)
            Frame(Finger(1, 400 + i * 10, 400), Finger(2, 400 + i * 10, 700));

        Assert.True(_mouse.WheelH < 0, $"hwheel {_mouse.WheelH}"); // natural: fingers right → scroll left
        Assert.Equal(0, _mouse.WheelV);
    }

    [Fact]
    public void FlickKeepsScrollingAfterLiftThenStops()
    {
        for (int i = 0; i < 10; i++)
            Frame(Finger(1, 800, 200 + i * 40), Finger(2, 1100, 200 + i * 40));
        int atLift = _mouse.WheelV;

        Idle(50);
        int coasted = _mouse.WheelV - atLift;
        Idle(500);
        int later = _mouse.WheelV;
        Idle(50);

        Assert.True(coasted > 0, $"coasted {coasted}");
        Assert.Equal(later, _mouse.WheelV); // inertia has died out
    }

    [Fact]
    public void NoInertiaWhenDisabledOrFingersStopped()
    {
        var noInertia = Settings with { Inertia = false };
        for (int i = 0; i < 10; i++)
            Frame(Finger(1, 800, 200 + i * 40), Finger(2, 1100, 200 + i * 40), settings: noInertia);
        int atLift = _mouse.WheelV;
        Idle(50, noInertia);

        Assert.Equal(atLift, _mouse.WheelV);
    }

    [Fact]
    public void ThumbHoldsClickWhileOtherFingerDrags()
    {
        Frame(Finger(1, 300, 900));
        Frame(Finger(1, 300, 900), pressed: true);
        for (int i = 0; i < 40; i++)
            Frame(Finger(1, 300, 900), Finger(2, 900 + i * 8, 400), pressed: true);
        int dragged = _mouse.MoveX;
        Frame(Finger(1, 300, 900));
        Idle();

        Assert.Equal(["Left↓", "Left↑"], _mouse.Events);
        Assert.True(dragged > 0, $"dragged {dragged}");
    }

    [Fact]
    public void ResetReleasesAHeldButton()
    {
        Frame(Finger(1, 300, 900));
        Frame(Finger(1, 300, 900), pressed: true);

        _engine.Reset(_mouse);

        Assert.Equal(["Left↓", "Left↑"], _mouse.Events);
    }

    [Fact]
    public void NoneMappingDoesNotClick()
    {
        var settings = Settings with { Click1 = MouseButton.None };
        Frame(Finger(1, 900, 500), settings: settings);
        Frame(Finger(1, 900, 500), pressed: true, settings: settings);
        Frame(Finger(1, 900, 500), settings: settings);
        Idle(settings: settings);

        Assert.Empty(_mouse.Events);
    }
}
