using System.Collections.ObjectModel;
using System.Diagnostics;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>One controller in the list: connected or remembered.</summary>
public sealed record ControllerCard(
    string Key,
    string Name,
    string Model,
    string Detail,
    string ProfileText,
    string? LinkedText,
    bool IsConnected,
    bool IsManaged,
    bool IsEditing,
    double Battery,
    bool HasBattery,
    bool IsCharging)
{
    public string UpperName => Name.ToUpperInvariant();
}

/// <summary>
/// All controllers the app knows: which one you edit, the profile each one always uses, linking
/// (one edit changes all linked controllers), an own copy, factory reset and forgetting.
/// </summary>
public sealed class ControllersViewModel : ObservableObject
{
    private readonly ControllerRegistry _registry;
    private readonly DualSenseManager _manager;
    private readonly ProfileService _profiles;
    private readonly IFileDialogs _dialogs;
    private readonly Action<string> _report;
    private ControllerCard? _selected;
    private string? _lastEditingKey;
    private bool _syncing;

    public ControllersViewModel(ControllerRegistry registry, DualSenseManager manager, ProfileService profiles, IFileDialogs dialogs, Action<string> report)
    {
        _registry = registry;
        _manager = manager;
        _profiles = profiles;
        _dialogs = dialogs;
        _report = report;
        LinkOthersCommand = new RelayCommand(LinkOthers, () => _selected is { IsManaged: true } && Cards.Count > 1);
        MakeIndependentCommand = new RelayCommand(MakeIndependent, () => _selected is { IsManaged: true, LinkedText: not null });
        FactoryResetCommand = new RelayCommand(FactoryReset, () => _selected is not null);
        ForgetCommand = new RelayCommand(Forget, () => _selected is { IsConnected: false });
        BluetoothSettingsCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }));
        Sync();
    }

    public ObservableCollection<ControllerCard> Cards { get; } = [];

    public RelayCommand LinkOthersCommand { get; }
    public RelayCommand MakeIndependentCommand { get; }
    public RelayCommand FactoryResetCommand { get; }
    public RelayCommand ForgetCommand { get; }
    public RelayCommand BluetoothSettingsCommand { get; }

    /// <summary>The card whose settings are shown below the list; picking a connected one also edits it.</summary>
    public ControllerCard? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            RaiseSelection();
            if (_syncing || value is not { IsConnected: true, IsManaged: true } || value.IsEditing)
                return;
            _manager.Select(value.Key);
            _report(L.Format($"Upravuješ ovládač {value.Name}."));
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>Name of the selected card (edited in place, saved when the text box loses focus).</summary>
    public string SelectedName
    {
        get => _selected?.Name ?? "";
        set
        {
            if (_selected is not null && !string.IsNullOrWhiteSpace(value))
                _registry.Rename(_selected.Key, value);
        }
    }

    /// <summary>"" stands for "not controlled" (a null value would read as "nothing selected").</summary>
    private const string Unmanaged = "";

    private IReadOnlyList<Option<string>> _profileOptions = [];

    public IReadOnlyList<Option<string>> ProfileOptions => _profileOptions;

    public string? SelectedProfile
    {
        get => _selected is null ? null : _registry.Find(_selected.Key)?.Profile ?? Unmanaged;
        set
        {
            // null comes from the combo box while its items are being replaced.
            if (_selected is null || _syncing || value is null || value == SelectedProfile)
                return;
            var name = _selected.Name;
            var profile = value == Unmanaged ? null : value;
            _registry.Assign(_selected.Key, profile);
            _report(profile is null
                ? L.Format($"{name}: aplikácia ho už neovláda.")
                : L.Format($"Pre ovládač „{name}“ bol uložený profil „{DisplayProfile(profile)}“."));
        }
    }

    public string SelectionHint => _selected switch
    {
        null => "",
        { IsManaged: false } => L.Text("Aplikácia tento ovládač neovláda. Priraď mu profil, aby si mohol upravovať jeho nastavenia."),
        { LinkedText: { } linked } => L.Format($"Prepojený s: {linked}. Čo zmeníš, platí pre všetky prepojené ovládače naraz."),
        { IsEditing: true } => L.Text("Tento ovládač práve upravuješ – zmeny v ostatných sekciách idú do jeho profilu."),
        { IsConnected: true } => L.Text("Kliknutím na kartu ho začneš upravovať."),
        _ => L.Text("Odpojený – profil si pamätá a použije ho hneď po pripojení."),
    };

    /// <summary>Name of the controller being edited, for the header.</summary>
    public string? EditingName => _registry.Find(_registry.SelectedKey)?.Name;

    /// <summary>For the Profiles page: who gets the profile picked there.</summary>
    public string ProfileTargetText
    {
        get
        {
            if (_registry.SelectedKey is not { } key || _registry.Find(key) is not { } binding)
                return L.Text("Profil sa použije pre pripojený ovládač a zapamätá sa mu.");
            var linked = _registry.LinkedWith(key);
            return linked.Count == 0
                ? L.Format($"Profil pre: {binding.Name}. Aplikácia ho použije pri každom pripojení.")
                : L.Format($"Profil pre: {binding.Name} a prepojené ovládače ({string.Join(", ", linked)}). Aplikácia ho použije pri každom pripojení.");
        }
    }

    /// <summary>Re-reads bindings and connection state (UI thread).</summary>
    public void Sync()
    {
        var editingKey = _manager.Connection.Controller?.Key;
        var devices = _manager.Devices;
        var visible = _manager.Controllers;
        var cards = new List<ControllerCard>();
        foreach (var binding in _registry.Bindings)
        {
            var device = devices.FirstOrDefault(d => SameKey(d.Info.Key, binding.Key));
            var present = visible.FirstOrDefault(c => SameKey(c.Key, binding.Key));
            var info = device?.Info ?? present;
            var linked = _registry.LinkedWith(binding.Key);
            string detail = info is null ? L.Text("Odpojený")
                : binding.Profile is null ? L.Format($"{info.ConnectionName} · pôvodný stav")
                : device?.Status is { } status ? $"{info.ConnectionName} · {status.BatteryPercent} %"
                : device is not null ? info.ConnectionName
                : L.Format($"{info.ConnectionName} · pripájam…");
            cards.Add(new ControllerCard(
                binding.Key,
                binding.Name,
                info?.ModelName ?? "DualSense",
                detail,
                binding.Profile is null ? L.Text("Bez profilu") : DisplayProfile(binding.Profile),
                linked.Count == 0 ? null : string.Join(", ", linked),
                info is not null,
                binding.Profile is not null,
                SameKey(binding.Key, editingKey),
                device?.Status?.BatteryPercent ?? 0,
                device?.Status is not null,
                device?.Status?.BatteryState is BatteryState.Charging));
        }
        // Connected first, then the remembered ones.
        cards = [.. cards.OrderByDescending(c => c.IsConnected).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

        _syncing = true;
        try
        {
            if (!cards.SequenceEqual(Cards))
            {
                Cards.Clear();
                foreach (var card in cards)
                    Cards.Add(card);
            }
            // Follow the edited controller when it changes; otherwise keep the user's card.
            bool editingChanged = !SameKey(editingKey, _lastEditingKey);
            _lastEditingKey = editingKey;
            var keep = editingChanged || _selected is null ? editingKey ?? _selected?.Key : _selected.Key;
            _selected = cards.FirstOrDefault(c => SameKey(c.Key, keep)) ?? cards.FirstOrDefault();
            OnPropertyChanged(nameof(Selected));
            RaiseSelection();
        }
        finally
        {
            _syncing = false;
        }
        var options = _profiles.Profiles.Select(p => new Option<string>(p.Name, p.DisplayName))
            .Append(new Option<string>(Unmanaged, L.Text("Bez ovládania aplikáciou"))).ToList();
        if (!options.SequenceEqual(_profileOptions))
        {
            _syncing = true;
            try
            {
                _profileOptions = options;
                OnPropertyChanged(nameof(ProfileOptions));
                OnPropertyChanged(nameof(SelectedProfile));
            }
            finally
            {
                _syncing = false;
            }
        }
        OnPropertyChanged(nameof(EditingName));
        OnPropertyChanged(nameof(ProfileTargetText));
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(SelectionHint));
        // The buttons depend on the selected card; WPF only re-asks on input, so ask now.
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void LinkOthers()
    {
        if (_selected is null)
            return;
        _registry.LinkOthers(_selected.Key);
        _report(L.Format($"Všetky ovládače používajú nastavenia z {_selected.Name} – zmena sa prejaví na všetkých naraz."));
    }

    private void MakeIndependent()
    {
        if (_selected is null)
            return;
        var copy = _registry.MakeIndependent(_selected.Key);
        if (copy is not null)
            _report(L.Format($"{_selected.Name} má vlastnú kópiu nastavení (profil „{copy.Name}“) a už nie je prepojený."));
    }

    private void FactoryReset()
    {
        if (_selected is null)
            return;
        var name = _selected.Name;
        bool connected = _selected.IsConnected;
        if (!_dialogs.Confirm(L.Format($"Obnoviť pôvodný stav – {name}"),
                L.Format($"Ovládač „{name}“ sa vráti do pôvodného stavu:\n\n") +
                L.Text("• spúšte bez odporu, vibrácie vypnuté, útlm 0\n") +
                L.Text("• svetelný pás modrý, indikátory hráča a mikrofónu zhasnuté\n") +
                L.Text("• aplikácia zabudne jeho profil aj kalibráciu gyroskopu a prestane ho ovládať\n\n") +
                L.Text("Tvoje profily ostanú. Pred predajom ešte ovládač odstráň z Bluetooth zariadení vo Windows ") +
                L.Text("(tlačidlo „Nastavenia Bluetooth“).\n\nPokračovať?")))
            return;
        _registry.FactoryReset(_selected.Key);
        _report(connected
            ? L.Format($"Pôvodné nastavenia ovládača „{name}“ boli obnovené.")
            : L.Format($"Ovládač „{name}“ nie je pripojený. Aplikácia odstránila jeho priradený profil a kalibráciu."));
    }

    private void Forget()
    {
        if (_selected is null)
            return;
        var name = _selected.Name;
        _registry.Forget(_selected.Key);
        _report(L.Format($"Ovládač {name} odstránený zo zoznamu."));
    }

    private static bool SameKey(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private string DisplayProfile(string name) =>
        _profiles.Profiles.FirstOrDefault(p => ProfileStore.SameName(p.Name, name))?.DisplayName ?? name;
}
