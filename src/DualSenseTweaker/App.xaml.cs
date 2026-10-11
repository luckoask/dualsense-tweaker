using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DualSenseTweaker.Core.Audio;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Input;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.Motion;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.Shell;
using DualSenseTweaker.ViewModels;
using DualSenseTweaker.Localization;

namespace DualSenseTweaker;

public partial class App : Application
{
    private SingleInstance? _instance;
    private PreferencesState? _preferences;
    private ProfileService? _profiles;
    private DualSenseManager? _manager;
    private ControllerRegistry? _registry;
    private AudioEngine? _audio;
    private GameProfileWatcher? _watcher;
    private TouchpadService? _touchpad;
    private GyroService? _gyro;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private (bool Enabled, HotkeyModifiers Modifiers)? _hotkeyConfig;
    private string? _shortcutPrefix;
    private bool _exiting;
    private bool _trayHintShown;
    private int _cleanedUp;
    private (AppLanguage Language, AppTheme Theme)? _appearanceConfig;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool uninstall = HasArgument(e, StartupRegistration.UninstallArgument);
        if (uninstall || HasArgument(e, StartupRegistration.ExitArgument))
        {
            // Run by the installer: let a running instance quit the normal way (controllers restored,
            // profiles saved) before its files are replaced or removed.
            SingleInstance.RequestExit("DualSenseTweaker", TimeSpan.FromSeconds(15));
            if (uninstall)
                StartupRegistration.Remove();
            Shutdown();
            return;
        }

        _instance = new SingleInstance("DualSenseTweaker");
        if (!_instance.IsFirst)
        {
            // Already running (e.g. in the tray): bring that window up instead.
            _instance.SignalFirstInstance();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        bool startInTray = HasArgument(e, StartupRegistration.TrayArgument);
        var dataDirectory = ProfileStore.DefaultRootDirectory;
        Log.Initialize(dataDirectory);
        Log.Info(L.Format($"DualSense Tweaker {typeof(App).Assembly.GetName().Version} štartuje{(startInTray ? L.Text(" v systémovej lište") : "")}."));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(L.Text("Neošetrená výnimka v úlohe"), args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, args) =>
        {
            Log.Info(L.Format($"Windows končí reláciu ({args.ReasonSessionEnding})."));
            Cleanup();
        };

        var startupClock = System.Diagnostics.Stopwatch.StartNew();
        var settings = new SettingsState();
        var refresh = new RefreshState();
        var live = new LiveOutput();
        _preferences = new PreferencesState();
        _profiles = new ProfileService(new ProfileStore(dataDirectory), settings, refresh, _preferences);
        ApplyAppearance();
        _registry = new ControllerRegistry(_profiles, settings, live, refresh, _preferences);
        _manager = new DualSenseManager(_registry.SourcesFor);
        _registry.Attach(_manager);
        _audio = new AudioEngine(settings, live, _manager);
        _watcher = new GameProfileWatcher(_profiles, _preferences);
        var mouse = new SendInputMouse();
        _touchpad = new TouchpadService(_manager, _preferences, mouse);
        _gyro = new GyroService(_manager, settings, _preferences, mouse);

        var startup = new StartupRegistration();
        TryRun(startup.RepairPath, L.Text("Cestu pre štart s Windows sa nepodarilo aktualizovať"));
        _tray = new TrayIcon(LoadTrayIcon(), TrayProfiles, ActivateFromTray, ShowMainWindow,
            () => startup.IsEnabled,
            enabled =>
            {
                TryRun(() => startup.SetEnabled(enabled), L.Text("Štart s Windows sa nepodarilo nastaviť"));
                _viewModel?.Preferences.Refresh();
            },
            ExitApplication);

        _viewModel = new MainViewModel(new AppServices(settings, refresh, _preferences, live, _manager, _audio,
            _profiles, _registry, _watcher, _touchpad, _gyro, new FileDialogs(), _tray, startup));

        _window = new MainWindow { DataContext = _viewModel };
        MainWindow = _window;
        ApplicationAppearance.Apply(this, _preferences.Current);
        _window.Closing += OnWindowClosing;
        _window.IsVisibleChanged += (_, _) => _viewModel.SetWindowVisible(_window.IsVisible);
        _viewModel.SetWindowVisible(false);

        // Works before the window is ever shown (start in tray): the handle is created on demand.
        _hotkeys = new HotkeyManager(_window);
        _hotkeys.Pressed += (_, slot) => _viewModel.OnHotkey(slot);
        ApplyHotkeys();
        _preferences.Changed += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            ApplyAppearance();
            ApplyHotkeys();
        });

        _manager.PsButtonSequence += (_, controller) =>
        {
            if (!_preferences.Current.OpenWithPsButton)
                return;
            // The controller that asked for the window is the one the user wants to edit.
            _manager.Select(controller);
            Dispatcher.InvokeAsync(ShowMainWindow);
        };

        if (!startInTray)
            _window.Show();
        _manager.Start();
        _audio.Start();
        _watcher.Start();
        Log.Info(L.Format($"Štart hotový za {startupClock.ElapsedMilliseconds} ms."));
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _tray?.PrepareMenu());
        _instance.ListenForShowRequests(() => Dispatcher.InvokeAsync(ShowMainWindow));
        _instance.ListenForExitRequests(() => Dispatcher.InvokeAsync(() =>
        {
            Log.Info(L.Text("Inštalátor požiadal o ukončenie."));
            ExitApplication();
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    /// <summary>
    /// Stops audio, then the device manager puts the controller back (rumble 0, triggers off,
    /// attenuation reset) and profile edits are flushed. Safe to call more than once.
    /// </summary>
    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
            return;
        TryRun(() => _watcher?.Dispose(), L.Text("Zastavenie automatického profilu"));
        TryRun(() => _touchpad?.Dispose(), L.Text("Uvoľnenie myši z dotykového panela"));
        TryRun(() => _gyro?.Dispose(), "Zastavenie gyra");
        TryRun(() => _hotkeys?.Dispose(), "Odregistrovanie skratiek");
        TryRun(() => _viewModel?.Dispose(), "Zatvorenie UI");
        TryRun(() => _audio?.Dispose(), "Zastavenie audia");
        TryRun(() => _manager?.Dispose(), L.Text("Uvoľnenie ovládača"));
        TryRun(() => _registry?.Dispose(), L.Text("Uvoľnenie zoznamu ovládačov"));
        TryRun(() => _profiles?.Dispose(), L.Text("Uloženie profilov"));
        TryRun(() => _tray?.Dispose(), L.Text("Odstránenie ikony z lišty"));
        TryRun(() => _instance?.Dispose(), L.Text("Uvoľnenie zámku inštancie"));
        Log.Info(L.Text("Koniec."));
    }

    private static void TryRun(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"{what} zlyhalo", ex);
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))
                       ?? throw new InvalidOperationException(L.Text("Chýba ikona aplikácie."));
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private IReadOnlyList<TrayProfile> TrayProfiles()
    {
        var active = _profiles!.Active.Name;
        return
        [
            .. _profiles.Profiles.Select((p, i) => new TrayProfile(
                p.Name,
                ProfileStore.SameName(p.Name, active),
                _shortcutPrefix is not null && i < 9 ? $"{_shortcutPrefix}+{i + 1}" : null, p.DisplayName)),
        ];
    }

    private void ActivateFromTray(string name)
    {
        _profiles!.Activate(name);
        Log.Info(L.Format($"Profil z lišty: {name}"));
    }

    private void ApplyAppearance()
    {
        if (_preferences is null) return;
        var config = (_preferences.Current.Language, _preferences.Current.Theme);
        if (_appearanceConfig == config) return;
        _appearanceConfig = config;
        ApplicationAppearance.Apply(this, _preferences.Current);
        _viewModel?.RefreshTranslations();
        _tray?.RefreshAppearance();
    }

    /// <summary>(Re)registers the global shortcuts when they are switched on/off or the modifiers change.</summary>
    private void ApplyHotkeys()
    {
        if (_hotkeys is null || _viewModel is null || _preferences is null || _cleanedUp != 0)
            return;
        var preferences = _preferences.Current;
        var modifiers = preferences.HotkeyModifiers ?? HotkeyManager.SuggestModifiers();
        var config = (preferences.HotkeysEnabled, modifiers);
        if (_hotkeyConfig == config)
            return;
        _hotkeyConfig = config;

        string label = HotkeyManager.Describe(modifiers);
        if (!preferences.HotkeysEnabled)
        {
            _hotkeys.Unregister();
            _shortcutPrefix = null;
            _viewModel.Preferences.SetHotkeyState(modifiers, L.Text("Globálne skratky sú vypnuté."));
            _viewModel.Profiles.SetShortcutPrefix(null);
            return;
        }

        var failed = _hotkeys.Register(modifiers);
        var status = L.Format($"{label}+1 … {label}+9 prepína profily v poradí zoznamu.");
        if (preferences.HotkeyModifiers is null && modifiers != HotkeyModifiers.CtrlAlt)
        {
            var altGr = HotkeyManager.TypedCharacters(HotkeyModifiers.CtrlAlt);
            status += L.Format($" Ctrl+Alt+číslica na tvojej klávesnici píše znaky ({string.Join(" ", altGr.Values)}), preto je zvolené {label}.");
        }
        var typed = HotkeyManager.TypedCharacters(modifiers);
        if (typed.Count > 0)
            status += L.Format($" Pozor: {label}+číslica na tvojej klávesnici píše {string.Join(" ", typed.Values)} – kým aplikácia beží, tieto znaky nenapíšeš.");
        if (failed.Count > 0)
            status += L.Format($" Obsadené inou aplikáciou: {string.Join(", ", failed.Select(d => $"{label}+{d}"))}.");
        Log.Info($"Skratky: {status}");

        _shortcutPrefix = label;
        _viewModel.Preferences.SetHotkeyState(modifiers, status);
        _viewModel.Profiles.SetShortcutPrefix(label);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;
        if (_preferences?.Current.CloseToTray == true)
        {
            e.Cancel = true;
            _window!.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.Notify(L.Text("DualSense Tweaker beží na pozadí"),
                    L.Text("Ikona je v systémovej lište. Ukončíš ho cez pravý klik na ikonu → Ukončiť."));
            }
            return;
        }
        _exiting = true;
        Dispatcher.InvokeAsync(() => Shutdown());
    }

    private void ShowMainWindow()
    {
        if (_window is null || _exiting)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Windows may refuse to bring a window from the tray to the front; a topmost toggle does it.
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private static bool HasArgument(StartupEventArgs e, string argument) =>
        e.Args.Any(a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));

    private void ExitApplication()
    {
        _exiting = true;
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(L.Text("Neošetrená výnimka v UI"), e.Exception);
        e.Handled = true;
        if (_window?.IsVisible == true)
            MessageBox.Show(_window, e.Exception.Message, L.Text("DualSense Tweaker – chyba"), MessageBoxButton.OK, MessageBoxImage.Error);
        else
            _tray?.Notify(L.Text("DualSense Tweaker – chyba"), e.Exception.Message, warning: true);
    }

    /// <summary>The process is going down: at least try to leave the controller in a sane state.</summary>
    private void OnFatalException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error(L.Text("Fatálna chyba, aplikácia končí"), e.ExceptionObject as Exception);
        try
        {
            _audio?.Dispose();
            _manager?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(L.Text("Po chybe sa nepodarilo obnoviť nastavenia ovládača"), ex);
        }
    }
}
