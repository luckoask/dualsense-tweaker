using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>Editor for one adaptive trigger (L2 or R2). Edits apply live; "Otestovať" re-sends the effect immediately.</summary>
public sealed class TriggerEditorViewModel : SettingsViewModel
{
    private readonly Func<OutputSnapshot, TriggerEffect> _get;
    private readonly Func<OutputSnapshot, TriggerEffect, OutputSnapshot> _set;

    public TriggerEditorViewModel(
        SettingsState state,
        string number,
        string title,
        Func<OutputSnapshot, TriggerEffect> get,
        Func<OutputSnapshot, TriggerEffect, OutputSnapshot> set,
        OutputGroups group,
        Func<OutputGroups, bool> resend,
        Action<string> report)
        : base(state)
    {
        Number = number;
        _title = title;
        _get = get;
        _set = set;
        TestCommand = new RelayCommand(
            () => report(resend(group)
                ? L.Format($"{Title}: efekt poslaný do ovládača – stlač spúšť a vyskúšaj ho.")
                : L.Text("Ovládač nie je pripojený.")),
            () => Mode != TriggerMode.Game);
    }

    public string Number { get; }
    private readonly string _title;
    public string Title => L.Text(_title);
    public string UpperTitle => Title.ToUpperInvariant();

    public RelayCommand TestCommand { get; }

    public IReadOnlyList<Option<TriggerMode>> ModeOptions =>
    [
        new(TriggerMode.Game, L.Text("Hra")),
        new(TriggerMode.Off, L.Text("Vypnuté")),
        new(TriggerMode.Resistance, L.Text("Odpor")),
        new(TriggerMode.Section, L.Text("Cvaknutie")),
        new(TriggerMode.Vibration, L.Text("Vibrácia")),
    ];

    private TriggerEffect Effect => _get(Current.Output);

    public TriggerMode Mode
    {
        get => Effect.Mode;
        set => Update(e => e with { Mode = value });
    }

    public double StartPosition
    {
        get => Effect.StartPosition;
        set => Update(e => e with { StartPosition = ToByte(value) });
    }

    public double EndPosition
    {
        get => Effect.EndPosition;
        set => Update(e => e with { EndPosition = ToByte(value) });
    }

    public double Strength
    {
        get => Effect.Strength;
        set => Update(e => e with { Strength = ToByte(value) });
    }

    public double Amplitude
    {
        get => Effect.Amplitude;
        set => Update(e => e with { Amplitude = ToByte(value) });
    }

    public double Frequency
    {
        get => Effect.Frequency;
        set => Update(e => e with { Frequency = ToByte(value) });
    }

    public bool ShowStart => Mode is TriggerMode.Resistance or TriggerMode.Section or TriggerMode.Vibration;
    public bool ShowEnd => Mode == TriggerMode.Section;
    public bool ShowStrength => Mode is TriggerMode.Resistance or TriggerMode.Section;
    public bool ShowVibration => Mode == TriggerMode.Vibration;

    public double StartMinimum => Mode == TriggerMode.Section ? TriggerEffect.MinSectionStart : 0;
    public double StartMaximum => Mode == TriggerMode.Section ? TriggerEffect.MaxSectionStart : TriggerEffect.MaxZone;
    public double EndMinimum => Effect.StartPosition + 1;
    public double EndMaximum => TriggerEffect.MaxSectionEnd;

    public string Description => Mode switch
    {
        TriggerMode.Game => L.Text("Spúšť ovláda samotná hra. Aplikácia jej efekty nemení."),
        TriggerMode.Off => L.Text("Žiadny odpor ani vibrácia – prepíše aj efekty hry."),
        TriggerMode.Resistance => L.Text("Stály odpor od nastavenej polohy po koniec stlačenia. Poloha 0 znamená uvoľnenú spúšť, 9 úplne stlačenú."),
        TriggerMode.Section => L.Text("Odpor medzi začiatkom a koncom, ktorý na konci povolí s cvaknutím – ako spúšť zbrane."),
        TriggerMode.Vibration => L.Text("Spúšť vibruje od nastavenej polohy. Intenzitu nastavíš od 1 do 8 a frekvenciu v hertzoch."),
        _ => "",
    };

    /// <summary>The trigger travel (zones 0–9) as the effect uses it, for the preview strip.</summary>
    public IReadOnlyList<ZoneCell> Zones
    {
        get
        {
            var e = Effect.Normalize();
            double Force(int value) => 0.25 + 0.75 * value / 8.0;
            return
            [
                .. Enumerable.Range(0, 10).Select(zone => e.Mode switch
                {
                    TriggerMode.Resistance when zone >= e.StartPosition => new ZoneCell(zone, Force(e.Strength), ZoneKind.Resistance),
                    TriggerMode.Section when zone >= e.StartPosition && zone < e.EndPosition => new ZoneCell(zone, Force(e.Strength), ZoneKind.Resistance),
                    TriggerMode.Section when zone == e.EndPosition => new ZoneCell(zone, 1, ZoneKind.Click),
                    TriggerMode.Vibration when zone >= e.StartPosition => new ZoneCell(zone, Force(e.Amplitude), ZoneKind.Vibration),
                    _ => new ZoneCell(zone, 0, ZoneKind.Free),
                }),
            ];
        }
    }

    /// <summary>The 11 bytes that go into the output report, for the curious.</summary>
    public string BytesText => TriggerEffectFactory.Build(Effect) is { } block
        ? string.Join(' ', block.Select(b => b.ToString("X2")))
        : L.Text("— (neposiela sa)");

    private void Update(Func<TriggerEffect, TriggerEffect> change) =>
        State.UpdateOutput(o => _set(o, change(_get(o))));
}

public enum ZoneKind { Free, Resistance, Click, Vibration }

/// <param name="Intensity">0–1, drives the fill opacity.</param>
public sealed record ZoneCell(int Zone, double Intensity, ZoneKind Kind);
