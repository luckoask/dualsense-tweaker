using System.Diagnostics;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Profiles;

/// <summary>
/// Switches to a game's profile while its .exe runs and back to the previous profile when it exits.
/// Only transitions act: a manual switch while the game runs is respected, and the profile is not
/// switched back if the user moved away from the game's profile in the meantime.
/// </summary>
public sealed class GameProfileWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly ProfileService _profiles;
    private readonly PreferencesState _preferences;
    private readonly Func<IReadOnlySet<string>> _runningProcesses;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private GameRule? _activeRule;
    private string? _profileBeforeGame;
    private int _busy;

    public GameProfileWatcher(ProfileService profiles, PreferencesState preferences, Func<IReadOnlySet<string>>? runningProcesses = null)
    {
        _profiles = profiles;
        _preferences = preferences;
        _runningProcesses = runningProcesses ?? GetRunningProcessNames;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _preferences.Changed += OnPreferencesChanged;
    }

    /// <summary>Raised (on a worker thread) with a human-readable message after an automatic switch.</summary>
    public event EventHandler<string>? Switched;

    public GameRule? ActiveRule
    {
        get
        {
            lock (_gate)
                return _activeRule;
        }
    }

    public void Start() => _timer.Change(TimeSpan.Zero, Interval);

    private string DisplayProfile(string name) =>
        _profiles.Profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, name))?.DisplayName ?? name;

    public void Dispose()
    {
        _preferences.Changed -= OnPreferencesChanged;
        _timer.Dispose();
    }

    /// <summary>Lower-case process names (without ".exe") of everything running.</summary>
    public static IReadOnlySet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName.ToLowerInvariant());
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating.
                }
            }
        }
        return names;
    }

    /// <summary>One evaluation against the given set of running process names.</summary>
    internal void Evaluate(IReadOnlySet<string> running)
    {
        var preferences = _preferences.Current;
        var match = preferences.AutoProfileEnabled
            ? preferences.GameRules.FirstOrDefault(r => running.Contains(r.Key) && _profiles.Exists(r.ProfileName))
            : null;

        string? activate = null;
        string? message = null;
        lock (_gate)
        {
            if (SameRule(match, _activeRule))
                return;

            if (match is not null)
            {
                _profileBeforeGame ??= _profiles.Active.Name;
                _activeRule = match;
                if (!ProfileStore.SameName(_profiles.Active.Name, match.ProfileName))
                {
                    activate = match.ProfileName;
                    message = L.Format($"Spustená hra: {match.ExeName}. Aktívny profil: {DisplayProfile(match.ProfileName)}.");
                }
            }
            else
            {
                var ended = _activeRule!;
                var previous = _profileBeforeGame;
                _activeRule = null;
                _profileBeforeGame = null;
                if (previous is not null && _profiles.Exists(previous)
                    && ProfileStore.SameName(_profiles.Active.Name, ended.ProfileName)
                    && !ProfileStore.SameName(previous, ended.ProfileName))
                {
                    activate = previous;
                    message = L.Format($"Hra {ended.ExeName} bola ukončená. Obnovený profil: {DisplayProfile(previous)}.");
                }
            }
        }

        if (activate is null)
            return;
        _profiles.Activate(activate);
        Log.Info(message!);
        Switched?.Invoke(this, message!);
    }

    private static bool SameRule(GameRule? a, GameRule? b) =>
        a is null ? b is null : b is not null && a.Key == b.Key && ProfileStore.SameName(a.ProfileName, b.ProfileName);

    private void OnPreferencesChanged(object? sender, EventArgs e) => _timer.Change(TimeSpan.Zero, Interval);

    private void Tick()
    {
        // Timer callbacks can overlap when enumerating processes is slow.
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return;
        try
        {
            Evaluate(_runningProcesses());
        }
        catch (Exception ex)
        {
            Log.Error(L.Text("Automatický profil zlyhal"), ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
