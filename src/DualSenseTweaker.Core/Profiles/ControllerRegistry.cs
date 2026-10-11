using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Profiles;

/// <summary>
/// Remembers which profile every controller uses (by MAC address, saved in settings.json) and
/// keeps each connected controller's output in sync with it. The UI edits the selected
/// controller's profile through the shared editor state; every controller on the same profile
/// follows those edits live — that is what "linked" controllers are.
/// </summary>
public sealed class ControllerRegistry : IDisposable
{
    private readonly ProfileService _profiles;
    private readonly SettingsState _editor;
    private readonly LiveOutput _editorLive;
    private readonly RefreshState _refresh;
    private readonly PreferencesState _preferences;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Output> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private DualSenseManager? _manager;
    private string? _selectedKey;
    private string _activeProfile;
    private int _quiet;

    /// <summary>What one controller is sent: its own state objects, so linking never needs a reconnect.</summary>
    private sealed class Output
    {
        public Output(RefreshState refresh) => Sources = new OutputSources(Settings, Live, refresh);

        public SettingsState Settings { get; } = new();
        public LiveOutput Live { get; } = new();
        public OutputSources Sources { get; }
        public string? Profile { get; set; }
    }

    public ControllerRegistry(ProfileService profiles, SettingsState editor, LiveOutput editorLive, RefreshState refresh, PreferencesState preferences)
    {
        _profiles = profiles;
        _editor = editor;
        _editorLive = editorLive;
        _refresh = refresh;
        _preferences = preferences;
        _activeProfile = profiles.Active.Name;
        _editor.Changed += OnEditorChanged;
        _editorLive.Changed += OnEditorLiveChanged;
        _profiles.ProfilesChanged += OnProfilesChanged;
    }

    /// <summary>A binding (name, profile) changed or another controller became the edited one.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ControllerBinding> Bindings => _preferences.Current.Controllers;

    /// <summary>The controller the UI edits (null before the first connection).</summary>
    public string? SelectedKey
    {
        get
        {
            lock (_gate)
                return _selectedKey;
        }
    }

    public ControllerBinding? Find(string? key) =>
        key is null ? null : Bindings.FirstOrDefault(b => SameKey(b.Key, key));

    /// <summary>Names of the other controllers on the same profile (they change together).</summary>
    public IReadOnlyList<string> LinkedWith(string key)
    {
        var binding = Find(key);
        if (binding?.Profile is not { } profile)
            return [];
        return [.. Bindings.Where(b => !SameKey(b.Key, key) && b.Profile is not null && ProfileStore.SameName(b.Profile, profile)).Select(b => b.Name)];
    }

    /// <summary>Connects to the manager: follows its selected controller and restores the last one the user edited.</summary>
    public void Attach(DualSenseManager manager)
    {
        _manager = manager;
        manager.ConnectionChanged += OnConnectionChanged;
        if (_preferences.Current.SelectedController is { } last)
            manager.Select(last);
    }

    /// <summary>
    /// For the manager (its thread): the output of this controller, or null when it is not
    /// controlled. A controller seen for the first time gets a name and the current profile.
    /// </summary>
    public OutputSources? SourcesFor(ControllerInfo controller)
    {
        var binding = EnsureBinding(controller.Key);
        if (binding.Profile is not { } profile)
            return null;
        Output? output;
        lock (_gate)
        {
            if (!_outputs.TryGetValue(controller.Key, out output))
                _outputs[controller.Key] = output = new Output(_refresh);
        }
        Sync(output, profile);
        return output.Sources;
    }

    public void Rename(string key, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return;
        if (UpdateBindings(list => list.Select(b => SameKey(b.Key, key) ? b with { Name = trimmed } : b)))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>This controller alone uses <paramref name="profile"/> from now on (null = leave it alone).</summary>
    public void Assign(string key, string? profile)
    {
        if (profile is not null && !_profiles.Exists(profile))
            return;
        UpdateBindings(list => list.Select(b => SameKey(b.Key, key) ? b with { Profile = profile } : b));
        if (profile is not null && SameKey(key, SelectedKey))
            ActivateQuietly(profile);
        Log.Info(L.Format($"Ovládač {Find(key)?.Name ?? key}: profil {profile ?? L.Text("žiadny (neovládaný)")}."));
        ResyncAll();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>All other controlled controllers take this controller's profile: one edit changes them all.</summary>
    public void LinkOthers(string key)
    {
        var profile = Find(key)?.Profile ?? _profiles.Active.Name;
        UpdateBindings(list => list.Select(b => SameKey(b.Key, key) || b.Profile is not null ? b with { Profile = profile } : b));
        if (SelectedKey is { } selected && Find(selected)?.Profile is { } selectedProfile)
            ActivateQuietly(selectedProfile);
        Log.Info(L.Format($"Ovládače prepojené na profil {profile}."));
        ResyncAll();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Unlinks the controller: it gets its own copy of its current profile, named after it.</summary>
    public Profile? MakeIndependent(string key)
    {
        var binding = Find(key);
        if (binding is null)
            return null;
        var copy = _profiles.CreateCopy(binding.Profile ?? _profiles.Active.Name, binding.Name);
        Assign(key, copy.Name);
        return copy;
    }

    /// <summary>
    /// Puts the controller back to factory look (default lightbar, LEDs off, triggers off, no rumble),
    /// forgets its profile and gyro calibration and stops controlling it until it gets a profile again.
    /// </summary>
    public void FactoryReset(string key)
    {
        _preferences.Update(p => p with
        {
            Controllers = [.. p.Controllers.Select(b => SameKey(b.Key, key) ? b with { Profile = null } : b)],
            Gyro = p.Gyro.WithoutCalibration(key),
        });
        lock (_gate)
            _outputs.Remove(key);
        Log.Info(L.Format($"Ovládač {Find(key)?.Name ?? key}: továrenský reset."));
        _manager?.FactoryReset(key);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a remembered controller (and its gyro calibration) from the list.</summary>
    public void Forget(string key)
    {
        _preferences.Update(p => p with
        {
            Controllers = [.. p.Controllers.Where(b => !SameKey(b.Key, key))],
            Gyro = p.Gyro.WithoutCalibration(key),
        });
        lock (_gate)
            _outputs.Remove(key);
        _manager?.RefreshManaged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _editor.Changed -= OnEditorChanged;
        _editorLive.Changed -= OnEditorLiveChanged;
        _profiles.ProfilesChanged -= OnProfilesChanged;
        if (_manager is not null)
            _manager.ConnectionChanged -= OnConnectionChanged;
    }

    private ControllerBinding EnsureBinding(string key)
    {
        if (Find(key) is { } existing)
            return existing;
        ControllerBinding? result = null;
        var active = _profiles.Active.Name;
        _preferences.Update(p =>
        {
            result = p.Controllers.FirstOrDefault(b => SameKey(b.Key, key));
            if (result is not null)
                return p;
            result = new ControllerBinding(key, NextName(p.Controllers), active);
            return p with { Controllers = [.. p.Controllers, result] };
        });
        Log.Info(L.Format($"Nový ovládač {result!.Name} ({key}) dostal profil {result.Profile}."));
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private static string NextName(IReadOnlyList<ControllerBinding> existing)
    {
        for (int n = 1; ; n++)
        {
            var name = L.Format($"Ovládač {n}");
            if (!existing.Any(b => string.Equals(b.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                return name;
        }
    }

    /// <summary>Mirrors the profile into the controller's own state: live edits for the edited profile, the saved settings otherwise.</summary>
    private void Sync(Output output, string profile)
    {
        string active;
        lock (_gate)
            active = _activeProfile;
        if (ProfileStore.SameName(profile, active))
        {
            output.Settings.Replace(_editor.Current);
            output.Live.Replace(_editorLive.Current);
        }
        else
        {
            var saved = _profiles.Profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, profile));
            output.Settings.Replace(saved?.Settings ?? _editor.Current);
            output.Live.Replace(LiveOverlay.None);
        }
        output.Profile = profile;
    }

    private void ResyncAll()
    {
        foreach (var (key, output) in Outputs())
        {
            if (Find(key)?.Profile is { } profile)
                Sync(output, profile);
        }
        _manager?.RefreshManaged();
    }

    private List<(string Key, Output Output)> Outputs()
    {
        lock (_gate)
            return [.. _outputs.Select(o => (o.Key, o.Value))];
    }

    private IEnumerable<Output> LinkedToEditor()
    {
        string active;
        lock (_gate)
            active = _activeProfile;
        return Outputs().Select(o => o.Output).Where(o => o.Profile is not null && ProfileStore.SameName(o.Profile, active));
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        var current = _editor.Current;
        foreach (var output in LinkedToEditor())
            output.Settings.Replace(current);
    }

    private void OnEditorLiveChanged(object? sender, EventArgs e)
    {
        var current = _editorLive.Current;
        foreach (var output in LinkedToEditor())
            output.Live.Replace(current);
    }

    /// <summary>The manager selected another controller: the editor switches to its profile.</summary>
    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        if (_manager?.Connection.Controller?.Key is { } key)
            SetSelected(key);
    }

    internal void SetSelected(string key)
    {
        lock (_gate)
        {
            if (SameKey(key, _selectedKey))
                return;
            _selectedKey = key;
        }
        if (Find(key)?.Profile is { } profile && !ProfileStore.SameName(profile, _profiles.Active.Name))
            ActivateQuietly(profile);
        _preferences.Update(p => p with { SelectedController = key });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        var active = _profiles.Active.Name;
        string previous;
        lock (_gate)
        {
            previous = _activeProfile;
            _activeProfile = active;
        }

        bool changed = false;
        if (!ProfileStore.SameName(previous, active) && Volatile.Read(ref _quiet) == 0)
        {
            // Switched from outside (profile tile, hotkey, tray, running game): the edited controller
            // and every controller linked to it follow.
            var selected = SelectedKey;
            changed |= UpdateBindings(list => list.Select(b =>
                b.Profile is not null && (ProfileStore.SameName(b.Profile, previous) || SameKey(b.Key, selected))
                    ? b with { Profile = active }
                    : b));
        }

        // A deleted profile hands its controllers to the active one.
        var names = _profiles.Profiles.Select(p => p.Name).ToList();
        changed |= UpdateBindings(list => list.Select(b =>
            b.Profile is not null && !names.Any(n => ProfileStore.SameName(n, b.Profile)) ? b with { Profile = active } : b));

        ResyncAll();
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ActivateQuietly(string profile)
    {
        if (ProfileStore.SameName(profile, _profiles.Active.Name) || !_profiles.Exists(profile))
            return;
        Interlocked.Increment(ref _quiet);
        try
        {
            _profiles.Activate(profile);
        }
        finally
        {
            Interlocked.Decrement(ref _quiet);
        }
    }

    /// <returns>True when a binding actually changed.</returns>
    private bool UpdateBindings(Func<IEnumerable<ControllerBinding>, IEnumerable<ControllerBinding>> change) =>
        _preferences.Update(p =>
        {
            var next = change(p.Controllers).ToList();
            return next.SequenceEqual(p.Controllers) ? p : p with { Controllers = next };
        });

    private static bool SameKey(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
