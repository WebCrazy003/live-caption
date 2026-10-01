import SwiftUI

/// Renders the small Markdown subset the coach writes — headings, bullets, numbered lists,
/// paragraphs, inline bold/italic/code — at a given font size. SwiftUI's `Text(markdown:)`
/// only does inline syntax, so block structure is handled here line by line.
struct MarkdownText: View {
    let markdown: String
    var fontSize: Double = 15

    var body: some View {
        VStack(alignment: .leading, spacing: fontSize * 0.35) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                view(for: block)
            }
        }
        .textSelection(.enabled)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private enum Block {
        case heading(String, level: Int)
        case bullet(String, indent: Int)
        case numbered(String, number: String)
        case paragraph(String)
        case code(String)
    }

    private var blocks: [Block] {
        var out: [Block] = []
        var paragraph: [String] = []
        var code: [String]?
        func flush() {
            if !paragraph.isEmpty { out.append(.paragraph(paragraph.joined(separator: " "))); paragraph = [] }
        }
        for raw in markdown.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            if raw.trimmingCharacters(in: .whitespaces).hasPrefix("```") {
                if let c = code { out.append(.code(c.joined(separator: "\n"))); code = nil }
                else { flush(); code = [] }
                continue
            }
            if code != nil { code!.append(raw); continue }
            let line = raw.trimmingCharacters(in: .whitespaces)
            let indent = (raw.prefix { $0 == " " }.count) / 2
            if line.isEmpty { flush(); continue }
            if let hashes = line.firstIndex(where: { $0 != "#" }), line.hasPrefix("#"),
               line[hashes] == " " {
                flush(); out.append(.heading(String(line[hashes...]).trimmingCharacters(in: .whitespaces),
                                             level: line.distance(from: line.startIndex, to: hashes)))
            } else if line.hasPrefix("- ") || line.hasPrefix("* ") || line.hasPrefix("• ") {
                flush(); out.append(.bullet(String(line.dropFirst(2)), indent: indent))
            } else if let dot = line.firstIndex(where: { $0 == "." || $0 == ")" }),
                      !line[..<dot].isEmpty, line[..<dot].allSatisfy(\.isNumber),
                      line.index(after: dot) < line.endIndex, line[line.index(after: dot)] == " " {
                flush(); out.append(.numbered(String(line[line.index(dot, offsetBy: 2)...]), number: String(line[..<dot])))
            } else {
                paragraph.append(line)
            }
        }
        if let c = code { out.append(.code(c.joined(separator: "\n"))) }
        flush()
        return out
    }

    @ViewBuilder private func view(for block: Block) -> some View {
        switch block {
        case .heading(let s, let level):
            inline(s).font(.system(size: fontSize * (level <= 2 ? 1.15 : 1.0), weight: .semibold))
                .padding(.top, fontSize * 0.3)
        case .bullet(let s, let indent):
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("•").font(.system(size: fontSize))
                inline(s).font(.system(size: fontSize))
            }
            .padding(.leading, CGFloat(indent) * fontSize)
        case .numbered(let s, let n):
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("\(n).").font(.system(size: fontSize)).monospacedDigit()
                inline(s).font(.system(size: fontSize))
            }
        case .paragraph(let s):
            inline(s).font(.system(size: fontSize))
        case .code(let s):
            Text(s).font(.system(size: fontSize * 0.9, design: .monospaced))
                .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
        }
    }

    private func inline(_ s: String) -> Text {
        if let a = try? AttributedString(markdown: s, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)) {
            return Text(a)
        }
        return Text(s)
    }
}
