using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Core.Audio;

/// <summary>Smoothed, normalized (0–1) band levels of the latest audio window.</summary>
/// <param name="Spectrum">Log-spaced bands for the visualizer (<see cref="AudioAnalyzer.SpectrumBands"/> values, 0–1).</param>
public sealed record AudioFeatures(double Bass, double Mid, double High, double Loudness, bool Beat, IReadOnlyList<float> Spectrum)
{
    public AudioFeatures(double bass, double mid, double high, double loudness, bool beat)
        : this(bass, mid, high, loudness, beat, SilentSpectrum)
    {
    }

    private static readonly float[] SilentSpectrum = new float[AudioAnalyzer.SpectrumBands];

    public static AudioFeatures Silent { get; } = new(0, 0, 0, 0, false);
}

/// <summary>
/// FFT band analysis of a mono stream: bass/mid/high and loudness after gain, sensitivity window,
/// noise gate and attack/release smoothing, plus energy-based beat detection on the bass band.
/// One analysis runs per call so latency stays at one capture buffer plus the FFT window.
/// </summary>
public sealed class AudioAnalyzer
{
    public const int FftSize = 1024;
    public const double BassLowHz = 20, BassHighHz = 200, MidHighHz = 2500, HighHighHz = 16000;
    public const int SpectrumBands = 32;
    private const double SpectrumLowHz = 30, SpectrumHighHz = 16000;
    private const int SpectrumAttackMs = 5, SpectrumMinReleaseMs = 120;

    private const double BeatAverageSeconds = 1.0;
    private const double MinBeatIntervalSeconds = 0.12;

    private readonly SpectrumAnalyzer _spectrum = new(FftSize);
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _magnitudes = new float[FftSize / 2 + 1];
    private readonly double[] _spectrumLevels = new double[SpectrumBands];
    private (int First, int Last)[] _spectrumBins = [];
    private int _spectrumRate;
    private int _ringPosition;
    private double _bass, _mid, _high, _loudness;
    private double _bassEnergyAverage;
    private double _previousBassEnergy;
    private double _sinceBeat = double.MaxValue;

    public AudioFeatures Process(ReadOnlySpan<float> mono, int sampleRate, AudioReactiveSettings settings)
    {
        foreach (float sample in mono)
        {
            _ring[_ringPosition] = sample;
            _ringPosition = (_ringPosition + 1) % FftSize;
        }
        return Analyze((double)mono.Length / sampleRate, sampleRate, settings);
    }

    /// <summary>Called when the capture delivers nothing (Windows sends no loopback data while silent).</summary>
    public AudioFeatures ProcessSilence(double seconds, int sampleRate, AudioReactiveSettings settings)
    {
        Array.Clear(_ring);
        return Analyze(seconds, sampleRate, settings);
    }

    /// <summary>Maps a level in dBFS to 0–1: gain, then a window of 60 dB (sensitivity 0) to 15 dB (sensitivity 1) below full scale, then the noise gate.</summary>
    internal static double Normalize(double linearLevel, AudioReactiveSettings s)
    {
        double db = 20 * Math.Log10(linearLevel + 1e-12) + s.GainDb;
        double range = 60 - 45 * s.Sensitivity;
        double value = Math.Clamp((db + range) / range, 0, 1);
        return value <= s.Threshold ? 0 : (value - s.Threshold) / (1 - s.Threshold);
    }

    private AudioFeatures Analyze(double dt, int sampleRate, AudioReactiveSettings s)
    {
        // Unroll the ring so the newest sample is last.
        int tail = FftSize - _ringPosition;
        _ring.AsSpan(_ringPosition).CopyTo(_window);
        _ring.AsSpan(0, _ringPosition).CopyTo(_window.AsSpan(tail));
        _spectrum.Compute(_window, _magnitudes);

        double binHz = (double)sampleRate / FftSize;
        double bassEnergy = BandEnergy(BassLowHz, BassHighHz, binHz);
        double midEnergy = BandEnergy(BassHighHz, MidHighHz, binHz);
        double highEnergy = BandEnergy(MidHighHz, HighHighHz, binHz);

        double sumSquares = 0;
        foreach (float sample in _window)
            sumSquares += sample * sample;
        // RMS of a full-scale sine is 1/√2; scale so it reads 1.0 like the band levels.
        double rms = Math.Sqrt(sumSquares / FftSize) * Math.Sqrt(2);

        double bassTarget = Normalize(Math.Sqrt(bassEnergy), s);
        _bass = Envelope.Follow(_bass, bassTarget, dt, s.AttackMs, s.ReleaseMs);
        _mid = Envelope.Follow(_mid, Normalize(Math.Sqrt(midEnergy), s), dt, s.AttackMs, s.ReleaseMs);
        _high = Envelope.Follow(_high, Normalize(Math.Sqrt(highEnergy), s), dt, s.AttackMs, s.ReleaseMs);
        _loudness = Envelope.Follow(_loudness, Normalize(rms, s), dt, s.AttackMs, s.ReleaseMs);

        bool beat = DetectBeat(bassEnergy, bassTarget > 0, dt, s.Sensitivity);
        return new AudioFeatures(_bass, _mid, _high, _loudness, beat, AnalyzeSpectrum(sampleRate, dt, s));
    }

    /// <summary>Peak magnitude per log-spaced band, through the same dB window as the bands; fast attack, visible release.</summary>
    private float[] AnalyzeSpectrum(int sampleRate, double dt, AudioReactiveSettings s)
    {
        if (sampleRate != _spectrumRate)
        {
            _spectrumRate = sampleRate;
            _spectrumBins = SpectrumBins(sampleRate);
        }

        var result = new float[SpectrumBands];
        int release = Math.Max(s.ReleaseMs, SpectrumMinReleaseMs);
        for (int band = 0; band < SpectrumBands; band++)
        {
            var (first, last) = _spectrumBins[band];
            float peak = 0;
            for (int k = first; k <= last; k++)
                peak = Math.Max(peak, _magnitudes[k]);
            _spectrumLevels[band] = Envelope.Follow(_spectrumLevels[band], Normalize(peak, s), dt, SpectrumAttackMs, release);
            result[band] = (float)_spectrumLevels[band];
        }
        return result;
    }

    internal static (int First, int Last)[] SpectrumBins(int sampleRate)
    {
        double binHz = (double)sampleRate / FftSize;
        int maxBin = FftSize / 2;
        var bins = new (int, int)[SpectrumBands];
        for (int band = 0; band < SpectrumBands; band++)
        {
            double low = SpectrumLowHz * Math.Pow(SpectrumHighHz / SpectrumLowHz, (double)band / SpectrumBands);
            double high = SpectrumLowHz * Math.Pow(SpectrumHighHz / SpectrumLowHz, (double)(band + 1) / SpectrumBands);
            int first = Math.Clamp((int)Math.Round(low / binHz), 1, maxBin);
            int last = Math.Clamp((int)Math.Round(high / binHz) - 1, first, maxBin);
            bins[band] = (first, last);
        }
        return bins;
    }

    /// <summary>A beat is a rising bass energy clearly above its ~1 s average (factor 2.0 at sensitivity 0 down to 1.25 at 1).</summary>
    private bool DetectBeat(double energy, bool aboveGate, double dt, double sensitivity)
    {
        _sinceBeat += dt;
        double factor = 2.0 - 0.75 * sensitivity;
        bool beat = aboveGate
                    && energy > _bassEnergyAverage * factor
                    && energy > _previousBassEnergy
                    && _sinceBeat >= MinBeatIntervalSeconds;
        if (beat)
            _sinceBeat = 0;

        _bassEnergyAverage += (energy - _bassEnergyAverage) * (1 - Math.Exp(-dt / BeatAverageSeconds));
        _previousBassEnergy = energy;
        return beat;
    }

    private double BandEnergy(double lowHz, double highHz, double binHz)
    {
        int first = Math.Max(1, (int)Math.Round(lowHz / binHz));
        int last = Math.Min(_magnitudes.Length - 1, (int)Math.Round(highHz / binHz));
        double energy = 0;
        for (int k = first; k <= last; k++)
            energy += _magnitudes[k] * _magnitudes[k];
        return energy;
    }
}
