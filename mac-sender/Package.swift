// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "LANAudioSender",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(
            name: "LANAudioSender",
            path: "Sources/LANAudioSender",
            // Embed an Info.plist into the CLI binary so macOS knows the microphone
            // usage description and will show the permission prompt (required to
            // capture from any input device, including BlackHole).
            linkerSettings: [
                .unsafeFlags([
                    "-Xlinker", "-sectcreate",
                    "-Xlinker", "__TEXT",
                    "-Xlinker", "__info_plist",
                    "-Xlinker", "Resources/Info.plist"
                ])
            ]
        )
    ]
)
