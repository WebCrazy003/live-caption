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
}
