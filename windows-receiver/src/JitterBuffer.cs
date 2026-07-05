using NAudio.Wave;

namespace LANAudioReceiver;

/// <summary>
/// Playback provider with an integrated jitter buffer and an adaptive resampler.
///
/// Incoming PCM frames are reordered by sequence into a flat float FIFO. The
/// playback side (pulled by WASAPI on its real-time thread) reads that FIFO with a
/// linear-interpolation resampler whose rate is continuously nudged — by a fraction
/// of a percent — to hold the buffer at a small target depth. This cancels the
/// clock-rate difference between the Mac's capture clock and the PC's render clock,
/// so latency stays low and constant without underruns.
///
/// Output is float at the device's mix format so it feeds WASAPI shared mode
/// directly (no separate resampler).
/// </summary>
public sealed class AdaptivePlayback : ISampleProvider
{
    private readonly object _lock = new();

    private readonly int _srcRate;
    private readonly int _srcChannels;
    private readonly int _dstChannels;
    private readonly double _baseStep;   // srcRate / dstRate

    // Reorder buffer
    private readonly SortedDictionary<uint, float[]> _pending = new();
    private uint _expectedSeq;
    private bool _seqInit;
    private const int MaxReorder = 8;

    // Flat interleaved (source channels) circular FIFO
    private readonly float[] _ring;
    private readonly int _capacity;
    private int _head;
    private int _count;

    // Resampler + drift-control state
    private double _frac;
    private double _drift = 1.0;
    private bool _started;
    private readonly int _targetFrames;

    public WaveFormat WaveFormat { get; }
    public float LastPeak { get; private set; }

    /// <summary>Current buffered audio in milliseconds (for status display).</summary>
    public int DepthMs { get { lock (_lock) return (int)(_count / (double)_srcChannels * 1000.0 / _srcRate); } }

    public AdaptivePlayback(int srcRate, int srcChannels, int dstRate, int dstChannels, int jitterMs)
    {
        _srcRate = srcRate;
        _srcChannels = srcChannels;
        _dstChannels = dstChannels;
        _baseStep = (double)srcRate / dstRate;
        _targetFrames = Math.Max(1, jitterMs * srcRate / 1000);
        _capacity = Math.Max(srcRate * srcChannels, _targetFrames * srcChannels * 8); // ≥ ~1 s
        _ring = new float[_capacity];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(dstRate, dstChannels);
    }

    // ---------------- Network side ----------------

    public void Push(uint seq, byte[] pcmS16, int frameSamples, int channels)
    {
        int n = pcmS16.Length / 2;
        var f = new float[n];
        float peak = 0;
        for (int i = 0, j = 0; j < n; i += 2, j++)
        {
            short s = (short)(pcmS16[i] | (pcmS16[i + 1] << 8));
            float v = s / 32768f;
            f[j] = v;
            float a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }

        lock (_lock)
        {
            LastPeak = Math.Max(peak, LastPeak * 0.8f);
            if (!_seqInit) { _expectedSeq = seq; _seqInit = true; }
            if (seq < _expectedSeq) return;      // late or duplicate
            _pending[seq] = f;
            Drain();
        }
    }

    private void Drain()
    {
        while (true)
        {
            if (_pending.TryGetValue(_expectedSeq, out var frame))
            {
                _pending.Remove(_expectedSeq);
                Enqueue(frame);
                _expectedSeq++;
            }
            else if (_pending.Count > MaxReorder)
            {
                // The expected packet is presumed lost: insert one silent frame of
                // the same length to preserve timing, then continue.
                int len = 0;
                foreach (var kv in _pending) { len = kv.Value.Length; break; }
                if (len > 0) Enqueue(new float[len]);
                _expectedSeq++;
            }
            else break;
        }
    }

    private void Enqueue(float[] samples)
    {
        if (_count + samples.Length > _capacity)
        {
            int drop = (_count + samples.Length) - _capacity; // drop oldest
            _head = (_head + drop) % _capacity;
            _count -= drop;
        }
        int tail = (_head + _count) % _capacity;
        for (int i = 0; i < samples.Length; i++)
        {
            _ring[tail] = samples[i];
            tail = tail + 1 == _capacity ? 0 : tail + 1;
        }
        _count += samples.Length;
    }

    // ---------------- Playback side ----------------

    public int Read(float[] buffer, int offset, int count)
    {
        int outFrames = count / _dstChannels;
        lock (_lock)
        {
            if (!_started)
            {
                if (_count >= _targetFrames * _srcChannels) _started = true;
                else { Array.Clear(buffer, offset, count); return count; }
            }

            UpdateDrift();
            double step = _baseStep * _drift;

            for (int produced = 0; produced < outFrames; produced++)
            {
                if (_count < 2 * _srcChannels)
                {
                    // Underrun: silence the rest and re-prebuffer.
                    int rem = (outFrames - produced) * _dstChannels;
                    Array.Clear(buffer, offset + produced * _dstChannels, rem);
                    _started = false;
                    return count;
                }

                int baseIdx = _head;
                int nextIdx = (_head + _srcChannels) % _capacity;
                int outBase = offset + produced * _dstChannels;
                for (int c = 0; c < _dstChannels; c++)
                {
                    int sc = c < _srcChannels ? c : _srcChannels - 1; // duplicate if fewer src channels
                    float a = _ring[(baseIdx + sc) % _capacity];
                    float b = _ring[(nextIdx + sc) % _capacity];
                    buffer[outBase + c] = (float)(a + (b - a) * _frac);
                }

                _frac += step;
                while (_frac >= 1.0 && _count >= 2 * _srcChannels)
                {
                    _head = (_head + _srcChannels) % _capacity;
                    _count -= _srcChannels;
                    _frac -= 1.0;
                }
            }
        }
        return count;
    }

    /// <summary>Nudge playback rate toward keeping the buffer at the target depth.
    /// Correction is capped at ±1% (inaudible) and slewed for smoothness.</summary>
    private void UpdateDrift()
    {
        double fill = _count / (double)_srcChannels;
        double error = (fill - _targetFrames) / _targetFrames;
        double target = 1.0 + Math.Clamp(error * 0.5, -0.01, 0.01);
        _drift += (target - _drift) * 0.05;
    }
}
