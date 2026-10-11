using System.Diagnostics;
using System.Numerics;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Motion;
using DualSenseTweaker.Core.State;
using Media3D = System.Windows.Media.Media3D;

namespace DualSenseTweaker.ViewModels;

/// <summary>Gyro tester (3D view, graphs, aim test), gyro → mouse settings and calibration.</summary>
public sealed class GyroViewModel : ObservableObject
{
    private const double GraphSeconds = 4;

    private static readonly string[] LiveProperties =
    [
        nameof(HasData), nameof(Orientation), nameof(Frame), nameof(History), nameof(AimState), nameof(AimStats),
        nameof(PitchText), nameof(RollText), nameof(YawText), nameof(GyroText), nameof(AccelText), nameof(GForceText),
        nameof(SampleRateText), nameof(IsStill), nameof(MotionStateText), nameof(MouseStateTitle), nameof(MouseMoving),
        nameof(IsCalibrating), nameof(CalibrationPercent), nameof(CalibrateLabel), nameof(CalibrationText),
        nameof(FactoryCalibrationText), nameof(BiasText),
    ];

    private readonly SettingsState _settings;
    private readonly PreferencesState _preferences;
    private readonly GyroService _service;
    private readonly AimTrainer _trainer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastUpdate;
    private ControllerInfo? _controller;
    private MotionSnapshot? _snapshot;
    private InputFrame _visualFrame = InputFrame.Neutral;
    private bool _previousRecenterButton;
    private string? _calibrationMessage;

    public GyroViewModel(SettingsState settings, PreferencesState preferences, GyroService service)
    {
        _settings = settings;
        _preferences = preferences;
        _service = service;
        PresetCommand = new RelayCommand(p => ApplyPreset(p as string));
        RecenterCommand = new RelayCommand(Recenter);
        ResetScoreCommand = new RelayCommand(() =>
        {
            _trainer.ResetScore();
            _trainer.Recenter();
            OnPropertyChanged(nameof(AimState));
            OnPropertyChanged(nameof(AimStats));
        });
        CalibrateCommand = new RelayCommand(ToggleCalibration, () => IsConnected);
        ForgetCalibrationCommand = new RelayCommand(() =>
        {
            _service.ResetCalibration();
            _calibrationMessage = L.Text("Uložená kalibrácia bola vymazaná. Gyroskop sa automaticky doladí, keď bude ovládač v pokoji.");
            OnPropertyChanged(nameof(CalibrationText));
        }, () => IsConnected);
    }

    private GyroSettings S => _settings.Current.Gyro;

    public RelayCommand PresetCommand { get; }
    public RelayCommand RecenterCommand { get; }
    public RelayCommand ResetScoreCommand { get; }
    public RelayCommand CalibrateCommand { get; }
    public RelayCommand ForgetCalibrationCommand { get; }

    // ── Gyro → mouse ─────────────────────────────────────────────────

    public IReadOnlyList<Option<GyroActivation>> ActivationOptions =>
    [
        new(GyroActivation.Always, L.Text("Vždy")),
        new(GyroActivation.HoldL2, L.Text("Pri mierení (drž L2)")),
        new(GyroActivation.HoldL1, L.Text("Drž L1")),
        new(GyroActivation.HoldR1, L.Text("Drž R1")),
        new(GyroActivation.TouchpadTouch, L.Text("Prst na dotykovom paneli")),
        new(GyroActivation.OffWhileL1, L.Text("Vždy, L1 pozastaví")),
    ];

    public IReadOnlyList<Option<GyroAxisMode>> AxisOptions =>
    [
        new(GyroAxisMode.PlayerSpace, L.Text("Podľa polohy")),
        new(GyroAxisMode.Yaw, L.Text("Otáčanie")),
        new(GyroAxisMode.Roll, L.Text("Nakláňanie")),
    ];

    public bool MouseEnabled
    {
        get => S.MouseEnabled;
        set => Update(g => g with { MouseEnabled = value });
    }

    public GyroActivation Activation
    {
        get => S.Activation;
        set => Update(g => g with { Activation = value });
    }

    public GyroAxisMode Axis
    {
        get => S.Axis;
        set => Update(g => g with { Axis = value });
    }

    public string AxisHint => S.Axis switch
    {
        GyroAxisMode.Yaw => L.Text("Otáčanie ovládača okolo jeho vlastnej zvislej osi – ako volant položený na stole."),
        GyroAxisMode.Roll => L.Text("Naklápanie doľava a doprava – ako volant pred sebou."),
        _ => L.Text("Odporúčané: otáčanie okolo skutočnej zvislej osi, nech držíš ovládač akokoľvek. Funguje rovnako naležato aj na výšku."),
    };

    public double Sensitivity
    {
        get => S.Sensitivity;
        set => Update(g => g with { Sensitivity = value });
    }

    public double VerticalPercent
    {
        get => S.VerticalPercent;
        set => Update(g => g with { VerticalPercent = (int)Math.Round(value) });
    }

    public bool InvertX
    {
        get => S.InvertX;
        set => Update(g => g with { InvertX = value });
    }

    public bool InvertY
    {
        get => S.InvertY;
        set => Update(g => g with { InvertY = value });
    }

    public double Acceleration
    {
        get => S.Acceleration;
        set => Update(g => g with { Acceleration = (int)Math.Round(value) });
    }

    public double Tightening
    {
        get => S.Tightening;
        set => Update(g => g with { Tightening = value });
    }

    public double Smoothing
    {
        get => S.Smoothing;
        set => Update(g => g with { Smoothing = value });
    }

    /// <summary>"Otočenie o 90° posunie kurzor o 1 080 px."</summary>
    public string SensitivityHint =>
        L.Format($"Otočenie o 90° = {S.Sensitivity * 90:N0} bodov (pri 1:1 rýchlosti myši vo Windows to sú pixely). Hra si body prepočíta vlastnou citlivosťou myši.");

    public bool AutoCalibration
    {
        get => _preferences.Current.Gyro.AutoCalibration;
        set => _preferences.Update(p => p with { Gyro = p.Gyro with { AutoCalibration = value } });
    }

    // ── Live ─────────────────────────────────────────────────────────

    public bool IsConnected => _controller is not null;
    public bool HasData => _snapshot is not null;

    public Media3D.Quaternion Orientation { get; private set; } = Media3D.Quaternion.Identity;

    /// <summary>Latest input report, shown on the 3D controller (buttons, sticks, triggers, touches).</summary>
    public InputFrame Frame => _visualFrame;

    public MotionPoint[] History { get; private set; } = [];

    public AimTestState AimState => _trainer.State;

    public string AimStats
    {
        get
        {
            var s = _trainer.State;
            int shots = s.Hits + s.Misses;
            return shots == 0
                ? L.Text("Namier na terč a stlač R2")
                : L.Format($"Zásahy: {s.Hits} · Presnosť: {100.0 * s.Hits / shots:0} % · Priemerný čas na terč: {s.AverageHitSeconds:0.00} s");
        }
    }

    public string PitchText => Angle(a => a.Pitch);
    public string RollText => Angle(a => -a.RollLeft);
    public string YawText => Angle(a => -a.YawLeft);

    public string GyroText => _snapshot is { } s
        ? $"{Clean(s.Reading.Gyro.X, 1),7:0.0}  {Clean(s.Reading.Gyro.Y, 1),7:0.0}  {Clean(s.Reading.Gyro.Z, 1),7:0.0}"
        : "—";

    public string AccelText => _snapshot is { } s
        ? $"{Clean(s.Reading.Accel.X, 2),6:0.00}  {Clean(s.Reading.Accel.Y, 2),6:0.00}  {Clean(s.Reading.Accel.Z, 2),6:0.00}"
        : "—";

    public string GForceText => _snapshot is { } s ? $"{s.Reading.Accel.Length():0.00} g" : "—";

    public string SampleRateText => _snapshot is { SampleRate: > 0 } s ? $"{s.SampleRate:0} Hz" : "—";

    public bool IsStill => _snapshot?.Reading.IsStill == true;

    public string MotionStateText => _snapshot is null
        ? IsConnected ? L.Text("Čakám na dáta…") : L.Text("Ovládač nie je pripojený")
        : _snapshot.Reading.IsStill ? L.Text("V pokoji") : L.Text("V pohybe");

    public string MouseStateTitle => !S.MouseEnabled ? L.Text("Myš pohybom je vypnutá")
        : !IsConnected ? L.Text("Čaká na ovládač")
        : _snapshot?.MouseMoving == true ? L.Text("Kurzor reaguje na pohyb")
        : ActivationWaitText;

    public string MouseStateSubtitle => S.MouseEnabled
        ? L.Text("Ovládanie myši vypneš kombináciou PS + R1. Počas držania tlačidla PS je pohyb kurzora pozastavený.")
        : L.Text("Zapni ju prepínačom alebo kombináciou PS + R1. Test mierenia funguje aj pri vypnutom ovládaní myši.");

    public bool MouseMoving => _snapshot?.MouseMoving == true;

    private string ActivationWaitText => S.Activation switch
    {
        GyroActivation.HoldL2 => L.Text("Čaká na L2"),
        GyroActivation.HoldL1 => L.Text("Čaká na L1"),
        GyroActivation.HoldR1 => L.Text("Čaká na R1"),
        GyroActivation.TouchpadTouch => L.Text("Čaká na dotyk panela"),
        GyroActivation.OffWhileL1 => L.Text("Pozastavené (L1)"),
        _ => L.Text("Pozastavené (PS)"),
    };

    // ── Calibration ──────────────────────────────────────────────────

    public bool IsCalibrating => _snapshot?.Calibration.Phase == CalibrationPhase.Running;

    public double CalibrationPercent => (_snapshot?.Calibration.Fraction ?? 0) * 100;

    public string CalibrateLabel => IsCalibrating ? L.Text("Zrušiť") : L.Text("Kalibrovať");

    public string CalibrationText
    {
        get
        {
            if (_snapshot is { Calibration.Phase: CalibrationPhase.Running } running)
                return running.Calibration.Moving
                    ? L.Text("Ovládač sa hýbe – polož ho na rovný stôl a nedotýkaj sa ho.")
                    : L.Format($"Meriam posun… {running.Calibration.Fraction * 100:0} %");
            return _calibrationMessage is not null ? L.Text(_calibrationMessage) : (IsConnected
                ? L.Text("Polož ovládač na stôl a stlač Kalibrovať. Trvá to 2 sekundy.")
                : L.Text("Pripoj ovládač."));
        }
    }

    public string FactoryCalibrationText => _snapshot is null ? "—"
        : _snapshot.FactoryCalibration ? L.Text("načítaná z ovládača") : L.Text("predvolená (ovládač ju neposlal)");

    public string BiasText => _snapshot is { } s
        ? $"{Clean(s.Bias.X, 2):0.00} / {Clean(s.Bias.Y, 2):0.00} / {Clean(s.Bias.Z, 2):0.00} °/s"
        : "—";

    public void SetConnection(ControllerInfo? controller)
    {
        if (Equals(controller, _controller))
            return;
        _controller = controller;
        _calibrationMessage = null;
        _snapshot = null;
        _visualFrame = InputFrame.Neutral;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Polled by the UI timer while the gyro section is visible.</summary>
    public void UpdateLive()
    {
        var now = _clock.Elapsed;
        double seconds = (now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;

        var snapshot = _service.Latest;
        var (movement, shots) = _service.TakePreview();
        bool wasCalibrating = IsCalibrating;
        _snapshot = snapshot;
        _visualFrame = snapshot is null ? InputFrame.Neutral : _service.TakeVisualInput();

        if (snapshot is not null)
        {
            var q = snapshot.Reading.Orientation;
            Orientation = new Media3D.Quaternion(q.X, q.Y, q.Z, q.W);

            // R3 (right stick click) re-centers the 3D view and the crosshair.
            bool recenter = snapshot.Frame.IsPressed(ControllerButtons.R3);
            if (recenter && !_previousRecenterButton)
                Recenter();
            _previousRecenterButton = recenter;
        }
        if (wasCalibrating && !IsCalibrating && snapshot is { Calibration.Phase: var phase })
        {
            _calibrationMessage = phase switch
            {
                CalibrationPhase.Done => L.Text("Hotovo – posun je uložený pre tento ovládač."),
                CalibrationPhase.Failed => L.Text("Kalibrácia zlyhala: namerané hodnoty sú príliš veľké. Skús to znova na stole."),
                _ => null,
            };
        }

        _trainer.Update(movement, shots, seconds);
        History = _service.History(GraphSeconds);
        foreach (var name in LiveProperties)
            OnPropertyChanged(name);
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void Recenter()
    {
        _service.Recenter();
        _trainer.Recenter();
    }

    private void ToggleCalibration()
    {
        if (IsCalibrating)
        {
            _service.CancelCalibration();
            _calibrationMessage = L.Text("Kalibrácia zrušená.");
        }
        else
        {
            _calibrationMessage = null;
            _service.StartCalibration();
        }
        OnPropertyChanged(nameof(CalibrationText));
    }

    /// <summary>Ready-made feels; everything stays adjustable afterwards.</summary>
    private void ApplyPreset(string? preset) => Update(g => preset switch
    {
        // Aim down sights: only while L2 is held, precise, no acceleration.
        "aim" => g with { Activation = GyroActivation.HoldL2, Axis = GyroAxisMode.PlayerSpace, Sensitivity = 10, VerticalPercent = 100, Acceleration = 0, Tightening = 0.8, Smoothing = 2 },
        // Always-on gyro aim with acceleration for quick 180° turns.
        "fast" => g with { Activation = GyroActivation.OffWhileL1, Axis = GyroAxisMode.PlayerSpace, Sensitivity = 14, VerticalPercent = 90, Acceleration = 60, Tightening = 1, Smoothing = 3 },
        // Air mouse for the desktop / presentations: only with a finger on the touchpad, calm.
        "desktop" => g with { Activation = GyroActivation.TouchpadTouch, Axis = GyroAxisMode.PlayerSpace, Sensitivity = 20, VerticalPercent = 100, Acceleration = 40, Tightening = 1.5, Smoothing = 6 },
        _ => new GyroSettings { MouseEnabled = g.MouseEnabled },
    });

    private string Angle(Func<(float Pitch, float RollLeft, float YawLeft), float> pick)
    {
        if (_snapshot is null)
            return "—";
        var angles = OrientationFilter.Angles(_snapshot.Reading.Orientation);
        return $"{Clean(pick(angles), 0):+0;−0;0}°";
    }

    /// <summary>Rounds and turns −0 into 0, so idle readouts do not flicker between −0,0 and 0,0.</summary>
    private static double Clean(float value, int digits) => Math.Round(value, digits) + 0.0;

    private void Update(Func<GyroSettings, GyroSettings> change) =>
        _settings.Update(s => s with { Gyro = change(s.Gyro) });
}
