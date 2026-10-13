import SwiftUI
import AppKit
import WebKit

/// A fenced block in an answer (owner, 2026-10-13): code, or a Mermaid diagram.
struct CodeBlock: Equatable {
    /// The fence's tag, lowercased (`swift`, `mermaid`); empty when untagged.
    let language: String
    let code: String
    /// False while the answer is still streaming the block.
    var closed = true

    var isMermaid: Bool { language == "mermaid" }

    var title: String {
        if isMermaid { return "Diagram" }
        return language.isEmpty ? "Code" : language.prefix(1).uppercased() + language.dropFirst()
    }
}

/// The answer panel's coloured box for a code block or a Mermaid diagram: a title bar with Copy
/// and Maximize, the code, or the rendered diagram. Maximize opens it in the Code & Diagram window.
struct CodeBlockPanel: View {
    let block: CodeBlock
    let fontSize: Double
    @Environment(\.openWindow) private var openWindow
    @State private var copied = false
    @State private var showingSource = false

    private var tint: Color { block.isMermaid ? .purple : .blue }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            titleBar
            Divider().overlay(tint.opacity(0.25))
            content.padding(8)
        }
        .background(tint.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(tint.opacity(0.35)))
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var titleBar: some View {
        HStack(spacing: 8) {
            Label(block.title, systemImage: block.isMermaid ? "point.3.connected.trianglepath.dotted"
                                                            : "chevron.left.forwardslash.chevron.right")
                .font(.caption.weight(.semibold)).foregroundStyle(tint)
            if !block.closed { ProgressView().controlSize(.mini) }
            Spacer()
            if block.isMermaid && block.closed {
                Button { showingSource.toggle() } label: {
                    Image(systemName: showingSource ? "photo" : "text.alignleft")
                }
                .help(showingSource ? "Show the diagram" : "Show the Mermaid source")
            }
            Button { copy(block.code) { copied = $0 } } label: {
                Image(systemName: copied ? "checkmark" : "doc.on.doc")
            }
            .help(block.isMermaid ? "Copy the Mermaid source" : "Copy the code")
            Button {
                CodeViewer.shared.show(block)
                openWindow(id: CodeViewer.windowID)
            } label: { Image(systemName: "arrow.up.left.and.arrow.down.right") }
            .help("Open in its own window")
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 8).padding(.vertical, 5)
    }

    @ViewBuilder private var content: some View {
        if block.isMermaid && block.closed && !showingSource {
            MermaidDiagram(source: block.code, interactive: false, maxHeight: 420)
        } else {
            CodeText(code: block.code, fontSize: fontSize * 0.9)
        }
    }
}

/// Monospaced, selectable code; long lines scroll sideways instead of wrapping.
struct CodeText: View {
    let code: String
    let fontSize: Double

    var body: some View {
        ScrollView(.horizontal, showsIndicators: true) {
            Text(code)
                .font(.system(size: fontSize, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize(horizontal: true, vertical: false)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// Puts `text` on the clipboard and flashes the button's checkmark.
@MainActor
func copy(_ text: String, flash: @escaping (Bool) -> Void) {
    let pb = NSPasteboard.general
    pb.clearContents()
    pb.setString(text, forType: .string)
    flash(true)
    DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { flash(false) }
}

// MARK: - Code & Diagram window

/// What the Code & Diagram window shows: the block whose Maximize was clicked last.
@MainActor
final class CodeViewer: ObservableObject {
    static let shared = CodeViewer()
    static let windowID = "code-viewer"

    @Published private(set) var block: CodeBlock?

    func show(_ block: CodeBlock) { self.block = block }
}

/// The external panel: the code, or the rendered diagram above its source. Close it like any window.
struct CodeViewerWindow: View {
    @ObservedObject var viewer = CodeViewer.shared
    @EnvironmentObject var env: AppEnvironment
    @State private var copied = false

    var body: some View {
        Group {
            if let block = viewer.block {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text(block.title).font(.headline)
                        Spacer()
                        Button { copy(block.code) { copied = $0 } } label: {
                            Label(copied ? "Copied" : (block.isMermaid ? "Copy source" : "Copy code"),
                                  systemImage: copied ? "checkmark" : "doc.on.doc")
                        }
                    }
                    if block.isMermaid {
                        VSplitView {
                            MermaidDiagram(source: block.code, interactive: true, maxHeight: nil)
                                .frame(minHeight: 200)
                            codeBox(block).frame(minHeight: 80)
                        }
                    } else {
                        codeBox(block)
                    }
                }
                .padding()
            } else {
                Text("Click Maximize on a code block or diagram in an answer.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(minWidth: 420, minHeight: 300)
        .navigationTitle(viewer.block?.title ?? "Code & Diagram")
        .background(WindowAccessor(alwaysOnTop: env.config.window.alwaysOnTop, opacity: 1,
                                   autosaveName: "LocalCaptionCodeViewer"))
    }

    private func codeBox(_ block: CodeBlock) -> some View {
        ScrollView([.vertical, .horizontal]) {
            Text(block.code)
                .font(.system(size: CGFloat(env.config.caption.fontSize) * 0.85, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize()
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
    }
}

// MARK: - Mermaid rendering

/// A Mermaid diagram drawn by mermaid.js in a web view. Inline (`interactive == false`) it sizes
/// to the diagram and passes scrolling to the answer list; in the window it fills the space and
/// pinch-zooms. If the diagram can't be drawn, its source is shown with the reason.
struct MermaidDiagram: View {
    let source: String
    let interactive: Bool
    let maxHeight: CGFloat?
    @Environment(\.colorScheme) private var colorScheme
    @State private var height: CGFloat = 120
    @State private var failure: String?

    var body: some View {
        if let failure {
            VStack(alignment: .leading, spacing: 6) {
                Label("Couldn't draw the diagram: \(failure)", systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(.orange)
                CodeText(code: source, fontSize: 12)
            }
        } else {
            let web = MermaidWebView(source: source, dark: colorScheme == .dark, interactive: interactive,
                                     onHeight: { height = $0 }, onFailure: { failure = $0 })
            if interactive {
                web
            } else {
                web.frame(height: min(height, maxHeight ?? .infinity))
            }
        }
    }
}

private struct MermaidWebView: NSViewRepresentable {
    let source: String
    let dark: Bool
    let interactive: Bool
    let onHeight: (CGFloat) -> Void
    let onFailure: (String) -> Void

    /// mermaid.js 11.4.1, bundled in the app (`app/Resources`, copied by run.sh / release.sh) so
    /// diagrams draw offline. Read once; nil if the bundle lacks it (e.g. under `swift run`).
    static let script: String? = Bundle.main.url(forResource: "mermaid.min", withExtension: "js")
        .flatMap { try? String(contentsOf: $0, encoding: .utf8) }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.userContentController.add(context.coordinator, name: "mermaid")
        if let script = Self.script {
            config.userContentController.addUserScript(
                WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        }
        let web = interactive ? WKWebView(frame: .zero, configuration: config)
                              : PassThroughWebView(frame: .zero, configuration: config)
        web.setValue(false, forKey: "drawsBackground")
        web.allowsMagnification = interactive
        return web
    }

    func updateNSView(_ web: WKWebView, context: Context) {
        let c = context.coordinator
        c.onHeight = onHeight; c.onFailure = onFailure
        let key = "\(dark)|\(source)"
        guard c.loaded != key else { return }
        c.loaded = key
        web.loadHTMLString(html, baseURL: nil)
    }

    static func dismantleNSView(_ web: WKWebView, coordinator: Coordinator) {
        web.configuration.userContentController.removeScriptMessageHandler(forName: "mermaid")
    }

    private var html: String {
        // JSON string literal; "</" escaped so the source can't close the <script> element.
        let data = (try? JSONEncoder().encode(source)) ?? Data("\"\"".utf8)
        let literal = String(decoding: data, as: UTF8.self).replacingOccurrences(of: "</", with: "<\\/")
        let fit = interactive ? "max-width:100%;max-height:100vh;" : "max-width:100%;height:auto;"
        return """
        <!doctype html><html><head><meta charset="utf-8">
        <style>html,body{margin:0;padding:0;background:transparent;overflow:\(interactive ? "auto" : "hidden");}
        body{display:flex;justify-content:center;align-items:\(interactive ? "center" : "flex-start");min-height:\(interactive ? "100vh" : "0");}
        #out svg{\(fit)}</style>
        </head><body><div id="out"></div><script>
        const post = m => window.webkit.messageHandlers.mermaid.postMessage(m);
        (async () => {
          try {
            if (!window.mermaid) throw new Error("the diagram library is missing from the app");
            mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: "\(dark ? "dark" : "default")" });
            const { svg } = await mermaid.render("d" + Date.now(), \(literal));
            document.getElementById("out").innerHTML = svg;
            post({ height: document.getElementById("out").getBoundingClientRect().height + 4 });
          } catch (e) { post({ error: String((e && e.message) || e) }); }
        })();
        </script></body></html>
        """
    }

    final class Coordinator: NSObject, WKScriptMessageHandler {
        var loaded: String?
        var onHeight: (CGFloat) -> Void = { _ in }
        var onFailure: (String) -> Void = { _ in }

        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            guard let body = message.body as? [String: Any] else { return }
            if let h = body["height"] as? Double { onHeight(max(40, CGFloat(h))) }
            if let e = body["error"] as? String { onFailure(e.components(separatedBy: "\n").first ?? e) }
        }
    }
}

/// Inline diagrams never scroll themselves: the wheel goes to the answer list around them.
private final class PassThroughWebView: WKWebView {
    override func scrollWheel(with event: NSEvent) { nextResponder?.scrollWheel(with: event) }
}
