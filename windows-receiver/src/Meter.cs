namespace LANAudioReceiver;

/// <summary>Renders a peak level (0..1) as a fixed-width console bar.</summary>
public static class Meter
{
    public static string Bar(float peak, int width = 20)
    {
        int filled = Math.Clamp((int)Math.Round(peak * width), 0, width);
        string bars = new string('#', filled) + new string('-', width - filled);
        string db = peak > 0 ? $"{20 * Math.Log10(peak),5:0} dB" : "  -inf dB";
        return $"[{bars}] {db}";
    }
}
