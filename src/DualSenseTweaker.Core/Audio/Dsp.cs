namespace DualSenseTweaker.Core.Audio;

/// <summary>Hann-windowed magnitude spectrum of a real signal (iterative radix-2 FFT).</summary>
internal sealed class SpectrumAnalyzer
{
    private readonly int _size;
    private readonly float[] _window;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly int[] _bitReverse;
    private readonly float[] _re;
    private readonly float[] _im;

    public SpectrumAnalyzer(int size)
    {
        if (size < 4 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two.");
        _size = size;
        _window = new float[size];
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        _bitReverse = new int[size];
        _re = new float[size];
        _im = new float[size];

        int bits = (int)Math.Log2(size);
        for (int i = 0; i < size; i++)
        {
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1)));
            int reversed = 0;
            for (int b = 0; b < bits; b++)
                reversed |= ((i >> b) & 1) << (bits - 1 - b);
            _bitReverse[i] = reversed;
        }
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / size);
        }
    }

    public int Size => _size;

    /// <summary>
    /// Writes |X[k]| for k = 0…size/2 into <paramref name="magnitudes"/>, normalized so a
    /// full-scale sine at a bin center reads about 1.0.
    /// </summary>
    public void Compute(ReadOnlySpan<float> samples, Span<float> magnitudes)
    {
        if (samples.Length != _size || magnitudes.Length < _size / 2 + 1)
            throw new ArgumentException("Unexpected buffer length.");

        for (int i = 0; i < _size; i++)
        {
            int j = _bitReverse[i];
            _re[j] = samples[i] * _window[i];
            _im[j] = 0;
        }

        for (int length = 2; length <= _size; length <<= 1)
        {
            int half = length / 2;
            int step = _size / length;
            for (int start = 0; start < _size; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = _re[b] * wr - _im[b] * wi;
                    float ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }

        // A Hann-windowed sine of amplitude A peaks at A·N/4.
        float scale = 4f / _size;
        for (int k = 0; k <= _size / 2; k++)
            magnitudes[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * scale;
    }
}

/// <summary>Second-order low-pass (RBJ cookbook, Q = 1/√2).</summary>
internal sealed class BiquadLowPass
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;
    private int _sampleRate;
    private double _cutoff;

    public void Configure(int sampleRate, double cutoffHz)
    {
        if (sampleRate == _sampleRate && cutoffHz == _cutoff)
            return;
        _sampleRate = sampleRate;
        _cutoff = cutoffHz;
        double w0 = 2 * Math.PI * Math.Min(cutoffHz, sampleRate * 0.45) / sampleRate;
        double alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        double cos = Math.Cos(w0);
        double a0 = 1 + alpha;
        _b0 = (1 - cos) / 2 / a0;
        _b1 = (1 - cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public float Process(float x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return (float)y;
    }
}

internal static class Envelope
{
    /// <summary>One-pole attack/release follower: moves towards <paramref name="target"/> with the matching time constant.</summary>
    public static double Follow(double current, double target, double dtSeconds, int attackMs, int releaseMs)
    {
        double tau = (target > current ? attackMs : releaseMs) / 1000.0;
        double k = 1 - Math.Exp(-dtSeconds / Math.Max(tau, 1e-4));
        return current + (target - current) * k;
    }
}
