import Foundation
import OSLog
import LocalCaptionKit

/// Accent mode's speech engine (SPEC-18): every utterance goes to the RTX agent. Interim
/// snapshots ask the primary model (with word timings, for the rolling caption); a final asks
/// the primary — the caption shows as soon as it answers — and, concurrently, the secondary,
/// whose text is delivered later through `onSecondary` for the corrector.
///
/// No RTX, no Accent mode (D7): there is no on-Mac fallback. A final keeps retrying for up to
/// `giveUpAfter` while the caption view says so; after `pauseAfter` the session is asked to
/// pause, so no new speech piles up behind the outage.
final class RTXEngine: SpeechEngine, @unchecked Sendable {
    struct Settings: Equatable {
        var primary: String
        var secondary: String?
        var bandpass: Bool
        var level: Bool
    }

    let label: String
    private let client: RTXClient
    private let settings: Settings
    private let pauseAfter: TimeInterval
    private let giveUpAfter: TimeInterval
    private let log = Logger(subsystem: "com.livecaption.app", category: "rtx")
    private let ids = LockedValue(0)
    private let pauseSignalled = LockedValue(false)
    /// When the current outage began — shared by every queued final, so an outage costs one
    /// `giveUpAfter`, not one per utterance.
    private let outageStart = LockedValue<Date?>(nil)
    /// Secondary requests still running, so Stop can wait for the last ones.
    private let secondaries = LockedValue(0)
    private var keepAlive: Task<Void, Never>?

    /// A final's secondary text: (start ms, text). Any thread.
    var onSecondary: ((Int, String) -> Void)?
    /// A connection problem to show (nil when it clears). Any thread.
    var onConnection: ((String?) -> Void)?
    /// The RTX has been unreachable for `pauseAfter`: pause capture. Any thread, once per outage.
    var onMustPause: (() -> Void)?

    init(client: RTXClient, settings: Settings, pauseAfter: TimeInterval = 30, giveUpAfter: TimeInterval = 120) {
        self.client = client
        self.settings = settings
        self.pauseAfter = pauseAfter
        self.giveUpAfter = giveUpAfter
        label = ([settings.primary] + (settings.secondary.map { [$0] } ?? [])).joined(separator: " + ") + " on RTX"
    }

    // MARK: Prepare: reach the agent, load the pair, wait for ready (D9)

    func prepare(onStatus: @escaping (String) -> Void,
                 onDownload: @escaping (String, Double) -> Void) async throws {
        onStatus("Connecting to the RTX desktop…")
        _ = try await client.hello()
        var status = try await client.status()
        if !status.isReady(primary: settings.primary, secondary: settings.secondary) {
            status = try await client.load(primary: settings.primary, secondary: settings.secondary)
        }
        var lastAnswer = Date()
        while !status.isReady(primary: settings.primary, secondary: settings.secondary) {
            try Task.checkCancellation()
            switch status.state {
            case .error: throw RTXError.http(500, status.line)
            case .downloading: onDownload(status.model ?? "model", status.progress)
            default: onStatus("Starting models on RTX — \(status.line)")
            }
            try await Task.sleep(nanoseconds: 1_000_000_000)
            // Loading a model can stall the agent's replies for a few seconds; only a minute of
            // silence counts as lost.
            do {
                status = try await client.status(timeout: 10)
                lastAnswer = Date()
            } catch RTXError.unreachable(let why) {
                if Date().timeIntervalSince(lastAnswer) > 60 { throw RTXError.unreachable(why) }
                continue
            }
            // The agent restarted, or unloaded an earlier pair, while we waited.
            if status.state == .idle { status = try await client.load(primary: settings.primary, secondary: settings.secondary) }
        }
        startKeepAlive()
    }

    /// While Accent mode stays selected, poll so the agent's idle unload does not fire, and
    /// reload if the agent restarted.
    private func startKeepAlive() {
        keepAlive?.cancel()
        keepAlive = Task { [client, settings, log] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 30_000_000_000)
                guard !Task.isCancelled, let st = try? await client.status() else { continue }
                if st.state == .idle {
                    log.info("agent has no models loaded: reloading")
                    _ = try? await client.load(primary: settings.primary, secondary: settings.secondary)
                }
            }
        }
    }

    func shutdown() {
        keepAlive?.cancel(); keepAlive = nil
    }

    // MARK: Decoding

    /// The moving caption: the primary model re-reads the utterance so far, with word timings.
    func transcribeInterim(_ request: SpeechRequest) async -> SpeechOutcome {
        let audio = cleaned(request.audio)
        do {
            guard let r = try await client.transcribe(audio, role: .primary, lane: .interim, id: nextId(),
                                                      words: true, timeout: 2) else { return .empty }
            let words = (r.words ?? []).map { CaptionWord(Filters.clean($0.word), start: $0.start, end: $0.end) }
            return outcome(r.text, words: words)
        } catch is CancellationError {
            return .cancelled
        } catch {
            return .failure(error.localizedDescription)
        }
    }

    func transcribeFinal(_ request: SpeechRequest) async -> SpeechOutcome {
        let audio = cleaned(request.audio)
        let startMs = request.startSample / 16
        let id = nextId()
        let secondary: Task<String?, Never>? = settings.secondary.map { _ in
            secondaries.update { $0 += 1 }
            return Task { [client, secondaries] in
                defer { secondaries.update { $0 -= 1 } }
                guard let r = try? await client.transcribe(audio, role: .secondary, lane: .final, id: id,
                                                           words: false, timeout: 20) else { return nil }
                return Filters.clean(r.text)
            }
        }
        var attempt = 0
        while true {
            do {
                let r = try await client.transcribe(audio, role: .primary, lane: .final, id: id, words: false, timeout: 8)
                recovered()
                var text = r.map { Filters.clean($0.text) } ?? ""
                if text.isEmpty, let alt = await secondary?.value, !alt.isEmpty {
                    text = alt                       // the secondary heard something the primary didn't
                } else if let secondary {
                    Task { [weak self] in
                        if let alt = await secondary.value, !alt.isEmpty { self?.onSecondary?(startMs, alt) }
                    }
                }
                return outcome(text, words: [])
            } catch is CancellationError {
                secondary?.cancel()
                return .cancelled
            } catch {
                let began = outageStart.get() ?? { let now = Date(); outageStart.set(now); return now }()
                let waited = Date().timeIntervalSince(began)
                log.error("final at \(startMs) ms failed (attempt \(attempt)): \(error.localizedDescription, privacy: .public)")
                guard waited < giveUpAfter, retryable(error) else {
                    secondary?.cancel()
                    return .failure(error.localizedDescription)
                }
                onConnection?("RTX desktop unreachable — retrying…")
                if waited >= pauseAfter, !pauseSignalled.get() {
                    pauseSignalled.set(true)
                    onMustPause?()
                }
                attempt += 1
                try? await Task.sleep(nanoseconds: UInt64(min(4.0, 0.5 * pow(2, Double(attempt - 1))) * 1e9))
            }
        }
    }

    private func recovered() {
        outageStart.set(nil)
        pauseSignalled.set(false)
        onConnection?(nil)
    }

    /// Capture resumed: a failure from now on is a new outage, with its own pause.
    func resetOutage() {
        outageStart.set(nil)
        pauseSignalled.set(false)
    }

    /// Wait (bounded) for secondary texts still in flight — Stop calls this so the last
    /// utterances keep theirs.
    func waitForSecondaries(timeout: TimeInterval) async {
        let deadline = Date().addingTimeInterval(timeout)
        while secondaries.get() > 0, Date() < deadline {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
    }

    /// Network trouble and a busy or reloading agent are worth waiting for; a rejected request is not.
    private func retryable(_ error: Error) -> Bool {
        switch error as? RTXError {
        case .unreachable?: return true
        case .http(let code, _)?: return code == 409 || code == 503 || code >= 500
        default: return false
        }
    }

    private func outcome(_ raw: String, words: [CaptionWord]) -> SpeechOutcome {
        let text = Filters.clean(raw)
        if text.isEmpty { return .empty }
        if Filters.isHallucination(text) || Filters.isNonEnglish(text) { return .filtered }
        return .success(text: text, words: words, fallbacks: 0)
    }

    private func cleaned(_ audio: [Float]) -> [Float] {
        AudioCleanup.apply(audio, bandpass: settings.bandpass, level: settings.level)
    }

    private func nextId() -> Int {
        let n = ids.get() + 1
        ids.set(n)
        return n
    }
}
