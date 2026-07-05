using LANAudioReceiver;

// ---- CLI ----
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        LAN Audio Bridge — Windows receiver

        Usage:
          dotnet run -- [--list-devices]

        Configuration is read from ./.env (see .env.example).
        """);
    return 0;
}

if (args.Contains("--list-devices"))
{
    Console.WriteLine("Available render (playback) devices:");
    foreach (var name in AudioPlayback.ListRenderDevices())
        Console.WriteLine($"  • {name}");
    return 0;
}

// ---- Startup ----
var cfg = Config.Load();

Console.WriteLine("LAN Audio Bridge — receiver");
Console.WriteLine($"  listen      : udp/{cfg.AudioPort} (audio), udp/{cfg.ControlPort} (control)");
Console.WriteLine($"  output dev  : {cfg.OutputDevice}");
Console.WriteLine($"  format      : {cfg.SampleRate} Hz, {cfg.Channels} ch, jitter={cfg.JitterMs} ms, codec={cfg.Codec}");
Console.WriteLine();

AudioPlayback playback;
try
{
    playback = new AudioPlayback(cfg.OutputDevice, cfg.SampleRate, cfg.Channels, cfg.JitterMs);
    playback.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

var codec = CodecFactory.Create(cfg.Codec);
using var receiver = new Receiver(cfg, codec, playback);
receiver.Start();

// ---- Clean shutdown ----
using var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Set();
};

// ---- Status line ----
_ = Task.Run(() =>
{
    while (!quit.IsSet)
    {
        string status = receiver.IsConnected ? "CONNECTED" : "waiting… ";
        string peer = string.IsNullOrEmpty(receiver.PeerName) ? "" : $"from {receiver.PeerName}";
        Console.Write($"\r{status} {peer,-16} buf {playback.DepthMs,3}ms  {Meter.Bar(playback.Peak)}   ");
        Thread.Sleep(250);
    }
});

quit.Wait();
Console.WriteLine("\nStopped.");
playback.Dispose();
return 0;
