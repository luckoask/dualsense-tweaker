using System.Collections.ObjectModel;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>Rules "game .exe → profile" for the automatic profile switch.</summary>
public sealed class AutoProfileViewModel : ObservableObject
{
    private readonly PreferencesState _preferences;
    private readonly ProfileService _profiles;
    private readonly GameProfileWatcher _watcher;
    private readonly IFileDialogs _dialogs;
    private readonly Action<string> _report;
    private string _newExe = "";
    private string? _newProfile;

    public AutoProfileViewModel(PreferencesState preferences, ProfileService profiles, GameProfileWatcher watcher,
        IFileDialogs dialogs, Action<string> report)
    {
        _preferences = preferences;
        _profiles = profiles;
        _watcher = watcher;
        _dialogs = dialogs;
        _report = report;
        AddCommand = new RelayCommand(Add, () => GameRule.NormalizeExe(NewExe).Length > 0 && NewProfile is not null);
        BrowseCommand = new RelayCommand(Browse);
        RemoveCommand = new RelayCommand(Remove);
        Sync();
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public ObservableCollection<Option<GameRule>> Rules { get; } = [];
    public ObservableCollection<Option<string>> ProfileOptions { get; } = [];

    public bool Enabled
    {
        get => _preferences.Current.AutoProfileEnabled;
        set => _preferences.Update(p => p with { AutoProfileEnabled = value });
    }

    public string NewExe
    {
        get => _newExe;
        set => SetField(ref _newExe, value ?? "");
    }

    public string? NewProfile
    {
        get => _newProfile;
        set => SetField(ref _newProfile, value);
    }

    public string ActiveRuleText => _watcher.ActiveRule is { } rule
        ? L.Format($"Spustená hra: {rule.ExeName}. Aktívny profil: {DisplayProfile(rule.ProfileName)}.")
        : Rules.Count == 0 ? L.Text("Zatiaľ žiadne pravidlá.") : L.Text("Žiadna zo sledovaných hier nebeží.");

    /// <summary>Re-reads rules and profile names (after preference or profile list changes).</summary>
    public void Sync()
    {
        var rules = _preferences.Current.GameRules.Select(r => new Option<GameRule>(r, DisplayProfile(r.ProfileName))).ToList();
        if (!rules.SequenceEqual(Rules))
        {
            Rules.Clear();
            foreach (var rule in rules)
                Rules.Add(rule);
        }

        var options = _profiles.Profiles.Select(p => new Option<string>(p.Name, p.DisplayName)).ToList();
        if (!options.SequenceEqual(ProfileOptions))
        {
            ProfileOptions.Clear();
            foreach (var option in options)
                ProfileOptions.Add(option);
        }
        if (NewProfile is null || !ProfileOptions.Any(p => ProfileStore.SameName(p.Value, NewProfile)))
            NewProfile = ProfileOptions.FirstOrDefault(p => ProfileStore.SameName(p.Value, ProfilePresets.AdventureName))?.Value ?? ProfileOptions.FirstOrDefault()?.Value;

        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ActiveRuleText));
    }

    public void RefreshActive() => OnPropertyChanged(nameof(ActiveRuleText));

    private void Add()
    {
        var exe = System.IO.Path.GetFileName(NewExe.Trim().Trim('"'));
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe += ".exe";
        var rule = new GameRule(exe, NewProfile!);
        _preferences.Update(p => p with
        {
            // A game has one rule; adding it again replaces the profile.
            GameRules = [.. p.GameRules.Where(r => r.Key != rule.Key), rule],
        });
        NewExe = "";
        _report(L.Format($"Pravidlo bolo pridané: {rule.ExeName} → {DisplayProfile(rule.ProfileName)}."));
    }

    private void Browse()
    {
        if (_dialogs.PickExecutable() is { } path)
            NewExe = System.IO.Path.GetFileName(path);
    }

    private void Remove(object? parameter)
    {
        if (parameter is not Option<GameRule> item)
            return;
        var rule = item.Value;
        _preferences.Update(p => p with { GameRules = [.. p.GameRules.Where(r => r != rule)] });
        _report(L.Format($"Pravidlo pre {rule.ExeName} odstránené."));
    }

    private string DisplayProfile(string name) =>
        _profiles.Profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, name))?.DisplayName ?? name;
}
