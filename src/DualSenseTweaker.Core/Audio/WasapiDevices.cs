using System.Runtime.InteropServices;
using DualSenseTweaker.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualSenseTweaker.Core.Audio;

/// <summary>
/// Plays the haptics signal on channels 3/4 of the DualSense USB audio device (front L/R stay silent).
/// The stream uses the device's own mix format so WASAPI needs no conversion, which also lets it use
/// IAudioClient3 low-latency periods where the driver supports them.
/// </summary>
internal sealed class HapticsOutput : IDisposable
{
    private readonly WasapiPlayer _player;
    private volatile bool _faulted;

    private HapticsOutput(WasapiPlayer player, string deviceName, Action onStopped)
    {
        _player = player;
        DeviceName = deviceName;
        _player.PlaybackStopped += (_, e) =>
        {
            _faulted = true;
            if (e.Exception is not null)
                Log.Warn(L.Format($"Haptický výstup sa zastavil: {e.Exception.Message}"));
            onStopped(); // e.g. the controller was unplugged: let the engine re-evaluate right away
        };
    }

    public string DeviceName { get; }
    public bool Faulted => _faulted;

    /// <summary>The DualSense's render endpoint ("Wireless Controller"), or null when it is not connected over USB.</summary>
    public static MMDevice? FindControllerEndpoint(MMDeviceEnumerator enumerator)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (IsDualSense(TryGet(() => device.DeviceFriendlyName)) || IsDualSense(TryGet(() => device.FriendlyName)))
                return device;
            device.Dispose();
        }
        return null;
    }

    /// <exception cref="InvalidOperationException">The device does not expose the 4 channels the actuators need.</exception>
    public static HapticsOutput Start(MMDevice device, HapticsBuffer buffer, int latencyMs, Action onStopped)
    {
        WaveFormat mixFormat;
        using (var client = device.CreateAudioClient())
            mixFormat = client.MixFormat;

        if (mixFormat.Channels < 4)
            throw new InvalidOperationException(
                L.Format($"Zvukové zariadenie ovládača má {mixFormat.Channels} kanály, haptika potrebuje 4. ") +
                L.Text("V nastaveniach zvuku Windows nastav pre „Wireless Controller“ 4 kanály (Quadraphonic)."));
        if (mixFormat.BitsPerSample != 32)
            throw new InvalidOperationException(L.Format($"Nepodporovaný formát zvukového zariadenia ({mixFormat.BitsPerSample}-bit)."));

        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency()
            .WithLatency(latencyMs)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        try
        {
            player.Init(new HapticsWaveProvider(mixFormat, buffer));
            player.Play();
            string latency = player.LowLatencyActive ? ", low-latency" : "";
            return new HapticsOutput(player, $"{device.FriendlyName} ({mixFormat.Channels} kan., {mixFormat.SampleRate} Hz{latency})", onStopped);
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _player.Stop();
        _player.Dispose();
    }

    private static bool IsDualSense(string? name) =>
        name is not null
        && !name.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase));

    private static string? TryGet(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private sealed class HapticsWaveProvider(WaveFormat format, HapticsBuffer buffer) : IWaveProvider
    {
        private float[] _left = [];
        private float[] _right = [];

        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> bytes)
        {
            var samples = MemoryMarshal.Cast<byte, float>(bytes);
            int channels = format.Channels;
            int frames = samples.Length / channels;
            if (_left.Length < frames)
            {
                _left = new float[frames];
                _right = new float[frames];
            }

            buffer.Read(_left.AsSpan(0, frames), _right.AsSpan(0, frames), format.SampleRate);
            samples.Clear();
            for (int i = 0; i < frames; i++)
            {
                // Quad layout FL, FR, BL, BR: the actuators are the third and fourth channel.
                samples[i * channels + 2] = _left[i];
                samples[i * channels + 3] = _right[i];
            }
            return bytes.Length; // never 0, that would end playback
        }
    }
}
