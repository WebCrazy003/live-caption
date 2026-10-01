// Extract a PDF's text layer the way the app will (PDFKit, pages joined by blank lines).
// Usage: swift pdf_text.swift <in.pdf> > out.txt
import PDFKit
let url = URL(fileURLWithPath: CommandLine.arguments[1])
guard let doc = PDFDocument(url: url) else { FileHandle.standardError.write("cannot open PDF\n".data(using: .utf8)!); exit(1) }
let pages = (0..<doc.pageCount).compactMap { doc.page(at: $0)?.string?.trimmingCharacters(in: .whitespacesAndNewlines) }
print(pages.filter { !$0.isEmpty }.joined(separator: "\n\n"))
