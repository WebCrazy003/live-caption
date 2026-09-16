import XCTest
import SwiftUI
import AppKit
@testable import LocalCaption

@MainActor
final class CaptionSelectionTests: XCTestCase {
    private func view(_ paragraphs: [String], current: String = "", hypothesis: String = "") -> CaptionTextView {
        CaptionTextView(paragraphs: paragraphs, current: current, hypothesis: hypothesis,
                        fontSize: 18, autoScroll: true, jumpRequest: 0, following: .constant(true))
    }

    func testCrossParagraphSelectionSurvivesLiveUpdates() throws {
        let initial = view(["First paragraph.", "Second paragraph."], current: "Current", hypothesis: "draft")
        let coordinator = initial.makeCoordinator()
        let scroll = initial.makeScrollView(coordinator: coordinator)
        scroll.frame = NSRect(x: 0, y: 0, width: 500, height: 300)
        initial.updateContent(scroll, coordinator: coordinator)
        let text = try XCTUnwrap(scroll.documentView as? NSTextView)
        XCTAssertFalse(text.isEditable)
        XCTAssertTrue(text.isSelectable)
        let selected = (text.string as NSString).range(of: "paragraph.\n\nSecond")
        text.setSelectedRange(selected)
        let updated = view(["First paragraph.", "Second paragraph."], current: "Current", hypothesis: "draft grows")
        updated.updateContent(scroll, coordinator: coordinator)
        XCTAssertEqual(text.selectedRange(), selected)
        XCTAssertEqual((text.string as NSString).substring(with: text.selectedRange()), "paragraph.\n\nSecond")
        XCTAssertFalse(coordinator.isFollowing)
        XCTAssertTrue(text.string.hasSuffix("Current draft grows"))
    }

    func testScrollUpPausesFollowingAndJumpReturnsToLatest() throws {
        let paragraphs = (0..<100).map { "Paragraph \($0) with enough caption text to occupy a line." }
        let initial = view(paragraphs)
        let coordinator = initial.makeCoordinator()
        let scroll = initial.makeScrollView(coordinator: coordinator)
        scroll.frame = NSRect(x: 0, y: 0, width: 400, height: 200)
        initial.updateContent(scroll, coordinator: coordinator)
        let text = try XCTUnwrap(scroll.documentView as? NSTextView)
        XCTAssertGreaterThan(text.frame.height, scroll.contentView.bounds.height)
        scroll.contentView.scroll(to: .zero)
        coordinator.scrolled()
        XCTAssertFalse(coordinator.isFollowing)
        let updated = view(paragraphs, current: "New caption")
        updated.updateContent(scroll, coordinator: coordinator)
        XCTAssertEqual(scroll.contentView.bounds.origin.y, 0, accuracy: 1)
        let jump = CaptionTextView(paragraphs: paragraphs, current: "New caption", hypothesis: "",
                                   fontSize: 18, autoScroll: true, jumpRequest: 1, following: .constant(false))
        jump.updateContent(scroll, coordinator: coordinator)
        XCTAssertTrue(coordinator.isFollowing)
        XCTAssertGreaterThan(scroll.contentView.bounds.origin.y, 0)
    }

    func testProvisionalFinalizationAndUnicodeKeepCorrectTextAndStyling() throws {
        let initial = view([], current: "Hello 👋", hypothesis: "world")
        let coordinator = initial.makeCoordinator()
        let scroll = initial.makeScrollView(coordinator: coordinator)
        initial.updateContent(scroll, coordinator: coordinator)
        let text = try XCTUnwrap(scroll.documentView as? NSTextView)
        let storage = try XCTUnwrap(text.textStorage)
        XCTAssertEqual(storage.attribute(.foregroundColor, at: storage.length - 1, effectiveRange: nil) as? NSColor,
                       .secondaryLabelColor)
        let finalized = view(["Hello 👋 world"], hypothesis: "Next")
        finalized.updateContent(scroll, coordinator: coordinator)
        XCTAssertEqual(text.string, "Hello 👋 world\n\nNext")
        XCTAssertEqual(storage.attribute(.foregroundColor, at: 10, effectiveRange: nil) as? NSColor, .labelColor)
        let reset = view([])
        reset.updateContent(scroll, coordinator: coordinator)
        XCTAssertEqual(text.string, "")
        XCTAssertEqual(text.selectedRange().length, 0)
    }
}
