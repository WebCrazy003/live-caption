import XCTest
@testable import LocalCaptionKit

final class SummaryTests: XCTestCase {

    func testWellFormedCard() {
        let raw = """
        MAIN: Fix the checkout speed
        - It's slow on mobile
        - Customers complain on Twitter
        WANT: Send a time estimate
        """
        let c = SummaryCard.parse(raw, id: 1)
        XCTAssertEqual(c.main, "Fix the checkout speed")
        XCTAssertEqual(c.bullets, ["It's slow on mobile", "Customers complain on Twitter"])
        XCTAssertEqual(c.want, "Send a time estimate")
        XCTAssertFalse(c.isEmpty)
        XCTAssertEqual(c.id, 1)
    }

    func testStripsSpecialTokens() {
        let raw = "MAIN: Refund the extra charge<|eot_id|>\nWANT: Look at the account today<|eot_id|>"
        let c = SummaryCard.parse(raw, id: 0)
        XCTAssertEqual(c.main, "Refund the extra charge")
        XCTAssertEqual(c.want, "Look at the account today")
    }

    func testFlattensNestedBulletsAndStripsWantDash() {
        // The exact "- -" / "WANT: - x" degeneracy the 1B spike produced on block 1.
        let raw = """
        MAIN: Fix the checkout speed
        WANT: - A proper estimate
        - - How long will it take
        - - To fix the checkout speed
        """
        let c = SummaryCard.parse(raw, id: 2)
        XCTAssertEqual(c.main, "Fix the checkout speed")
        XCTAssertEqual(c.want, "A proper estimate")            // leading "- " stripped
        XCTAssertTrue(c.bullets.contains("How long will it take"))
        XCTAssertTrue(c.bullets.contains("To fix the checkout speed"))
    }

    func testDeduplicatesRepetitiveBullets() {
        // The block-2 repetition failure mode: collapse near-identical bullets.
        let raw = """
        MAIN: I need help with my account
        - I need my account to be refunded
        - I need my account to be refunded
        - I need my account to be fixed
        """
        let c = SummaryCard.parse(raw, id: 3)
        XCTAssertEqual(c.bullets.count, 2)
    }

    func testCapsBullets() {
        let raw = "MAIN: x\n- a\n- b\n- c\n- d\n- e\n- f"
        let c = SummaryCard.parse(raw, id: 0, maxBullets: 3)
        XCTAssertEqual(c.bullets.count, 3)
    }

    func testFallbackMainWhenNoMainLine() {
        let c = SummaryCard.parse("They want a corrected invoice before Friday", id: 0)
        XCTAssertEqual(c.main, "They want a corrected invoice before Friday")
        XCTAssertTrue(c.bullets.isEmpty)
        XCTAssertEqual(c.want, "")
    }

    func testFallbackPromotesFirstBullet() {
        let c = SummaryCard.parse("- audit the hot paths\n- add a cache", id: 0)
        XCTAssertEqual(c.main, "audit the hot paths")
        XCTAssertEqual(c.bullets, ["add a cache"])
    }

    func testEmptyOutput() {
        let c = SummaryCard.parse("   \n  \n", id: 0)
        XCTAssertTrue(c.isEmpty)
    }

    func testNothingImportantCard() {
        let c = SummaryCard.parse("MAIN: (nothing important)\nWANT: -", id: 0)
        XCTAssertEqual(c.main, "(nothing important)")
        XCTAssertEqual(c.want, "")               // "-" placeholder dropped
        XCTAssertTrue(c.bullets.isEmpty)
    }

    func testPromptWrapsChunk() {
        let p = SummaryPrompt.user("  hello world  ")
        XCTAssertTrue(p.contains("\"hello world\""))   // trimmed + quoted
        XCTAssertTrue(p.contains("Now write the card."))
        XCTAssertFalse(SummaryPrompt.system.isEmpty)
    }

    // MARK: New SUMMARY/TODO shape (SPEC-10 refresh)

    func testParsesSummaryTodoFormat() {
        let raw = "SUMMARY: They want the design file tomorrow.\nTODO: Send the design file."
        let c = SummaryCard.parse(raw, id: 0)
        XCTAssertEqual(c.main, "They want the design file tomorrow.")
        XCTAssertEqual(c.want, "Send the design file.")
    }

    func testPromptIncludesBackground() {
        let p = SummaryPrompt.user("new text", background: "earlier text")
        XCTAssertTrue(p.contains("earlier text"))
        XCTAssertTrue(p.contains("new text"))
        XCTAssertTrue(p.contains("Background"))
    }

    func testPromptOmitsBackgroundWhenEmpty() {
        let p = SummaryPrompt.user("new text", background: "   ")
        XCTAssertFalse(p.contains("Background"))
    }

    // MARK: ToDo gate — the anti-hallucination guard

    func testLooksLikeRequest() {
        XCTAssertTrue(SummaryCard.looksLikeRequest("Could you send the file?"))
        XCTAssertTrue(SummaryCard.looksLikeRequest("please email me the quote"))
        XCTAssertTrue(SummaryCard.looksLikeRequest("Is that ok?"))            // has "?"
        XCTAssertFalse(SummaryCard.looksLikeRequest("The weather is nice and we went hiking."))
        XCTAssertFalse(SummaryCard.looksLikeRequest("The quarterly numbers looked strong."))
    }

    func testTodoDroppedWhenSourceHasNoRequest() {
        // Model invented a task from a plain statement — the gate must strip it.
        let raw = "SUMMARY: The weather is nice.\nTODO: Go hiking"
        let c = SummaryCard.parse(raw, id: 0, requestContext: "The weather is nice and I went hiking.")
        XCTAssertEqual(c.main, "The weather is nice.")
        XCTAssertEqual(c.want, "")               // dropped: no ask in the source
    }

    func testTodoKeptWhenSourceHasRequest() {
        let raw = "SUMMARY: They need the file.\nTODO: Send the file by tomorrow."
        let c = SummaryCard.parse(raw, id: 0, requestContext: "Can you please send me the file by tomorrow?")
        XCTAssertEqual(c.want, "Send the file by tomorrow.")   // kept: source asks
    }
}
