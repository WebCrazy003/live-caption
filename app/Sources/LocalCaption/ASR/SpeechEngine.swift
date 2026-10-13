import Foundation
import LocalCaptionKit

/// What the streaming orchestrator needs from a speech engine: the on-device WhisperKit pair.
protocol SpeechEngine: AnyObject {
    /// For the status line, e.g. "tiny.en · small.en".
    var label: String { get }
    /// Get ready to decode: download/load models. Throws with a message the session screen shows.
    func prepare(onStatus: @escaping (String) -> Void,
                 onDownload: @escaping (String, Double) -> Void) async throws
    func transcribeInterim(_ request: SpeechRequest) async -> SpeechOutcome
    func transcribeFinal(_ request: SpeechRequest) async -> SpeechOutcome
    /// Stop background work (keep-alive polling). The engine is not used afterwards.
    func shutdown()
}

extension WhisperEngine: SpeechEngine {
    var label: String { "\(interimName) · \(finalName)" }
    func transcribeInterim(_ request: SpeechRequest) async -> SpeechOutcome { await transcribeInterim(request.audio) }
    func transcribeFinal(_ request: SpeechRequest) async -> SpeechOutcome { await transcribeFinal(request.audio) }
    func shutdown() {}
}
