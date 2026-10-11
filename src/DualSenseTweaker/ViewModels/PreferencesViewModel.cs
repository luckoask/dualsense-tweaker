using System.Diagnostics;
using System.IO;
using System.Security;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>App behavior: tray, start with Windows, notifications, global shortcuts, logs.</summary>
public sealed class PreferencesViewModel : ObservableObject
{
    private readonly PreferencesState _state;
    private readonly IStartupRegistration _startup;
    private readonly Action<string> _report;
    private HotkeyModifiers _effectiveModifiers;
    private string _hotkeyStatus = "";

    public PreferencesViewModel(PreferencesState state, IStartupRegistration startup, Action<string> report)
    {
        _state = state;
        _startup = startup;
        _report = report;
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder, () => Log.FilePath is not null);
    }

    private AppPreferences Current => _state.Current;

    public RelayCommand OpenLogFolderCommand { get; }

    public IReadOnlyList<Option<AppLanguage>> LanguageOptions { get; } =
    [
        new(AppLanguage.Slovak, "Slovenčina · SK"),
        new(AppLanguage.English, "English · EN"),
        new(AppLanguage.Czech, "Čeština · CZ"),
    ];

    public IReadOnlyList<Option<AppTheme>> ThemeOptions =>
    [
        new(AppTheme.Dark, L.Text("Tmavý")),
        new(AppTheme.Light, L.Text("Svetlý")),
    ];

    public AppLanguage Language
    {
        get => Current.Language;
        set => _state.Update(p => p with { Language = value });
    }

    public AppTheme Theme
    {
        get => Current.Theme;
        set => _state.Update(p => p with { Theme = value });
    }

    public bool StartWithWindows
    {
        get => _startup.IsEnabled;
        set
        {
            try
            {
                _startup.SetEnabled(value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                _report(L.Format($"Štart s Windows sa nepodarilo nastaviť: {ex.Message}"));
            }
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => Current.CloseToTray;
        set => _state.Update(p => p with { CloseToTray = value });
    }

    public bool OpenWithPsButton
    {
        get => Current.OpenWithPsButton;
        set => _state.Update(p => p with { OpenWithPsButton = value });
    }

    public bool LowBatteryNotifications
    {
        get => Current.LowBatteryNotifications;
        set => _state.Update(p => p with { LowBatteryNotifications = value });
    }

    public bool ProfileSwitchNotifications
    {
        get => Current.ProfileSwitchNotifications;
        set => _state.Update(p => p with { ProfileSwitchNotifications = value });
    }

    public bool HotkeysEnabled
    {
        get => Current.HotkeysEnabled;
        set => _state.Update(p => p with { HotkeysEnabled = value });
    }

    public IReadOnlyList<Option<HotkeyModifiers>> ModifierOptions =>
    [
        new(HotkeyModifiers.CtrlAlt, "Ctrl+Alt"),
        new(HotkeyModifiers.CtrlAltShift, "Ctrl+Alt+Shift"),
        new(HotkeyModifiers.CtrlShift, "Ctrl+Shift"),
    ];

    /// <summary>The combination in use (the user's choice, or the app's layout-safe suggestion).</summary>
    public HotkeyModifiers HotkeyModifiers
    {
        get => _effectiveModifiers;
        set => _state.Update(p => p with { HotkeyModifiers = value });
    }

    public string HotkeyStatus
    {
        get => L.Text(_hotkeyStatus);
        private set => SetField(ref _hotkeyStatus, value);
    }

    public string LogPath => Log.FilePath ?? "—";

    /// <summary>Called by the app after (re)registering the shortcuts.</summary>
    public void SetHotkeyState(HotkeyModifiers effective, string status)
    {
        _effectiveModifiers = effective;
        HotkeyStatus = status;
        OnPropertyChanged(nameof(HotkeyModifiers));
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void OpenLogFolder()
    {
        var directory = Path.GetDirectoryName(Log.FilePath);
        if (directory is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
