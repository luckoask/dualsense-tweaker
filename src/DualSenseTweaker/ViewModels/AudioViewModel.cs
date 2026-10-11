using DualSenseTweaker.Core.Audio;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>Audio-reactive mapping, live level meters and "Audio → haptika".</summary>
public sealed class AudioViewModel : SettingsViewModel
{
    private static readonly TimeSpan BeatHold = TimeSpan.FromMilliseconds(120);

    private readonly AudioEngine _engine;
    private ConnectionType? _connection;
    private AudioFeatures _levels = AudioFeatures.Silent;
    private DateTime _beatUntil;

    public AudioViewModel(SettingsState state, AudioEngine engine) : base(state)
    {
        _engine = engine;
        ToggleCommand = new RelayCommand(() => Enabled = !Enabled);
    }

    /// <summary>The big START / STOP button.</summary>
    public RelayCommand ToggleCommand { get; }

    public string ToggleLabel => Enabled ? L.Text("ZASTAVIŤ") : L.Text("SPUSTIŤ");

    private AudioReactiveSettings Reactive => Current.Reactive;
    private HapticsSettings Haptics => Current.Haptics;

    // ── Signal ───────────────────────────────────────────────────────

    public bool Enabled
    {
        get => Reactive.Enabled;
        set => State.Update(s => s with
        {
            Reactive = s.Reactive.WithEnabled(value),
            Output = value ? s.Output with { LightbarEffect = LightbarEffectMode.Static } : s.Output,
        });
    }

    public double GainDb
    {
        get => Reactive.GainDb;
        set => State.UpdateReactive(r => r with { GainDb = Math.Round(value) });
    }

    public double SensitivityPercent
    {
        get => Math.Round(Reactive.Sensitivity * 100);
        set => State.UpdateReactive(r => r with { Sensitivity = Math.Round(value) / 100 });
    }

    public double ThresholdPercent
    {
        get => Math.Round(Reactive.Threshold * 100);
        set => State.UpdateReactive(r => r with { Threshold = Math.Round(value) / 100 });
    }

    public double AttackMs
    {
        get => Reactive.AttackMs;
        set => State.UpdateReactive(r => r with { AttackMs = (int)Math.Round(value) });
    }

    public double ReleaseMs
    {
        get => Reactive.ReleaseMs;
        set => State.UpdateReactive(r => r with { ReleaseMs = (int)Math.Round(value) });
    }

    // ── Mapping ──────────────────────────────────────────────────────

    public IReadOnlyList<ColorSwatch> Swatches => Palette.Swatches;

    public IReadOnlyList<Option<ReactiveLightbarMode>> LightbarModeOptions =>
    [
        new(ReactiveLightbarMode.Static, L.Text("Statická farba")),
        new(ReactiveLightbarMode.Loudness, L.Text("Jas podľa hlasitosti")),
        new(ReactiveLightbarMode.Spectrum, L.Text("Farba zo spektra")),
    ];

    public IReadOnlyList<Option<RumbleTarget>> RumbleTargetOptions =>
    [
        new(RumbleTarget.Heavy, L.Text("Silný")),
        new(RumbleTarget.Soft, L.Text("Jemný")),
        new(RumbleTarget.Both, L.Text("Oba")),
    ];

    public bool BassToRumble
    {
        get => Reactive.BassToRumble;
        set => State.UpdateReactive(r => r with { BassToRumble = value });
    }

    public RumbleTarget RumbleTarget
    {
        get => Reactive.RumbleTarget;
        set => State.UpdateReactive(r => r with { RumbleTarget = value });
    }

    public double RumbleMax
    {
        get => Reactive.RumbleMax;
        set => State.UpdateReactive(r => r with { RumbleMax = ToByte(value) });
    }

    public ReactiveLightbarMode LightbarMode
    {
        get => Reactive.Lightbar;
        set => State.Update(s => s with
        {
            Reactive = s.Reactive with { Lightbar = value },
            Output = s.Reactive.Enabled && value != ReactiveLightbarMode.Static
                ? s.Output with { LightbarEffect = LightbarEffectMode.Static } : s.Output,
        });
    }

    public bool ShowSpectrumColors => Reactive.Lightbar == ReactiveLightbarMode.Spectrum;

    public string BassColorHex
    {
        get => Reactive.BassColor.ToHex();
        set => SetColor(value, (r, c) => r with { BassColor = c });
    }

    public string MidColorHex
    {
        get => Reactive.MidColor.ToHex();
        set => SetColor(value, (r, c) => r with { MidColor = c });
    }

    public string HighColorHex
    {
        get => Reactive.HighColor.ToHex();
        set => SetColor(value, (r, c) => r with { HighColor = c });
    }

    public string FlashColorHex
    {
        get => Reactive.FlashColor.ToHex();
        set => SetColor(value, (r, c) => r with { FlashColor = c });
    }

    public double MinBrightness
    {
        get => Reactive.MinBrightness;
        set => State.UpdateReactive(r => r with { MinBrightness = ToByte(value) });
    }

    public bool BeatFlash
    {
        get => Reactive.BeatFlash;
        set => State.Update(s => s with
        {
            Reactive = s.Reactive with { BeatFlash = value },
            Output = s.Reactive.Enabled && value
                ? s.Output with { LightbarEffect = LightbarEffectMode.Static } : s.Output,
        });
    }

    public double FlashMs
    {
        get => Reactive.FlashMs;
        set => State.UpdateReactive(r => r with { FlashMs = (int)Math.Round(value) });
    }

    // ── Haptics ──────────────────────────────────────────────────────

    public bool HapticsEnabled
    {
        get => Haptics.Enabled;
        set => State.UpdateHaptics(h => h with { Enabled = value });
    }

    public double HapticsStrengthPercent
    {
        get => Math.Round(Haptics.Strength * 100);
        set => State.UpdateHaptics(h => h with { Strength = Math.Round(value) / 100 });
    }

    public double HapticsCutoffHz
    {
        get => Haptics.CutoffHz;
        set => State.UpdateHaptics(h => h with { CutoffHz = (int)Math.Round(value) });
    }

    /// <summary>Audio haptics need the controller's USB audio device; over Bluetooth the option is disabled.</summary>
    public bool HapticsAvailable => _connection != ConnectionType.Bluetooth;

    public string HapticsHint => _connection == ConnectionType.Bluetooth
        ? L.Text("HD haptika zo zvuku je dostupná iba cez USB. Pri pripojení cez Bluetooth sa namiesto nej používajú klasické vibrácie podľa basov.")
        : L.Text("Vyžaduje pripojenie cez USB a štvorkanálový zvukový výstup ovládača vo Windows. Počas prehrávania haptiky sú klasické vibrácie z aplikácie pozastavené.");

    public string EngineStatus => L.Text(_engine.Status.Message);

    // ── Meters ───────────────────────────────────────────────────────

    public IReadOnlyList<float> Spectrum => _levels.Spectrum;

    public double BassLevel => _levels.Bass * 100;
    public double MidLevel => _levels.Mid * 100;
    public double HighLevel => _levels.High * 100;
    public double LoudnessLevel => _levels.Loudness * 100;
    public bool BeatVisible => DateTime.UtcNow < _beatUntil;

    public void SetConnection(ConnectionType? connection)
    {
        _connection = connection;
        OnPropertyChanged(nameof(HapticsAvailable));
        OnPropertyChanged(nameof(HapticsHint));
    }

    public void RefreshEngineStatus() => OnPropertyChanged(nameof(EngineStatus));

    /// <summary>Polled by a UI timer (~30 Hz).</summary>
    public void UpdateMeters()
    {
        var levels = _engine.Levels;
        if (levels.Beat)
            _beatUntil = DateTime.UtcNow + BeatHold;
        bool changed = !ReferenceEquals(levels, _levels);
        _levels = levels;
        if (changed)
        {
            OnPropertyChanged(nameof(Spectrum));
            OnPropertyChanged(nameof(BassLevel));
            OnPropertyChanged(nameof(MidLevel));
            OnPropertyChanged(nameof(HighLevel));
            OnPropertyChanged(nameof(LoudnessLevel));
        }
        OnPropertyChanged(nameof(BeatVisible));
    }

    private void SetColor(string hex, Func<AudioReactiveSettings, RgbColor, AudioReactiveSettings> apply)
    {
        if (RgbColor.TryParse(hex, out var color))
            State.UpdateReactive(r => apply(r, color));
    }
}
