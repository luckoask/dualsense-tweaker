namespace DualSenseTweaker.Core.State;

/// <summary>Modifier combination for the global "switch to profile N" shortcuts (N = 1–9).</summary>
public enum HotkeyModifiers { CtrlAlt, CtrlAltShift, CtrlShift }

public enum AppLanguage { Slovak, English, Czech }
public enum AppTheme { Dark, Light }

/// <summary>Auto-profile rule: while <see cref="ExeName"/> runs, <see cref="ProfileName"/> is active.</summary>
public sealed record GameRule(string ExeName, string ProfileName)
{
    /// <summary>Process name as Windows reports it: no path, no ".exe", lower case.</summary>
    public string Key => NormalizeExe(ExeName);

    public static string NormalizeExe(string exe)
    {
        var name = Path.GetFileName(exe.Trim().Trim('"'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }
}

/// <summary>
/// A remembered controller (key = MAC address): its name and the profile it always uses.
/// <see cref="Profile"/> null = the app leaves it alone (factory state).
/// </summary>
public sealed record ControllerBinding(string Key, string Name, string? Profile);

/// <summary>App-wide behavior that does not belong to a profile (saved in settings.json).</summary>
public sealed record AppPreferences
{
    public AppLanguage Language { get; init; } = AppLanguage.Slovak;
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    /// <summary>Closing the window keeps the app running in the notification area.</summary>
    public bool CloseToTray { get; init; } = true;
    /// <summary>Pressing the PS button 5× quickly brings up the app window.</summary>
    public bool OpenWithPsButton { get; init; } = true;
    public bool LowBatteryNotifications { get; init; } = true;
    /// <summary>Balloon when a hotkey or a running game switches the profile.</summary>
    public bool ProfileSwitchNotifications { get; init; } = true;
    public bool HotkeysEnabled { get; init; } = true;
    /// <summary>Null until the user picks one; the app then avoids combinations that type characters (AltGr layouts).</summary>
    public HotkeyModifiers? HotkeyModifiers { get; init; }
    public bool AutoProfileEnabled { get; init; } = true;
    public IReadOnlyList<GameRule> GameRules { get; init; } = [];
    public TouchpadSettings Touchpad { get; init; } = new();
    public GyroPreferences Gyro { get; init; } = new();
    public IReadOnlyList<ControllerBinding> Controllers { get; init; } = [];
    /// <summary>Key of the controller the user edited last.</summary>
    public string? SelectedController { get; init; }

    public AppPreferences Normalize()
    {
        var rules = GameRules ?? [];
        bool clean = rules.All(r => r is not null && !string.IsNullOrWhiteSpace(r.ExeName) && !string.IsNullOrWhiteSpace(r.ProfileName));
        var controllers = Controllers ?? [];
        static bool ValidBinding(ControllerBinding? b) => b is not null && !string.IsNullOrWhiteSpace(b.Key) && !string.IsNullOrWhiteSpace(b.Name);
        bool controllersClean = controllers.All(ValidBinding)
                                && controllers.Select(b => b.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == controllers.Count;
        return this with
        {
            Language = Enum.IsDefined(Language) ? Language : AppLanguage.Slovak,
            Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.Dark,
            Controllers = controllersClean ? controllers
                : [.. controllers.Where(ValidBinding).GroupBy(b => b.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())],
            Touchpad = (Touchpad ?? new TouchpadSettings()).Normalize(),
            Gyro = (Gyro ?? new GyroPreferences()).Normalize(),
            HotkeyModifiers = HotkeyModifiers is { } m && !Enum.IsDefined(m) ? null : HotkeyModifiers,
            // Keep the same list instance when nothing is wrong, so equal values stay equal.
            GameRules = clean ? rules : [.. rules.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ExeName) && !string.IsNullOrWhiteSpace(r.ProfileName))],
        };
    }
}

public sealed class PreferencesState(AppPreferences? initial = null)
    : VersionedState<AppPreferences>(initial ?? new AppPreferences(), p => p.Normalize());
