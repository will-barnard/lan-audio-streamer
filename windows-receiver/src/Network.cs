using System.Net;
using System.Net.Sockets;

namespace LANAudioReceiver;

/// <summary>Receives AUDIO packets and answers the control heartbeat
/// (HELLO -> HELLO_ACK, PING -> PONG). Optionally broadcasts discovery ANNOUNCE.</summary>
public sealed class Receiver : IDisposable
{
    private readonly Config _cfg;
    private readonly IAudioCodec _codec;
    private readonly AudioPlayback _playback;
    private readonly UdpClient _audio;
    private readonly UdpClient _control;
    private readonly UdpClient? _discovery;
    private readonly CancellationTokenSource _cts = new();

    public string PeerName { get; private set; } = "";
    public DateTime LastAudioUtc { get; private set; } = DateTime.MinValue;

    public bool IsConnected =>
        LastAudioUtc != DateTime.MinValue && (DateTime.UtcNow - LastAudioUtc).TotalSeconds < 6;

    public Receiver(Config cfg, IAudioCodec codec, AudioPlayback playback)
    {
        _cfg = cfg;
        _codec = codec;
        _playback = playback;
        _audio = new UdpClient(cfg.AudioPort);
        _control = new UdpClient(cfg.ControlPort);
        if (cfg.Discovery)
        {
            _discovery = new UdpClient { EnableBroadcast = true };
        }
    }

    public void Start()
    {
        _ = Task.Run(() => AudioLoop(_cts.Token));
        _ = Task.Run(() => ControlLoop(_cts.Token));
        if (_cfg.Discovery) _ = Task.Run(() => DiscoveryLoop(_cts.Token));
    }

    private async Task AudioLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _audio.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var d = r.Buffer;
            if (d.Length < Protocol.AudioHeaderSize || !Protocol.HasMagic(d)) continue;
            if (d[5] != Protocol.TypeAudio) continue;

            int channels = d[7];
            uint seq = BitConverter.ToUInt32(d, 12);
            int frameSamples = (int)BitConverter.ToUInt32(d, 16);
            int payloadLen = d.Length - Protocol.AudioHeaderSize;
            if (payloadLen <= 0) continue;

            var payload = new byte[payloadLen];
            Array.Copy(d, Protocol.AudioHeaderSize, payload, 0, payloadLen);

            var pcm = _codec.Decode(payload, frameSamples, channels);
            _playback.Jitter.Push(seq, pcm, frameSamples);
            LastAudioUtc = DateTime.UtcNow;
        }
    }

    private async Task ControlLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _control.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var d = r.Buffer;
            if (!Protocol.HasMagic(d)) continue;

            switch (d[5])
            {
                case Protocol.TypeHello:
                    if (d.Length >= 9)
                    {
                        int nameLen = d[8];
                        if (d.Length >= 9 + nameLen)
                            PeerName = System.Text.Encoding.UTF8.GetString(d, 9, nameLen);
                    }
                    await SendHelloAck(r.RemoteEndPoint);
                    LastAudioUtc = LastAudioUtc == DateTime.MinValue ? DateTime.UtcNow : LastAudioUtc;
                    break;

                case Protocol.TypePing:
                    await SendPong(r.RemoteEndPoint, d);
                    break;

                case Protocol.TypeBye:
                    PeerName = "";
                    break;
            }
        }
    }

    private async Task SendHelloAck(IPEndPoint to)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(_cfg.DeviceName);
        int len = Math.Min(name.Length, 255);
        var p = new byte[Protocol.HeaderSize + 1 + len];
        Protocol.WriteHeader(p, Protocol.TypeHelloAck, 0, (byte)_cfg.Channels);
        p[Protocol.HeaderSize] = (byte)len;
        Array.Copy(name, 0, p, Protocol.HeaderSize + 1, len);
        await _control.SendAsync(p, p.Length, to);
    }

    private async Task SendPong(IPEndPoint to, byte[] ping)
    {
        // Echo the 8-byte tSend so the sender can compute RTT.
        var p = new byte[Protocol.HeaderSize + 8];
        Protocol.WriteHeader(p, Protocol.TypePong, 0, (byte)_cfg.Channels);
        if (ping.Length >= Protocol.HeaderSize + 8)
            Array.Copy(ping, Protocol.HeaderSize, p, Protocol.HeaderSize, 8);
        await _control.SendAsync(p, p.Length, to);
    }

    private async Task DiscoveryLoop(CancellationToken ct)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(_cfg.DeviceName);
        int len = Math.Min(name.Length, 255);
        var p = new byte[Protocol.HeaderSize + 4 + 1 + len];
        Protocol.WriteHeader(p, Protocol.TypeAnnounce, 0, (byte)_cfg.Channels);
        BitConverter.GetBytes((uint)_cfg.AudioPort).CopyTo(p, Protocol.HeaderSize);
        p[Protocol.HeaderSize + 4] = (byte)len;
        Array.Copy(name, 0, p, Protocol.HeaderSize + 5, len);
        var to = new IPEndPoint(IPAddress.Broadcast, _cfg.DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try { await _discovery!.SendAsync(p, p.Length, to); } catch { /* ignore */ }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _audio.Dispose();
        _control.Dispose();
        _discovery?.Dispose();
    }
}
