using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Localization;
using DualSenseTweaker.MacroGamer.Presentation;

namespace DualSenseTweaker.MacroGamer;

public partial class App : Application
{
    private CompanionInstance? _instance;
    private MainViewModel? _viewModel;
    private CompanionRuntime? _runtime;
    private EmergencyHotkey? _hotkey;
    private DispatcherTimer? _display;
    private bool _closing, _cleaned, _checkingGame;
    private long _lastGameCheck;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) { CompanionInstance.RequestExit(); Shutdown(); return; }
        _instance = new(); if (!_instance.IsFirst) { _instance.RequestShow(); _instance.Dispose(); _instance = null; Shutdown(); return; }
        try
        {
            string worker = FindWorker(); _runtime = new(worker);
            TextCatalog.Apply(this, _runtime.Settings.Language, _runtime.Settings.Theme);
            var window = new MainWindow(); MainWindow = window;
            _viewModel = new(_runtime, new WindowsInteraction(window), action => Dispatcher.BeginInvoke(action), TextCatalog.Text);
            window.DataContext = _viewModel;
            _viewModel.AppearanceRequested += (language, theme) => { TextCatalog.Apply(this, language, theme); _viewModel.RefreshAll(); };
            window.SourceInitialized += (_, _) =>
            {
                _hotkey = new(window, () => _viewModel.StopAllCommand.Execute(null));
                _viewModel.EmergencyAvailable = _hotkey.Available;
                _viewModel.EmergencyStatus = _hotkey.Available ? "Ctrl+Shift+F10" : TextCatalog.Text("Skratka je obsadená; použi STOP VŠETKO") + $" ({_hotkey.Error})";
                _viewModel.RefreshStatus();
            };
            window.Closing += Closing;
            _instance.Listen(() => Dispatcher.BeginInvoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }),
                () => Dispatcher.BeginInvoke(window.Close));
            DispatcherUnhandledException += OnUiError;
            window.Show(); await _runtime.StartInputAsync();
            _display = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, async (_, _) =>
            {
                _viewModel.RefreshStatus();
                if (_checkingGame || !_viewModel.AutoGameProfiles || Stopwatch.GetElapsedTime(_lastGameCheck) < TimeSpan.FromSeconds(2)) return;
                _checkingGame = true; _lastGameCheck = Stopwatch.GetTimestamp();
                try { await _viewModel.EvaluateGamesAsync(WindowsInteraction.ForegroundExecutable()); }
                catch (Exception ex) { _viewModel.Error(ex); }
                finally { _checkingGame = false; }
            }, Dispatcher); _display.Start();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Macro Gamer", MessageBoxButton.OK, MessageBoxImage.Error); _instance?.Dispose(); Shutdown(1); }
    }
    private static string FindWorker()
    {
        foreach (string relative in new[] { "MacroGamer.ScriptHost.exe", "script-worker/MacroGamer.ScriptHost.exe", "MacroGamer.ScriptHost.dll", "script-worker/MacroGamer.ScriptHost.dll" })
        { string path = Path.Combine(AppContext.BaseDirectory, relative); if (File.Exists(path)) return path; }
        throw new FileNotFoundException("Macro Gamer script worker is missing. Repair the optional Macro Gamer MSI feature.");
    }
    private async void Closing(object? sender, CancelEventArgs e)
    {
        if (_cleaned) return; e.Cancel = true; if (_closing) return; _closing = true;
        _display?.Stop(); MainWindow.IsEnabled = false;
        try
        {
            if (_viewModel is not null) await _viewModel.DisposeAsync();
            _hotkey?.Dispose(); _instance?.Dispose(); _instance = null; _cleaned = true; MainWindow.Close(); Shutdown();
        }
        catch (Exception ex) { _viewModel?.Error(ex); MainWindow.IsEnabled = true; _closing = false; _display?.Start(); MessageBox.Show(MainWindow, ex.Message, "Macro Gamer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; _viewModel?.Error(e.Exception);
        if (_runtime is not null) try { await _runtime.StopAllAsync("UI error"); } catch (Exception ex) { _runtime.Diagnostics.Write(ex.Message, "Error"); }
    }
    protected override void OnExit(ExitEventArgs e) { _display?.Stop(); _hotkey?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}

internal sealed class EmergencyHotkey : IDisposable
{
    private bool _disposed;
    private readonly IntPtr _handle; private readonly HwndSource _source; private readonly Action _stop;
    public bool Available { get; }
    public int Error { get; }
    public EmergencyHotkey(Window window, Action stop)
    {
        _stop = stop; _handle = new WindowInteropHelper(window).Handle; _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(Hook); Available = RegisterHotKey(_handle, 0x4D47, 0x0002 | 0x0004 | 0x4000, 0x79); Error = Available ? 0 : Marshal.GetLastPInvokeError();
    }
    private IntPtr Hook(IntPtr handle, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    { if (message == 0x0312 && wparam.ToInt32() == 0x4D47) { handled = true; _stop(); } return IntPtr.Zero; }
    public void Dispose() { if (_disposed) return; _disposed = true; if (Available) UnregisterHotKey(_handle, 0x4D47); _source.RemoveHook(Hook); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal sealed class CompanionInstance : IDisposable
{
    private static string Name => @"Local\MacroGamer." + InputPipeIdentity.CurrentUserPipeName;
    private readonly Mutex _mutex; private readonly EventWaitHandle _show, _exit; private RegisteredWaitHandle? _showWait, _exitWait;
    private bool _disposed; public bool IsFirst { get; }
    public CompanionInstance()
    { _mutex = new(true, Name + ".Instance", out bool created); IsFirst = created; _show = new(false, EventResetMode.AutoReset, Name + ".Show"); _exit = new(false, EventResetMode.AutoReset, Name + ".Exit"); }
    public void RequestShow() => _show.Set();
    public void Listen(Action show, Action exit)
    { _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, false); _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => exit(), null, Timeout.Infinite, false); }
    public static void RequestExit()
    {
        if (!EventWaitHandle.TryOpenExisting(Name + ".Exit", out var signal)) return;
        using (signal) signal.Set();
        if (!Mutex.TryOpenExisting(Name + ".Instance", out var mutex)) return;
        using (mutex) { try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) return; } catch (AbandonedMutexException) { } mutex.ReleaseMutex(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _showWait?.Unregister(null); _exitWait?.Unregister(null); if (IsFirst) _mutex.ReleaseMutex(); _mutex.Dispose(); _show.Dispose(); _exit.Dispose(); }
}
