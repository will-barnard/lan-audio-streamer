# LAN Audio Bridge — Wire Protocol (LAB1)

This document is the **single source of truth** both applications implement against.
The macOS sender and the Windows receiver share no code, only this spec.

All multi-byte integers are **little-endian**. All ports are UDP.

---

## 1. Transport overview

| Channel       | Default port | Direction            | Purpose                                  |
|---------------|--------------|----------------------|------------------------------------------|
| Audio         | `45678`      | sender → receiver    | PCM/Opus audio frames                    |
| Control       | `45679`      | bidirectional        | HELLO / heartbeat / BYE                  |
| Discovery     | `45680`      | broadcast            | Optional auto-discovery announcements    |

Audio is deliberately one-way and connectionless. There is **no retransmission** —
a lost packet is simply a lost 5 ms of audio, concealed by the receiver's jitter
buffer. This is the correct tradeoff for low-latency LAN audio.

---

## 2. Common packet header (8 bytes)

Every packet on every channel starts with this header.

```
Offset  Size  Field      Notes
0       4     magic      ASCII "LAB1" = 0x4C 0x41 0x42 0x31
4       1     version    protocol version, currently 1
5       1     type       message type (see below)
6       1     codec      0 = PCM_S16LE, 1 = OPUS
7       1     channels   1 = mono, 2 = stereo
```

### Message types

| Value | Name        | Channel   | Sent by  |
|-------|-------------|-----------|----------|
| 0     | AUDIO       | Audio     | sender   |
| 1     | HELLO       | Control   | sender   |
| 2     | HELLO_ACK   | Control   | receiver |
| 3     | PING        | Control   | sender   |
| 4     | PONG        | Control   | receiver |
| 5     | BYE         | Control   | either   |
| 6     | ANNOUNCE    | Discovery | receiver |

---

## 3. AUDIO packet (type 0)

Header (8 bytes) followed by a 12-byte audio sub-header and then the payload.

```
Offset  Size  Field         Notes
0       8     header         common header, type = 0
8       4     sampleRate     e.g. 48000
12      4     sequence       uint32, increments by 1 per packet, wraps at 2^32
16      4     frameSamples   samples PER CHANNEL in this packet (e.g. 240)
20      N     payload        audio data
```

Payload size:
- **PCM_S16LE**: `frameSamples * channels * 2` bytes, interleaved
  (L,R,L,R… for stereo). Signed 16-bit.
- **OPUS**: a single Opus packet (variable length). Decodes to `frameSamples`
  samples per channel.

### Framing defaults
- Sample rate: `48000`
- Channels: `2`
- Frame: `5 ms` → `240` samples/channel
- PCM payload per packet: `240 * 2 * 2 = 960` bytes → total datagram `980` bytes,
  safely under the 1500-byte Ethernet MTU (no IP fragmentation).
- Packet rate: `200` packets/second.

The receiver reads `sampleRate`, `channels`, `codec`, and `frameSamples` from each
packet, so mismatched sender/receiver defaults still interoperate.

---

## 4. Control packets

### HELLO (type 1) — sender → receiver
Header only, plus an optional UTF-8 device name.
```
0   8   header (type = 1)
8   1   nameLength
9   M   name (UTF-8, M = nameLength)
```

### HELLO_ACK (type 2) — receiver → sender
Same layout as HELLO, carries the receiver's device name.

### PING (type 3) / PONG (type 4)
Header only, plus an 8-byte timestamp for round-trip measurement.
```
0   8   header
8   8   tSend (uint64, millis since sender start)
```
The receiver echoes `tSend` unchanged in the PONG so the sender can compute RTT.

### BYE (type 5)
Header only. A courtesy "I'm shutting down" message. Optional; either side may
disappear without a BYE and the other side handles it via heartbeat timeout.

---

## 5. Connection lifecycle & reconnection

The connection is **soft state**. There is no handshake required before audio flows.

**Sender:**
1. On start, sends `HELLO` to `RECEIVER_HOST:CONTROL_PORT`.
2. Immediately begins streaming `AUDIO` packets to `RECEIVER_HOST:AUDIO_PORT`
   (audio does not wait for HELLO_ACK).
3. Sends a `PING` every `2 s`.
4. Marks status **CONNECTED** when it receives `HELLO_ACK` or any `PONG`,
   **DISCONNECTED** if no `PONG` arrives for `6 s`.
5. While disconnected it keeps sending audio + PING; when the receiver returns,
   `PONG` resumes and status flips back to CONNECTED. No manual reconnect needed.

**Receiver:**
1. Listens on `AUDIO_PORT` and `CONTROL_PORT`.
2. Replies to `HELLO` with `HELLO_ACK` and to `PING` with `PONG`.
3. Feeds incoming AUDIO into the jitter buffer.
4. Marks status **DISCONNECTED** if no AUDIO/PING for `6 s`, and silences output.

Because both sides retry idempotently, restarting either container/daemon simply
resumes the stream — which is the "turn it off and on" behaviour you want.

---

## 6. Jitter buffer (receiver)

- Target depth: `JITTER_MS` (default `30 ms` ≈ 6 packets).
- Packets are ordered by `sequence`. Late packets whose slot has already played
  are dropped.
- On underrun (buffer empty) the receiver outputs silence for that frame.
- On overrun (buffer exceeds `2 × JITTER_MS`) it drops the oldest frames to bound
  latency (handles clock drift between the two machines' audio clocks — the Mac
  capture clock and the PC render clock are never perfectly matched).

---

## 7. Discovery (optional)

When `DISCOVERY=on`:
- The receiver broadcasts an `ANNOUNCE` packet to `255.255.255.255:DISCOVERY_PORT`
  every `2 s`, containing its device name and audio port.
- A sender configured with `RECEIVER_HOST=auto` listens for `ANNOUNCE` and targets
  the first responder. With an explicit IP, discovery is ignored.

ANNOUNCE layout:
```
0   8   header (type = 6)
8   4   audioPort (uint32)
12  1   nameLength
13  M   name (UTF-8)
```

Discovery is off by default; manual `.env` configuration is the primary path.

---

## 8. Versioning

`version` is bumped on any incompatible header/layout change. A receiver that sees
a `version` it doesn't understand drops the packet and logs once. New optional
fields should be added as new message types, not by reshuffling existing offsets —
this keeps forward compatibility for future features (encryption, multi-receiver,
recording) without breaking the wire format.
