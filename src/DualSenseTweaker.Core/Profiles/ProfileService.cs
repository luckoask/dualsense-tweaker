using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Profiles;

/// <summary>
/// Keeps the active profile and <see cref="SettingsState"/> in sync: switching applies a profile,
/// every edit is written back to the active profile (debounced auto-save).
/// </summary>
public sealed class ProfileService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly ProfileStore _store;
    private readonly SettingsState _settings;
    private readonly RefreshState _refresh;
    private readonly PreferencesState _preferences;
    private readonly Lock _gate = new();
    private readonly Lock _saveGate = new();
    private readonly Timer _saveTimer;
    private readonly Timer _appSettingsTimer;
    private List<Profile> _profiles;
    private Profile _active;
    private bool _dirty;
    private bool _applying;

    public ProfileService(ProfileStore store, SettingsState settings, RefreshState refresh, PreferencesState? preferences = null)
    {
        _store = store;
        _settings = settings;
        _refresh = refresh;
        _preferences = preferences ?? new PreferencesState();
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _appSettingsTimer = new Timer(_ => SaveAppSettings(), null, Timeout.Infinite, Timeout.Infinite);

        var appSettings = store.LoadAppSettings();
        _refresh.Replace(appSettings.Refresh);
        _preferences.Replace(appSettings.Preferences);
        _profiles = [.. store.LoadAll()];
        _active = _profiles.FirstOrDefault(p => appSettings.ActiveProfile is not null && ProfileStore.SameName(p.Name, appSettings.ActiveProfile))
                  ?? _profiles.FirstOrDefault(p => p.Kind == ProfileKind.Custom)
                  ?? _profiles[0];
        Apply(_active);

        _settings.Changed += OnSettingsChanged;
        _refresh.Changed += OnAppSettingsChanged;
        _preferences.Changed += OnAppSettingsChanged;
    }

    /// <summary>Raised when the list or the active profile changes (on the calling thread).</summary>
    public event EventHandler? ProfilesChanged;

    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            lock (_gate)
                return [.. _profiles];
        }
    }

    public Profile Active
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    public void Activate(string name)
    {
        Profile target;
        lock (_gate)
        {
            target = _profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, name))
                     ?? throw new ArgumentException(L.Format($"Profil „{name}“ neexistuje."), nameof(name));
            if (ReferenceEquals(target, _active))
                return;
        }
        Flush();
        lock (_gate)
            _active = target;
        Apply(target);
        SaveAppSettings();
        Log.Info(L.Format($"Aktívny profil: {target.Name}"));
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the current settings as a new custom profile and switches to it.</summary>
    /// <exception cref="ArgumentException">The name is empty or taken.</exception>
    public Profile CreateFromCurrent(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException(L.Text("Zadaj názov profilu."));
        if (Profiles.Any(p => ProfileStore.SameName(p.Name, trimmed)))
            throw new ArgumentException(L.Format($"Profil „{trimmed}“ už existuje."));
        return AddAndActivate(new Profile(trimmed, ProfileKind.Custom, _settings.Current.WithoutRumble()));
    }

    /// <summary>Copies a profile under a new (made unique) name without switching to it.</summary>
    /// <exception cref="ArgumentException">The source does not exist.</exception>
    public Profile CreateCopy(string sourceName, string name)
    {
        Flush();
        var source = Profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, sourceName))
                     ?? throw new ArgumentException(L.Format($"Profil „{sourceName}“ neexistuje."), nameof(sourceName));
        var copy = new Profile(UniqueName(string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim()), ProfileKind.Custom,
            source.Settings.WithoutRumble());
        lock (_gate)
            _profiles.Add(copy);
        Save(copy);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    /// <exception cref="InvalidDataException">The file is not a valid profile.</exception>
    public Profile Import(string path)
    {
        var imported = ProfileStore.Import(path);
        return AddAndActivate(imported with { Name = UniqueName(imported.Name) });
    }

    public void ExportActive(string path)
    {
        Flush();
        ProfileStore.Export(Active, path);
    }

    /// <summary>Deletes a custom profile (built-ins and the last profile stay).</summary>
    public bool Delete(string name)
    {
        Profile? victim;
        lock (_gate)
        {
            victim = _profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, name));
            if (victim is null || victim.IsBuiltIn || _profiles.Count == 1)
                return false;
            _profiles.Remove(victim);
            if (ReferenceEquals(victim, _active))
            {
                _active = _profiles.FirstOrDefault(p => p.Kind == ProfileKind.Custom) ?? _profiles[0];
                _dirty = false;
                Apply(_active);
            }
        }
        _store.Delete(victim.Name);
        SaveAppSettings();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Restores the active profile to its preset (Competitive/Adventure) or neutral defaults (custom).</summary>
    public void ResetActive()
    {
        Profile reset;
        lock (_gate)
        {
            reset = _active with { Settings = ProfilePresets.For(_active.Kind) };
            Replace(_active, reset);
        }
        Apply(reset);
        Save(reset);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending edits of the active profile to disk.</summary>
    public void Flush()
    {
        Profile? toSave = null;
        lock (_gate)
        {
            if (_dirty)
            {
                _dirty = false;
                toSave = _active;
            }
        }
        if (toSave is not null)
            Save(toSave);
    }

    /// <summary>Activates the profile at <paramref name="slot"/> (0-based, list order) – used by the hotkeys.</summary>
    /// <returns>The activated profile, or null if there is no profile in that slot.</returns>
    public Profile? ActivateSlot(int slot)
    {
        var profiles = Profiles;
        if (slot < 0 || slot >= profiles.Count)
            return null;
        Activate(profiles[slot].Name);
        return profiles[slot];
    }

    public bool Exists(string name) => Profiles.Any(p => ProfileStore.SameName(p.Name, name));

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _refresh.Changed -= OnAppSettingsChanged;
        _preferences.Changed -= OnAppSettingsChanged;
        _saveTimer.Dispose();
        _appSettingsTimer.Dispose();
        Flush();
        SaveAppSettings();
    }

    private Profile AddAndActivate(Profile profile)
    {
        Flush();
        lock (_gate)
        {
            _profiles.Add(profile);
            _active = profile;
        }
        Save(profile);
        Apply(profile);
        SaveAppSettings();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    private void Apply(Profile profile)
    {
        _applying = true;
        try
        {
            // Profiles never contain manual rumble, so switching also stops it.
            _settings.Replace(profile.Settings);
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_applying)
            return;
        lock (_gate)
        {
            var updated = _active with { Settings = _settings.Current.WithoutRumble() };
            if (updated == _active)
                return; // e.g. only manual rumble changed
            Replace(_active, updated);
            _dirty = true;
        }
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    // Sliders change these continuously; write settings.json once they settle (Dispose writes the rest).
    private void OnAppSettingsChanged(object? sender, EventArgs e) => _appSettingsTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);

    /// <summary>Caller holds the lock.</summary>
    private void Replace(Profile old, Profile updated)
    {
        int index = _profiles.IndexOf(old);
        if (index >= 0)
            _profiles[index] = updated;
        if (ReferenceEquals(_active, old))
            _active = updated;
    }

    private void Save(Profile profile)
    {
        try
        {
            // The debounce timer and UI actions (switch, reset) can save the same file at once.
            lock (_saveGate)
                _store.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(L.Format($"Profil {profile.Name} sa nepodarilo uložiť: {ex.Message}"));
        }
    }

    private void SaveAppSettings()
    {
        var settings = new AppSettings
        {
            ActiveProfile = Active.Name,
            Refresh = _refresh.Current,
            Preferences = _preferences.Current,
        };
        lock (_saveGate)
            _store.SaveAppSettings(settings);
    }

    private string UniqueName(string name)
    {
        var existing = Profiles;
        if (!existing.Any(p => ProfileStore.SameName(p.Name, name)))
            return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!existing.Any(p => ProfileStore.SameName(p.Name, candidate)))
                return candidate;
        }
    }
}
