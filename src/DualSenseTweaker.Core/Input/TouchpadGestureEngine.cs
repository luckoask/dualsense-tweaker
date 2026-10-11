using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Input;

public interface IMouseOutput
{
    void Move(int dx, int dy);
    void Button(MouseButton button, bool down);

    /// <param name="vertical">In 1/120 of a wheel notch; positive scrolls up.</param>
    /// <param name="horizontal">In 1/120 of a notch; positive scrolls right.</param>
    void Wheel(int vertical, int horizontal);
}

/// <summary>
/// Laptop-style touchpad gestures from raw DualSense touch frames: one finger moves the cursor
/// (with acceleration), two fingers scroll (axis lock, natural direction, inertia), a physical
/// press clicks by finger count, a short touch taps, and while the pad is held down another
/// finger drags. Pure logic; the caller feeds every input report and supplies the mouse.
/// </summary>
public sealed class TouchpadGestureEngine
{
    private static readonly TimeSpan TapMaxDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PressSettle = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan InertiaMaxPause = TimeSpan.FromMilliseconds(80);
    private const double TapMaxTravel = 60;          // touch units (pad is 1920 × 1080)
    private const double ScrollLockTravel = 24;
    private const double MaxJump = 300;              // bigger per-report jumps are sensor glitches
    private const double WheelPerUnit = 1.2;         // 1/120 notches per touch unit at 100 % → full pad height ≈ 11 notches
    private const double InertiaTauMs = 325;
    private const double InertiaStartVelocity = 0.3; // touch units per ms
    private const double InertiaStopVelocity = 0.02;

    private enum ScrollAxis { Undecided, Vertical, Horizontal, Both }

    private InputFrame _previous;
    private TimeSpan _previousTime;
    private bool _hasPrevious;

    // Touch session: from the first finger down until all fingers are up.
    private bool _session;
    private TimeSpan _sessionStart;
    private int _maxFingers;
    private double _travel;
    private bool _clicked;
    private bool _scrolled;

    // Physical press of the pad.
    private bool _pressed;
    private MouseButton _held;
    private byte? _anchorId;
    private TimeSpan _settleUntil;

    // Sub-pixel and sub-notch remainders.
    private double _moveX, _moveY, _wheelV, _wheelH;

    private ScrollAxis _axis;
    private double _lockX, _lockY;
    private bool _scrolling;
    private double _velX, _velY;
    private TimeSpan _lastScroll;

    private bool _coasting;
    private double _coastX, _coastY;

    public void Process(InputFrame frame, TimeSpan time, TouchpadSettings s, IMouseOutput output)
    {
        double dt = _hasPrevious ? Math.Max((time - _previousTime).TotalMilliseconds, 0.5) : 0;
        int fingers = frame.FingerCount;

        if (_coasting)
        {
            if (fingers > 0)
                _coasting = false; // touching the pad stops the inertia, like on a laptop
            else
                Coast(dt, s, output);
        }

        if (fingers > 0 && !_session)
            StartSession(time);
        _maxFingers = Math.Max(_maxFingers, fingers);

        HandlePress(frame, time, fingers, s, output);
        if (_hasPrevious && fingers > 0)
            HandleMotion(frame, time, dt, fingers, s, output);

        if (_scrolling && fingers < 2)
        {
            _scrolling = false;
            bool flicked = time - _lastScroll <= InertiaMaxPause
                           && Math.Max(Math.Abs(_velX), Math.Abs(_velY)) >= InertiaStartVelocity;
            if (s.Inertia && flicked)
            {
                _coasting = true;
                _coastX = _velX;
                _coastY = _velY;
            }
        }

        if (fingers == 0 && _session)
            EndSession(time, s, output);

        _previous = frame;
        _previousTime = time;
        _hasPrevious = true;
    }

    /// <summary>Releases a held button and forgets all motion (disconnect, disable, exit).</summary>
    public void Reset(IMouseOutput output)
    {
        if (_held != MouseButton.None)
            output.Button(_held, false);
        _held = MouseButton.None;
        _pressed = false;
        _anchorId = null;
        _session = false;
        _scrolling = false;
        _coasting = false;
        _hasPrevious = false;
        _moveX = _moveY = _wheelV = _wheelH = 0;
    }

    private void StartSession(TimeSpan time)
    {
        _session = true;
        _sessionStart = time;
        _maxFingers = 0;
        _travel = 0;
        _clicked = false;
        _scrolled = false;
        _axis = ScrollAxis.Undecided;
        _lockX = _lockY = 0;
        _velX = _velY = 0;
    }

    private void EndSession(TimeSpan time, TouchpadSettings s, IMouseOutput output)
    {
        _session = false;
        bool tap = s.TapToClick && !_clicked && time - _sessionStart <= TapMaxDuration && _travel <= TapMaxTravel;
        if (!tap)
            return;
        var button = _maxFingers >= 2 ? s.Tap2 : s.Tap1;
        if (button == MouseButton.None)
            return;
        output.Button(button, true);
        output.Button(button, false);
    }

    /// <summary>The button follows the number of fingers at the moment of the press and is released with the pad.</summary>
    private void HandlePress(InputFrame frame, TimeSpan time, int fingers, TouchpadSettings s, IMouseOutput output)
    {
        if (frame.TouchpadClick == _pressed)
            return;
        _pressed = frame.TouchpadClick;
        _settleUntil = time + PressSettle; // pressing or releasing the pad nudges the finger

        if (_pressed)
        {
            _clicked = true;
            _held = fingers >= 2 ? s.Click2 : s.Click1;
            _anchorId = fingers == 1 ? (frame.First.Active ? frame.First.Id : frame.Second.Id) : null;
            if (_held != MouseButton.None)
                output.Button(_held, true);
        }
        else
        {
            if (_held != MouseButton.None)
                output.Button(_held, false);
            _held = MouseButton.None;
            _anchorId = null;
        }
    }

    private void HandleMotion(InputFrame frame, TimeSpan time, double dt, int fingers, TouchpadSettings s, IMouseOutput output)
    {
        var d1 = Delta(frame.First);
        var d2 = Delta(frame.Second);
        if (d1.Ok)
            _travel += Math.Sqrt(d1.Dx * d1.Dx + d1.Dy * d1.Dy);
        if (d2.Ok)
            _travel += Math.Sqrt(d2.Dx * d2.Dx + d2.Dy * d2.Dy);
        if (time < _settleUntil)
            return;

        if (_pressed)
        {
            // Click-and-drag: the thumb holds the pad down, the other finger moves the cursor.
            var drag = d1.Ok && d2.Ok
                ? d1.Id == _anchorId ? d2 : d2.Id == _anchorId ? d1 : Larger(d1, d2)
                : d1.Ok ? d1 : d2;
            if (drag.Ok)
                MoveCursor(drag.Dx, drag.Dy, dt, s, output);
            return;
        }

        if (fingers >= 2)
        {
            if (d1.Ok && d2.Ok)
                Scroll((d1.Dx + d2.Dx) / 2, (d1.Dy + d2.Dy) / 2, dt, time, s, output);
            return;
        }

        // After a two-finger scroll the leftover finger does not move the cursor until all fingers lift.
        var single = d1.Ok ? d1 : d2;
        if (single.Ok && !_scrolled)
            MoveCursor(single.Dx, single.Dy, dt, s, output);
    }

    private (bool Ok, byte Id, double Dx, double Dy) Delta(TouchPoint now)
    {
        if (!now.Active)
            return default;
        var before = _previous.First.Active && _previous.First.Id == now.Id ? _previous.First
            : _previous.Second.Active && _previous.Second.Id == now.Id ? _previous.Second
            : default;
        if (!before.Active)
            return default;
        double dx = now.X - before.X, dy = now.Y - before.Y;
        if (Math.Abs(dx) > MaxJump || Math.Abs(dy) > MaxJump)
            return default;
        return (true, now.Id, dx, dy);
    }

    private static (bool Ok, byte Id, double Dx, double Dy) Larger((bool Ok, byte Id, double Dx, double Dy) a, (bool Ok, byte Id, double Dx, double Dy) b) =>
        a.Dx * a.Dx + a.Dy * a.Dy >= b.Dx * b.Dx + b.Dy * b.Dy ? a : b;

    /// <summary>Base gain from the speed setting, boosted up to 3.5× for fast swipes by the acceleration setting.</summary>
    private void MoveCursor(double dx, double dy, double dt, TouchpadSettings s, IMouseOutput output)
    {
        double velocity = Math.Sqrt(dx * dx + dy * dy) / dt;
        double gain = (0.1 + 1.4 * s.CursorSpeed / 100.0)
                      * (1 + s.Acceleration / 100.0 * 2.5 * Math.Clamp((velocity - 0.3) / 2.5, 0, 1));
        _moveX += dx * gain;
        _moveY += dy * gain;
        int x = (int)_moveX, y = (int)_moveY;
        _moveX -= x;
        _moveY -= y;
        if (x != 0 || y != 0)
            output.Move(x, y);
    }

    private void Scroll(double dx, double dy, double dt, TimeSpan time, TouchpadSettings s, IMouseOutput output)
    {
        _scrolling = true;
        double emitX = dx, emitY = dy;
        if (_axis == ScrollAxis.Undecided)
        {
            // Decide the axis after a little travel, then emit everything traveled so far.
            _lockX += dx;
            _lockY += dy;
            if (Math.Abs(_lockX) + Math.Abs(_lockY) < ScrollLockTravel)
                return;
            double ax = Math.Abs(_lockX), ay = Math.Abs(_lockY);
            _axis = !s.HorizontalScrolling || ay >= 2 * ax ? ScrollAxis.Vertical
                : ax >= 2 * ay ? ScrollAxis.Horizontal
                : ScrollAxis.Both;
            _scrolled = true;
            emitX = _lockX;
            emitY = _lockY;
        }

        if (_axis == ScrollAxis.Vertical)
            emitX = dx = 0;
        else if (_axis == ScrollAxis.Horizontal)
            emitY = dy = 0;

        EmitScroll(emitX, emitY, s, output);
        _velX = 0.6 * _velX + 0.4 * dx / dt;
        _velY = 0.6 * _velY + 0.4 * dy / dt;
        _lastScroll = time;
    }

    private void Coast(double dt, TouchpadSettings s, IMouseOutput output)
    {
        EmitScroll(_coastX * dt, _coastY * dt, s, output);
        double decay = Math.Exp(-dt / InertiaTauMs);
        _coastX *= decay;
        _coastY *= decay;
        if (Math.Abs(_coastX) < InertiaStopVelocity && Math.Abs(_coastY) < InertiaStopVelocity)
            _coasting = false;
    }

    /// <summary>Natural direction: fingers down → content down (wheel up); fingers right → content right (scroll left).</summary>
    private void EmitScroll(double dx, double dy, TouchpadSettings s, IMouseOutput output)
    {
        double k = WheelPerUnit * s.ScrollSpeed / 100.0 * (s.NaturalScrolling ? 1 : -1);
        _wheelV += dy * k;
        _wheelH -= dx * k;
        int v = (int)_wheelV, h = (int)_wheelH;
        _wheelV -= v;
        _wheelH -= h;
        if (v != 0 || h != 0)
            output.Wheel(v, h);
    }
}
