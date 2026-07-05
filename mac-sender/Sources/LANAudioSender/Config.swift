import Foundation

/// Typed configuration loaded from a `.env` file in the current working directory.
/// Environment variables of the same name override file values.
struct Config {
    var receiverHost: String
    var audioPort: UInt16
    var controlPort: UInt16
    var discoveryPort: UInt16
    var inputDevice: String
    var sampleRate: Int
    var channels: Int
    var frameMs: Int
    var codec: String
    var deviceName: String
    var discovery: Bool

    /// Samples per channel in one packet (e.g. 48000 * 5ms / 1000 = 240).
    var frameSamples: Int { sampleRate * frameMs / 1000 }

    var codecByte: UInt8 { codec.lowercased() == "opus" ? 1 : 0 }

    static func load(path: String = ".env") -> Config {
        var kv = parseEnvFile(path)
        // Environment overrides file.
        for (k, v) in ProcessInfo.processInfo.environment { kv[k] = v }

        func str(_ key: String, _ def: String) -> String { kv[key].map(trim) ?? def }
        func int(_ key: String, _ def: Int) -> Int { Int(str(key, "\(def)")) ?? def }

        return Config(
            receiverHost: str("RECEIVER_HOST", "127.0.0.1"),
            audioPort: UInt16(int("AUDIO_PORT", 45678)),
            controlPort: UInt16(int("CONTROL_PORT", 45679)),
            discoveryPort: UInt16(int("DISCOVERY_PORT", 45680)),
            inputDevice: str("AUDIO_INPUT_DEVICE", "BlackHole 2ch"),
            sampleRate: int("SAMPLE_RATE", 48000),
            channels: int("CHANNELS", 2),
            frameMs: int("FRAME_MS", 5),
            codec: str("CODEC", "pcm"),
            deviceName: str("DEVICE_NAME", Host.current().localizedName ?? "Mac"),
            discovery: str("DISCOVERY", "off").lowercased() == "on"
        )
    }

    private static func parseEnvFile(_ path: String) -> [String: String] {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return [:] }
        var result: [String: String] = [:]
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let line = trim(String(rawLine))
            if line.isEmpty || line.hasPrefix("#") { continue }
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = trim(String(line[..<eq]))
            let value = trim(String(line[line.index(after: eq)...]))
            result[key] = value
        }
        return result
    }
}

private func trim(_ s: String) -> String {
    s.trimmingCharacters(in: .whitespacesAndNewlines)
}
