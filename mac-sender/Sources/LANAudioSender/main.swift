import Foundation
import AVFoundation

/// Requests microphone/input access and blocks until the user responds. Capturing
/// from any input device (including BlackHole) requires this permission on macOS.
func ensureMicrophoneAccess() -> Bool {
    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized:
        return true
    case .notDetermined:
        let sem = DispatchSemaphore(value: 0)
        var granted = false
        AVCaptureDevice.requestAccess(for: .audio) { granted = $0; sem.signal() }
        sem.wait()
        return granted
    default:
        return false
    }
}

// MARK: - CLI

let args = CommandLine.arguments

if args.contains("--help") || args.contains("-h") {
    print("""
    LAN Audio Bridge — macOS sender

    Usage:
      swift run LANAudioSender [--list-devices]

    Configuration is read from ./.env (see .env.example).
    """)
    exit(0)
}

if args.contains("--list-devices") {
    print("Available input devices:")
    for d in AudioCapture.inputDevices() {
        print("  • \(d.name)")
    }
    exit(0)
}

// MARK: - Startup

let cfg = Config.load()
let meter = Meter()

print("LAN Audio Bridge — sender")
print("  target      : \(cfg.receiverHost):\(cfg.audioPort) (audio), :\(cfg.controlPort) (control)")
print("  input device: \(cfg.inputDevice)")
print("  format      : \(cfg.sampleRate) Hz, \(cfg.channels) ch, \(cfg.frameMs) ms frames, codec=\(cfg.codec)")
print("")

guard ensureMicrophoneAccess() else {
    FileHandle.standardError.write(Data("""
    ERROR: Microphone access was denied.
    Capturing from BlackHole needs microphone permission. Open
    System Settings → Privacy & Security → Microphone, enable your terminal
    (Terminal/iTerm), then run this again. If it never appeared, run:
        tccutil reset Microphone
    and restart the sender to re-trigger the prompt.\n
    """.utf8))
    exit(1)
}

let codec = makeCodec(cfg.codec)

let sender = AudioSender(
    host: cfg.receiverHost,
    audioPort: cfg.audioPort,
    controlPort: cfg.controlPort,
    sampleRate: cfg.sampleRate,
    channels: cfg.channels,
    codecByte: codec.codecByte,
    deviceName: cfg.deviceName
)

let capture = AudioCapture(
    sampleRate: cfg.sampleRate,
    channels: cfg.channels,
    frameSamples: cfg.frameSamples
)

capture.onFrame = { pcm in
    sender.sendAudio(codec.encode(pcm), frameSamples: cfg.frameSamples)
}
capture.onLevel = { peak, rms in
    meter.update(peak: peak, rms: rms)
}

do {
    try capture.selectDevice(named: cfg.inputDevice)
    try capture.start()
} catch {
    FileHandle.standardError.write(Data("ERROR: \(error)\n".utf8))
    exit(1)
}

sender.start()

// MARK: - Timers

// Heartbeat every 2s.
let pingTimer = DispatchSource.makeTimerSource(queue: .global())
pingTimer.schedule(deadline: .now() + 2, repeating: 2)
pingTimer.setEventHandler { sender.sendPing() }
pingTimer.resume()

// Status line every 250ms.
let uiTimer = DispatchSource.makeTimerSource(queue: .main)
uiTimer.schedule(deadline: .now() + 0.25, repeating: 0.25)
uiTimer.setEventHandler {
    let status = sender.isConnected ? "CONNECTED   " : "waiting…    "
    let rtt = sender.isConnected && sender.lastRttMs < 10000 ? "rtt \(sender.lastRttMs)ms" : "        "
    FileHandle.standardError.write(Data("\r\(status) \(rtt)  \(meter.bar())".utf8))
}
uiTimer.resume()

// MARK: - Clean shutdown

signal(SIGINT, SIG_IGN)
let sigSrc = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
sigSrc.setEventHandler {
    capture.stop()
    sender.sendBye()
    print("\nStopped.")
    exit(0)
}
sigSrc.resume()

dispatchMain()
