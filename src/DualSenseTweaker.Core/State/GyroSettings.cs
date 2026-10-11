namespace DualSenseTweaker.Core.State;

/// <summary>When the gyro moves the mouse (the "gyro on/off" button of Steam Input / GyroWiki).</summary>
public enum GyroActivation
{
    Always,
    /// <summary>Only while aiming down sights (L2 held).</summary>
    HoldL2,
    HoldL1,
    HoldR1,
    /// <summary>While a finger rests on the touchpad.</summary>
    TouchpadTouch,
    /// <summary>Always on, L1 pauses it so the controller can be re-centered ("ratchet").</summary>
    OffWhileL1,
}

/// <summary>Which rotation turns the view left and right.</summary>
public enum GyroAxisMode
{
    /// <summary>Turning around the real-world vertical axis, whichever way the controller is held (GyroWiki "player space").</summary>
    PlayerSpace,
    /// <summary>Turning the controller like a steering wheel lying flat (local yaw).</summary>
    Yaw,
    /// <summary>Tilting the controller left and right (local roll).</summary>
    Roll,
}

/// <summary>Gyro → mouse ("gyro aim"), stored per profile so every game can have its own feel.</summary>
public sealed record GyroSettings
{
    public const double MinSensitivity = 1, MaxSensitivity = 60;

    public bool MouseEnabled { get; init; }
    public GyroActivation Activation { get; init; } = GyroActivation.Always;
    public GyroAxisMode Axis { get; init; } = GyroAxisMode.PlayerSpace;

    /// <summary>Mouse counts per degree of horizontal rotation.</summary>
    public double Sensitivity { get; init; } = 12;
    /// <summary>Vertical sensitivity relative to horizontal, 10–200 %.</summary>
    public int VerticalPercent { get; init; } = 100;
    public bool InvertX { get; init; }
    public bool InvertY { get; init; }

    /// <summary>Extra speed for fast turns, 0–100 % (up to 4× at 100 %).</summary>
    public int Acceleration { get; init; }
    /// <summary>Soft deadzone in °/s: slower movement is scaled down instead of cut off.</summary>
    public double Tightening { get; init; } = 1;
    /// <summary>Movements slower than this (°/s) are smoothed; faster ones stay direct.</summary>
    public double Smoothing { get; init; } = 3;

    public GyroSettings Normalize() => this with
    {
        Activation = Enum.IsDefined(Activation) ? Activation : GyroActivation.Always,
        Axis = Enum.IsDefined(Axis) ? Axis : GyroAxisMode.PlayerSpace,
        Sensitivity = Math.Round(Math.Clamp(Finite(Sensitivity, 12), MinSensitivity, MaxSensitivity), 1),
        VerticalPercent = Math.Clamp(VerticalPercent, 10, 200),
        Acceleration = Math.Clamp(Acceleration, 0, 100),
        Tightening = Math.Round(Math.Clamp(Finite(Tightening, 1), 0, 10), 1),
        Smoothing = Math.Round(Math.Clamp(Finite(Smoothing, 3), 0, 20), 1),
    };

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}

/// <summary>Remaining gyro offset (°/s) measured for one controller (key = MAC address).</summary>
public sealed record GyroCalibrationEntry(string ControllerKey, float BiasX, float BiasY, float BiasZ);

/// <summary>App-wide gyro behavior (saved in settings.json).</summary>
public sealed record GyroPreferences
{
    /// <summary>Re-measures the offset whenever the controller lies still.</summary>
    public bool AutoCalibration { get; init; } = true;
    public IReadOnlyList<GyroCalibrationEntry> Calibrations { get; init; } = [];

    public GyroCalibrationEntry? Find(string key) =>
        Calibrations.LastOrDefault(c => string.Equals(c.ControllerKey, key, StringComparison.OrdinalIgnoreCase));

    public GyroPreferences WithCalibration(GyroCalibrationEntry entry) => this with
    {
        Calibrations = [.. Calibrations.Where(c => !string.Equals(c.ControllerKey, entry.ControllerKey, StringComparison.OrdinalIgnoreCase)), entry],
    };

    public GyroPreferences WithoutCalibration(string key) => this with
    {
        Calibrations = [.. Calibrations.Where(c => !string.Equals(c.ControllerKey, key, StringComparison.OrdinalIgnoreCase))],
    };

    public GyroPreferences Normalize()
    {
        var list = Calibrations ?? [];
        static bool Valid(GyroCalibrationEntry? c) => c is not null && !string.IsNullOrWhiteSpace(c.ControllerKey)
            && float.IsFinite(c.BiasX) && float.IsFinite(c.BiasY) && float.IsFinite(c.BiasZ);
        // Keep the same list instance when nothing is wrong, so equal values stay equal.
        return list.All(Valid) ? this with { Calibrations = list } : this with { Calibrations = [.. list.Where(Valid)] };
    }
}
