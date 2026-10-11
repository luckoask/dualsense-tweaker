using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.ViewModels;
using Microsoft.Win32;

namespace DualSenseTweaker.Shell;

/// <summary>"Start with Windows" via HKCU\…\Run; the app starts hidden in the tray.</summary>
internal sealed class StartupRegistration : IStartupRegistration
{
    public const string TrayArgument = "--tray";
    /// <summary>The installer asks a running instance to quit (upgrade, repair, uninstall).</summary>
    public const string ExitArgument = "--exit";
    /// <summary>The uninstaller: quit and drop "start with Windows" of this user.</summary>
    public const string UninstallArgument = "--uninstall";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DualSenseTweaker";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info(L.Format($"Štart s Windows: {(enabled ? L.Text("zapnutý") : L.Text("vypnutý"))}"));
    }

    /// <summary>Removes the Run entry without logging (used by the uninstaller before logging starts).</summary>
    public static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the registered path current when the .exe was moved.</summary>
    public void RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string value && !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, Command);
    }
}

/// <summary>One instance per user session; a second start just brings the first window up.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequest;
    private readonly EventWaitHandle _exitRequest;
    private RegisteredWaitHandle? _wait;
    private RegisteredWaitHandle? _exitWait;

    public SingleInstance(string id)
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.Instance", out bool createdNew);
        IsFirst = createdNew;
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Show");
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Exit");
    }

    /// <summary>
    /// Asks the running instance to quit the normal way (controllers restored, profiles saved) and
    /// waits until it is gone.
    /// </summary>
    /// <returns>False when it is still running after <paramref name="timeout"/>.</returns>
    public static bool RequestExit(string id, TimeSpan timeout)
    {
        if (!EventWaitHandle.TryOpenExisting($@"Local\{id}.Exit", out var exit))
            return true; // not running
        using (exit)
            exit.Set();
        if (!Mutex.TryOpenExisting($@"Local\{id}.Instance", out var mutex))
            return true;
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(timeout))
                    return false;
            }
            catch (AbandonedMutexException)
            {
                // The process ended without releasing it; we own it now either way.
            }
            mutex.ReleaseMutex();
            return true;
        }
    }

    public bool IsFirst { get; }

    public void SignalFirstInstance() => _showRequest.Set();

    /// <param name="onShow">Called on a thread-pool thread whenever another instance starts.</param>
    public void ListenForShowRequests(Action onShow) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    /// <param name="onExit">Called on a thread-pool thread when the installer asks the app to quit.</param>
    public void ListenForExitRequests(Action onExit) =>
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitRequest, (_, _) => onExit(), null, Timeout.Infinite, executeOnlyOnce: true);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _exitWait?.Unregister(null);
        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _showRequest.Dispose();
        _exitRequest.Dispose();
    }
}
