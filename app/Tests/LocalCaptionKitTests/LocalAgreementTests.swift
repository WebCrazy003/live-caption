import XCTest
@testable import LocalCaptionKit

final class LocalAgreementTests: XCTestCase {
    private func words(_ s: String) -> [String] { s.split(separator: " ").map(String.init) }

    func testCommitsAgreedPrefix() {
        var la = LocalAgreement()
        _ = la.update(words("the quick brown"))
        let r = la.update(words("the quick brown fox"))
        // "the quick brown" agreed by both hypotheses → committed; "fox" provisional.
        XCTAssertEqual(r.committed, ["the", "quick", "brown"])
        XCTAssertEqual(r.provisional, ["fox"])
    }

    func testCommittedNeverShrinks() {
        var la = LocalAgreement()
        _ = la.update(words("hello there friend"))
        _ = la.update(words("hello there friend indeed"))   // commits "hello there friend"
        let r = la.update(words("hello there"))             // shorter/divergent hypothesis
        XCTAssertEqual(r.committed, ["hello", "there", "friend"], "committed prefix must not shrink")
    }

    func testFirstHypothesisIsAllProvisional() {
        var la = LocalAgreement()
        let r = la.update(words("just starting"))
        XCTAssertTrue(r.committed.isEmpty)
        XCTAssertEqual(r.provisional, ["just", "starting"])
    }

    func testResetClears() {
        var la = LocalAgreement()
        _ = la.update(words("a b"))
        _ = la.update(words("a b"))
        la.reset()
        let r = la.update(words("c d"))
        XCTAssertTrue(r.committed.isEmpty)
    }
}
