using System.Numerics;
using DualSenseTweaker.Core.Device;

namespace DualSenseTweaker.Core.Motion;

/// <summary>One processed IMU sample.</summary>
/// <param name="Dt">Seconds since the previous sample (from the controller's own sensor clock).</param>
/// <param name="Gyro">Angular velocity in °/s with the remaining offset removed: X pitch (up +), Y yaw (left +), Z roll (left +).</param>
/// <param name="Accel">Acceleration in g (X right, Y up from the face, Z toward the player).</param>
/// <param name="Up">Low-passed, normalized "up" direction in controller space (from gravity).</param>
public readonly record struct MotionReading(
    TimeSpan Time, float Dt, Vector3 Gyro, Vector3 Accel, Vector3 Up, Quaternion Orientation, bool IsStill);

public enum CalibrationPhase { Idle, Running, Done, Failed }

/// <summary>Progress of a manual "hold still" calibration.</summary>
public readonly record struct CalibrationProgress(CalibrationPhase Phase, double Fraction, bool Moving);

/// <summary>
/// IMU pipeline of one controller: calibration → remaining-offset removal → stillness detection
/// (auto-calibration while the controller rests) → sensor fusion. Pure logic, one sample at a time.
/// </summary>
public sealed class MotionProcessor
{
    /// <summary>The sensor clock ticks at 3 MHz.</summary>
    private const double SensorTicksPerSecond = 3_000_000;
    private const float MaxDt = 0.05f;
    private const float StillGyroDeviation = 1.2f;   // °/s from the running mean
    private const float StillAccelDeviation = 0.03f; // g
    private const float MaxPlausibleOffset = 6f;     // °/s; a bigger "offset" is a slow rotation
    private const float StillAfterSeconds = 0.6f;
    private const float AutoCalibrationTau = 2.5f;   // s
    private const float MeanTau = 0.25f;             // s
    private const float UpTau = 0.1f;                // s
    public const double ManualCalibrationSeconds = 2.0;

    private readonly OrientationFilter _orientation = new();
    private bool _hasSample;
    private uint _lastTimestamp;
    private TimeSpan _lastTime;
    private Vector3 _gyroMean;
    private Vector3 _accelMean;
    private Vector3 _up = Vector3.UnitY;
    private float _stillSeconds;

    private bool _calibrating;
    private double _calibrationSeconds;
    private Vector3 _calibrationSum;
    private double _calibrationWeight;
    private bool _calibrationMoving;
    private CalibrationPhase _lastPhase = CalibrationPhase.Idle;

    public MotionCalibration Calibration { get; set; } = MotionCalibration.Default;

    /// <summary>Remaining gyro offset in °/s, subtracted from every sample.</summary>
    public Vector3 Bias { get; set; }

    public bool AutoCalibration { get; set; } = true;

    public CalibrationProgress Calibrating => new(
        _calibrating ? CalibrationPhase.Running : _lastPhase,
        _calibrating ? Math.Clamp(_calibrationSeconds / ManualCalibrationSeconds, 0, 1) : _lastPhase == CalibrationPhase.Done ? 1 : 0,
        _calibrating && _calibrationMoving);

    /// <summary>Raised (on the input thread) when a manual calibration finished with the new offset.</summary>
    public event Action<Vector3>? CalibrationCompleted;

    public MotionReading Process(in MotionSample sample, TimeSpan time)
    {
        var rawGyro = Calibration.Gyro(sample);
        var accel = Calibration.Accel(sample);

        float dt = 0;
        if (_hasSample)
        {
            uint ticks = unchecked(sample.Timestamp - _lastTimestamp); // wraps every ~24 minutes
            double sensorDt = ticks / SensorTicksPerSecond;
            double wallDt = (time - _lastTime).TotalSeconds;
            // A Bluetooth hiccup can deliver a burst of old reports; the sensor clock knows the truth,
            // but fall back to the wall clock when it is obviously off.
            dt = (float)(sensorDt > 0 && sensorDt < 0.25 ? sensorDt : Math.Clamp(wallDt, 0, MaxDt));
            dt = Math.Min(dt, MaxDt);
        }
        else
        {
            _gyroMean = rawGyro;
            _accelMean = accel;
            if (accel.LengthSquared() > 0.25f)
                _up = Vector3.Normalize(accel);
        }
        _hasSample = true;
        _lastTimestamp = sample.Timestamp;
        _lastTime = time;

        bool still = UpdateStillness(rawGyro, accel, dt);
        UpdateCalibration(rawGyro, dt);

        var gyro = rawGyro - Bias;
        if (dt > 0)
        {
            if (accel.LengthSquared() > 0.25f)
                _up = Vector3.Normalize(Vector3.Lerp(_up, Vector3.Normalize(accel), Alpha(dt, UpTau)));
            _orientation.Update(gyro, accel, dt);
        }
        return new MotionReading(time, dt, gyro, accel, _up, _orientation.Orientation, still);
    }

    public void Recenter() => _orientation.Recenter();

    /// <summary>Starts measuring the offset; the controller must lie still for <see cref="ManualCalibrationSeconds"/>.</summary>
    public void StartCalibration()
    {
        _calibrating = true;
        RestartCalibrationWindow();
    }

    public void CancelCalibration()
    {
        if (_calibrating)
            _lastPhase = CalibrationPhase.Idle;
        _calibrating = false;
    }

    /// <summary>Forgets the sensor state (new controller); keeps <see cref="Bias"/> and <see cref="Calibration"/>.</summary>
    public void Reset()
    {
        _hasSample = false;
        _stillSeconds = 0;
        _orientation.Reset();
        _up = Vector3.UnitY;
        CancelCalibration();
        _lastPhase = CalibrationPhase.Idle;
    }

    private bool UpdateStillness(Vector3 rawGyro, Vector3 accel, float dt)
    {
        if (dt <= 0)
            return _stillSeconds >= StillAfterSeconds;
        float alpha = Alpha(dt, MeanTau);
        _gyroMean = Vector3.Lerp(_gyroMean, rawGyro, alpha);
        _accelMean = Vector3.Lerp(_accelMean, accel, alpha);

        bool calm = MaxAbs(rawGyro - _gyroMean) < StillGyroDeviation
                    && MaxAbs(accel - _accelMean) < StillAccelDeviation
                    && MaxAbs(rawGyro) < MaxPlausibleOffset;
        _stillSeconds = calm ? _stillSeconds + dt : 0;
        bool still = _stillSeconds >= StillAfterSeconds;

        if (still && AutoCalibration && !_calibrating)
            Bias = Vector3.Lerp(Bias, rawGyro, Alpha(dt, AutoCalibrationTau));
        return still;
    }

    private void UpdateCalibration(Vector3 rawGyro, float dt)
    {
        if (!_calibrating || dt <= 0)
            return;
        // Any movement restarts the window, so the result is a pure "at rest" average.
        bool calm = _stillSeconds > 0;
        _calibrationMoving = !calm;
        if (!calm)
        {
            RestartCalibrationWindow();
            _calibrationMoving = true;
            return;
        }
        _calibrationSum += rawGyro * dt;
        _calibrationWeight += dt;
        _calibrationSeconds += dt;
        if (_calibrationSeconds < ManualCalibrationSeconds || _calibrationWeight <= 0)
            return;

        var bias = _calibrationSum / (float)_calibrationWeight;
        _calibrating = false;
        if (MaxAbs(bias) > MaxPlausibleOffset)
        {
            _lastPhase = CalibrationPhase.Failed;
            return;
        }
        Bias = bias;
        _lastPhase = CalibrationPhase.Done;
        CalibrationCompleted?.Invoke(bias);
    }

    private void RestartCalibrationWindow()
    {
        _calibrationSum = Vector3.Zero;
        _calibrationWeight = 0;
        _calibrationSeconds = 0;
        _lastPhase = CalibrationPhase.Running;
    }

    private static float Alpha(float dt, float tau) => 1f - MathF.Exp(-dt / tau);

    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
}
