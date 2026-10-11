using System.Numerics;

namespace DualSenseTweaker.Core.Motion;

/// <summary>
/// Sensor fusion (Mahony complementary filter): integrates the gyro and slowly pulls the estimate
/// toward the gravity measured by the accelerometer, so pitch and roll stay correct while yaw has
/// no reference and drifts slowly (re-center it). <see cref="Orientation"/> rotates controller
/// space (X right, Y up from the face, Z toward the player) into world space (Y up).
/// </summary>
public sealed class OrientationFilter
{
    private const float DegToRad = MathF.PI / 180f;
    /// <summary>Correction gain in rad/s per unit of gravity error (≈ 1 s time constant).</summary>
    private const float Gain = 1.0f;
    /// <summary>Faster convergence right after start so the first frames already show the real tilt.</summary>
    private const float StartupGain = 10f;
    private const float StartupSeconds = 0.5f;

    private float _age;

    public Quaternion Orientation { get; private set; } = Quaternion.Identity;

    /// <param name="gyro">Angular velocity in °/s (bias already removed).</param>
    /// <param name="accel">Acceleration in g.</param>
    /// <param name="dt">Seconds since the previous sample.</param>
    public void Update(Vector3 gyro, Vector3 accel, float dt)
    {
        if (!(dt > 0) || dt > 0.25f)
            return;
        _age += dt;
        var omega = gyro * DegToRad;

        // Trust gravity only when the controller is not being shaken (|a| ≈ 1 g).
        float length = accel.Length();
        float trust = Math.Clamp(1f - MathF.Abs(length - 1f) * 5f, 0f, 1f);
        if (trust > 0 && length > 1e-3f)
        {
            var measuredUp = accel / length;
            var predictedUp = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(Orientation));
            var error = Vector3.Cross(measuredUp, predictedUp);
            omega += error * (_age < StartupSeconds ? StartupGain : Gain) * trust;
        }

        float angle = omega.Length() * dt;
        if (angle > 1e-7f)
            Orientation = Quaternion.Normalize(Orientation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(omega), angle));
    }

    /// <summary>Turns the estimate so the controller faces straight ahead again (keeps pitch and roll).</summary>
    public void Recenter()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -Angles(Orientation).YawLeft * DegToRad);
        Orientation = Quaternion.Normalize(turn * Orientation);
    }

    public void Reset()
    {
        Orientation = Quaternion.Identity;
        _age = 0;
    }

    /// <summary>
    /// Pitch (nose up +), roll (tilted left +) and yaw (turned left +) in degrees. Pitch comes from the
    /// direction of the nose, so roll and yaw stay meaningful up to almost vertical.
    /// </summary>
    public static (float Pitch, float RollLeft, float YawLeft) Angles(Quaternion orientation)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, orientation);
        var right = Vector3.Transform(Vector3.UnitX, orientation);
        var up = Vector3.Transform(Vector3.UnitY, orientation);
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        float yaw = MathF.Atan2(-forward.X, -forward.Z);
        float roll = MathF.Atan2(right.Y, up.Y);
        return (pitch / DegToRad, roll / DegToRad, yaw / DegToRad);
    }
}
