using System.Windows.Media;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.Core.Device;

namespace DualSenseTweaker.ViewModels;

/// <summary>Lightbar, LEDs, manual rumble, motor attenuation and controller audio volumes.</summary>
public sealed class OutputViewModel : SettingsViewModel
{
    private readonly Action<string>? _report;

    public OutputViewModel(SettingsState state, Action<string>? report = null) : base(state)
    {
        _report = report;
        StopRumbleCommand = new RelayCommand(
            () => State.UpdateOutput(o => o with { RumbleHeavy = 0, RumbleSoft = 0 }),
            () => RumbleHeavy > 0 || RumbleSoft > 0);
    }

    public RelayCommand StopRumbleCommand { get; }

    private OutputSnapshot Output => Current.Output;

    // ── Lightbar ─────────────────────────────────────────────────────

    public IReadOnlyList<ColorSwatch> Swatches => Palette.Swatches;

    public IReadOnlyList<Option<LightbarEffectMode>> LightbarEffectOptions =>
    [
        new(LightbarEffectMode.Static, L.Text("Stála farba")),
        new(LightbarEffectMode.Rainbow, L.Text("Dúha")),
    ];

    public LightbarEffectMode LightbarEffect
    {
        get => Output.LightbarEffect;
        set
        {
            if (value != Output.LightbarEffect)
                SetLightbar(o => o, value);
        }
    }

    public bool IsRainbow => Output.LightbarEffect == LightbarEffectMode.Rainbow;

    public double RainbowCycleSeconds
    {
        get => Output.RainbowCycleSeconds;
        set => State.UpdateOutput(o => o with { RainbowCycleSeconds = (int)Math.Clamp(Math.Round(value), 1, 60) });
    }

    public string LightbarHex
    {
        get => Output.LightbarColor.ToHex();
        set
        {
            if (RgbColor.TryParse(value, out var color))
                SetLightbar(o => o with { LightbarColor = color });
        }
    }

    /// <summary>The lightbar color for the HUE picker.</summary>
    public Color LightbarColor
    {
        get => Color.FromRgb(Output.LightbarColor.R, Output.LightbarColor.G, Output.LightbarColor.B);
        set => SetLightbar(o => o with { LightbarColor = new RgbColor(value.R, value.G, value.B) });
    }

    public double LightbarRed
    {
        get => Output.LightbarColor.R;
        set => SetLightbar(o => o with { LightbarColor = o.LightbarColor with { R = ToByte(value) } });
    }

    public double LightbarGreen
    {
        get => Output.LightbarColor.G;
        set => SetLightbar(o => o with { LightbarColor = o.LightbarColor with { G = ToByte(value) } });
    }

    public double LightbarBlue
    {
        get => Output.LightbarColor.B;
        set => SetLightbar(o => o with { LightbarColor = o.LightbarColor with { B = ToByte(value) } });
    }

    public double LightbarBrightness
    {
        get => Output.LightbarBrightness;
        set
        {
            if (IsRainbow)
                State.UpdateOutput(o => o with { LightbarBrightness = ToByte(value) });
            else
                SetLightbar(o => o with { LightbarBrightness = ToByte(value) });
        }
    }

    /// <summary>The static color as the controller shows it (brightness applied).</summary>
    public Brush LightbarPreview => Palette.ToBrush(Output.EffectiveLightbar);

    /// <summary>
    /// Selecting a manual color or effect takes over the lightbar from audio.
    /// Audio-reactive light ("color from spectrum", "brightness by loudness", beat flashes) would keep
    /// overriding or blinking it, so the lightbar part of the audio mapping is switched off; bass →
    /// rumble keeps working.
    /// </summary>
    private void SetLightbar(Func<OutputSnapshot, OutputSnapshot> change, LightbarEffectMode effect = LightbarEffectMode.Static)
    {
        bool takeOver = Current.Reactive is { Enabled: true } r && (r.Lightbar != ReactiveLightbarMode.Static || r.BeatFlash);
        State.Update(s => s with
        {
            Output = change(s.Output) with { LightbarEffect = effect },
            Reactive = takeOver ? s.Reactive with { Lightbar = ReactiveLightbarMode.Static, BeatFlash = false } : s.Reactive,
        });
        if (takeOver)
            _report?.Invoke(effect == LightbarEffectMode.Rainbow
                ? L.Text("Svetelný pás teraz zobrazuje dúhu. Zvukové vibrácie pokračujú. Reakciu svetla na zvuk zapneš v sekcii Reakcia na zvuk.")
                : L.Text("Svetelný pás používa uloženú farbu. Zvuk jeho farbu ani jas nemení. Túto reakciu zapneš v sekcii Reakcia na zvuk → Prepojenie so zvukom."));
    }

    private RgbColor? _liveLightbar;
    private TimeSpan _effectTime;

    /// <summary>Current color, including rainbow or audio-reactive light and beat flashes.</summary>
    public Brush LiveLightbar => Palette.ToBrush(LightbarEffects.Resolve(Output, _liveLightbar, _effectTime));

    public bool IsLightbarReactive => !IsRainbow && _liveLightbar is not null;

    /// <summary>Polled by a UI timer; shares the HID animation clock without persisting animated colors.</summary>
    public void UpdateLive(RgbColor? lightbar, TimeSpan? effectTime = null)
    {
        var before = LightbarEffects.Resolve(Output, _liveLightbar, _effectTime);
        bool wasReactive = IsLightbarReactive;
        _liveLightbar = lightbar;
        _effectTime = effectTime ?? LightbarEffects.CurrentTime;
        if (before != LightbarEffects.Resolve(Output, _liveLightbar, _effectTime))
            OnPropertyChanged(nameof(LiveLightbar));
        if (wasReactive != IsLightbarReactive)
            OnPropertyChanged(nameof(IsLightbarReactive));
    }

    // ── LEDs ─────────────────────────────────────────────────────────

    public IReadOnlyList<Option<PlayerLedMode>> PlayerLedOptions =>
    [
        new(PlayerLedMode.Off, L.Text("Vyp.")),
        new(PlayerLedMode.Player1, "1"),
        new(PlayerLedMode.Player2, "2"),
        new(PlayerLedMode.Player3, "3"),
        new(PlayerLedMode.Player4, "4"),
        new(PlayerLedMode.All, L.Text("Všetky")),
    ];

    public IReadOnlyList<Option<LedBrightness>> LedBrightnessOptions =>
    [
        new(LedBrightness.High, L.Text("Vysoký")),
        new(LedBrightness.Medium, L.Text("Stredný")),
        new(LedBrightness.Low, L.Text("Nízky")),
    ];

    public IReadOnlyList<Option<MicLedMode>> MicLedOptions =>
    [
        new(MicLedMode.Off, L.Text("Vyp.")),
        new(MicLedMode.On, L.Text("Zap.")),
        new(MicLedMode.Blink, L.Text("Bliká")),
    ];

    public PlayerLedMode PlayerLeds
    {
        get => Output.PlayerLeds;
        set => State.UpdateOutput(o => o with { PlayerLeds = value });
    }

    /// <summary>The 5 player LEDs left to right, for the preview strip.</summary>
    public IReadOnlyList<bool> PlayerLedPreview
    {
        get
        {
            byte mask = Output.PlayerLeds.ToLedMask();
            return [.. Enumerable.Range(0, 5).Select(bit => (mask & (1 << bit)) != 0)];
        }
    }

    public LedBrightness LedBrightness
    {
        get => Output.LedBrightness;
        set => State.UpdateOutput(o => o with { LedBrightness = value });
    }

    public MicLedMode MicLed
    {
        get => Output.MicLed;
        set => State.UpdateOutput(o => o with { MicLed = value });
    }

    // ── Rumble ───────────────────────────────────────────────────────

    public double RumbleHeavy
    {
        get => Output.RumbleHeavy;
        set => State.UpdateOutput(o => o with { RumbleHeavy = ToByte(value) });
    }

    public double RumbleSoft
    {
        get => Output.RumbleSoft;
        set => State.UpdateOutput(o => o with { RumbleSoft = ToByte(value) });
    }

    public double RumbleAttenuation
    {
        get => Output.RumbleAttenuation;
        set => State.UpdateOutput(o => o with { RumbleAttenuation = ToByte(value) });
    }

    public double TriggerAttenuation
    {
        get => Output.TriggerAttenuation;
        set => State.UpdateOutput(o => o with { TriggerAttenuation = ToByte(value) });
    }

    // ── Controller audio ─────────────────────────────────────────────

    public IReadOnlyList<Option<AudioOutputRoute>> AudioRouteOptions =>
    [
        new(AudioOutputRoute.Auto, L.Text("Automaticky")),
        new(AudioOutputRoute.Headphones, L.Text("Slúchadlá")),
        new(AudioOutputRoute.Speaker, L.Text("Reproduktor")),
        new(AudioOutputRoute.HeadphonesAndSpeaker, L.Text("Oba výstupy")),
    ];

    public double MaxSpeakerVolume => OutputSnapshot.MaxSpeakerVolume;
    public double MaxHeadphoneVolume => OutputSnapshot.MaxHeadphoneVolume;

    public bool AudioControlEnabled
    {
        get => Output.AudioControlEnabled;
        set => State.UpdateOutput(o => o with { AudioControlEnabled = value });
    }

    public AudioOutputRoute AudioRoute
    {
        get => Output.AudioRoute;
        set => State.UpdateOutput(o => o with { AudioRoute = value });
    }

    public double SpeakerVolume
    {
        get => Output.SpeakerVolume;
        set => State.UpdateOutput(o => o with { SpeakerVolume = ToByte(value) });
    }

    public double HeadphoneVolume
    {
        get => Output.HeadphoneVolume;
        set => State.UpdateOutput(o => o with { HeadphoneVolume = ToByte(value) });
    }
}
