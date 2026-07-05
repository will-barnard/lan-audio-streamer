using NAudio.Wave;

namespace LANAudioReceiver;

/// <summary>
/// An <see cref="IWaveProvider"/> that reorders incoming PCM frames by sequence
/// number, absorbs network jitter with a target buffer depth, conceals lost
/// packets with silence, and bounds latency against clock drift by dropping when
/// the buffer grows too deep.
///
/// WASAPI pulls from <see cref="Read"/> on its own real-time thread; the network
/// thread calls <see cref="Push"/>. Access is locked.
/// </summary>
public sealed class JitterWaveProvider : IWaveProvider
{
    private readonly object _lock = new();
    private readonly SortedDictionary<uint, byte[]> _frames = new();
    private readonly int _jitterMs;
    private readonly int _sampleRate;
    private readonly int _channels;

    private uint _playhead;
    private bool _started;
    private int _targetFrames = 3;
    private int _frameBytes;
    private byte[] _residual = Array.Empty<byte>();
    private int _residualPos;

    public WaveFormat WaveFormat { get; }

    /// <summary>Peak (0..1) of the most recently received frame, for the meter.</summary>
    public float LastPeak { get; private set; }

    /// <summary>Frames currently buffered (for status display).</summary>
    public int Depth { get { lock (_lock) return _frames.Count; } }

    public JitterWaveProvider(int sampleRate, int channels, int jitterMs)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _jitterMs = jitterMs;
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
    }

    public void Push(uint seq, byte[] pcm, int frameSamples)
    {
        lock (_lock)
        {
            _frameBytes = pcm.Length;
            if (frameSamples > 0)
            {
                double frameMs = frameSamples * 1000.0 / _sampleRate;
                _targetFrames = Math.Max(1, (int)Math.Ceiling(_jitterMs / frameMs));
            }

            // Ignore packets for slots we've already played past.
            if (_started && seq < _playhead) return;

            _frames[seq] = pcm;
            UpdatePeak(pcm);

            // Overrun guard: bound latency against clock drift.
            int maxFrames = _targetFrames * 3;
            if (_frames.Count > maxFrames)
            {
                // Jump the playhead forward, dropping the oldest frames.
                uint newest = 0;
                foreach (var k in _frames.Keys) newest = k;
                uint target = newest > (uint)_targetFrames ? newest - (uint)_targetFrames : 0;
                var stale = _frames.Keys.Where(k => k < target).ToList();
                foreach (var k in stale) _frames.Remove(k);
                _playhead = target;
                _residual = Array.Empty<byte>();
                _residualPos = 0;
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        lock (_lock)
        {
            while (written < count)
            {
                // Serve leftover bytes from the current frame first.
                if (_residualPos < _residual.Length)
                {
                    int n = Math.Min(count - written, _residual.Length - _residualPos);
                    Array.Copy(_residual, _residualPos, buffer, offset + written, n);
                    _residualPos += n;
                    written += n;
                    continue;
                }

                // Prebuffer: wait until we have the target depth before starting.
                if (!_started)
                {
                    if (_frames.Count >= _targetFrames)
                    {
                        _started = true;
                        foreach (var k in _frames.Keys) { _playhead = k; break; } // smallest key
                    }
                    else
                    {
                        return FillSilence(buffer, offset + written, count - written) + written;
                    }
                }

                // Fetch the next frame in order.
                if (_frames.TryGetValue(_playhead, out var frame))
                {
                    _frames.Remove(_playhead);
                    _playhead++;
                    _residual = frame;
                    _residualPos = 0;
                }
                else if (_frames.Count > 0)
                {
                    // A packet is missing but later ones arrived: conceal with one
                    // frame of silence and move on (packet loss concealment).
                    _playhead++;
                    _residual = new byte[Math.Max(_frameBytes, 2)];
                    _residualPos = 0;
                }
                else
                {
                    // Underrun: nothing buffered. Emit silence without advancing so
                    // we resume cleanly when packets arrive.
                    return FillSilence(buffer, offset + written, count - written) + written;
                }
            }
        }
        return written;
    }

    private static int FillSilence(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }

    private void UpdatePeak(byte[] pcm)
    {
        float peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            float a = Math.Abs(s / 32768f);
            if (a > peak) peak = a;
        }
        LastPeak = Math.Max(peak, LastPeak * 0.7f);
    }
}
