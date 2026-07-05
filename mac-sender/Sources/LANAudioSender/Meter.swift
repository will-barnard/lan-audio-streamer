import Foundation

/// Thread-safe holder for the latest audio level, plus a console renderer.
final class Meter {
    private let lock = NSLock()
    private var peak: Float = 0
    private var rms: Float = 0

    func update(peak: Float, rms: Float) {
        lock.lock()
        // Fast attack, slow release for a readable meter.
        self.peak = max(peak, self.peak * 0.7)
        self.rms = rms
        lock.unlock()
    }

    /// Renders a fixed-width bar like `[####------]  -12 dB`.
    func bar(width: Int = 20) -> String {
        lock.lock(); let p = peak; lock.unlock()
        let filled = Int((p * Float(width)).rounded())
        let clamped = min(max(filled, 0), width)
        let bars = String(repeating: "#", count: clamped) + String(repeating: "-", count: width - clamped)
        let db = p > 0 ? 20 * log10(p) : -Float.infinity
        let dbStr = p > 0 ? String(format: "%5.0f dB", db) : "  -inf dB"
        return "[\(bars)] \(dbStr)"
    }
}
