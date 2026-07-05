namespace LANAudioReceiver;

/// <summary>Shared wire-protocol constants and helpers (see PROTOCOL.md).
/// This is the C# mirror of the format the Swift sender writes.</summary>
public static class Protocol
{
    public static readonly byte[] Magic = { 0x4C, 0x41, 0x42, 0x31 }; // "LAB1"
    public const byte Version = 1;

    public const byte TypeAudio = 0;
    public const byte TypeHello = 1;
    public const byte TypeHelloAck = 2;
    public const byte TypePing = 3;
    public const byte TypePong = 4;
    public const byte TypeBye = 5;
    public const byte TypeAnnounce = 6;

    public const int HeaderSize = 8;      // magic(4) ver(1) type(1) codec(1) channels(1)
    public const int AudioHeaderSize = 20; // header(8) + sampleRate(4) + seq(4) + frameSamples(4)

    public static bool HasMagic(ReadOnlySpan<byte> d) =>
        d.Length >= 6 && d[0] == Magic[0] && d[1] == Magic[1] && d[2] == Magic[2] && d[3] == Magic[3];

    /// <summary>Builds an 8-byte common header.</summary>
    public static void WriteHeader(Span<byte> dst, byte type, byte codec, byte channels)
    {
        Magic.CopyTo(dst);
        dst[4] = Version;
        dst[5] = type;
        dst[6] = codec;
        dst[7] = channels;
    }
}
