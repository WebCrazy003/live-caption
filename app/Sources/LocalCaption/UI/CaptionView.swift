import SwiftUI
import AppKit
import OSLog

/// One native text document allows selection to span paragraph boundaries.
struct CaptionView: View {
    let paragraphs: [String]
    let current: String
    let hypothesis: String
    let isReady: Bool
    let fontSize: Double
    let autoScroll: Bool
    @State private var following = true
    @State private var jumpRequest = 0

    var body: some View {
        CaptionTextView(paragraphs: paragraphs, current: current, hypothesis: hypothesis,
                        fontSize: fontSize, autoScroll: autoScroll,
                        jumpRequest: jumpRequest, following: $following)
            .overlay(alignment: .topLeading) {
                if paragraphs.isEmpty && current.isEmpty && hypothesis.isEmpty && isReady {
                    Text("Play some audio…")
                        .font(.system(size: fontSize)).foregroundStyle(.tertiary)
                        .padding(5).allowsHitTesting(false)
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if !following {
                    Button("Jump to latest") { jumpRequest += 1 }
                        .padding(8)
                }
            }
    }
}

struct CaptionTextView: NSViewRepresentable {
    let paragraphs: [String]
    let current: String
    let hypothesis: String
    let fontSize: Double
    let autoScroll: Bool
    let jumpRequest: Int
    @Binding var following: Bool

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        makeScrollView(coordinator: context.coordinator)
    }

    func makeScrollView(coordinator: Coordinator) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        let text = NSTextView(frame: .zero)
        text.delegate = coordinator
        text.isEditable = false
        text.isSelectable = true
        text.isRichText = false
        text.drawsBackground = false
        text.minSize = .zero
        text.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        text.isVerticallyResizable = true
        text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]
        text.textContainerInset = NSSize(width: 0, height: 5)
        text.textContainer?.widthTracksTextView = true
        text.textContainer?.containerSize = NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude)
        text.setAccessibilityLabel("Live captions")
        scroll.documentView = text
        coordinator.scroll = scroll
        scroll.contentView.postsBoundsChangedNotifications = true
        coordinator.observer = NotificationCenter.default.addObserver(
            forName: NSView.boundsDidChangeNotification, object: scroll.contentView, queue: .main
        ) { [weak coordinator] _ in
            coordinator?.scrolled()
        }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        updateContent(scroll, coordinator: context.coordinator)
    }

    func updateContent(_ scroll: NSScrollView, coordinator: Coordinator) {
        coordinator.parent = self
        guard let text = scroll.documentView as? NSTextView, let storage = text.textStorage else { return }
        coordinator.updating = true
        defer { coordinator.updating = false }
        let committed = (paragraphs + (current.isEmpty ? [] : [current])).joined(separator: "\n\n")
        let separator = hypothesis.isEmpty || committed.isEmpty ? "" : (current.isEmpty ? "\n\n" : " ")
        let full = committed + separator + hypothesis
        let old = text.string
        let selections = text.selectedRanges
        let origin = scroll.contentView.bounds.origin
        let jump = coordinator.lastJump != jumpRequest
        coordinator.lastJump = jumpRequest
        let shouldFollow = jump || (autoScroll && coordinator.isFollowing && text.selectedRange().length == 0)

        if old != full || coordinator.lastFont != fontSize || coordinator.lastCommitted != committed.utf16.count {
            // Replace only the changed suffix; completed text stays in the text storage.
            var prefix = 0
            for (a, b) in zip(old, full) {
                guard a == b else { break }
                prefix += String(a).utf16.count
            }
            let style = NSMutableParagraphStyle()
            style.lineSpacing = 3
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: fontSize),
                .foregroundColor: NSColor.labelColor, .paragraphStyle: style
            ]
            storage.beginEditing()
            if old != full {
                let suffix = (full as NSString).substring(from: prefix)
                storage.replaceCharacters(in: NSRange(location: prefix, length: storage.length - prefix),
                                          with: NSAttributedString(string: suffix, attributes: attributes))
            }
            let restyleStart = coordinator.lastFont != fontSize ? 0 : min(prefix, max(0, coordinator.lastCommitted))
            storage.setAttributes(attributes, range: NSRange(location: restyleStart, length: storage.length - restyleStart))
            let provisionalStart = (committed + separator).utf16.count
            if provisionalStart < storage.length {
                storage.addAttribute(.foregroundColor, value: NSColor.secondaryLabelColor,
                                     range: NSRange(location: provisionalStart, length: storage.length - provisionalStart))
            }
            storage.endEditing()
            text.selectedRanges = selections.map {
                let range = $0.rangeValue
                let start = min(range.location, storage.length)
                return NSValue(range: NSRange(location: start, length: min(range.length, storage.length - start)))
            }
            coordinator.lastFont = fontSize
            coordinator.lastCommitted = committed.utf16.count
            Coordinator.logger.info("caption_observed")
        }
        if let container = text.textContainer { text.layoutManager?.ensureLayout(for: container) }
        if shouldFollow {
            if jump { text.setSelectedRange(NSRange(location: storage.length, length: 0)) }
            text.scrollRangeToVisible(NSRange(location: storage.length, length: 0))
            coordinator.setFollowing(true)
        } else {
            scroll.contentView.scroll(to: origin)
            scroll.reflectScrolledClipView(scroll.contentView)
            if text.selectedRange().length > 0 { coordinator.setFollowing(false) }
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        static let logger = Logger(subsystem: "com.livecaption.app", category: "caption-latency")
        var parent: CaptionTextView
        weak var scroll: NSScrollView?
        var observer: NSObjectProtocol?
        var updating = false
        var isFollowing = true
        var lastJump = 0
        var lastFont: Double?
        var lastCommitted = -1
        init(_ parent: CaptionTextView) { self.parent = parent; super.init() }
        deinit { if let observer { NotificationCenter.default.removeObserver(observer) } }

        func setFollowing(_ value: Bool) {
            guard isFollowing != value else { return }
            isFollowing = value
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.parent.following = self.isFollowing
            }
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard !updating, let text = notification.object as? NSTextView else { return }
            if text.selectedRange().length > 0 { setFollowing(false) }
        }

        func scrolled() {
            guard !updating, let scroll, let text = scroll.documentView as? NSTextView else { return }
            let atBottom = scroll.contentView.bounds.maxY >= text.bounds.maxY - 8
            setFollowing(atBottom && text.selectedRange().length == 0)
        }
    }
}
