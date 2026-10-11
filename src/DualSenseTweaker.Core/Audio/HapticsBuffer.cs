namespace DualSenseTweaker.Core.Audio;

/// <summary>
/// Hands low-passed stereo audio from the loopback capture thread to the haptics output thread.
/// Resamples between the two devices' rates (linear interpolation is enough after the low-pass)
/// and keeps the queue short: it primes to a small target and drops the oldest audio when the
/// two clocks drift apart, so latency stays bounded.
/// </summary>
internal sealed class HapticsBuffer
{
    private const int CapacityMs = 500;

    private readonly Lock _gate = new();
    private readonly BiquadLowPass _lowPassLeft = new();
    private readonly BiquadLowPass _lowPassRight = new();
    private readonly int _targetMs;
    private readonly int _maxMs;
    private float[] _left = [];
    private float[] _right = [];
    private int _readIndex;
    private int _count;
    private double _fraction;
    private bool _primed;
    private int _sourceRate;

    public HapticsBuffer(int targetLatencyMs = 15, int maxLatencyMs = 60)
    {
        _targetMs = targetLatencyMs;
        _maxMs = maxLatencyMs;
    }

    public int BufferedFrames
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <param name="interleaved">Captured samples; channels 0/1 are used (front left/right).</param>
    public void Write(ReadOnlySpan<float> interleaved, int channels, int sampleRate, double gain, int cutoffHz)
    {
        if (channels <= 0 || sampleRate <= 0)
            return;
        lock (_gate)
        {
            if (sampleRate != _sourceRate)
                Reset(sampleRate);
            _lowPassLeft.Configure(sampleRate, cutoffHz);
            _lowPassRight.Configure(sampleRate, cutoffHz);

            int frames = interleaved.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                float l = interleaved[i * channels];
                float r = channels > 1 ? interleaved[i * channels + 1] : l;
                Push(Clip(_lowPassLeft.Process(l) * gain), Clip(_lowPassRight.Process(r) * gain));
            }

            int maxFrames = sampleRate * _maxMs / 1000;
            if (_count > maxFrames)
                Drop(_count - sampleRate * _targetMs / 1000);
        }
    }

    /// <summary>Fills both spans with frames at <paramref name="outputRate"/>; silence on underrun.</summary>
    public void Read(Span<float> left, Span<float> right, int outputRate)
    {
        lock (_gate)
        {
            if (_sourceRate == 0 || (!_primed && _count < _sourceRate * _targetMs / 1000))
            {
                left.Clear();
                right.Clear();
                return;
            }
            _primed = true;

            double step = (double)_sourceRate / outputRate;
            for (int i = 0; i < left.Length; i++)
            {
                if (_count < 2)
                {
                    // Underrun: wait for a full target worth of audio before playing again.
                    _primed = false;
                    left[i..].Clear();
                    right[i..].Clear();
                    return;
                }
                int next = (_readIndex + 1) % _left.Length;
                float t = (float)_fraction;
                left[i] = _left[_readIndex] + (_left[next] - _left[_readIndex]) * t;
                right[i] = _right[_readIndex] + (_right[next] - _right[_readIndex]) * t;

                _fraction += step;
                int advance = (int)_fraction;
                _fraction -= advance;
                Drop(advance);
            }
        }
    }

    private void Reset(int sampleRate)
    {
        _sourceRate = sampleRate;
        int capacity = sampleRate * CapacityMs / 1000;
        _left = new float[capacity];
        _right = new float[capacity];
        _readIndex = 0;
        _count = 0;
        _fraction = 0;
        _primed = false;
    }

    private void Push(float l, float r)
    {
        if (_count == _left.Length)
            Drop(1);
        int write = (_readIndex + _count) % _left.Length;
        _left[write] = l;
        _right[write] = r;
        _count++;
    }

    private void Drop(int frames)
    {
        frames = Math.Clamp(frames, 0, _count);
        _readIndex = (_readIndex + frames) % Math.Max(_left.Length, 1);
        _count -= frames;
    }

    private static float Clip(double value) => (float)Math.Clamp(value, -1.0, 1.0);
}
