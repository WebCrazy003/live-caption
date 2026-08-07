import SwiftUI
import AppKit
import LocalCaptionKit

/// Opens a past session read-only (SPEC.md §10 / SPEC-06): loads the saved `.txt` into a
/// selectable, non-editable view. No re-transcription — raw audio isn't kept.
struct TranscriptViewer: View {
    @EnvironmentObject var env: AppEnvironment
    let sessionID: Int64

    @State private var record: SessionRecord?
    @State private var text = ""
    @State private var loadError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let rec = record {
                header(rec)
                Divider()
                if let err = loadError {
                    ContentUnavailableCompat(err)
                } else {
                    ScrollView {
                        Text(text)
                            .font(.system(size: CGFloat(env.config.caption.fontSize)))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            } else {
                ContentUnavailableCompat("Session not found.")
            }
        }
        .padding()
        .navigationTitle(record?.sessionName ?? "Session")
        .onAppear(perform: load)
        .onChange(of: sessionID) { load() }
    }

    private func header(_ rec: SessionRecord) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(rec.sessionName).font(.headline)
                HStack(spacing: 6) {
                    Text(rec.createdAt.prefix(19).replacingOccurrences(of: "T", with: " "))
                    Text("·")
                    Text(TimeFormat.clock(rec.durationSeconds))
                }
                .font(.caption).foregroundStyle(.secondary).monospacedDigit()
            }
            Spacer()
            Label("Read-only", systemImage: "lock").font(.caption).foregroundStyle(.secondary)
            if let path = rec.transcriptFile {
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
                } label: { Label("Reveal in Finder", systemImage: "folder") }
                .buttonStyle(.link)
            }
        }
    }

    private func load() {
        record = try? env.store.fetch(id: sessionID)
        guard let rec = record else { text = ""; loadError = nil; return }
        if let path = rec.transcriptFile,
           let contents = try? String(contentsOfFile: path, encoding: .utf8) {
            text = contents; loadError = nil
        } else {
            text = ""; loadError = "Transcript file is missing or was moved."
        }
    }
}

/// Small stand-in for ContentUnavailableView (keeps a single look for empty/error states).
private struct ContentUnavailableCompat: View {
    let message: String
    init(_ message: String) { self.message = message }
    var body: some View {
        VStack(spacing: 8) {
            Image(systemName: "doc.text.magnifyingglass").font(.largeTitle).foregroundStyle(.tertiary)
            Text(message).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
