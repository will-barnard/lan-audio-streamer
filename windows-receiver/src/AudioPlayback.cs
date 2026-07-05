using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LANAudioReceiver;

/// <summary>Renders received PCM into a chosen WASAPI render device (VB-CABLE).
/// Uses <see cref="AdaptivePlayback"/>, which outputs float at the device's mix
/// format and rate-matches the sender, so no separate resampler is needed.</summary>
public sealed class AudioPlayback : IDisposable
{
    private readonly WasapiOut _output;
    private readonly AdaptivePlayback _provider;

    public AudioPlayback(string deviceName, int sampleRate, int channels, int jitterMs)
    {
        var device = FindRenderDevice(deviceName)
            ?? throw new InvalidOperationException(
                $"Render device not found: \"{deviceName}\". Run with --list-devices.");

        var mix = device.AudioClient.MixFormat;
        _provider = new AdaptivePlayback(sampleRate, channels, mix.SampleRate, mix.Channels, jitterMs);

        // Low-latency shared-mode output (~20 ms device buffer), event-driven.
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
        _output.Init(_provider); // ISampleProvider overload (float samples)
    }

    public void Start() => _output.Play();

    public void Push(uint seq, byte[] pcm, int frameSamples, int channels) =>
        _provider.Push(seq, pcm, frameSamples, channels);

    public float Peak => _provider.LastPeak;
    public int DepthMs => _provider.DepthMs;

    public static MMDevice? FindRenderDevice(string name)
    {
        using var en = new MMDeviceEnumerator();
        var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var d in devices)
            if (string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase))
                return d;
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

    public void Dispose() => _output.Dispose();
}
