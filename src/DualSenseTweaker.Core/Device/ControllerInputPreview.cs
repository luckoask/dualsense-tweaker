namespace DualSenseTweaker.Core.Device;

/// <summary>Keeps short presses visible for one UI frame without changing the real input used for aiming.</summary>
internal sealed class ControllerInputPreview
{
    private InputFrame _latest = InputFrame.Neutral;
    private ControllerButtons _pressed;
    private bool _ps, _touchClick;
    private byte _l2, _r2;
    private TouchPoint _first, _second;
    private TimeSpan _windowStart;
    private bool _hasWindow;

    public void Update(in InputFrame frame, TimeSpan time = default)
    {
        // Input continues while the window is hidden: never replay minutes of old clicks on opening Gyro.
        if (!_hasWindow || time - _windowStart > TimeSpan.FromMilliseconds(50))
        {
            ClearPending();
            _windowStart = time;
            _hasWindow = true;
        }
        _latest = frame;
        _pressed |= frame.Buttons;
        _ps |= frame.PsButton;
        _touchClick |= frame.TouchpadClick;
        _l2 = Math.Max(_l2, frame.Analog.L2);
        _r2 = Math.Max(_r2, frame.Analog.R2);
        if (frame.First.Active) _first = frame.First;
        if (frame.Second.Active) _second = frame.Second;
    }

    public InputFrame Take()
    {
        var frame = _latest with
        {
            Buttons = _latest.Buttons | _pressed,
            PsButton = _latest.PsButton || _ps,
            TouchpadClick = _latest.TouchpadClick || _touchClick,
            Analog = _latest.Analog with { L2 = Math.Max(_latest.Analog.L2, _l2), R2 = Math.Max(_latest.Analog.R2, _r2) },
            First = _latest.First.Active ? _latest.First : _first,
            Second = _latest.Second.Active ? _latest.Second : _second,
        };
        ClearPending();
        _hasWindow = false;
        return frame;
    }

    public void Reset()
    {
        _latest = InputFrame.Neutral;
        ClearPending();
        _hasWindow = false;
    }

    private void ClearPending()
    {
        _pressed = ControllerButtons.None;
        _ps = _touchClick = false;
        _l2 = _r2 = 0;
        _first = _second = default;
    }
}
