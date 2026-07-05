// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "LANAudioSender",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(
            name: "LANAudioSender",
            path: "Sources/LANAudioSender"
        )
    ]
)
