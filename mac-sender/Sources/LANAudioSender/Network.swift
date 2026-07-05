import Foundation
import Network

/// Little-endian append helpers for building packets (see PROTOCOL.md).
extension Data {
    mutating func appendLE(_ v: UInt32) {
        var le = v.littleEndian
        Swift.withUnsafeBytes(of: &le) { append(contentsOf: $0) }
    }
    mutating func appendLE(_ v: UInt64) {
        var le = v.littleEndian
        Swift.withUnsafeBytes(of: &le) { append(contentsOf: $0) }
    }
}

/// Sends AUDIO packets and maintains the control heartbeat (HELLO / PING),
/// tracking connection state from HELLO_ACK / PONG replies.
final class AudioSender {
    private let audioConn: NWConnection
    private let controlConn: NWConnection
    private let queue = DispatchQueue(label: "lanaudio.net")

    private let sampleRate: UInt32
    private let channels: UInt8
    private let codecByte: UInt8
    private let deviceName: String

    private var seq: UInt32 = 0
    private let startNs = DispatchTime.now().uptimeNanoseconds
    private(set) var lastReplyMs: UInt64 = 0
    private(set) var lastRttMs: UInt64 = 0

    private static let magic: [UInt8] = [0x4C, 0x41, 0x42, 0x31] // "LAB1"
    private static let version: UInt8 = 1

    init(host: String, audioPort: UInt16, controlPort: UInt16,
         sampleRate: Int, channels: Int, codecByte: UInt8, deviceName: String) {
        let endpointHost = NWEndpoint.Host(host)
        let params = NWParameters.udp
        self.audioConn = NWConnection(host: endpointHost,
                                      port: NWEndpoint.Port(rawValue: audioPort)!, using: params)
        self.controlConn = NWConnection(host: endpointHost,
                                        port: NWEndpoint.Port(rawValue: controlPort)!, using: params)
        self.sampleRate = UInt32(sampleRate)
        self.channels = UInt8(channels)
        self.codecByte = codecByte
        self.deviceName = deviceName
    }

    func nowMs() -> UInt64 {
        (DispatchTime.now().uptimeNanoseconds - startNs) / 1_000_000
    }

    var isConnected: Bool { lastReplyMs != 0 && nowMs() - lastReplyMs < 6000 }

    // MARK: - Lifecycle

    func start() {
        audioConn.start(queue: queue)
        controlConn.start(queue: queue)
        receiveControl()
        sendHello()
    }

    // MARK: - Packet builders

    private func header(type: UInt8) -> Data {
        var d = Data()
        d.append(contentsOf: Self.magic)
        d.append(Self.version)
        d.append(type)
        d.append(codecByte)
        d.append(channels)
        return d
    }

    func sendAudio(_ payload: Data, frameSamples: Int) {
        var p = header(type: 0)
        p.appendLE(sampleRate)
        p.appendLE(seq)
        p.appendLE(UInt32(frameSamples))
        p.append(payload)
        seq &+= 1
        audioConn.send(content: p, completion: .contentProcessed { _ in })
    }

    func sendHello() {
        var p = header(type: 1)
        let name = Array(deviceName.utf8.prefix(255))
        p.append(UInt8(name.count))
        p.append(contentsOf: name)
        controlConn.send(content: p, completion: .contentProcessed { _ in })
    }

    func sendPing() {
        var p = header(type: 3)
        p.appendLE(nowMs())
        controlConn.send(content: p, completion: .contentProcessed { _ in })
    }

    func sendBye() {
        let p = header(type: 5)
        controlConn.send(content: p, completion: .contentProcessed { _ in })
    }

    // MARK: - Control receive

    private func receiveControl() {
        controlConn.receiveMessage { [weak self] data, _, _, error in
            guard let self = self else { return }
            if let d = data, d.count >= 6 {
                let s = d.startIndex
                let magicOK = d[s] == 0x4C && d[s+1] == 0x41 && d[s+2] == 0x42 && d[s+3] == 0x31
                if magicOK {
                    let type = d[s+5]
                    if type == 2 || type == 4 { // HELLO_ACK or PONG
                        self.lastReplyMs = self.nowMs()
                        if type == 4, d.count >= 16 { // PONG echoes tSend after the 8-byte header
                            var tSend: UInt64 = 0
                            for i in 0..<8 { tSend |= UInt64(d[s+8+i]) << (8*i) }
                            self.lastRttMs = self.nowMs() &- tSend
                        }
                    }
                }
            }
            if error == nil { self.receiveControl() }
        }
    }
}
