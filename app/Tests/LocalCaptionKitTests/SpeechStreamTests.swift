import XCTest
@testable import LocalCaptionKit

final class SpeechStreamTests: XCTestCase {
    private func words(_ text: String, first: Double = 0, step: Double = 1) -> [CaptionWord] {
        text.split(separator: " ").enumerated().map {
            CaptionWord(String($0.element), start: first + Double($0.offset) * step,
                        end: first + Double($0.offset) * step + step * 0.8)
        }
    }

    func testMovingWindowsKeepAdvancing() {
        var caption = RollingCaption()
        caption.update(words("one two three four five six"), startSample: 0, endSample: 96000)
        caption.update(words("one two three four five six seven"), startSample: 0, endSample: 112000)
        caption.update(words("two three four five six seven eight"), startSample: 16000, endSample: 128000)
        caption.update(words("three four five six seven eight nine"), startSample: 32000, endSample: 144000)
        caption.update(words("four five six seven eight nine ten"), startSample: 48000, endSample: 160000)
        XCTAssertEqual(caption.text, "one two three four five six seven eight nine ten")
    }

    func testRepeatedPhrasesMatchByTimeAndAllowPunctuationCorrections() {
        var caption = RollingCaption()
        caption.update(words("we can we can do this", step: 0.3), startSample: 0, endSample: 32000)
        caption.update(words("we can do this, today", step: 0.3), startSample: 9600, endSample: 40000)
        XCTAssertEqual(caption.text, "we can we can do this, today")
    }

    func testShorterSameOriginCorrectsOnlyItsWindowAndEmptyDoesNotClear() {
        var caption = RollingCaption()
        caption.update(words("one two three"), startSample: 0, endSample: 48000)
        caption.update(words("two three four"), startSample: 16000, endSample: 64000)
        caption.update(words("two corrected"), startSample: 16000, endSample: 80000)
        XCTAssertEqual(caption.text, "one two corrected")
        XCTAssertFalse(caption.update([], startSample: 16000, endSample: 90000))
        XCTAssertEqual(caption.text, "one two corrected")
    }

    func testNoLexicalOverlapUsesAudioTimeAndRejectsOlderResults() {
        var caption = RollingCaption()
        caption.update(words("one two three"), startSample: 0, endSample: 48000)
        caption.update(words("corrected words"), startSample: 16000, endSample: 64000)
        XCTAssertEqual(caption.text, "one corrected words")
        XCTAssertFalse(caption.update(words("stale"), startSample: 0, endSample: 16000))
    }

    func testSilenceNeverEnqueuesDecodeAndPrerollIsBounded() {
        var segmenter = SpeechSegmenter(session: UUID(), tuning: .init(maxUtteranceS: 5))
        for second in 0..<300 {
            XCTAssertTrue(segmenter.append(Array(repeating: 0, count: 16000), now: Double(second)).isEmpty)
        }
        let partials = segmenter.append(Array(repeating: 0.1, count: 8000), now: 300.5)
        XCTAssertEqual(partials.count, 1)
        XCTAssertFalse(partials[0].isFinal)
        XCTAssertEqual(partials[0].startSample, 300 * 16000 - 3200)
        let finals = segmenter.finish(now: 300.5).filter(\.isFinal)
        XCTAssertEqual(finals.count, 1)
        XCTAssertEqual(finals[0].audio.count, 11200)
    }

    func testUtteranceCapAndFinishPreserveEverySpeechSample() {
        var segmenter = SpeechSegmenter(session: UUID(), tuning: .init(maxUtteranceS: 20))
        let count = 45 * 16000 + 731
        var requests = segmenter.append(Array(repeating: 0.1, count: count), now: 45)
        requests += segmenter.finish(now: 45)
        let finals = requests.filter(\.isFinal)
        XCTAssertEqual(finals.map(\.utterance), [0, 1, 2])
        XCTAssertEqual(finals.map(\.audio.count).reduce(0, +), count)
        XCTAssertEqual(finals[1].startSample, finals[0].endSample)
        XCTAssertEqual(finals[2].startSample, finals[1].endSample)
        XCTAssertTrue(requests.filter { !$0.isFinal }.allSatisfy { $0.audio.count <= 96000 })
        XCTAssertTrue(segmenter.finish(now: 46).isEmpty)
    }

    func testQuietFramesEndpointOnlyAfterSpeech() {
        var segmenter = SpeechSegmenter(session: UUID())
        _ = segmenter.append(Array(repeating: 0.1, count: 16000), now: 1)
        XCTAssertTrue(segmenter.append(Array(repeating: 0, count: 8000), now: 1.5).isEmpty)
        let requests = segmenter.append(Array(repeating: 0, count: 1600), now: 1.6)
        XCTAssertEqual(requests.filter(\.isFinal).count, 1)
        let transitions = segmenter.drainTransitions()
        XCTAssertEqual(transitions.map(\.started), [true, false])
        XCTAssertEqual(transitions.map(\.sample), [0, 25600])
        XCTAssertTrue(segmenter.drainTransitions().isEmpty)
        XCTAssertTrue(segmenter.append(Array(repeating: 0, count: 160000), now: 11.6).isEmpty)
    }

    func testCaptureOverflowIsBoundedLatchedAndReported() {
        let buffer = CaptureBuffer(capacity: 100)
        buffer.append(Array(repeating: 1, count: 150), now: 1)
        let first = buffer.drain(now: 2)
        XCTAssertEqual(first.samples.count, 100)
        XCTAssertEqual(first.droppedSamples, 50)
        XCTAssertEqual(first.oldestAgeMs, 1000)
        buffer.append(Array(repeating: 1, count: 20), now: 3)
        let second = buffer.drain(now: 3)
        XCTAssertTrue(second.samples.isEmpty)
        XCTAssertEqual(second.droppedSamples, 20)
    }

    func testProcessorDrainsResidualAudioAtStop() async {
        let buffer = CaptureBuffer()
        let processor = CaptureProcessor(buffer: buffer, segmenter: SpeechSegmenter(session: UUID()))
        buffer.append(Array(repeating: 0.1, count: 1731))
        let output = await processor.poll(finish: true)
        XCTAssertEqual(output.totalSamples, 1731)
        XCTAssertEqual(output.requests.filter(\.isFinal).first?.audio.count, 1731)
        let again = await processor.poll(finish: true)
        XCTAssertTrue(again.requests.isEmpty)
    }
}
