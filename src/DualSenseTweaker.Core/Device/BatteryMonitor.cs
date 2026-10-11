namespace DualSenseTweaker.Core.Device;

public enum BatteryAlert { None, Low, Critical }

/// <summary>
/// Decides when to warn about the battery: once when it drops to 15 %, once more at 5 %
/// (the controller reports in 10 % steps: 5, 15, 25 …). Charging or recovering to 25 %
/// re-arms the warnings, so reconnecting a low controller does not spam.
/// </summary>
public sealed class BatteryMonitor
{
    public const int LowPercent = 15;
    public const int CriticalPercent = 5;
    public const int RearmPercent = 25;

    private BatteryAlert _lastAlert;

    public BatteryAlert Update(ControllerStatus? status)
    {
        if (status is null)
            return BatteryAlert.None;
        if (status.BatteryState is BatteryState.Charging or BatteryState.Full || status.BatteryPercent >= RearmPercent)
        {
            _lastAlert = BatteryAlert.None;
            return BatteryAlert.None;
        }
        if (status.BatteryState != BatteryState.Discharging)
            return BatteryAlert.None;

        var level = status.BatteryPercent <= CriticalPercent ? BatteryAlert.Critical
            : status.BatteryPercent <= LowPercent ? BatteryAlert.Low
            : BatteryAlert.None;
        if (level <= _lastAlert)
            return BatteryAlert.None;
        _lastAlert = level;
        return level;
    }
}
