import Foundation
import AVFoundation
import CoreAudio

/// Captures audio from a chosen CoreAudio input device, converts it to the target
/// format (interleaved S16LE at the configured sample rate/channels), and emits
/// fixed-size PCM frames.
final class AudioCapture {
    private let engine = AVAudioEngine()
    private let targetFormat: AVAudioFormat
    private let channels: Int
    private let frameBytes: Int
    private var accumulator = Data()

    /// Called with one PCM frame (interleaved S16LE, `frameSamples * channels * 2` bytes).
    var onFrame: ((Data) -> Void)?
    /// Called with (peak, rms) in the range 0.0...1.0 for the most recent frame.
    var onLevel: ((Float, Float) -> Void)?

    init(sampleRate: Int, channels: Int, frameSamples: Int) {
        self.channels = channels
        self.frameBytes = frameSamples * channels * 2
        self.targetFormat = AVAudioFormat(
            commonFormat: .pcmFormatInt16,
            sampleRate: Double(sampleRate),
            channels: AVAudioChannelCount(channels),
            interleaved: true
        )!
    }

    // MARK: - Device selection

    func selectDevice(named name: String) throws {
        guard let id = Self.deviceID(named: name) else {
            throw CaptureError.deviceNotFound(name)
        }
        guard let unit = engine.inputNode.audioUnit else {
            throw CaptureError.noAudioUnit
        }
        var dev = id
        let status = AudioUnitSetProperty(
            unit,
            kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global,
            0,
            &dev,
            UInt32(MemoryLayout<AudioDeviceID>.size)
        )
        if status != noErr { throw CaptureError.setDeviceFailed(status) }
    }

    // MARK: - Lifecycle

    func start() throws {
        let input = engine.inputNode
        let inFormat = input.inputFormat(forBus: 0)
        guard inFormat.sampleRate > 0 else { throw CaptureError.invalidInputFormat }
        guard let converter = AVAudioConverter(from: inFormat, to: targetFormat) else {
            throw CaptureError.converterFailed
        }

        input.installTap(onBus: 0, bufferSize: 2048, format: inFormat) { [weak self] buffer, _ in
            self?.process(buffer, with: converter, inputSampleRate: inFormat.sampleRate)
        }
        engine.prepare()
        try engine.start()
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }

    // MARK: - Processing

    private func process(_ buffer: AVAudioPCMBuffer, with converter: AVAudioConverter, inputSampleRate: Double) {
        let ratio = targetFormat.sampleRate / inputSampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
        guard capacity > 0,
              let out = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }

        var fed = false
        var convError: NSError?
        let status = converter.convert(to: out, error: &convError) { _, outStatus in
            if fed {
                outStatus.pointee = .noDataNow
                return nil
            }
            fed = true
            outStatus.pointee = .haveData
            return buffer
        }
        if status == .error { return }

        let frames = Int(out.frameLength)
        guard frames > 0, let base = out.int16ChannelData else { return }
        // Interleaved Int16: a single buffer holds all channels at channelData[0].
        let sampleCount = frames * channels
        base[0].withMemoryRebound(to: UInt8.self, capacity: sampleCount * 2) { bytes in
            accumulator.append(bytes, count: sampleCount * 2)
        }
        emitFrames()
    }

    private func emitFrames() {
        while accumulator.count >= frameBytes {
            let frame = Data(accumulator.prefix(frameBytes))
            accumulator.removeFirst(frameBytes)
            reportLevel(frame)
            onFrame?(frame)
        }
    }

    private func reportLevel(_ frame: Data) {
        guard let onLevel = onLevel else { return }
        var peak: Float = 0
        var sumSquares: Float = 0
        let sampleCount = frame.count / 2
        frame.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let samples = raw.bindMemory(to: Int16.self)
            for i in 0..<sampleCount {
                let v = Float(samples[i]) / 32768.0
                let a = abs(v)
                if a > peak { peak = a }
                sumSquares += v * v
            }
        }
        let rms = sampleCount > 0 ? (sumSquares / Float(sampleCount)).squareRoot() : 0
        onLevel(peak, rms)
    }

    // MARK: - CoreAudio device enumeration

    struct DeviceInfo { let id: AudioDeviceID; let name: String }

    static func inputDevices() -> [DeviceInfo] {
        var size: UInt32 = 0
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size)
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &ids)

        return ids.compactMap { id in
            guard hasInputChannels(id), let name = deviceName(id) else { return nil }
            return DeviceInfo(id: id, name: name)
        }
    }

    static func deviceID(named name: String) -> AudioDeviceID? {
        inputDevices().first { $0.name == name }?.id
            ?? inputDevices().first { $0.name.localizedCaseInsensitiveContains(name) }?.id
    }

    private static func deviceName(_ id: AudioDeviceID) -> String? {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var name: CFString = "" as CFString
        var size = UInt32(MemoryLayout<CFString>.size)
        let status = withUnsafeMutablePointer(to: &name) { ptr -> OSStatus in
            AudioObjectGetPropertyData(id, &addr, 0, nil, &size, ptr)
        }
        return status == noErr ? (name as String) : nil
    }

    private static func hasInputChannels(_ id: AudioDeviceID) -> Bool {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size) == noErr, size > 0 else { return false }
        let bufferList = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { bufferList.deallocate() }
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, bufferList) == noErr else { return false }
        let abl = UnsafeMutableAudioBufferListPointer(bufferList.assumingMemoryBound(to: AudioBufferList.self))
        return abl.reduce(0) { $0 + Int($1.mNumberChannels) } > 0
    }
}

enum CaptureError: Error, CustomStringConvertible {
    case deviceNotFound(String)
    case noAudioUnit
    case setDeviceFailed(OSStatus)
    case invalidInputFormat
    case converterFailed

    var description: String {
        switch self {
        case .deviceNotFound(let n): return "Input device not found: \"\(n)\". Run with --list-devices."
        case .noAudioUnit: return "Could not access the input audio unit."
        case .setDeviceFailed(let s): return "Failed to set input device (OSStatus \(s))."
        case .invalidInputFormat: return "Input device reported an invalid format."
        case .converterFailed: return "Could not create the audio format converter."
        }
    }
}
