using System.Numerics;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.Input;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Motion;

/// <summary>One point of the live gyro / accelerometer graphs.</summary>
public readonly record struct MotionPoint(double Time, Vector3 Gyro, Vector3 Accel);

/// <summary>What the gyro tester shows.</summary>
public sealed record MotionSnapshot(
    MotionReading Reading,
    InputFrame Frame,
    double SampleRate,
    Vector3 Bias,
    bool FactoryCalibration,
    CalibrationProgress Calibration,
    bool ActivationHeld,
    bool MouseMoving);

/// <summary>
/// Runs the IMU pipeline on every input report of the active controller: sensor fusion for the
/// tester, gyro → mouse for games, calibration per controller and the PS + R1 toggle.
/// </summary>
public sealed class GyroService : IDisposable
{
    private const int HistoryCapacity = 4096;
    private const byte ShotThreshold = 160;

    private readonly DualSenseManager _manager;
    private readonly SettingsState _settings;
    private readonly PreferencesState _preferences;
    private readonly IMouseOutput _mouse;
    private readonly MotionProcessor _processor = new();
    private readonly GyroMouseEngine _engine = new();
    private readonly ControllerInputPreview _inputPreview = new();
    private readonly MotionPoint[] _history = new MotionPoint[HistoryCapacity];
    private readonly Lock _gate = new();
    private int _historyStart, _historyCount;
    private string? _controllerKey;
    private MotionSnapshot? _latest;
    private Vector3? _completedCalibration;
    private bool _previousCombo, _previousShot, _wasActive;
    private Vector2 _preview;
    private int _shots;
    private int _rateCount;
    private TimeSpan _rateStart;
    private double _sampleRate;

    public GyroService(DualSenseManager manager, SettingsState settings, PreferencesState preferences, IMouseOutput mouse)
    {
        _manager = manager;
        _settings = settings;
        _preferences = preferences;
        _mouse = mouse;
        _processor.AutoCalibration = preferences.Current.Gyro.AutoCalibration;
        _processor.CalibrationCompleted += bias => _completedCalibration = bias;
        _manager.InputReceived += OnInput;
        _manager.ConnectionChanged += OnConnectionChanged;
        _preferences.Changed += OnPreferencesChanged;
        OnConnectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Raised (on the input thread) after PS + R1 switched gyro → mouse on or off.</summary>
    public event EventHandler<bool>? Toggled;

    /// <summary>Raised (on the input thread) when a manual calibration finished; the offset is saved.</summary>
    public event EventHandler<Vector3>? Calibrated;

    /// <summary>Latest processed sample, or null without a controller.</summary>
    public MotionSnapshot? Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>The samples of the last <paramref name="seconds"/>, oldest first.</summary>
    public MotionPoint[] History(double seconds)
    {
        lock (_gate)
        {
            if (_historyCount == 0)
                return [];
            double newest = _history[(_historyStart + _historyCount - 1) % HistoryCapacity].Time;
            int take = 0;
            while (take < _historyCount && newest - _history[(_historyStart + _historyCount - 1 - take) % HistoryCapacity].Time <= seconds)
                take++;
            var result = new MotionPoint[take];
            for (int i = 0; i < take; i++)
                result[i] = _history[(_historyStart + _historyCount - take + i) % HistoryCapacity];
            return result;
        }
    }

    /// <summary>
    /// Mouse counts the gyro produced since the last call (whether or not the real mouse is on) and
    /// how many times R2 was pulled — the aim test uses exactly the same math as the game would get.
    /// </summary>
    public (Vector2 Movement, int Shots) TakePreview()
    {
        lock (_gate)
        {
            var result = (_preview, _shots);
            _preview = Vector2.Zero;
            _shots = 0;
            return result;
        }
    }

    public void Recenter()
    {
        lock (_gate)
            _processor.Recenter();
    }

    public InputFrame TakeVisualInput()
    {
        lock (_gate)
            return _inputPreview.Take();
    }

    public void StartCalibration()
    {
        lock (_gate)
            _processor.StartCalibration();
        Log.Info(L.Text("Gyro: kalibrácia spustená."));
    }

    public void CancelCalibration()
    {
        lock (_gate)
            _processor.CancelCalibration();
    }

    /// <summary>Forgets the saved offset of the connected controller.</summary>
    public void ResetCalibration()
    {
        string? key;
        lock (_gate)
        {
            key = _controllerKey;
            _processor.CancelCalibration();
            _processor.Bias = Vector3.Zero;
        }
        if (key is not null)
            _preferences.Update(p => p with { Gyro = p.Gyro.WithoutCalibration(key) });
    }

    public void Dispose()
    {
        _manager.InputReceived -= OnInput;
        _manager.ConnectionChanged -= OnConnectionChanged;
        _preferences.Changed -= OnPreferencesChanged;
    }

    private void OnInput(InputFrame frame, TimeSpan time)
    {
        var gyro = _settings.Current.Gyro;
        bool toggle;
        Vector3? calibrated;
        lock (_gate)
        {
            _inputPreview.Update(frame, time);
            var reading = _processor.Process(frame.Motion, time);
            AddHistory(new MotionPoint(time.TotalSeconds, reading.Gyro, reading.Accel));
            UpdateRate(time);

            bool combo = frame.IsPressed(ControllerButtons.PS) && frame.IsPressed(ControllerButtons.R1);
            toggle = combo && !_previousCombo;
            _previousCombo = combo;

            // Holding PS pauses the gyro, so PS combinations never nudge the cursor.
            bool held = GyroMouseEngine.IsActive(gyro.Activation, frame) && !frame.IsPressed(ControllerButtons.PS);
            bool moving = false;
            if (held)
            {
                var counts = _engine.Compute(reading, gyro);
                _preview += counts;
                if (gyro.MouseEnabled)
                {
                    var (dx, dy) = _engine.ToCounts(counts);
                    if (dx != 0 || dy != 0)
                        _mouse.Move(dx, dy);
                    moving = true;
                }
            }
            else if (_wasActive)
            {
                _engine.Reset();
            }
            _wasActive = held;

            bool shot = frame.Analog.R2 >= ShotThreshold;
            if (shot && !_previousShot)
                _shots++;
            _previousShot = shot;

            _latest = new MotionSnapshot(reading, frame, _sampleRate, _processor.Bias,
                _manager.Connection.Calibration.IsFactory, _processor.Calibrating, held, moving);
            calibrated = _completedCalibration;
            _completedCalibration = null;
        }

        if (calibrated is { } bias)
            SaveCalibration(bias);
        if (toggle)
        {
            bool enabled = !gyro.MouseEnabled;
            _settings.Update(s => s with { Gyro = s.Gyro with { MouseEnabled = enabled } });
            Log.Info(L.Format($"Gyro myš {(enabled ? L.Text("zapnutá") : L.Text("vypnutá"))} (PS + R1)."));
            Toggled?.Invoke(this, enabled);
        }
    }

    private void SaveCalibration(Vector3 bias)
    {
        string? key;
        lock (_gate)
            key = _controllerKey;
        Log.Info(L.Format($"Gyro: kalibrácia hotová, posun {bias.X:0.00} / {bias.Y:0.00} / {bias.Z:0.00} °/s."));
        if (key is not null)
            _preferences.Update(p => p with { Gyro = p.Gyro.WithCalibration(new GyroCalibrationEntry(key, bias.X, bias.Y, bias.Z)) });
        Calibrated?.Invoke(this, bias);
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        var connection = _manager.Connection;
        var key = connection.Controller?.Key;
        lock (_gate)
        {
            if (key == _controllerKey && ReferenceEquals(_processor.Calibration, connection.Calibration))
                return;
            _controllerKey = key;
            _processor.Reset();
            _processor.Calibration = connection.Calibration;
            _processor.Bias = key is not null && _preferences.Current.Gyro.Find(key) is { } saved
                ? new Vector3(saved.BiasX, saved.BiasY, saved.BiasZ)
                : Vector3.Zero;
            _engine.Reset();
            _inputPreview.Reset();
            _historyStart = _historyCount = 0;
            _latest = null;
            _wasActive = _previousCombo = _previousShot = false;
            _rateCount = 0;
            _sampleRate = 0;
        }
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        bool auto = _preferences.Current.Gyro.AutoCalibration;
        lock (_gate)
            _processor.AutoCalibration = auto;
    }

    private void AddHistory(MotionPoint point)
    {
        if (_historyCount < HistoryCapacity)
        {
            _history[(_historyStart + _historyCount) % HistoryCapacity] = point;
            _historyCount++;
        }
        else
        {
            _history[_historyStart] = point;
            _historyStart = (_historyStart + 1) % HistoryCapacity;
        }
    }

    private void UpdateRate(TimeSpan time)
    {
        if (_rateCount == 0)
            _rateStart = time;
        _rateCount++;
        var span = time - _rateStart;
        if (span < TimeSpan.FromSeconds(1))
            return;
        _sampleRate = (_rateCount - 1) / span.TotalSeconds;
        _rateCount = 0;
    }
}
