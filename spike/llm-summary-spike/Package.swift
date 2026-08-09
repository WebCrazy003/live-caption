// swift-tools-version: 6.0
import PackageDescription

// On-device summary spike for SPEC-10.
// Loads a small MLX instruct model (Llama-3.2 1B by default, 3B opt-in) and
// measures the three numbers the spec's blockers hang on:
//   B6  memory footprint of the resident LLM (it must live beside 2 Whisper models + a call app)
//   B7  generation latency / tokens-per-second for a ~100-word block
//   B8  summary quality on messy, ASR-style transcript text (eyeball the output)
let package = Package(
    name: "LLMSummarySpike",
    platforms: [.macOS(.v14)],
    dependencies: [
        // NOTE: mlx-swift-lm (the new home) requires Swift tools 6.1; this machine
        // has 6.0.3 (Xcode 16.2). So we use the older mlx-swift-examples repo, which
        // still ships MLXLLM/MLXLMCommon and has 6.0-compatible tags. Broad range →
        // SwiftPM picks the newest version this toolchain can build.
        .package(url: "https://github.com/ml-explore/mlx-swift-examples", "1.0.0" ..< "3.0.0"),
    ],
    targets: [
        .executableTarget(
            name: "LLMSummarySpike",
            dependencies: [
                .product(name: "MLXLLM", package: "mlx-swift-examples"),
                .product(name: "MLXLMCommon", package: "mlx-swift-examples"),
            ],
            // Keep the spike's own code in Swift 5 mode so strict-concurrency
            // diagnostics from the deps don't leak into this simple script.
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
