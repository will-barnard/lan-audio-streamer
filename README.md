# LAN Audio Bridge

Stream **macOS desktop audio → Windows** over your local network with low latency,
so the audio shows up on Windows as a selectable input device for OBS, Discord,
DAWs, etc.

Two small native daemons, each configured with a `.env` file and started/stopped
with one script. Set it up once per machine, then just turn it on and off.

```
┌────────────── macOS (sender) ──────────────┐        ┌──────────── Windows (receiver) ────────────┐
│  Desktop audio                              │        │                                            │
│      │                                      │        │   UDP :45678 ──► jitter buffer ──► WASAPI  │
│  BlackHole (loopback device)                │  UDP   │                                     │      │
│      │                                      │ 45678  │                              "CABLE Input" │
│  CoreAudio capture ─► PCM ─► UDP send ───────────────►                                     │      │
│      │            heartbeat :45679  ◄───────────────►  PONG                          VB-CABLE      │
│  level meter + status (console)             │        │      │                              │      │
└─────────────────────────────────────────────┘        │  OBS / Discord select "CABLE Output" ◄─────┘
                                                        └────────────────────────────────────────────┘
```

---

## Project structure

```
lan-audio-streamer/
├── README.md              This file
├── PROTOCOL.md            The UDP wire protocol (source of truth for both apps)
├── .gitignore
├── mac-sender/            macOS sender — Swift / SwiftPM executable
│   ├── Package.swift
│   ├── .env.example
│   ├── run.sh             load .env + start
│   ├── stop.sh            stop
│   └── Sources/LANAudioSender/
│       ├── main.swift         entrypoint, wiring, CLI (--list-devices)
│       ├── Config.swift       .env parsing + typed config
│       ├── AudioCapture.swift CoreAudio capture from selected input device
│       ├── Codec.swift        PCM (+ Opus hook) encoding
│       ├── Network.swift      UDP send + heartbeat (Network.framework)
│       └── Meter.swift        RMS/peak level meter → console
└── windows-receiver/      Windows receiver — .NET console app
    ├── LANAudioReceiver.csproj
    ├── .env.example
    ├── run.ps1
    ├── stop.ps1
    └── src/
        ├── Program.cs        entrypoint, wiring, CLI (--list-devices)
        ├── Config.cs         .env parsing + typed config
        ├── AudioPlayback.cs  WASAPI render to VB-CABLE (NAudio)
        ├── JitterBuffer.cs   reorder + de-jitter + drift bounding
        ├── Codec.cs          PCM (+ Opus hook) decoding
        ├── Network.cs        UDP receive + heartbeat
        └── Meter.cs          RMS/peak level meter → console
```

Clean separation between capture/playback, codec, networking, UI (console), and
config — on both sides.

---

## Prerequisites

### macOS sender
1. **Xcode command line tools** (`xcode-select --install`) — provides `swift`.
2. **BlackHole** virtual audio device — the loopback that lets us capture desktop
   audio. Install: `brew install blackhole-2ch` (or download from
   https://existential.audio/blackhole/).
3. Route desktop audio into BlackHole. Simplest reliable setup: open **Audio MIDI
   Setup**, create a **Multi-Output Device** containing both your speakers/headphones
   **and** "BlackHole 2ch", and select it as your Mac's system output. Now you still
   hear audio *and* it's captured. (See `mac-sender/.env.example` for the device name.)

### Windows receiver
1. **.NET SDK 8.0+** — https://dotnet.microsoft.com/download
2. **VB-CABLE** virtual audio device — https://vb-audio.com/Cable/ . After install
   you'll have two endpoints: **"CABLE Input"** (a playback device — we render into
   it) and **"CABLE Output"** (a recording device — apps select this as their mic).

---

## Setup & run

### On the Windows PC (receiver) — start this first
```powershell
cd windows-receiver
copy .env.example .env
notepad .env            # set AUDIO_OUTPUT_DEVICE if not "CABLE Input"
dotnet build -c Release
.\run.ps1               # turn it ON
# .\stop.ps1            # turn it OFF
```
Then in OBS/Discord/your DAW, choose **"CABLE Output"** as the microphone/input.

Find the PC's LAN IP with `ipconfig` (e.g. `192.168.1.50`).

### On the Mac (sender)
```bash
cd mac-sender
cp .env.example .env
nano .env               # set RECEIVER_HOST to the PC's IP, pick input device
swift build -c release
swift run LANAudioSender --list-devices   # confirm your capture device name
./run.sh                # turn it ON
# ./stop.sh             # turn it OFF
```

You'll see live connection status and level meters in each console.

---

## Configuration (`.env`)

Keys are documented inline in each `.env.example`. The important ones:

| Key                   | Side | Meaning                                             |
|-----------------------|------|-----------------------------------------------------|
| `RECEIVER_HOST`       | Mac  | Windows PC IP (use an explicit IP; see note below)  |
| `AUDIO_INPUT_DEVICE`  | Mac  | capture device name, e.g. `BlackHole 2ch`           |
| `AUDIO_OUTPUT_DEVICE` | Win  | render device name, e.g. `CABLE Input`              |
| `AUDIO_PORT`          | both | must match on both sides (default `45678`)          |
| `CONTROL_PORT`        | both | heartbeat port (default `45679`)                    |
| `SAMPLE_RATE`         | both | default `48000`                                     |
| `CHANNELS`            | both | default `2`                                         |
| `FRAME_MS`            | Mac  | packet size in ms (default `5`)                     |
| `JITTER_MS`           | Win  | buffer depth; raise if audio is choppy (default 30) |
| `CODEC`               | both | `pcm` (default) or `opus`                           |
| `DISCOVERY`           | both | `off` (default) or `on`                             |

> **Note:** The receiver can broadcast discovery beacons (`DISCOVERY=on`), but the
> sender does not yet auto-consume them — set `RECEIVER_HOST` to the PC's IP for now.
> Sender-side `auto` is a small addition on top of the existing `ANNOUNCE` message.

---

## Audio pipeline

**Capture (Mac):** CoreAudio pulls from the selected input device (BlackHole) at
48 kHz stereo → float samples → converted to interleaved S16LE → sliced into 5 ms
frames.

**Encode:** default is raw PCM (no codec). On a LAN, 48 kHz/16-bit stereo is only
~1.5 Mbps — trivial — and gives you the best possible quality and the lowest latency
(no encode/decode delay). The `Codec` layer is pluggable; set `CODEC=opus` to switch
once the Opus hook is wired (see "Future extensions").

**Transport:** one UDP datagram per frame (~980 bytes), 200/sec. See `PROTOCOL.md`.

**Receive (Win):** datagrams → jitter buffer (reorder by sequence, absorb network
jitter, bound latency against clock drift) → decode → WASAPI render into "CABLE
Input".

**Present:** VB-CABLE loops "CABLE Input" to "CABLE Output", which every app sees as
a normal microphone.

**Expected latency:** roughly 20–50 ms end-to-end (capture + `JITTER_MS` + render).
Great for monitoring and streaming; not intended for live musical performance.

---

## Networking protocol

See **`PROTOCOL.md`** for the full byte-level spec: the 8-byte common header, the
AUDIO/HELLO/PING/PONG/BYE/ANNOUNCE message types, the reconnection state machine,
and the discovery beacon. Both apps implement that document; neither shares code.

---

## Troubleshooting

- **No audio on Windows but status is CONNECTED:** confirm the app is set to record
  from **"CABLE Output"**, and that the Mac's system output is the Multi-Output
  device containing BlackHole.
- **Choppy audio:** raise `JITTER_MS` (e.g. 50). Prefer wired Ethernet over Wi-Fi.
- **`--list-devices` doesn't show BlackHole/VB-CABLE:** the virtual device isn't
  installed or the machine wasn't rebooted after install.
- **Firewall:** allow the app through on the UDP ports on the Windows side.
- **Nothing connects:** both machines must be on the same subnet; ping the PC's IP
  from the Mac first.

---

## Future extensions

The architecture is designed so these drop in without reshaping the wire format or
the module boundaries:

- **Opus codec** — implement `encode/decode` in `Codec.swift` (libopus) and
  `Codec.cs` (Concentus, a pure-C# Opus port — no native dependency). The protocol
  already carries a `codec` byte and variable-length payloads.
- **Auto-discovery** — the `ANNOUNCE` message type and `DISCOVERY=on` path are
  specified; flip the default once tested.
- **Multiple receivers** — sender sends to a list of hosts (or multicast); no
  protocol change needed.
- **Encryption** — wrap the UDP payload in libsodium/`CryptoKit` secretbox; add a
  new `version` and a key in `.env`.
- **Recording** — tee the decoded PCM on the receiver to a WAV writer.
- **Self-contained virtual device** — replace VB-CABLE with a custom signed Windows
  audio driver (WDK). Large effort; VB-CABLE is the pragmatic default.
- **Native GUIs** — the console daemons can be wrapped in SwiftUI / WinUI 3 later;
  the audio/net/codec modules are UI-agnostic.
