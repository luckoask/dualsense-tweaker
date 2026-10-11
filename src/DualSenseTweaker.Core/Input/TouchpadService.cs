using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Input;

/// <summary>
/// Runs the touchpad gestures on every input report of the active controller and injects the
/// mouse events. PS + touchpad press toggles the feature (that press never clicks). Held buttons
/// are released when the controller disconnects, the feature is switched off or the app exits.
/// </summary>
public sealed class TouchpadService : IDisposable
{
    private readonly DualSenseManager _manager;
    private readonly PreferencesState _preferences;
    private readonly IMouseOutput _mouse;
    private readonly TouchpadGestureEngine _engine = new();
    private readonly Lock _gate = new();
    private InputFrame _latest;
    private bool _previousPress;
    private bool _swallowPress;
    private bool _engineActive;

    public TouchpadService(DualSenseManager manager, PreferencesState preferences, IMouseOutput mouse)
    {
        _manager = manager;
        _preferences = preferences;
        _mouse = mouse;
        _manager.InputReceived += OnInput;
        _manager.ConnectionChanged += OnConnectionChanged;
        _preferences.Changed += OnPreferencesChanged;
    }

    /// <summary>Raised (on the input thread) after PS + touchpad press switched the mouse on or off.</summary>
    public event EventHandler<bool>? Toggled;

    /// <summary>Latest contacts, for the live touchpad view.</summary>
    public InputFrame Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    public void Dispose()
    {
        _manager.InputReceived -= OnInput;
        _manager.ConnectionChanged -= OnConnectionChanged;
        _preferences.Changed -= OnPreferencesChanged;
        Release();
    }

    private void OnInput(InputFrame frame, TimeSpan time)
    {
        bool toggle = false;
        lock (_gate)
        {
            _latest = frame;
            bool pressEdge = frame.TouchpadClick && !_previousPress;
            _previousPress = frame.TouchpadClick;
            if (pressEdge && frame.PsButton)
            {
                toggle = true;
                _swallowPress = true;
            }
            if (_swallowPress)
            {
                if (!frame.TouchpadClick)
                    _swallowPress = false;
                frame = frame with { TouchpadClick = false };
            }

            var settings = _preferences.Current.Touchpad;
            if (settings.Enabled && !toggle)
            {
                _engineActive = true;
                _engine.Process(frame, time, settings, _mouse);
            }
            else if (_engineActive)
            {
                _engineActive = false;
                _engine.Reset(_mouse);
            }
        }

        if (!toggle)
            return;
        bool enabled = !_preferences.Current.Touchpad.Enabled;
        _preferences.Update(p => p with { Touchpad = p.Touchpad with { Enabled = enabled } });
        Log.Info(L.Format($"Dotykový panel ako myš {(enabled ? L.Text("zapnutý") : L.Text("vypnutý"))} (PS + klik)."));
        Toggled?.Invoke(this, enabled);
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (!_manager.Connection.IsConnected)
            Release();
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        if (!_preferences.Current.Touchpad.Enabled)
            Release();
    }

    private void Release()
    {
        lock (_gate)
        {
            _engine.Reset(_mouse);
            _engineActive = false;
            _latest = default;
        }
    }
}
