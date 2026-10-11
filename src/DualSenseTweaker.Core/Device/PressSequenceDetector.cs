namespace DualSenseTweaker.Core.Device;

/// <summary>
/// Recognizes a quick series of presses of one button (e.g. 5× PS): every press must follow the
/// previous one within <see cref="MaxGap"/>. Fed with the button state of each input report.
/// </summary>
public sealed class PressSequenceDetector(int count = 5, TimeSpan? maxGap = null)
{
    public TimeSpan MaxGap { get; } = maxGap ?? TimeSpan.FromMilliseconds(700);

    private int _presses;
    private TimeSpan _lastPress;
    private bool _wasDown;

    /// <returns>True on the press that completes the series (the count then starts over).</returns>
    public bool Update(bool down, TimeSpan now)
    {
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed)
            return false;

        _presses = _presses > 0 && now - _lastPress <= MaxGap ? _presses + 1 : 1;
        _lastPress = now;
        if (_presses < count)
            return false;
        _presses = 0;
        return true;
    }
}
