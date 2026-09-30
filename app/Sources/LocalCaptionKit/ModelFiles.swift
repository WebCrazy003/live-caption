import Foundation

/// On-disk completeness check for a downloaded WhisperKit model folder.
///
/// A download interrupted part-way (app quit, network drop) leaves `config.json` and
/// every `.mlmodelc` directory in place with only some of their files, so directory
/// presence alone cannot be trusted: CoreML then fails at load with
/// "Could not open …/weights/weight.bin". The Hub client moves each file into place
/// only once it is whole, so a file that exists is complete — what has to be checked
/// is that none is missing.
public enum ModelFiles {
    /// Compiled models WhisperKit cannot load without.
    static let requiredParts = ["AudioEncoder.mlmodelc", "MelSpectrogram.mlmodelc", "TextDecoder.mlmodelc"]
    /// Files CoreML needs inside each compiled ML Program.
    static let partFiles = ["coremldata.bin", "model.mil", "weights/weight.bin"]

    /// True when `folder` holds every file needed to load the model. Optional parts
    /// (e.g. `TextDecoderContextPrefill.mlmodelc`) are loaded when present, so they
    /// must be whole too.
    public static func isComplete(_ folder: URL) -> Bool {
        let fm = FileManager.default
        guard fm.fileExists(atPath: folder.appendingPathComponent("config.json").path) else { return false }
        let present = ((try? fm.contentsOfDirectory(atPath: folder.path)) ?? [])
            .filter { $0.hasSuffix(".mlmodelc") }
        for part in Set(requiredParts).union(present) {
            for file in partFiles
            where !fm.fileExists(atPath: folder.appendingPathComponent(part).appendingPathComponent(file).path) {
                return false
            }
        }
        return true
    }
}
