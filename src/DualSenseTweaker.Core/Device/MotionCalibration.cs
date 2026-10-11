using System.Buffers.Binary;
using System.Numerics;

namespace DualSenseTweaker.Core.Device;

/// <summary>
/// Factory IMU calibration from feature report 0x05: raw gyro → °/s and raw accelerometer → g.
/// Same math as SDL (SDL_hidapi_ps5.c) and Linux (hid-playstation.c). The gyro bias is subtracted
/// like SDL does: measured on a real controller at rest, the raw readings equal the stored bias.
/// </summary>
public sealed record MotionCalibration(
    Vector3 GyroBias, Vector3 GyroScale,
    Vector3 AccelBias, Vector3 AccelScale,
    bool IsFactory)
{
    private const float GyroResPerDegree = 1024f;
    private const float AccelResPerG = 8192f;

    /// <summary>Without calibration data: 1/16 °/s and 1/8192 g per unit (SDL's fallback).</summary>
    public static MotionCalibration Default { get; } = new(
        Vector3.Zero, new Vector3(64f / GyroResPerDegree),
        Vector3.Zero, new Vector3(1f / AccelResPerG),
        false);

    /// <summary>Angular velocity in °/s: X = pitch (nose up +), Y = yaw (turn left +), Z = roll (tilt left +).</summary>
    public Vector3 Gyro(in MotionSample sample) =>
        (new Vector3(sample.GyroX, sample.GyroY, sample.GyroZ) - GyroBias) * GyroScale;

    /// <summary>Acceleration in g, pointing up when at rest: X right, Y up from the face, Z toward the player.</summary>
    public Vector3 Accel(in MotionSample sample) =>
        (new Vector3(sample.AccelX, sample.AccelY, sample.AccelZ) - AccelBias) * AccelScale;

    /// <returns>Null when the report is too short or the values make no sense (clones, broken devices).</returns>
    internal static MotionCalibration? Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 35 || report[0] != DualSenseProtocol.FeatureCalibration)
            return null;
        Span<short> v = stackalloc short[17];
        for (int i = 0; i < v.Length; i++)
            v[i] = BinaryPrimitives.ReadInt16LittleEndian(report[(1 + i * 2)..]);

        float pitchBias = v[0], yawBias = v[1], rollBias = v[2];
        float pitchPlus = v[3], pitchMinus = v[4];
        float yawPlus = v[5], yawMinus = v[6];
        float rollPlus = v[7], rollMinus = v[8];
        float speed2x = v[9] + v[10];
        float accXPlus = v[11], accXMinus = v[12];
        float accYPlus = v[13], accYMinus = v[14];
        float accZPlus = v[15], accZMinus = v[16];

        // Linux: sens = speed_2x / (|plus − bias| + |minus − bias|), in °/s per unit.
        float GyroScale(float plus, float minus, float bias)
        {
            float span = Math.Abs(plus - bias) + Math.Abs(minus - bias);
            return span > 0 && speed2x > 0 ? speed2x / span : float.NaN;
        }

        float AccelScale(float plus, float minus) => plus - minus is var range && range > 0 ? 2f / range : float.NaN;

        var gyroScale = new Vector3(GyroScale(pitchPlus, pitchMinus, pitchBias), GyroScale(yawPlus, yawMinus, yawBias), GyroScale(rollPlus, rollMinus, rollBias));
        var accelScale = new Vector3(AccelScale(accXPlus, accXMinus), AccelScale(accYPlus, accYMinus), AccelScale(accZPlus, accZMinus));
        var accelBias = new Vector3(
            accXPlus - (accXPlus - accXMinus) / 2,
            accYPlus - (accYPlus - accYMinus) / 2,
            accZPlus - (accZPlus - accZMinus) / 2);

        // A sane DualSense is ≈ 1/16 °/s and ≈ 1/8192 g per unit; anything far off is not trusted.
        if (!InRange(gyroScale, 0.02f, 0.2f) || !InRange(accelScale, 1f / 20000, 1f / 3000))
            return null;
        return new MotionCalibration(new Vector3(pitchBias, yawBias, rollBias), gyroScale, accelBias, accelScale, true);
    }

    private static bool InRange(Vector3 v, float min, float max) =>
        v.X >= min && v.X <= max && v.Y >= min && v.Y <= max && v.Z >= min && v.Z <= max;
}
