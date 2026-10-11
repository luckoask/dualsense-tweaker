using DualSenseTweaker.Core.Audio;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Input;
using DualSenseTweaker.Core.Motion;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>Balloon notifications and tooltip of the notification-area icon.</summary>
public interface INotifier
{
    void Notify(string title, string message, bool warning = false);
    void SetStatus(string text);
}

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public interface IFileDialogs
{
    string? PickProfileToImport();
    string? PickExportPath(string suggestedName);
    string? PickExecutable();

    /// <summary>Yes/No question for destructive actions.</summary>
    bool Confirm(string title, string message);
}

/// <summary>Everything the view models talk to; built once in App.</summary>
public sealed record AppServices(
    SettingsState Settings,
    RefreshState Refresh,
    PreferencesState Preferences,
    LiveOutput Live,
    DualSenseManager Manager,
    AudioEngine Audio,
    ProfileService Profiles,
    ControllerRegistry ControllerRegistry,
    GameProfileWatcher Watcher,
    TouchpadService Touchpad,
    GyroService Gyro,
    IFileDialogs Dialogs,
    INotifier Notifier,
    IStartupRegistration Startup);
