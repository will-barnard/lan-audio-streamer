import Foundation

/// Encodes a raw PCM frame into the payload that goes on the wire.
///
/// The pipeline is codec-agnostic: `encode` takes interleaved S16LE PCM and returns
/// the bytes to place after the audio sub-header, plus the codec byte to advertise.
/// PCM is pass-through. Opus is a documented extension point — drop libopus in here
/// and the rest of the app is unchanged.
protocol AudioCodec {
    /// Codec identifier written to the packet header (see PROTOCOL.md).
    var codecByte: UInt8 { get }
    /// Encode one interleaved S16LE frame into a wire payload.
    func encode(_ pcm: Data) -> Data
}

/// Pass-through PCM. Best quality and lowest latency on a LAN.
struct PCMCodec: AudioCodec {
    let codecByte: UInt8 = 0
    func encode(_ pcm: Data) -> Data { pcm }
}

/// Placeholder for Opus. Wiring libopus here is the only change needed to switch
/// `CODEC=opus`; the header already advertises codec == 1 and carries a
/// variable-length payload.
struct OpusCodecStub: AudioCodec {
    let codecByte: UInt8 = 1
    init() {
        FileHandle.standardError.write(Data(
            "WARNING: CODEC=opus is not wired yet; sending raw PCM. See Codec.swift.\n".utf8))
    }
    func encode(_ pcm: Data) -> Data { pcm } // TODO: opus_encode
}

func makeCodec(_ name: String) -> AudioCodec {
    name.lowercased() == "opus" ? OpusCodecStub() : PCMCodec()
}
