using System.Diagnostics;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;
using HidSharp;

namespace DualSenseTweaker.Core.Device;

/// <summary>Where the device writer takes its values from.</summary>
public sealed record OutputSources(SettingsState Settings, LiveOutput Live, RefreshState Refresh);

/// <summary>
/// One open DualSense connection. Initializes the controller, reads input reports on one thread
/// and writes output reports on another. This is the only class that talks HID; what to send
/// always comes from <see cref="OutputSources"/>.
/// </summary>
public sealed class DualSenseDevice : IDisposable
{
    private static readonly TimeSpan MinSendInterval = TimeSpan.FromMilliseconds(4); // ≤ 250 reports/s
    private static readonly TimeSpan InputSilenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EnhancedModeRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ThreadStopTimeout = TimeSpan.FromSeconds(2);
    private const int ReadTimeoutMs = 1000;
    private const int WriteTimeoutMs = 1000;
    private const int MaxConsecutiveWriteTimeouts = 5;

    private readonly HidStream _stream;
    private readonly OutputSources _sources;
    private readonly LightbarInitialization _lightbarInitialization;
    private readonly int _maxInputLength;
    private readonly int _maxOutputLength;
    private readonly int _maxFeatureLength;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _writeGate = new();
    private readonly Thread _reader;
    private readonly Thread _writer;
    private volatile bool _stopping;
    private volatile bool _factoryReset;
    private string? _failureReason;
    private int _disposed;
    private int _forcedGroups;
    private ControllerStatus? _status;
    private long _inputReports;
    private long _outputReports;
    private int _sequence;
    private int _writeTimeouts;

    private DualSenseDevice(ControllerInfo info, HidStream stream, OutputSources sources, FirmwareInfo? firmware,
        MotionCalibration calibration, int maxInputLength, int maxOutputLength, int maxFeatureLength)
    {
        Info = info;
        Firmware = firmware;
        Calibration = calibration;
        UsesVibrationV2 = info.IsEdge || firmware?.UpdateVersion >= DualSenseProtocol.VibrationV2MinUpdateVersion;
        _stream = stream;
        _sources = sources;
        _lightbarInitialization = new LightbarInitialization(info.Connection);
        _maxInputLength = maxInputLength;
        _maxOutputLength = maxOutputLength;
        _maxFeatureLength = maxFeatureLength;
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "DualSense input" };
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "DualSense output", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Raised on the input thread when battery or jack state changes.</summary>
    public event EventHandler<ControllerStatus>? StatusChanged;

    /// <summary>Raised once, on an I/O thread, when the connection is lost. The owner must dispose the device.</summary>
    public event EventHandler? Failed;

    /// <summary>Raised on the input thread when the PS button was pressed 5× in quick succession.</summary>
    public event EventHandler? PsButtonSequence;

    /// <summary>Every full input report (on the input thread, ~250×/s): buttons and touchpad, with the receive time.</summary>
    public event Action<InputFrame, TimeSpan>? InputReceived;

    public ControllerInfo Info { get; }
    public FirmwareInfo? Firmware { get; }
    public MotionCalibration Calibration { get; }
    public bool UsesVibrationV2 { get; }
    public ControllerStatus? Status => Volatile.Read(ref _status);
    public bool HasFailed => Volatile.Read(ref _failureReason) is not null;
    public string? FailureReason => Volatile.Read(ref _failureReason);
    public DeviceStatistics Statistics => new(Interlocked.Read(ref _inputReports), Interlocked.Read(ref _outputReports));

    /// <exception cref="IOException">The controller could not be opened or initialized.</exception>
    internal static DualSenseDevice Open(ControllerInfo info, HidDevice hid, OutputSources sources)
    {
        int maxInput = hid.GetMaxInputReportLength();
        int maxOutput = hid.GetMaxOutputReportLength();
        int maxFeature = hid.GetMaxFeatureReportLength();
        int reportSize = info.Connection == ConnectionType.Bluetooth
            ? DualSenseProtocol.OutputReportBtSize
            : DualSenseProtocol.OutputReportUsbSize;
        if (maxOutput < reportSize)
            throw new IOException(L.Format($"Neočakávaná dĺžka output reportu ({maxOutput} B, potrebných {reportSize} B)."));

        if (!hid.TryOpen(out HidStream stream))
            throw new IOException(L.Text("Zariadenie sa nepodarilo otvoriť."));
        try
        {
            stream.ReadTimeout = ReadTimeoutMs;
            stream.WriteTimeout = WriteTimeoutMs;

            // Reading the calibration report also switches a Bluetooth controller to full input reports.
            var calibrationReport = FeatureReports.TryGet(stream, maxFeature,
                DualSenseProtocol.FeatureCalibration, DualSenseProtocol.FeatureCalibrationSize);
            if (info.Connection == ConnectionType.Bluetooth && calibrationReport is null)
                Log.Warn(L.Text("Rozšírený BT režim sa nepodarilo zapnúť, skúsim znova po prvom vstupnom reporte."));
            var calibration = calibrationReport is null ? null : MotionCalibration.Parse(calibrationReport);
            if (calibrationReport is not null && calibration is null)
                Log.Warn(L.Text("Kalibrácia gyroskopu z ovládača je neplatná, použijem predvolené hodnoty."));

            var firmwareReport = FeatureReports.TryGet(stream, maxFeature,
                DualSenseProtocol.FeatureFirmwareInfo, DualSenseProtocol.FeatureFirmwareInfoSize);
            var firmware = firmwareReport is null ? null : FirmwareInfo.Parse(firmwareReport);

            var device = new DualSenseDevice(info, stream, sources, firmware, calibration ?? MotionCalibration.Default,
                maxInput, maxOutput, maxFeature);
            Log.Info(L.Format($"Pripojené {info.DisplayName}: in={maxInput} B, out={maxOutput} B, feature={maxFeature} B, ") +
                     $"FW={firmware?.FirmwareText ?? "?"}, feature verzia={firmware?.UpdateVersionText ?? "?"}, " +
                     L.Format($"rumble={(device.UsesVibrationV2 ? "v2" : "v1")}, IMU kalibrácia={(calibration is null ? L.Text("predvolená") : L.Text("z ovládača"))}"));
            device.Start();
            return device;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Re-sends the given groups with the next report even if nothing changed.</summary>
    public void RequestResend(OutputGroups groups)
    {
        Interlocked.Or(ref _forcedGroups, (int)groups);
        Wake();
    }

    /// <summary>Closes the connection leaving the controller like new (default lightbar, LEDs off, triggers off).</summary>
    public void ResetToFactoryAndClose()
    {
        _factoryReset = true;
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stopping = true;
        Unsubscribe();
        _wake.Set();
        bool writerStopped = Join(_writer);
        if (writerStopped && !HasFailed)
            SendFinalReport();

        _stream.Dispose(); // also unblocks a pending Read
        bool readerStopped = Join(_reader);
        if (writerStopped && readerStopped)
            _wake.Dispose();
    }

    /// <summary>
    /// Reading the calibration feature report switches a Bluetooth DualSense from the reduced
    /// 0x01 input report to the full 0x31 report.
    /// </summary>
    private static bool RequestEnhancedMode(HidStream stream, int maxFeatureLength) =>
        FeatureReports.TryGet(stream, maxFeatureLength,
            DualSenseProtocol.FeatureCalibration, DualSenseProtocol.FeatureCalibrationSize) is not null;

    private static bool Join(Thread thread) =>
        thread != Thread.CurrentThread && (!thread.IsAlive || thread.Join(ThreadStopTimeout));

    private void Start()
    {
        _sources.Settings.Changed += OnSourceChanged;
        _sources.Live.Changed += OnSourceChanged;
        _sources.Refresh.Changed += OnSourceChanged;
        _reader.Start();
        _writer.Start();
    }

    private void Unsubscribe()
    {
        _sources.Settings.Changed -= OnSourceChanged;
        _sources.Live.Changed -= OnSourceChanged;
        _sources.Refresh.Changed -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Wake();

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // A change raced with Dispose; nothing left to wake.
        }
    }

    private void ReaderLoop()
    {
        var buffer = new byte[Math.Max(_maxInputLength, DualSenseProtocol.InputReportBtSize)];
        var sinceInput = Stopwatch.StartNew();
        var sinceEnhancedRequest = Stopwatch.StartNew();
        var clock = Stopwatch.StartNew();
        var psSequence = new PressSequenceDetector();
        long crcErrors = 0;

        while (!_stopping)
        {
            int count;
            try
            {
                count = _stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                if (sinceInput.Elapsed > InputSilenceTimeout)
                {
                    Fail(L.Text("Ovládač prestal posielať dáta."));
                    return;
                }
                continue;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Fail(L.Text("Ovládač bol odpojený."), ex);
                return;
            }

            sinceInput.Restart();
            Interlocked.Increment(ref _inputReports);

            switch (InputReportParser.TryParse(buffer.AsSpan(0, count), Info.Connection, out var status, out var frame))
            {
                case InputParseResult.Ok:
                    if (_lightbarInitialization.ObserveSensorTimestamp(frame.Motion.Timestamp))
                        Wake();
                    UpdateStatus(status!);
                    var now = clock.Elapsed;
                    InputReceived?.Invoke(frame, now);
                    if (psSequence.Update(frame.PsButton, now))
                    {
                        Log.Info(L.Format($"{Info.DisplayName}: tlačidlo PS stlačené 5×."));
                        PsButtonSequence?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                case InputParseResult.SimpleBluetoothReport when sinceEnhancedRequest.Elapsed > EnhancedModeRetryInterval:
                    Log.Info(L.Text("Ovládač posiela zjednodušený BT report, znova žiadam rozšírený režim."));
                    RequestEnhancedMode(_stream, _maxFeatureLength);
                    sinceEnhancedRequest.Restart();
                    break;
                case InputParseResult.CrcMismatch:
                    if (crcErrors++ % 1000 == 0)
                        Log.Warn(L.Format($"Neplatné CRC vstupného reportu (spolu {crcErrors})."));
                    break;
            }
        }
    }

    private void UpdateStatus(ControllerStatus status)
    {
        var previous = Status;
        if (status == previous)
            return;
        Volatile.Write(ref _status, status);
        if (previous is null || previous.HeadphonesConnected != status.HeadphonesConnected)
            Wake(); // the Auto audio route depends on it
        StatusChanged?.Invoke(this, status);
    }

    private ComposedOutput Compose(TimeSpan effectTime) => OutputComposer.Compose(
        _sources.Settings.Current.Output, _sources.Live.Current, Status?.HeadphonesConnected ?? false, effectTime);

    /// <summary>
    /// Sends the groups that changed (at most every <see cref="MinSendInterval"/>, bursts coalesce)
    /// plus, every refresh interval, the groups the user wants re-applied.
    /// </summary>
    private void WriterLoop()
    {
        using var timerResolution = TimerResolution.Request1Ms();
        var clock = Stopwatch.StartNew();
        ComposedOutput? lastSent = null;

        var lastSend = clock.Elapsed;
        var lastRefresh = lastSend;

        while (!_stopping && !HasFailed)
        {
            var now = Compose(LightbarEffects.CurrentTime);
            var refresh = _sources.Refresh.Current;
            var refreshInterval = TimeSpan.FromMilliseconds(refresh.IntervalMs);
            var elapsed = clock.Elapsed;
            int forced = Volatile.Read(ref _forcedGroups);

            var groups = OutputComposer.Changes(lastSent, now) | (OutputGroups)forced;
            bool refreshDue = elapsed - lastRefresh >= refreshInterval;
            if (refreshDue)
                groups |= OutputComposer.RefreshGroups(now, refresh);

            groups = _lightbarInitialization.FilterGroups(groups);
            if (groups == OutputGroups.None && !_lightbarInitialization.NeedsReset)
            {
                if (refreshDue)
                    lastRefresh = elapsed;
                var untilRefresh = lastRefresh + refreshInterval - elapsed;
                var output = _sources.Settings.Current.Output;
                if (output.LightbarEffect == LightbarEffectMode.Rainbow && output.LightbarBrightness > 0)
                    untilRefresh = untilRefresh < LightbarEffects.FrameInterval ? untilRefresh : LightbarEffects.FrameInterval;
                _wake.WaitOne(untilRefresh > TimeSpan.FromMilliseconds(1) ? untilRefresh : TimeSpan.FromMilliseconds(1));
                continue;
            }

            var sinceLast = elapsed - lastSend;
            if (sinceLast < MinSendInterval)
            {
                // Re-composing after the pause coalesces bursts of changes into one report.
                Thread.Sleep(MinSendInterval - sinceLast);
                continue;
            }

            if (_lightbarInitialization.NeedsReset)
            {
                bool resetSent = Send(OutputReportBuilder.BuildLightbarReset(Info.Connection, NextSequence()));
                lastSend = clock.Elapsed;
                if (resetSent)
                {
                    _lightbarInitialization.MarkResetSent();
                    // Reset also releases the player and mic LEDs. Restore all three from the
                    // latest state, including a color changed while the Bluetooth animation ran.
                    Interlocked.Or(ref _forcedGroups, (int)(OutputGroups.Lightbar | OutputGroups.Leds));
                    Log.Info(L.Format($"{Info.DisplayName}: odoslané prevzatie ovládania svetelného pásu."));
                }
                continue;
            }

            groups = OutputComposer.KeepRumbleAlive(groups, now);
            bool sent = Send(OutputReportBuilder.Build(now, groups, Info.Connection, NextSequence(), UsesVibrationV2));
            lastSend = clock.Elapsed;
            if (sent)
            {
                lastSent = now;
                Interlocked.And(ref _forcedGroups, ~forced);
                if (refreshDue)
                    lastRefresh = lastSend;
            }
        }
    }

    /// <summary>
    /// On exit or controller switch: motors to 0, triggers off, attenuation reset; then a neutral
    /// report so the actuators go back to audio haptics. Lightbar and LEDs keep their state.
    /// </summary>
    private void SendFinalReport()
    {
        var final = _factoryReset
            ? OutputReportBuilder.BuildFactoryReset(Info.Connection, NextSequence(), UsesVibrationV2)
            : OutputReportBuilder.BuildRestore(Info.Connection, NextSequence(), UsesVibrationV2);
        if (Send(final))
            Send(OutputReportBuilder.BuildNeutral(Info.Connection, NextSequence()));
        Log.Info(_factoryReset
            ? L.Format($"{Info.DisplayName}: továrenský reset (klasické vibrácie 0, spúšte off, útlm 0, LED zhasnuté, svetelný pás modrý).")
            : L.Format($"{Info.DisplayName}: obnovený pôvodný stav (klasické vibrácie 0, spúšte off, útlm 0)."));
    }

    private bool Send(byte[] report)
    {
        // Windows expects a write of exactly the declared output report length (Bluetooth declares
        // more than the 78 B of report 0x31); padding here keeps every report a single WriteFile.
        byte[] buffer = report;
        if (report.Length < _maxOutputLength)
        {
            buffer = new byte[_maxOutputLength];
            report.CopyTo(buffer, 0);
        }

        try
        {
            lock (_writeGate)
                _stream.Write(buffer);
            Interlocked.Increment(ref _outputReports);
            _writeTimeouts = 0;
            return true;
        }
        catch (TimeoutException)
        {
            if (++_writeTimeouts >= MaxConsecutiveWriteTimeouts)
                Fail(L.Text("Ovládač neprijíma výstupné reporty (timeout)."));
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(L.Text("Zápis do ovládača zlyhal."), ex);
            return false;
        }
    }

    private byte NextSequence() => (byte)(Interlocked.Increment(ref _sequence) & 0x0F);

    private void Fail(string reason, Exception? ex = null)
    {
        if (_stopping || Interlocked.CompareExchange(ref _failureReason, reason, null) is not null)
            return;
        Log.Warn($"{Info.DisplayName}: {reason}{(ex is null ? "" : $" ({ex.GetType().Name}: {ex.Message})")}");
        Wake();
        Failed?.Invoke(this, EventArgs.Empty);
    }
}
