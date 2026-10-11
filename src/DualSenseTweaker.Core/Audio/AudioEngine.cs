using System.Diagnostics;
using System.Runtime.InteropServices;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualSenseTweaker.Core.Audio;

public enum HapticsState
{
    Off,
    /// <summary>Audio plays on the controller's haptic channels over USB.</summary>
    Active,
    /// <summary>Connected over Bluetooth: no audio device, bass drives rumble instead.</summary>
    BluetoothFallback,
    NoController,
    /// <summary>Over USB, but the controller's audio device was not found or failed; rumble is used instead.</summary>
    Unavailable,
}

public sealed record AudioEngineStatus(bool Capturing, string? CaptureDevice, HapticsState Haptics, string? HapticsDevice, string Message)
{
    public static AudioEngineStatus Idle { get; } = new(false, null, HapticsState.Off, null, L.Text("Reakcia na zvuk je vypnutá."));
}

/// <summary>
/// System audio → controller. Captures the default output device via WASAPI loopback, analyzes
/// it (bands, beat) into <see cref="LiveOutput"/> for lightbar and rumble, and optionally plays
/// the low-passed signal on the DualSense's USB haptic channels. Devices are (re)opened on its
/// own thread whenever settings, the controller connection or the Windows default device change.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int CaptureBufferMs = 20;
    private const int HapticsLatencyMs = 30;
    private static readonly TimeSpan SilenceAfter = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SilenceTick = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    /// <summary>Loopback packets arrive every ~5 ms; ~125 updates/s is plenty for light and rumble and spares Bluetooth.</summary>
    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromMilliseconds(8);

    private readonly SettingsState _settings;
    private readonly LiveOutput _live;
    private readonly DualSenseManager _manager;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly Lock _analysisGate = new();
    private readonly AudioAnalyzer _analyzer = new();
    private readonly ReactiveMapper _mapper = new();
    private readonly HapticsBuffer _hapticsBuffer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private WasapiRecorder? _capture;
    private WaveFormat? _captureFormat;
    private string? _captureDevice;
    private HapticsOutput? _haptics;
    private volatile HapticsState _hapticsState;
    private string? _hapticsError;
    private Timer? _silenceTimer;
    private IDisposable? _timerResolution;
    private float[] _floatBuffer = [];
    private float[] _mono = [];
    private TimeSpan _lastProcessed;
    private TimeSpan _lastPublished;
    private double _unpublishedSeconds;
    private TimeSpan _nextCaptureAttempt;
    private TimeSpan _nextHapticsAttempt;
    private volatile bool _restartCapture;
    private volatile bool _disposed;
    private AudioFeatures _levels = AudioFeatures.Silent;
    private AudioEngineStatus _status = AudioEngineStatus.Idle;

    public AudioEngine(SettingsState settings, LiveOutput live, DualSenseManager manager)
    {
        _settings = settings;
        _live = live;
        _manager = manager;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio engine" };
    }

    public event EventHandler? StatusChanged;

    public AudioEngineStatus Status => Volatile.Read(ref _status);

    /// <summary>Latest analysis, for level meters.</summary>
    public AudioFeatures Levels => Volatile.Read(ref _levels);

    public void Start()
    {
        _settings.Changed += OnInputsChanged;
        _manager.ConnectionChanged += OnInputsChanged;
        _thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.Changed -= OnInputsChanged;
        _manager.ConnectionChanged -= OnInputsChanged;
        _signal.Set();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(3));
    }

    private void OnInputsChanged(object? sender, EventArgs e) => _signal.Set();

    private void Run()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render)
                {
                    _restartCapture = true;
                    _signal.Set();
                }
            };
            _notifications.DeviceAdded += (_, _) => _signal.Set();
            _notifications.DeviceRemoved += (_, _) => _signal.Set();
            _notifications.DeviceStateChanged += (_, _) => _signal.Set();
        }
        catch (COMException ex)
        {
            Log.Error(L.Text("Audio subsystém Windows nie je dostupný"), ex);
            SetStatus(AudioEngineStatus.Idle with { Message = L.Text("Audio subsystém Windows nie je dostupný.") });
            return;
        }

        while (!_disposed)
        {
            try
            {
                Reconcile();
            }
            catch (Exception ex)
            {
                Log.Error("Chyba v audio engine", ex);
            }
            _signal.WaitOne(TimeSpan.FromSeconds(1));
        }

        StopHaptics();
        StopCapture();
        _live.Replace(LiveOverlay.None);
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    /// <summary>Brings capture and haptics output in line with settings and the controller connection.</summary>
    private void Reconcile()
    {
        var settings = _settings.Current;
        var connection = _manager.Connection.Controller?.Connection;
        bool wantCapture = settings.Reactive.Enabled || settings.Haptics.Enabled;

        if (!wantCapture)
        {
            StopHaptics();
            StopCapture();
            _hapticsState = HapticsState.Off;
            _live.Replace(LiveOverlay.None);
            Volatile.Write(ref _levels, AudioFeatures.Silent);
            SetStatus(AudioEngineStatus.Idle);
            return;
        }

        if (_restartCapture || _capture is null)
        {
            _restartCapture = false;
            StopCapture();
            if (_clock.Elapsed >= _nextCaptureAttempt)
                StartCapture();
        }

        _hapticsState = settings.Haptics.Enabled switch
        {
            false => HapticsState.Off,
            true when connection is null => HapticsState.NoController,
            true when connection == ConnectionType.Bluetooth => HapticsState.BluetoothFallback,
            _ => EnsureHaptics(),
        };
        if (_hapticsState != HapticsState.Active)
            StopHaptics();

        SetStatus(BuildStatus());
    }

    private HapticsState EnsureHaptics()
    {
        if (_haptics is { Faulted: false })
            return HapticsState.Active;
        StopHaptics();
        if (_clock.Elapsed < _nextHapticsAttempt)
            return HapticsState.Unavailable;

        try
        {
            using var device = HapticsOutput.FindControllerEndpoint(_enumerator!);
            if (device is null)
            {
                _hapticsError = L.Text("Zvukové zariadenie ovládača sa nenašlo.");
                _nextHapticsAttempt = _clock.Elapsed + RetryDelay;
                return HapticsState.Unavailable;
            }
            Volatile.Write(ref _haptics, HapticsOutput.Start(device, _hapticsBuffer, HapticsLatencyMs, () => _signal.Set()));
            _hapticsError = null;
            Log.Info($"Haptika: {_haptics.DeviceName}");
            return HapticsState.Active;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _hapticsError = ex.Message;
            _nextHapticsAttempt = _clock.Elapsed + RetryDelay;
            Log.Warn(L.Format($"Haptiku sa nepodarilo spustiť: {ex.Message}"));
            return HapticsState.Unavailable;
        }
    }

    private void StartCapture()
    {
        try
        {
            using var device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            // IAudioClient3 low-latency mode is not available for loopback, so a short standard buffer it is.
            var capture = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithBufferLength(CaptureBufferMs)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
            capture.DataAvailable += (buffer, flags, _, _) =>
                OnDataAvailable(capture, buffer, flags.HasFlag(AudioClientBufferFlags.Silent));
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    Log.Warn(L.Format($"Zachytávanie zvuku sa zastavilo: {e.Exception.Message}"));
                _restartCapture = true;
                _signal.Set();
            };

            _captureDevice = device.FriendlyName;
            _lastProcessed = _clock.Elapsed;
            capture.StartRecording();
            var format = capture.WaveFormat;
            if (format.BitsPerSample is not (32 or 16))
            {
                capture.Dispose();
                throw new InvalidOperationException(L.Format($"Nepodporovaný formát výstupu ({format.BitsPerSample}-bit)."));
            }
            _captureFormat = format;
            _capture = capture;
            // 1 ms timer resolution only while capturing, so the 20 ms silence tick is not stretched to ~31 ms.
            _timerResolution = TimerResolution.Request1Ms();
            _silenceTimer = new Timer(_ => OnSilenceTick(), null, SilenceTick, SilenceTick);
            Log.Info($"Loopback: {_captureDevice} ({format.SampleRate} Hz, {format.Channels} kan., {format.BitsPerSample}-bit)");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _captureDevice = null;
            _nextCaptureAttempt = _clock.Elapsed + RetryDelay;
            Log.Warn(L.Format($"Zachytávanie zvuku sa nepodarilo spustiť: {ex.Message}"));
        }
    }

    private void StopCapture()
    {
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        _timerResolution?.Dispose();
        _timerResolution = null;
        var capture = _capture;
        _capture = null;
        _captureFormat = null;
        if (capture is null)
            return;
        try
        {
            capture.StopRecording();
        }
        catch (COMException)
        {
            // Device already gone.
        }
        capture.Dispose();
    }

    private void StopHaptics()
    {
        var haptics = Interlocked.Exchange(ref _haptics, null);
        haptics?.Dispose();
    }

    /// <summary>Runs on the capture thread for every WASAPI packet (~every 10 ms while audio plays).</summary>
    private void OnDataAvailable(WasapiRecorder capture, ReadOnlySpan<byte> buffer, bool silent)
    {
        var format = capture.WaveFormat;
        if (buffer.IsEmpty || !ReferenceEquals(capture, _capture) && _capture is not null)
            return;

        int channels = format.Channels;
        ReadOnlySpan<float> samples = ToFloat(buffer, format.BitsPerSample, silent);
        int frames = samples.Length / channels;
        if (_mono.Length < frames)
            _mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float l = samples[i * channels];
            float r = channels > 1 ? samples[i * channels + 1] : l;
            _mono[i] = (l + r) * 0.5f;
        }

        var settings = _settings.Current;
        if (Volatile.Read(ref _haptics) is not null)
            _hapticsBuffer.Write(samples, channels, format.SampleRate, settings.Haptics.Strength, settings.Haptics.CutoffHz);

        lock (_analysisGate)
        {
            var now = _clock.Elapsed;
            var features = _analyzer.Process(_mono.AsSpan(0, frames), format.SampleRate, settings.Reactive);
            Publish(features, settings, (now - _lastProcessed).TotalSeconds);
            _lastProcessed = now;
        }
    }

    /// <summary>Windows delivers no loopback packets while nothing plays; decay the levels instead of freezing them.</summary>
    private void OnSilenceTick()
    {
        var format = _captureFormat;
        if (format is null || _disposed)
            return;
        lock (_analysisGate)
        {
            var now = _clock.Elapsed;
            var idle = now - _lastProcessed;
            if (idle < SilenceAfter)
                return;
            var settings = _settings.Current;
            var features = _analyzer.ProcessSilence(idle.TotalSeconds, format.SampleRate, settings.Reactive);
            Publish(features, settings, idle.TotalSeconds);
            _lastProcessed = now;
        }
    }

    private void Publish(AudioFeatures features, ProfileSettings settings, double dt)
    {
        Volatile.Write(ref _levels, features);
        _unpublishedSeconds += dt;
        var now = _clock.Elapsed;
        if (!features.Beat && now - _lastPublished < MinPublishInterval)
            return;
        dt = _unpublishedSeconds;
        _unpublishedSeconds = 0;
        _lastPublished = now;

        bool hapticsActive = _hapticsState == HapticsState.Active && Volatile.Read(ref _haptics) is not null;
        // Without audio haptics (Bluetooth or no audio device) "Audio → haptika" falls back to bass → rumble.
        byte reactiveRumble = settings.Reactive.Enabled && settings.Reactive.BassToRumble ? settings.Reactive.RumbleMax : (byte)0;
        byte fallbackRumble = settings.Haptics.Enabled && !hapticsActive
            ? (byte)Math.Clamp(settings.Haptics.Strength * 170, 0, 255)
            : (byte)0;
        byte rumbleMax = hapticsActive ? (byte)0 : Math.Max(reactiveRumble, fallbackRumble);

        var overlay = _mapper.Map(features, settings.Reactive, settings.Output,
            lightbarReactive: settings.Reactive.Enabled, rumbleMax, hapticsActive, dt);
        _live.Replace(overlay);
    }

    /// <summary>Packets flagged silent may contain garbage and must be read as zeros.</summary>
    private ReadOnlySpan<float> ToFloat(ReadOnlySpan<byte> bytes, int bitsPerSample, bool silent)
    {
        int count = bytes.Length / (bitsPerSample / 8);
        if (bitsPerSample == 32 && !silent)
            return MemoryMarshal.Cast<byte, float>(bytes);

        if (_floatBuffer.Length < count)
            _floatBuffer = new float[count];
        var target = _floatBuffer.AsSpan(0, count);
        if (silent)
        {
            target.Clear();
            return target;
        }
        var pcm = MemoryMarshal.Cast<byte, short>(bytes);
        for (int i = 0; i < pcm.Length; i++)
            target[i] = pcm[i] / 32768f;
        return target;
    }

    private AudioEngineStatus BuildStatus()
    {
        bool capturing = _capture is not null;
        string capture = capturing ? L.Format($"Zachytávam: {_captureDevice}") : L.Text("Zachytávanie zvuku nebeží.");
        string haptics = _hapticsState switch
        {
            HapticsState.Active => $"Haptika: {_haptics?.DeviceName}",
            HapticsState.BluetoothFallback => L.Text("HD haptika cez Bluetooth nie je dostupná. Používam vibrácie podľa basov."),
            HapticsState.NoController => L.Text("Haptika čaká na ovládač."),
            HapticsState.Unavailable => L.Format($"Haptika nedostupná ({_hapticsError}) – používam klasické vibrácie z basov."),
            _ => "",
        };
        return new AudioEngineStatus(capturing, _captureDevice, _hapticsState, _haptics?.DeviceName,
            haptics.Length == 0 ? capture : $"{capture} · {haptics}");
    }

    private void SetStatus(AudioEngineStatus status)
    {
        if (status == Volatile.Read(ref _status))
            return;
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
