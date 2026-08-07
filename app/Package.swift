// swift-tools-version: 5.9
import PackageDescription

// LocalCaption — full app (Phase 1 foundation).
// Split into a data-only, testable `LocalCaptionKit` library and a `LocalCaption`
// executable that hosts the SwiftUI app + audio/ASR modules. Kept on Swift 5
// language mode (tools 5.9) to match the proven `minimal/` package and avoid
// strict-concurrency churn in the @unchecked Sendable audio code.
let package = Package(
    name: "LocalCaption",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(url: "https://github.com/argmaxinc/WhisperKit.git", from: "0.9.0"),
        .package(url: "https://github.com/groue/GRDB.swift.git", from: "6.0.0"),
    ],
    targets: [
        // Data layer: config, sqlite store, filters. No SwiftUI / WhisperKit — unit-testable.
        .target(
            name: "LocalCaptionKit",
            dependencies: [
                .product(name: "GRDB", package: "GRDB.swift"),
            ],
            path: "Sources/LocalCaptionKit"
        ),
        // App: SwiftUI shell + audio capture + ASR streaming.
        .executableTarget(
            name: "LocalCaption",
            dependencies: [
                "LocalCaptionKit",
                .product(name: "WhisperKit", package: "WhisperKit"),
                .product(name: "GRDB", package: "GRDB.swift"),
            ],
            path: "Sources/LocalCaption"
        ),
        .testTarget(
            name: "LocalCaptionKitTests",
            dependencies: ["LocalCaptionKit"],
            path: "Tests/LocalCaptionKitTests"
        ),
    ]
)
