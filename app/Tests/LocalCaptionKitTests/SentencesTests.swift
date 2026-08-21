import XCTest
@testable import LocalCaptionKit

final class SentencesTests: XCTestCase {
    func testSplitKeepsPunctuation() {
        XCTAssertEqual(Sentences.split("Hi there. How are you? Good!"),
                       ["Hi there.", "How are you?", "Good!"])
    }

    func testSplitTrailingFragment() {
        XCTAssertEqual(Sentences.split("Done. Half a thought"),
                       ["Done.", "Half a thought"])
    }

    func testLastN() {
        let t = "One. Two. Three. Four."
        XCTAssertEqual(Sentences.lastN(t, n: 2), "Three. Four.")
        XCTAssertEqual(Sentences.lastN(t, n: 10), "One. Two. Three. Four.")
        XCTAssertEqual(Sentences.lastN(t, n: 0), "")
    }

    func testNoPunctuation() {
        XCTAssertEqual(Sentences.lastN("just one line", n: 5), "just one line")
    }

    func testLastNIncludesProvisionalCaption() {
        XCTAssertEqual(
            Sentences.lastN("One. Two.", appending: "temporary words", n: 2),
            "Two. temporary words"
        )
    }

    func testLastNHandlesOnlyProvisionalCaption() {
        XCTAssertEqual(
            Sentences.lastN("", appending: "  available immediately  ", n: 10),
            "available immediately"
        )
    }
}
