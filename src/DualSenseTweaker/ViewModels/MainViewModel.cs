using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>An entry of the side navigation.</summary>
public sealed record NavSection(string Key, string Title, string Subtitle, Geometry Icon)
{
    /// <summary>"01", "02" … in navigation order.</summary>
    public string Number { get; init; } = "";
    public string UpperTitle => L.Text(Title).ToUpperInvariant();
    public string TranslatedSubtitle => L.Text(Subtitle);
}

/// <summary>
/// Root view model. Sub-models edit the states; this one wires them to the controller manager,
/// the audio engine, profiles and the shell (tray, hotkeys), and marshals their events to the
/// UI thread. Nothing here deals with reports or bytes.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ConnectionProperties =
    [
        nameof(IsConnected), nameof(ConnectionText), nameof(ModelText), nameof(DeviceTitle), nameof(FirmwareText),
        nameof(HeaderStatus),
        nameof(RumbleModeText), nameof(HasStatus), nameof(BatteryPercent), nameof(BatteryText),
        nameof(BatteryShortText), nameof(BatteryStateText), nameof(IsCharging), nameof(HeadphonesText), nameof(MicrophoneText),
    ];

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _liveTimer;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private readonly BatteryMonitor _batteryMonitor = new();
    private ConnectionSnapshot _connection;
    private NavSection _selectedSection;
    private string _statusMessage = "";
    private string _rateText = "";
    private DeviceStatistics? _lastStatistics;
    private TimeSpan _lastStatisticsTime;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _connection = services.Manager.Connection;

        Output = new OutputViewModel(services.Settings, Report);
        LeftTrigger = new TriggerEditorViewModel(services.Settings, "01", L.Text("L2 · ľavá spúšť"),
            o => o.LeftTrigger, (o, e) => o with { LeftTrigger = e }, OutputGroups.LeftTrigger, services.Manager.RequestResend, Report);
        RightTrigger = new TriggerEditorViewModel(services.Settings, "02", L.Text("R2 · pravá spúšť"),
            o => o.RightTrigger, (o, e) => o with { RightTrigger = e }, OutputGroups.RightTrigger, services.Manager.RequestResend, Report);
        Audio = new AudioViewModel(services.Settings, services.Audio);
        Profiles = new ProfilesViewModel(services.Profiles, services.Dialogs, Report);
        AutoProfile = new AutoProfileViewModel(services.Preferences, services.Profiles, services.Watcher, services.Dialogs, Report);
        Refresh = new RefreshViewModel(services.Refresh);
        Preferences = new PreferencesViewModel(services.Preferences, services.Startup, Report);
        Touchpad = new TouchpadViewModel(services.Preferences, services.Touchpad);
        Gyro = new GyroViewModel(services.Settings, services.Preferences, services.Gyro);
        Controllers = new ControllersViewModel(services.ControllerRegistry, services.Manager, services.Profiles, services.Dialogs, Report);
        RescanCommand = new RelayCommand(services.Manager.Rescan);

        Sections =
        [
            new("device", L.Text("Zariadenie"), L.Text("Pripojenie, batéria, zvuk ovládača a obnovovanie nastavení"),
                Icon("M6 9h12a4 4 0 0 1 3.9 3.2l.8 4.1a2 2 0 0 1-3.4 1.8L17 16H7l-2.3 2.1a2 2 0 0 1-3.4-1.8l.8-4.1A4 4 0 0 1 6 9z M8 11v3 M6.5 12.5h3 M15.5 12h1 M17.5 14h1")),
            new("profiles", L.Text("Profily"), L.Text("Prepínanie, globálne skratky a automatický profil podľa hry"),
                Icon("M12 3l9 5-9 5-9-5z M3 13l9 5 9-5 M3 17l9 5 9-5")),
            new("triggers", L.Text("Spúšte"), L.Text("Adaptívne spúšte L2 a R2"),
                Icon("M7 21V9a5 5 0 0 1 10 0v12 M7 14h10 M12 4v5")),
            new("lights", L.Text("Svetlá"), L.Text("Svetelný pás, indikátory hráča a mikrofónu"),
                Icon("M9 18h6 M10 21h4 M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3z")),
            new("touchpad", L.Text("Dotykový panel"), L.Text("Dotykový panel ovládača DualSense ako myš: kurzor, klikanie, ťahanie a posúvanie ako na notebooku"),
                Icon("M3 7a3 3 0 0 1 3-3h12a3 3 0 0 1 3 3v8a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3z M12 18v3 M9.5 10.5a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0")),
            new("gyro", L.Text("Gyroskop"), L.Text("Živý 3D náhľad, test mierenia, ovládanie myši pohybom a kalibrácia"),
                Icon("M21 12a9 9 0 1 1-3-6.7 M21 3v5h-5 M10 12a2 2 0 1 0 4 0a2 2 0 1 0-4 0")),
            new("haptics", L.Text("Vibrácie a haptika"), L.Text("Klasické vibrácie, útlm v ovládači a HD haptika zo zvuku"),
                Icon("M9 4h6v16H9z M5 8v8 M19 8v8 M2 10v4 M22 10v4")),
            new("audio", L.Text("Reakcia na zvuk"), L.Text("Spektrum, spracovanie signálu a mapovanie zvuku na ovládač"),
                Icon("M4 14v6 M8 9v11 M12 4v16 M16 10v10 M20 14v6")),
            new("settings", L.Text("Nastavenia"), L.Text("Jazyk, vzhľad, spúšťanie a správanie aplikácie"),
                Icon("M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8 M12 2v3 M12 19v3 M2 12h3 M19 12h3 M5 5l2 2 M17 17l2 2 M5 19l2-2 M17 7l2-2")),
        ];
        Sections = [.. Sections.Select((s, i) => s with { Number = $"{i + 1:00}" })];
        _selectedSection = Sections[0];

        services.Settings.Changed += OnSettingsChanged;
        services.Refresh.Changed += OnRefreshChanged;
        services.Preferences.Changed += OnPreferencesChanged;
        services.Manager.ControllersChanged += OnControllersChanged;
        services.Manager.ConnectionChanged += OnConnectionChanged;
        services.Manager.DevicesChanged += OnDevicesChanged;
        services.ControllerRegistry.Changed += OnDevicesChanged;
        services.Manager.StatusChanged += OnConnectionChanged;
        services.Audio.StatusChanged += OnAudioStatusChanged;
        services.Profiles.ProfilesChanged += OnProfilesChanged;
        services.Watcher.Switched += OnAutoProfileSwitched;
        services.Touchpad.Toggled += OnTouchpadToggled;
        services.Gyro.Toggled += OnGyroToggled;

        SyncConnection();
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRates(), _dispatcher);
        _liveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateLive(), _dispatcher);
    }

    public OutputViewModel Output { get; }
    public TriggerEditorViewModel LeftTrigger { get; }
    public TriggerEditorViewModel RightTrigger { get; }
    public AudioViewModel Audio { get; }
    public ProfilesViewModel Profiles { get; }
    public AutoProfileViewModel AutoProfile { get; }
    public RefreshViewModel Refresh { get; }
    public PreferencesViewModel Preferences { get; }
    public TouchpadViewModel Touchpad { get; }
    public GyroViewModel Gyro { get; }
    public ControllersViewModel Controllers { get; }
    public RelayCommand RescanCommand { get; }

    // ── Navigation ───────────────────────────────────────────────────

    public IReadOnlyList<NavSection> Sections { get; private set; }

    public new void RefreshTranslations()
    {
        string key = CurrentSection;
        Sections = [.. Sections.Select(s => s with { })];
        _selectedSection = Sections.Single(s => s.Key == key);
        Profiles.Sync(force: true);
        Controllers.Sync();
        AutoProfile.Sync();
        foreach (ObservableObject model in new ObservableObject[] { Output, LeftTrigger, RightTrigger, Audio, Profiles, AutoProfile, Refresh, Preferences, Touchpad, Gyro, Controllers })
            model.RefreshTranslations();
        OnPropertyChanged(string.Empty);
        UpdateTrayStatus();
    }

    public NavSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is not null && SetField(ref _selectedSection, value))
            {
                _liveTimer.Interval = TimeSpan.FromMilliseconds(value.Key == "gyro" ? 16 : 33);
                OnPropertyChanged(nameof(CurrentSection));
            }
        }
    }

    /// <summary>Key of the visible section (bound with EqualsToVisibilityConverter).</summary>
    public string CurrentSection => _selectedSection.Key;


    // ── Connection info ──────────────────────────────────────────────

    public bool IsConnected => _connection.IsConnected;
    public string ConnectionText => _connection.Controller?.ConnectionName ?? L.Text("Odpojené");
    public string ModelText => _connection.Controller?.ModelName ?? "—";
    public string DeviceTitle => _connection.Controller?.ModelName ?? L.Text("Žiadny ovládač");

    public string AppVersion { get; } =
        "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>Header status pill: "OVLÁDAČ 1 · BLUETOOTH · 85 %" / "ODPOJENÉ" – which controller the UI edits.</summary>
    public string HeaderStatus
    {
        get
        {
            if (_connection.Controller is not { } c)
                return L.Text("ODPOJENÉ");
            var parts = new List<string>();
            if (_services.ControllerRegistry.Find(c.Key)?.Name is { } name && _services.ControllerRegistry.Bindings.Count > 1)
                parts.Add(name);
            parts.Add(c.ConnectionName);
            if (_connection.Status is { } s)
                parts.Add($"{s.BatteryPercent} %");
            return string.Join(" · ", parts).ToUpperInvariant();
        }
    }

    public string FirmwareText => _connection.Firmware is { } fw
        ? $"{fw.FirmwareText} (feature {fw.UpdateVersionText})"
        : "—";

    public string RumbleModeText => IsConnected ? (_connection.UsesVibrationV2 ? "v2" : L.Text("v1 (starší firmvér)")) : "—";
    public bool HasStatus => _connection.Status is not null;
    public double BatteryPercent => _connection.Status?.BatteryPercent ?? 0;

    public bool IsCharging => _connection.Status?.BatteryState is BatteryState.Charging;

    public string BatteryText => _connection.Status is { } status
        ? $"{status.BatteryPercent} % · {DescribeBattery(status.BatteryState)}"
        : "—";

    public string BatteryShortText => _connection.Status is { } status ? $"{status.BatteryPercent} %" : "—";

    public string BatteryStateText => _connection.Status is { } status ? DescribeBattery(status.BatteryState) : L.Text("Ovládač nie je pripojený");

    public string HeadphonesText => _connection.Status is { } status
        ? status.HeadphonesConnected ? L.Text("Pripojené") : L.Text("Nepripojené")
        : "—";

    public string MicrophoneText => _connection.Status is { } status
        ? status.MicrophoneConnected ? L.Text("Pripojený") : L.Text("Nepripojený")
        : "—";

    public string StatusMessage
    {
        get => L.Text(_statusMessage);
        private set => SetField(ref _statusMessage, value);
    }

    public string RateText
    {
        get => L.Text(_rateText);
        private set => SetField(ref _rateText, value);
    }

    /// <summary>Global shortcut pressed (slot 0 = first profile).</summary>
    public void OnHotkey(int slot)
    {
        var profile = _services.Profiles.ActivateSlot(slot);
        if (profile is null)
            return;
        var message = L.Format($"Profil „{profile.DisplayName}“ (skratka {slot + 1}).");
        StatusMessage = message;
        if (_services.Preferences.Current.ProfileSwitchNotifications)
            _services.Notifier.Notify(L.Text("Profil prepnutý"), message);
    }

    /// <summary>The live timers only run while the window is visible.</summary>
    public void SetWindowVisible(bool visible)
    {
        _statsTimer.IsEnabled = visible;
        _liveTimer.IsEnabled = visible;
        if (visible)
            UpdateRates();
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        _liveTimer.Stop();
        var s = _services;
        s.Settings.Changed -= OnSettingsChanged;
        s.Refresh.Changed -= OnRefreshChanged;
        s.Preferences.Changed -= OnPreferencesChanged;
        s.Manager.ControllersChanged -= OnControllersChanged;
        s.Manager.ConnectionChanged -= OnConnectionChanged;
        s.Manager.DevicesChanged -= OnDevicesChanged;
        s.ControllerRegistry.Changed -= OnDevicesChanged;
        s.Manager.StatusChanged -= OnConnectionChanged;
        s.Audio.StatusChanged -= OnAudioStatusChanged;
        s.Profiles.ProfilesChanged -= OnProfilesChanged;
        s.Watcher.Switched -= OnAutoProfileSwitched;
        s.Touchpad.Toggled -= OnTouchpadToggled;
        s.Gyro.Toggled -= OnGyroToggled;
    }

    private static Geometry Icon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static string DescribeBattery(BatteryState state) => state switch
    {
        BatteryState.Discharging => L.Text("vybíja sa"),
        BatteryState.Charging => L.Text("nabíja sa"),
        BatteryState.Full => L.Text("nabitá"),
        BatteryState.NotCharging => L.Text("nenabíja sa (teplota alebo napätie sú mimo povoleného rozsahu)"),
        _ => L.Text("neznámy stav"),
    };

    private void Report(string message) => RunOnUi(() => StatusMessage = message);

    private void OnSettingsChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        Output.Refresh();
        LeftTrigger.Refresh();
        RightTrigger.Refresh();
        Audio.Refresh();
        Gyro.Refresh();
    });

    private void OnRefreshChanged(object? sender, EventArgs e) => RunOnUi(Refresh.Refresh);

    private void OnPreferencesChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        Preferences.Refresh();
        Touchpad.Refresh();
        Gyro.Refresh();
        AutoProfile.Sync();
    });

    private void OnControllersChanged(object? sender, EventArgs e) => RunOnUi(Controllers.Sync);

    private void OnDevicesChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        Controllers.Sync();
        OnPropertyChanged(nameof(HeaderStatus));
    });

    private void OnConnectionChanged(object? sender, EventArgs e) => RunOnUi(SyncConnection);

    private void OnAudioStatusChanged(object? sender, EventArgs e) => RunOnUi(Audio.RefreshEngineStatus);

    private void OnProfilesChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        Profiles.Sync();
        AutoProfile.Sync();
        Controllers.Sync();
        UpdateTrayStatus();
    });

    private void OnTouchpadToggled(object? sender, bool enabled) => RunOnUi(() =>
    {
        var message = enabled ? L.Text("Dotykový panel ovláda myš.") : L.Text("Myš cez dotykový panel je vypnutá (zapneš ju PS + klik).");
        StatusMessage = message;
        _services.Notifier.Notify(L.Text("Dotykový panel"), message);
    });

    private void OnGyroToggled(object? sender, bool enabled) => RunOnUi(() =>
    {
        var message = enabled
            ? L.Format($"Myš ovládaš pohybom ovládača. Aktívny profil: {_services.Profiles.Active.DisplayName}.")
            : L.Text("Ovládanie myši pohybom je vypnuté. Zapneš ho kombináciou PS + R1.");
        StatusMessage = message;
        _services.Notifier.Notify(L.Text("Gyroskop"), message);
    });

    private void OnAutoProfileSwitched(object? sender, string message) => RunOnUi(() =>
    {
        StatusMessage = message;
        AutoProfile.RefreshActive();
        if (_services.Preferences.Current.ProfileSwitchNotifications)
            _services.Notifier.Notify(L.Text("Automatický profil"), message);
    });

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }


    private void SyncConnection()
    {
        var previousMessage = _connection.Message;
        _connection = _services.Manager.Connection;
        foreach (var name in ConnectionProperties)
            OnPropertyChanged(name);
        if (_connection.Message != previousMessage || StatusMessage.Length == 0)
            StatusMessage = _connection.Message;
        Audio.SetConnection(_connection.Controller?.Connection);
        Touchpad.SetConnection(_connection.Controller);
        Gyro.SetConnection(_connection.Controller);
        CheckBattery();
        UpdateTrayStatus();
        Controllers.Sync();
    }

    private void CheckBattery()
    {
        var alert = _batteryMonitor.Update(_connection.Status);
        if (alert == BatteryAlert.None)
            return;
        var text = alert == BatteryAlert.Critical
            ? L.Format($"Batéria ovládača je takmer vybitá ({_connection.Status!.BatteryPercent} %). Pripoj ho na nabíjanie.")
            : L.Format($"Batéria ovládača je slabá ({_connection.Status!.BatteryPercent} %).");
        StatusMessage = text;
        if (_services.Preferences.Current.LowBatteryNotifications)
            _services.Notifier.Notify(alert == BatteryAlert.Critical ? L.Text("Kriticky slabá batéria") : L.Text("Slabá batéria"), text, warning: true);
    }

    private void UpdateTrayStatus()
    {
        var battery = _connection.Status is { } s ? $"{s.BatteryPercent} %" : _connection.IsConnected ? L.Text("pripojený") : L.Text("odpojený");
        var name = _services.ControllerRegistry.Find(_connection.Controller?.Key)?.Name ?? L.Text("Ovládač");
        _services.Notifier.SetStatus(L.Format($"DualSense Tweaker od LCKS\nProfil: {_services.Profiles.Active.DisplayName}\n{name}: {battery}"));
    }


    private void UpdateLive()
    {
        Audio.UpdateMeters();
        Output.UpdateLive(_services.Live.Current.Lightbar);
        Touchpad.UpdateLive();
        if (CurrentSection == "gyro")
            Gyro.UpdateLive();
    }

    private void UpdateRates()
    {
        var stats = _services.Manager.Statistics;
        var now = _statsClock.Elapsed;
        if (stats is null)
        {
            RateText = "";
        }
        else if (_lastStatistics is { } previous && stats.Value.OutputReports >= previous.OutputReports)
        {
            double seconds = (now - _lastStatisticsTime).TotalSeconds;
            double inputRate = (stats.Value.InputReports - previous.InputReports) / seconds;
            double outputRate = (stats.Value.OutputReports - previous.OutputReports) / seconds;
            RateText = L.Format($"Vstup {inputRate:0}/s · Výstup {outputRate:0}/s · Odoslané: {stats.Value.OutputReports}");
        }
        _lastStatistics = stats;
        _lastStatisticsTime = now;
    }
}
