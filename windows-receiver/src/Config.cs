namespace LANAudioReceiver;

/// <summary>Typed configuration loaded from a <c>.env</c> file in the working directory.
/// Environment variables of the same name override file values.</summary>
public sealed class Config
{
    public int AudioPort { get; init; }
    public int ControlPort { get; init; }
    public int DiscoveryPort { get; init; }
    public string OutputDevice { get; init; } = "CABLE Input";
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int JitterMs { get; init; }
    public string Codec { get; init; } = "pcm";
    public string DeviceName { get; init; } = "Windows-PC";
    public bool Discovery { get; init; }

    public static Config Load(string path = ".env")
    {
        var kv = ParseEnvFile(path);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            kv[(string)e.Key] = (string?)e.Value ?? "";

        string Str(string key, string def) => kv.TryGetValue(key, out var v) ? v.Trim() : def;
        int Int(string key, int def) => int.TryParse(Str(key, def.ToString()), out var v) ? v : def;

        return new Config
        {
            AudioPort = Int("AUDIO_PORT", 45678),
            ControlPort = Int("CONTROL_PORT", 45679),
            DiscoveryPort = Int("DISCOVERY_PORT", 45680),
            OutputDevice = Str("AUDIO_OUTPUT_DEVICE", "CABLE Input"),
            SampleRate = Int("SAMPLE_RATE", 48000),
            Channels = Int("CHANNELS", 2),
            JitterMs = Int("JITTER_MS", 30),
            Codec = Str("CODEC", "pcm"),
            DeviceName = Str("DEVICE_NAME", Environment.MachineName),
            Discovery = Str("DISCOVERY", "off").ToLowerInvariant() == "on",
        };
    }

    private static Dictionary<string, string> ParseEnvFile(string path)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            result[key] = value;
        }
        return result;
    }
}
