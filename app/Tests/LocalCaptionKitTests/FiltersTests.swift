import XCTest
@testable import LocalCaptionKit

final class FiltersTests: XCTestCase {
    func testCleanStripsSpecialTokens() {
        XCTAssertEqual(Filters.clean("<|startoftranscript|>Hello<|endoftext|>"), "Hello")
        XCTAssertEqual(Filters.clean("[BLANK_AUDIO]  spaced   out "), "spaced out")
    }

    func testHallucinationBlocklist() {
        XCTAssertTrue(Filters.isHallucination("Thank you."))
        XCTAssertTrue(Filters.isHallucination("you"))
        XCTAssertTrue(Filters.isHallucination("   "))
    }

    func testHeavyRepetitionIsHallucination() {
        XCTAssertTrue(Filters.isHallucination("come come come come"))
        XCTAssertTrue(Filters.isHallucination("you you you"))
    }

    func testRealSpeechPasses() {
        XCTAssertFalse(Filters.isHallucination("Happy to be here, thanks for having me."))
    }

    func testCounts() {
        XCTAssertEqual(Filters.wordCount("one two three"), 3)
        XCTAssertEqual(Filters.sentenceCount("Hi. How are you? Good!"), 3)
    }

    func testMetadataQualityGate() {
        // Healthy segment passes.
        XCTAssertFalse(Filters.isLowQuality(avgLogprob: -0.3, noSpeechProb: 0.05, compressionRatio: 1.4))
        // Each threshold trips independently.
        XCTAssertTrue(Filters.isLowQuality(avgLogprob: -0.3, noSpeechProb: 0.75, compressionRatio: 1.4)) // silence
        XCTAssertTrue(Filters.isLowQuality(avgLogprob: -1.5, noSpeechProb: 0.05, compressionRatio: 1.4)) // garble
        XCTAssertTrue(Filters.isLowQuality(avgLogprob: -0.3, noSpeechProb: 0.05, compressionRatio: 3.0)) // repetition
    }
}
