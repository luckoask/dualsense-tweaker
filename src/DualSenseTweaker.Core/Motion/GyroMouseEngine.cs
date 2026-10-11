using System.Numerics;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Motion;

/// <summary>
/// Gyro → mouse, following Jibb Smart's GyroWiki: choice of turning axis (player space, yaw, roll),
/// soft "tightening" instead of a hard deadzone, tiered smoothing that only touches slow movement,
/// and sensitivity acceleration for fast turns. Pure logic; the caller feeds every sample.
/// </summary>
public sealed class GyroMouseEngine
{
    /// <summary>GyroWiki's "yaw relax factor" for player space.</summary>
    private const float YawRelax = 1.41f;
    private const float SmoothingTau = 0.05f;   // s
    private const float AccelSlow = 20f;        // °/s, below: base sensitivity
    private const float AccelFast = 160f;       // °/s, above: full acceleration
    private const float AccelMaxBoost = 3f;     // 100 % acceleration = up to 4×
    private const byte TriggerHeld = 30;        // L2 analog value that counts as "aiming"

    private Vector2 _smoothed;
    private double _remainderX, _remainderY;

    /// <summary>Whether the activation button/condition currently allows gyro movement.</summary>
    public static bool IsActive(GyroActivation activation, in InputFrame frame) => activation switch
    {
        GyroActivation.HoldL2 => frame.Analog.L2 >= TriggerHeld || frame.IsPressed(ControllerButtons.L2),
        GyroActivation.HoldL1 => frame.IsPressed(ControllerButtons.L1),
        GyroActivation.HoldR1 => frame.IsPressed(ControllerButtons.R1),
        GyroActivation.TouchpadTouch => frame.First.Active || frame.Second.Active,
        GyroActivation.OffWhileL1 => !frame.IsPressed(ControllerButtons.L1),
        _ => true,
    };

    /// <summary>
    /// Rotation as a 2D turn rate in °/s: X = turning left, Y = nose up.
    /// </summary>
    public static Vector2 TurnRate(Vector3 gyro, Vector3 up, GyroAxisMode axis)
    {
        switch (axis)
        {
            case GyroAxisMode.Roll:
                return new Vector2(gyro.Z, gyro.X);
            case GyroAxisMode.Yaw:
                return new Vector2(gyro.Y, gyro.X);
            default:
                // Player space: rotation around the world's vertical axis, ignoring the pitch axis so
                // pitching never leaks into turning; relaxed so a tilted grip still turns at full speed.
                if (up.LengthSquared() < 0.25f)
                    return new Vector2(gyro.Y, gyro.X);
                var n = Vector3.Normalize(up);
                float worldYaw = gyro.Y * n.Y + gyro.Z * n.Z;
                float limit = MathF.Sqrt(gyro.Y * gyro.Y + gyro.Z * gyro.Z);
                return new Vector2(MathF.CopySign(MathF.Min(MathF.Abs(worldYaw) * YawRelax, limit), worldYaw), gyro.X);
        }
    }

    /// <summary>Mouse movement in (fractional) counts for this sample; positive X = right, positive Y = down.</summary>
    public Vector2 Compute(in MotionReading reading, GyroSettings s)
    {
        float dt = reading.Dt;
        if (dt <= 0)
            return Vector2.Zero;

        var rate = TurnRate(reading.Gyro, reading.Up, s.Axis);
        float speed = rate.Length();

        // Tightening: slow movement is scaled down smoothly, so resting hands do not jitter the aim
        // but deliberate slow movement is never cut off completely.
        float tightening = (float)s.Tightening;
        if (tightening > 0 && speed < tightening)
            rate *= speed / tightening;

        // Tiered smoothing: only the slow part of the movement goes through the low-pass.
        float upper = (float)s.Smoothing;
        if (upper > 0)
        {
            float lower = upper / 2;
            float direct = Math.Clamp((speed - lower) / (upper - lower), 0f, 1f);
            _smoothed = Vector2.Lerp(_smoothed, rate * (1 - direct), 1f - MathF.Exp(-dt / SmoothingTau));
            rate = rate * direct + _smoothed;
        }

        float boost = 1f;
        if (s.Acceleration > 0)
            boost += AccelMaxBoost * s.Acceleration / 100f * Math.Clamp((speed - AccelSlow) / (AccelFast - AccelSlow), 0f, 1f);

        float sensitivity = (float)s.Sensitivity * boost;
        // Turning left / nose up moves the mouse left / up.
        var counts = new Vector2(-rate.X * sensitivity, -rate.Y * sensitivity * s.VerticalPercent / 100f) * dt;
        if (s.InvertX)
            counts.X = -counts.X;
        if (s.InvertY)
            counts.Y = -counts.Y;
        return counts;
    }

    /// <summary>Whole counts to send now; the fractions carry over so slow movement is not lost.</summary>
    public (int Dx, int Dy) ToCounts(Vector2 counts)
    {
        _remainderX += counts.X;
        _remainderY += counts.Y;
        int dx = (int)Math.Truncate(_remainderX);
        int dy = (int)Math.Truncate(_remainderY);
        _remainderX -= dx;
        _remainderY -= dy;
        return (dx, dy);
    }

    /// <summary>Drops smoothing history and remainders (gyro paused, controller changed).</summary>
    public void Reset()
    {
        _smoothed = Vector2.Zero;
        _remainderX = _remainderY = 0;
    }
}
