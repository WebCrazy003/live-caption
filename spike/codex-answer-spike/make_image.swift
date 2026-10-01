// Render a fake "screen-shared coding question" to PNG for the S0.5 image probe.
// Usage: swift make_image.swift <out.png>
import AppKit

let text = """
Interview exercise — 15 minutes

Given a list of Order(id, customerId, amount, createdAt),
return the top 3 customers by total spend in the last 30 days.

Use Java 17 streams. Discuss complexity and how you would
do this in SQL for 50 million rows.
"""
let size = NSSize(width: 1200, height: 520)
let image = NSImage(size: size)
image.lockFocus()
NSColor.white.setFill()
NSRect(origin: .zero, size: size).fill()
let style = NSMutableParagraphStyle(); style.lineSpacing = 8
let attrs: [NSAttributedString.Key: Any] = [
    .font: NSFont.monospacedSystemFont(ofSize: 30, weight: .regular),
    .foregroundColor: NSColor.black, .paragraphStyle: style,
]
(text as NSString).draw(in: NSRect(x: 40, y: 30, width: 1120, height: 460), withAttributes: attrs)
image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
