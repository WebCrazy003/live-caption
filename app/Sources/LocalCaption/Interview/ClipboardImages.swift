import Foundation
import AppKit
import UniformTypeIdentifiers

/// Clipboard screenshots for an Ask (SPEC-14 §Clipboard screenshots). The only place the app
/// reads the clipboard, and only for images: called on an Ask when
/// `interview.include_clipboard_images` is on, in Interview mode. Text is never read; non-image
/// items are copied back verbatim when images are removed, never inspected, logged or sent.
enum ClipboardImages {
    static let maxImages = 4
    static let maxSide: CGFloat = 2048
    static let maxSourceBytes = 20 * 1024 * 1024

    private static let imageTypes: [NSPasteboard.PasteboardType] = [
        .png, .tiff, NSPasteboard.PasteboardType("public.jpeg"), NSPasteboard.PasteboardType("public.heic"),
    ]

    struct Snapshot {
        let changeCount: Int
        /// PNG data, already downscaled, in clipboard order.
        let images: [Data]
        /// Images left out (too big, unreadable, over the limit) — shown as a note.
        let skipped: Int
        let allItemsWereImages: Bool
        /// Non-image items, every type's data, for writing back after removal.
        let otherItems: [[NSPasteboard.PasteboardType: Data]]
    }

    /// How many items look like images — types only, no data (for the Ask button badge).
    static func imageCount(_ pb: NSPasteboard = .general) -> Int {
        (pb.pasteboardItems ?? []).filter(isImageItem).count
    }

    static func read(_ pb: NSPasteboard = .general) -> Snapshot {
        let items = pb.pasteboardItems ?? []
        var images: [Data] = []
        var others: [[NSPasteboard.PasteboardType: Data]] = []
        var skipped = 0
        for item in items {
            guard isImageItem(item) else {
                var copy: [NSPasteboard.PasteboardType: Data] = [:]
                for t in item.types { if let d = item.data(forType: t) { copy[t] = d } }
                others.append(copy)
                continue
            }
            guard images.count < maxImages, let source = imageData(item), source.count <= maxSourceBytes,
                  let png = downscaledPNG(source) else { skipped += 1; continue }
            images.append(png)
        }
        return Snapshot(changeCount: pb.changeCount, images: images, skipped: skipped,
                        allItemsWereImages: others.isEmpty, otherItems: others)
    }

    /// Write the snapshot's images as `<turn>-<n>.png` into the interview's attachments folder.
    static func save(_ snapshot: Snapshot, turn: Int, to folder: URL) throws -> [URL] {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return try snapshot.images.enumerated().map { i, png in
            let url = folder.appendingPathComponent("\(turn)-\(i + 1).png")
            try png.write(to: url, options: .atomic)
            return url
        }
    }

    /// Remove the sent images — only if the user hasn't copied anything since the read.
    @discardableResult
    static func removeImages(after snapshot: Snapshot, _ pb: NSPasteboard = .general) -> Bool {
        guard !snapshot.images.isEmpty, pb.changeCount == snapshot.changeCount else { return false }
        pb.clearContents()
        guard !snapshot.allItemsWereImages else { return true }
        let rebuilt = snapshot.otherItems.map { types -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (t, d) in types { item.setData(d, forType: t) }
            return item
        }
        pb.writeObjects(rebuilt)
        return true
    }

    // MARK: Helpers

    private static func isImageItem(_ item: NSPasteboardItem) -> Bool {
        if item.types.contains(where: imageTypes.contains) { return true }
        if let url = fileURL(item), let type = UTType(filenameExtension: url.pathExtension) {
            return type.conforms(to: .image)
        }
        return false
    }

    private static func fileURL(_ item: NSPasteboardItem) -> URL? {
        guard item.types.contains(.fileURL), let s = item.string(forType: .fileURL) else { return nil }
        return URL(string: s)
    }

    private static func imageData(_ item: NSPasteboardItem) -> Data? {
        for t in imageTypes { if let d = item.data(forType: t) { return d } }
        if let url = fileURL(item) {
            let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
            guard size <= maxSourceBytes else { return nil }
            return try? Data(contentsOf: url)
        }
        return nil
    }

    /// Re-encode as PNG with the longest side ≤ 2048 px.
    static func downscaledPNG(_ data: Data) -> Data? {
        guard let src = NSBitmapImageRep(data: data), let cg = src.cgImage else { return nil }
        let w = CGFloat(cg.width), h = CGFloat(cg.height)
        let scale = min(1, maxSide / max(w, h))
        if scale == 1 { return NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:]) }
        let nw = Int((w * scale).rounded()), nh = Int((h * scale).rounded())
        guard let ctx = CGContext(data: nil, width: nw, height: nh, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: nw, height: nh))
        guard let out = ctx.makeImage() else { return nil }
        return NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:])
    }
}
