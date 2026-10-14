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
    @StateObject private var zoom = DiagramZoom()

    var body: some View {
        Group {
            if let block = viewer.block {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text(block.title).font(.headline)
                        Spacer()
                        if block.isMermaid { zoomControls }
                        Button { copy(block.code) { copied = $0 } } label: {
                            Label(copied ? "Copied" : (block.isMermaid ? "Copy source" : "Copy code"),
                                  systemImage: copied ? "checkmark" : "doc.on.doc")
                        }
                    }
                    if block.isMermaid {
                        VSplitView {
                            MermaidDiagram(source: block.code, interactive: true, maxHeight: nil, zoom: zoom)
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

    /// Zoom out / in / fit. Drag pans; pinch or ⌘-scroll zooms; double-click fits.
    private var zoomControls: some View {
        HStack(spacing: 2) {
            Button { zoom.run("zoomBy(1/1.25)") } label: { Image(systemName: "minus.magnifyingglass") }
                .help("Zoom out (⌘−)").keyboardShortcut("-", modifiers: .command)
            Button { zoom.run("fit()") } label: { Image(systemName: "arrow.up.left.and.down.right.magnifyingglass") }
                .help("Fit the diagram (⌘0)").keyboardShortcut("0", modifiers: .command)
            Button { zoom.run("zoomBy(1.25)") } label: { Image(systemName: "plus.magnifyingglass") }
                .help("Zoom in (⌘+)").keyboardShortcut("=", modifiers: .command)
        }
        .buttonStyle(.bordered)
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
/// to the diagram and passes scrolling to the answer list; in the window it fills the space, drags
/// to pan and zooms. If the diagram can't be drawn, its source is shown with the reason.
struct MermaidDiagram: View {
    let source: String
    let interactive: Bool
    let maxHeight: CGFloat?
    var zoom: DiagramZoom?
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
                                     zoom: zoom, onHeight: { height = $0 }, onFailure: { failure = $0 })
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
    let zoom: DiagramZoom?
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
        zoom?.web = web
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
        return """
        <!doctype html><html><head><meta charset="utf-8">
        <style>html,body{margin:0;padding:0;background:transparent;overflow:hidden;}
        \(interactive ? Self.panZoomStyle : Self.inlineStyle)</style>
        </head><body><div id="out"></div><script>
        const post = m => window.webkit.messageHandlers.mermaid.postMessage(m);
        \(interactive ? Self.panZoomScript : "")
        (async () => {
          try {
            if (!window.mermaid) throw new Error("the diagram library is missing from the app");
            mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: "\(dark ? "dark" : "default")" });
            const { svg } = await mermaid.render("d" + Date.now(), \(literal));
            document.getElementById("out").innerHTML = svg;
            if (window.ready) ready();
            post({ height: document.getElementById("out").getBoundingClientRect().height + 4 });
          } catch (e) { post({ error: String((e && e.message) || e) }); }
        })();
        </script></body></html>
        """
    }

    private static let inlineStyle = """
        body{display:flex;justify-content:center;} #out svg{max-width:100%;height:auto;}
        """

    private static let panZoomStyle = """
        body{width:100vw;height:100vh;cursor:grab;user-select:none;} body.dragging{cursor:grabbing;}
        #out{position:absolute;left:0;top:0;transform-origin:0 0;}
        #out svg{max-width:none !important;display:block;}
        """

    /// The window's diagram: drag to pan, pinch / ⌘-scroll to zoom at the pointer, scroll to pan,
    /// double-click to fit. `zoomBy` and `fit` are also called by the toolbar buttons.
    private static let panZoomScript = """
        let s = 1, x = 0, y = 0, w = 0, h = 0;
        const out = () => document.getElementById("out");
        const apply = () => { out().style.transform = `translate(${x}px,${y}px) scale(${s})`; };
        let fitted = false;
        function fit() {
          if (!w || !innerWidth || !innerHeight) return;
          fitted = true;
          s = Math.min(innerWidth / w, innerHeight / h) * 0.95;
          x = (innerWidth - w * s) / 2; y = (innerHeight - h * s) / 2; apply();
        }
        function zoomBy(f, cx = innerWidth / 2, cy = innerHeight / 2) {
          const n = Math.min(20, Math.max(0.05, s * f)); f = n / s;
          x = cx - (cx - x) * f; y = cy - (cy - y) * f; s = n; apply();
        }
        function ready() {
          const svg = out().querySelector("svg"); if (!svg) return;
          const vb = svg.viewBox && svg.viewBox.baseVal;
          w = vb && vb.width ? vb.width : svg.getBoundingClientRect().width;
          h = vb && vb.height ? vb.height : svg.getBoundingClientRect().height;
          svg.setAttribute("width", w); svg.setAttribute("height", h); svg.style.maxWidth = "none";
          fit();
        }
        let drag = null;
        addEventListener("mousedown", e => { drag = { mx: e.clientX, my: e.clientY, x, y }; document.body.classList.add("dragging"); });
        addEventListener("mousemove", e => { if (!drag) return; x = drag.x + e.clientX - drag.mx; y = drag.y + e.clientY - drag.my; apply(); });
        addEventListener("mouseup", () => { drag = null; document.body.classList.remove("dragging"); });
        addEventListener("dblclick", fit);
        addEventListener("wheel", e => {
          e.preventDefault();
          if (e.ctrlKey || e.metaKey) zoomBy(Math.exp(-e.deltaY * 0.01), e.clientX, e.clientY);
          else { x -= e.deltaX; y -= e.deltaY; apply(); }
        }, { passive: false });
        // WebKit reports a trackpad pinch as gesture events, not ⌘-wheel.
        let pinch = 1;
        addEventListener("gesturestart", e => { e.preventDefault(); pinch = 1; });
        addEventListener("gesturechange", e => { e.preventDefault(); zoomBy(e.scale / pinch, e.clientX, e.clientY); pinch = e.scale; });
        let lastW = innerWidth, lastH = innerHeight;
        addEventListener("resize", () => { if (!fitted) { fit(); lastW = innerWidth; lastH = innerHeight; return; } x += (innerWidth - lastW) / 2; y += (innerHeight - lastH) / 2; lastW = innerWidth; lastH = innerHeight; apply(); });
        """

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

/// Runs the window diagram's zoom functions from the toolbar.
@MainActor
final class DiagramZoom: ObservableObject {
    weak var web: WKWebView?
    func run(_ js: String) { web?.evaluateJavaScript(js) }
}

/// Inline diagrams never scroll themselves: the wheel goes to the answer list around them.
private final class PassThroughWebView: WKWebView {
    override func scrollWheel(with event: NSEvent) { nextResponder?.scrollWheel(with: event) }
}
