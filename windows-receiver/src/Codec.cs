namespace LANAudioReceiver;

/// <summary>Decodes a wire payload into interleaved S16LE PCM.
/// PCM is pass-through; Opus is a documented extension point.</summary>
public interface IAudioCodec
{
    byte[] Decode(byte[] payload, int frameSamples, int channels);
}

/// <summary>Pass-through PCM (best quality/latency on a LAN).</summary>
public sealed class PcmCodec : IAudioCodec
{
    public byte[] Decode(byte[] payload, int frameSamples, int channels) => payload;
}

/// <summary>Placeholder for Opus. Add the Concentus NuGet package (see .csproj),
/// then replace the body with an OpusDecoder call. The header already tells us
/// <paramref name="frameSamples"/> and <paramref name="channels"/>.</summary>
public sealed class OpusCodecStub : IAudioCodec
{
    public OpusCodecStub() =>
        Console.Error.WriteLine("WARNING: CODEC=opus is not wired yet; expecting raw PCM. See Codec.cs.");

    public byte[] Decode(byte[] payload, int frameSamples, int channels) => payload; // TODO: opus_decode
}

public static class CodecFactory
{
    public static IAudioCodec Create(string name) =>
        name.ToLowerInvariant() == "opus" ? new OpusCodecStub() : new PcmCodec();
}
