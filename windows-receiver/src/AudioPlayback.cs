using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LANAudioReceiver;

/// <summary>Renders received PCM into a chosen WASAPI render device (VB-CABLE).
/// The <see cref="JitterWaveProvider"/> feeds a resampler matched to the device's
/// shared mix format, so it works regardless of the device's native format.</summary>
public sealed class AudioPlayback : IDisposable
{
    private readonly WasapiOut _output;
    private readonly MediaFoundationResampler _resampler;
    public JitterWaveProvider Jitter { get; }

    public AudioPlayback(string deviceName, int sampleRate, int channels, int jitterMs)
    {
        var device = FindRenderDevice(deviceName)
            ?? throw new InvalidOperationException(
                $"Render device not found: \"{deviceName}\". Run with --list-devices.");

        Jitter = new JitterWaveProvider(sampleRate, channels, jitterMs);

        // Convert our 16-bit PCM to the device's shared mix format (usually float).
        var mixFormat = device.AudioClient.MixFormat;
        _resampler = new MediaFoundationResampler(Jitter, mixFormat) { ResamplerQuality = 60 };

        // Event-sync shared mode, ~50 ms device buffer.
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        _output.Init(_resampler);
    }

    public void Start() => _output.Play();

    public float Peak => Jitter.LastPeak;
    public int Depth => Jitter.Depth;

    public static MMDevice? FindRenderDevice(string name)
    {
        using var en = new MMDeviceEnumerator();
        var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var d in devices)
            if (string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase))
                return d;
        // Fall back to a substring match (e.g. "CABLE Input" vs full friendly name).
        foreach (var d in devices)
            if (d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    public static IEnumerable<string> ListRenderDevices()
    {
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            yield return d.FriendlyName;
    }

    public void Dispose()
    {
        _output.Dispose();
        _resampler.Dispose();
    }
}
