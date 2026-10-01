import XCTest
import AppKit
@testable import LocalCaption
import LocalCaptionKit

/// SPEC-14: what an Ask sends, the busy policy, typed/quick/regenerate turns, and the clipboard
/// screenshot helpers (on a private pasteboard — never the user's clipboard).
@MainActor
final class LiveAskTests: XCTestCase {
    private typealias FakeEngine = InterviewFlowTests.FakeEngine

    private var tmp: URL!
    private var env: AppEnvironment!
    private var engine: FakeEngine!
    private var transcript: (segments: [AskSelection.Segment], interim: String, audioMs: Int) = ([], "", 0)

    override func setUp() async throws {
        tmp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("lc-ask-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
        var cfg = Config()
        cfg.interview.mode = .interview
        env = AppEnvironment(config: cfg, store: try Store(url: tmp.appendingPathComponent("db.sqlite")))
        env.library = InterviewLibrary(root: tmp.appendingPathComponent("library"))
        env.interviewsRoot = tmp.appendingPathComponent("interviews")
        engine = FakeEngine()
        env.codex = CodexService(engine: engine)
        await env.codex.refresh()
    }

    override func tearDown() async throws { try? FileManager.default.removeItem(at: tmp) }

    private func preparedInterview() async -> InterviewController {
        let interview = InterviewController(env: env)
        interview.transcriptSource = { [unowned self] in self.transcript }
        interview.draft.company = "Acme"
        await interview.prepare()
        XCTAssertEqual(interview.prepState, .ready)
        engine.reply = { _ in [.delta("**Q:** Why us?\n"), .completed("**Q:** Why us?\nBecause…")] }
        return interview
    }

    private var askTexts: [String] {
        engine.sent.dropFirst().compactMap { if case .text(let t)? = $0.input.first { return t }; return nil }
    }

    // MARK: Selection → turn

    func testAskBeforeReadySendsNothing() async {
        let interview = InterviewController(env: env)
        await interview.ask()
        XCTAssertEqual(interview.status, "Still preparing…")
        XCTAssertTrue(engine.sent.isEmpty)
    }

    func testAskSendsTheInterviewersLatestWordsThenOnlyWhatIsNew() async throws {
        let interview = await preparedInterview()
        transcript = ([.init(text: "Thanks for joining.", tStartMs: 0, tEndMs: 1500)], "why do you want to work here", 6000)

        await interview.ask()

        XCTAssertEqual(askTexts, [InterviewPrompt.ask("Thanks for joining. why do you want to work here")])
        XCTAssertEqual(engine.sent.last?.effort, "low")
        let turn = try XCTUnwrap(interview.turns.last)
        XCTAssertEqual(turn.kind, .ask)
        XCTAssertEqual(turn.audioFromMs, 0); XCTAssertEqual(turn.audioToMs, 6000)
        XCTAssertEqual(turn.status, .completed)

        // The interim's final lands; nothing else was said → nothing new.
        transcript = ([.init(text: "Thanks for joining.", tStartMs: 0, tEndMs: 1500),
                       .init(text: "Why do you want to work here?", tStartMs: 2000, tEndMs: 6800)], "", 7000)
        await interview.ask()
        XCTAssertEqual(interview.status, "Nothing new since your last ask")
        XCTAssertEqual(askTexts.count, 1)

        transcript.segments.append(.init(text: "And when could you start?", tStartMs: 9000, tEndMs: 11000))
        transcript.audioMs = 12000
        await interview.ask()
        XCTAssertEqual(askTexts.last, InterviewPrompt.ask("And when could you start?"))
    }

    func testLastSentencesMode() async {
        env.config.interview.sendMode = .lastSentences
        env.config.interview.sendSentences = 1
        let interview = await preparedInterview()
        transcript = ([.init(text: "Hi there. Tell me about Kafka.", tStartMs: 0, tEndMs: 3000)], "", 3500)
        await interview.ask()
        XCTAssertEqual(askTexts, [InterviewPrompt.ask("Tell me about Kafka.")])
    }

    // MARK: Busy policy

    func testInterruptPolicyCutsTheOldAnswerAndAnswersTheNewQuestion() async throws {
        let interview = await preparedInterview()
        engine.holdIf = { $0.contains("first question") }
        transcript = ([], "first question", 1000)
        let first = Task { await interview.ask() }
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertTrue(interview.isStreaming)

        transcript = ([], "second question", 2000)
        await interview.ask()
        await first.value

        XCTAssertEqual(engine.interrupts, 1)
        XCTAssertEqual(interview.turns.map(\.status), [.interrupted, .completed])
        XCTAssertEqual(interview.turns.first?.answer, "Partial")
        XCTAssertEqual(askTexts.last, InterviewPrompt.ask("second question"))
    }

    func testQueuePolicyMergesFurtherAsksAndSendsThemAfterwards() async throws {
        env.config.interview.busyPolicy = .queue
        let interview = await preparedInterview()
        engine.holdIf = { $0.contains("one") && !$0.contains("two") }
        transcript = ([], "one", 1000)
        let first = Task { await interview.ask() }
        try await Task.sleep(nanoseconds: 100_000_000)

        transcript = ([], "two", 2000); await interview.ask()
        transcript = ([], "three", 3000); await interview.ask()
        XCTAssertEqual(interview.status, "Queued — sends when this answer finishes.")
        XCTAssertEqual(askTexts.count, 1, "nothing more is sent while the first answer streams")

        engine.release()
        await first.value
        XCTAssertEqual(engine.interrupts, 0)
        XCTAssertEqual(askTexts, [InterviewPrompt.ask("one"), InterviewPrompt.ask("two three")])
        XCTAssertEqual(interview.turns.map(\.status), [.completed, .completed])
    }

    // MARK: Typed / quick / regenerate

    func testTypedQuickAndRegenerateGoToTheSameThread() async throws {
        let interview = await preparedInterview()
        transcript = ([], "why us", 1000)
        await interview.ask()
        await interview.regenerate()
        await interview.sendQuick(Config.Interview.defaultQuickPrompts[0])
        await interview.sendTyped("  make it about Swift  ")

        XCTAssertEqual(Set(engine.sent.map(\.threadId)), ["thr1"])
        XCTAssertEqual(interview.turns.map(\.kind), [.ask, .regenerate, .quick, .typed])
        XCTAssertEqual(askTexts.suffix(3), [InterviewPrompt.regenerate, Config.Interview.defaultQuickPrompts[0].text,
                                             "make it about Swift"])
        let rec = try InterviewFiles.read(from: XCTUnwrap(interview.folder))
        XCTAssertEqual(rec.turns.count, 4)
        XCTAssertEqual(rec.prep.extraTurns, 1, "a typed turn before Start counts as prep chat")
    }

    // MARK: Clipboard helpers

    private func pngData(width: Int, height: Int) -> Data {
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                   bytesPerRow: 0, bitsPerPixel: 0)!
        return rep.representation(using: .png, properties: [:])!
    }

    func testClipboardImagesAreReadDownscaledSavedAndRemoved() throws {
        let pb = NSPasteboard(name: .init("lc-test-\(UUID().uuidString)"))
        defer { pb.releaseGlobally() }
        pb.clearContents()
        let image = NSPasteboardItem(); image.setData(pngData(width: 4000, height: 1000), forType: .png)
        let text = NSPasteboardItem(); text.setString("keep me", forType: .string)
        pb.writeObjects([image, text])

        XCTAssertEqual(ClipboardImages.imageCount(pb), 1)
        let snap = ClipboardImages.read(pb)
        XCTAssertEqual(snap.images.count, 1)
        XCTAssertFalse(snap.allItemsWereImages)
        let saved = try ClipboardImages.save(snap, turn: 3, to: tmp.appendingPathComponent("attachments"))
        XCTAssertEqual(saved.map(\.lastPathComponent), ["3-1.png"])
        let rep = try XCTUnwrap(NSBitmapImageRep(data: Data(contentsOf: saved[0])))
        XCTAssertEqual(max(rep.pixelsWide, rep.pixelsHigh), 2048, "longest side capped")

        XCTAssertTrue(ClipboardImages.removeImages(after: snap, pb))
        XCTAssertEqual(ClipboardImages.imageCount(pb), 0)
        XCTAssertEqual(pb.string(forType: .string), "keep me", "non-image items are written back verbatim")
    }

    func testClipboardIsLeftAloneIfTheUserCopiedSomethingNew() {
        let pb = NSPasteboard(name: .init("lc-test-\(UUID().uuidString)"))
        defer { pb.releaseGlobally() }
        pb.clearContents()
        let image = NSPasteboardItem(); image.setData(pngData(width: 10, height: 10), forType: .png)
        pb.writeObjects([image])
        let snap = ClipboardImages.read(pb)
        XCTAssertTrue(snap.allItemsWereImages)

        pb.clearContents(); pb.setString("copied later", forType: .string)
        XCTAssertFalse(ClipboardImages.removeImages(after: snap, pb))
        XCTAssertEqual(pb.string(forType: .string), "copied later")
    }

    func testAtMostFourImages() {
        let pb = NSPasteboard(name: .init("lc-test-\(UUID().uuidString)"))
        defer { pb.releaseGlobally() }
        pb.clearContents()
        pb.writeObjects((0..<6).map { _ in
            let i = NSPasteboardItem(); i.setData(pngData(width: 8, height: 8), forType: .png); return i
        })
        let snap = ClipboardImages.read(pb)
        XCTAssertEqual(snap.images.count, 4)
        XCTAssertEqual(snap.skipped, 2)
    }

    // MARK: Hotkey key codes

    func testEveryGrammarKeyMapsOnAMacExceptF21ToF24() {
        for key in (1...20).map({ "F\($0)" }) + ["A", "Z", "0", "9", "Space", "Enter", "Tab", "Up", "PageDown", "End"] {
            XCTAssertNotNil(HotkeyKeys.keyCode(for: key), key)
        }
        XCTAssertNil(HotkeyKeys.keyCode(for: "F21"))
    }
}
