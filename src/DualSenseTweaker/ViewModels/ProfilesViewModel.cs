using System.Collections.ObjectModel;
using System.IO;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

public sealed record ProfileItem(string Name, ProfileKind Kind, string? Shortcut)
{
    private static readonly System.Windows.Media.Geometry CompetitiveIcon = ParseIcon("M12 3v4 M12 17v4 M3 12h4 M17 12h4 M12 12m-5 0a5 5 0 1 0 10 0a5 5 0 1 0-10 0 M12 12h.01");
    private static readonly System.Windows.Media.Geometry AdventureIcon = ParseIcon("M3 20l6-11 4 6 3-4 5 9z M14.5 5.5a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0");
    private static readonly System.Windows.Media.Geometry CustomIcon = ParseIcon("M4 6h9 M17 6h3 M4 12h3 M11 12h9 M4 18h11 M19 18h1 M15 4v4 M9 10v4 M17 16v4");

    public bool IsBuiltIn => Kind != ProfileKind.Custom;
    public string KindLabel => IsBuiltIn ? L.Text("predvoľba") : L.Text("vlastný");
    public string DisplayName => ProfilePresets.DisplayName(Name, Kind);
    public string UpperName => DisplayName.ToUpperInvariant();

    public System.Windows.Media.Geometry Icon => Kind switch
    {
        ProfileKind.Competitive => CompetitiveIcon,
        ProfileKind.Adventure => AdventureIcon,
        _ => CustomIcon,
    };

    public string Description => Kind switch
    {
        ProfileKind.Competitive => L.Text("Bez rozptyľovania – svetlá zhasnuté, spúšte bez odporu"),
        ProfileKind.Adventure => L.Text("Odpor na spúšťach, silné klasické vibrácie a svetlo podľa zvuku"),
        _ => L.Text("Vlastné nastavenia, všetko podľa teba"),
    };

    /// <summary>"Ctrl+Alt+Shift+1 · predvoľba"</summary>
    public string Detail => Shortcut is null ? KindLabel : $"{Shortcut} · {KindLabel}";

    private static System.Windows.Media.Geometry ParseIcon(string data)
    {
        var geometry = System.Windows.Media.Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>One key cap in the shortcut bar ("1" → "Competitive").</summary>
public sealed record KeyHint(string Key, string Label);

/// <summary>Quick switching, create/delete/reset and import/export of profiles.</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly ProfileService _service;
    private readonly IFileDialogs _dialogs;
    private readonly Action<string> _report;
    private ProfileItem? _active;
    private string _newProfileName = "";
    private string? _shortcutPrefix;
    private bool _syncing;

    public ProfilesViewModel(ProfileService service, IFileDialogs dialogs, Action<string> report)
    {
        _service = service;
        _dialogs = dialogs;
        _report = report;
        CreateCommand = new RelayCommand(Create, () => !string.IsNullOrWhiteSpace(NewProfileName));
        DeleteCommand = new RelayCommand(Delete, () => Active is { IsBuiltIn: false } && Items.Count > 1);
        ResetCommand = new RelayCommand(Reset);
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
        Sync();
    }

    public ObservableCollection<ProfileItem> Items { get; } = [];

    public RelayCommand CreateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }

    public ProfileItem? Active
    {
        get => _active;
        set
        {
            if (!SetField(ref _active, value) || _syncing || value is null)
                return;
            _service.Activate(value.Name);
            _report(L.Format($"Aktívny profil: {value.DisplayName}."));
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set => SetField(ref _newProfileName, value ?? "");
    }

    public string ActiveDescription => _active?.Kind switch
    {
        ProfileKind.Competitive => L.Text("Bez rozptyľovania: svetelný pás a indikátory hráča zhasnuté, spúšte bez odporu, aplikácia nevibruje a vibrácie hier sú utlmené na minimum."),
        ProfileKind.Adventure => L.Text("Zážitok: odpor na oboch spúšťach, silné klasické vibrácie z basov a svetelný pás reagujúci na zvuk."),
        _ => L.Text("Vlastný profil – všetko nastaviteľné."),
    };

    public string ResetLabel => _active?.IsBuiltIn == true
        ? L.Format($"Obnoviť profil {_active.DisplayName}")
        : L.Text("Obnoviť predvolené hodnoty");

    /// <summary>"Ctrl+Alt" etc., or null when global shortcuts are off.</summary>
    public void SetShortcutPrefix(string? prefix)
    {
        _shortcutPrefix = prefix;
        Sync();
        OnPropertyChanged(nameof(ShortcutBarPrefix));
    }

    /// <summary>"CTRL + ALT + SHIFT +" for the bottom bar; empty when shortcuts are off.</summary>
    public string ShortcutBarPrefix => _shortcutPrefix is null ? "" : _shortcutPrefix.ToUpperInvariant().Replace("+", " + ") + " +";

    public ObservableCollection<KeyHint> ShortcutHints { get; } = [];

    /// <summary>Re-reads the list and the active profile from the service.</summary>
    public void Sync(bool force = false)
    {
        _syncing = true;
        try
        {
            var profiles = _service.Profiles
                .Select((p, i) => new ProfileItem(p.Name, p.Kind, _shortcutPrefix is not null && i < 9 ? $"{_shortcutPrefix}+{i + 1}" : null))
                .ToList();
            if (force || !profiles.SequenceEqual(Items))
            {
                Items.Clear();
                foreach (var item in profiles)
                    Items.Add(item);
            }

            var hints = _shortcutPrefix is null
                ? new List<KeyHint>()
                : profiles.Take(5).Select((p, i) => new KeyHint((i + 1).ToString(), p.DisplayName)).ToList();
            if (!hints.SequenceEqual(ShortcutHints))
            {
                ShortcutHints.Clear();
                foreach (var hint in hints)
                    ShortcutHints.Add(hint);
            }
            var active = _service.Active;
            Active = Items.FirstOrDefault(i => ProfileStore.SameName(i.Name, active.Name));
            OnPropertyChanged(nameof(ActiveDescription));
            OnPropertyChanged(nameof(ResetLabel));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Create()
    {
        try
        {
            var created = _service.CreateFromCurrent(NewProfileName);
            NewProfileName = "";
            _report(L.Format($"Profil „{created.Name}“ vytvorený z aktuálnych nastavení."));
        }
        catch (ArgumentException ex)
        {
            _report(ex.Message);
        }
    }

    private void Delete()
    {
        var profile = Active;
        if (profile is not null && _service.Delete(profile.Name))
            _report(L.Format($"Profil „{profile.DisplayName}“ bol odstránený."));
    }

    private void Reset()
    {
        _service.ResetActive();
        _report(L.Format($"Profil „{_service.Active.DisplayName}“ bol obnovený na predvolené nastavenia."));
    }

    private void Import()
    {
        var path = _dialogs.PickProfileToImport();
        if (path is null)
            return;
        try
        {
            var imported = _service.Import(path);
            _report(L.Format($"Profil „{imported.DisplayName}“ bol načítaný."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _report(L.Format($"Profil sa nepodarilo načítať: {ex.Message}"));
        }
    }

    private void Export()
    {
        var path = _dialogs.PickExportPath(_service.Active.Name);
        if (path is null)
            return;
        try
        {
            _service.ExportActive(path);
            _report(L.Format($"Profil „{_service.Active.DisplayName}“ bol uložený do súboru {path}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report(L.Format($"Profil sa nepodarilo uložiť: {ex.Message}"));
        }
    }
}

/// <summary>Which groups the app re-applies periodically (app-wide, saved in settings.json).</summary>
public sealed class RefreshViewModel(RefreshState state) : ObservableObject
{
    public bool Lightbar
    {
        get => state.Current.Lightbar;
        set => state.Update(r => r with { Lightbar = value });
    }

    public bool Triggers
    {
        get => state.Current.Triggers;
        set => state.Update(r => r with { Triggers = value });
    }

    public bool Leds
    {
        get => state.Current.Leds;
        set => state.Update(r => r with { Leds = value });
    }

    public double IntervalMs
    {
        get => state.Current.IntervalMs;
        set => state.Update(r => r with { IntervalMs = (int)Math.Round(value) });
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}
