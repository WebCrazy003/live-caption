import SwiftUI
import AppKit
import UniformTypeIdentifiers
import LocalCaptionKit

/// Interview library manager (SPEC-13 §Library): CVs, JDs, notes and interview skills.
struct LibraryView: View {
    @ObservedObject var library: InterviewLibrary
    @Environment(\.dismiss) private var dismiss

    @State private var editingDoc: InterviewLibraryIndex.Document?
    @State private var viewingSkill: InterviewLibraryIndex.Skill?
    @State private var pasting: InterviewLibraryIndex.DocumentKind?
    @State private var notice: String?
    @State private var error: String?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Interview library").font(.title3.weight(.semibold))
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding()
            Divider()
            List {
                ForEach(InterviewLibraryIndex.DocumentKind.allCases, id: \.self) { kind in
                    Section {
                        let docs = library.documents(of: kind)
                        if docs.isEmpty {
                            Text("None yet").foregroundStyle(.secondary)
                        }
                        ForEach(docs) { doc in documentRow(doc) }
                        HStack {
                            Button { importDocument(kind) } label: { Label("Import file…", systemImage: "doc.badge.plus") }
                            Button { pasting = kind } label: { Label("Paste text…", systemImage: "doc.on.clipboard") }
                        }
                        .buttonStyle(.borderless)
                    } header: {
                        Text(kind.sectionTitle)
                    }
                }
                Section {
                    if library.skills.isEmpty { Text("None yet").foregroundStyle(.secondary) }
                    ForEach(library.skills) { skill in skillRow(skill) }
                    Button { importSkill() } label: { Label("Import skill…", systemImage: "wand.and.stars") }
                        .buttonStyle(.borderless)
                } header: {
                    Text("Interview skills")
                } footer: {
                    Text("A folder with SKILL.md, or a single .md file. Its text, plus any .md/.txt files with it, "
                         + "goes into the prep message; scripts and other files are left out.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if let message = error ?? notice ?? library.lastError {
                Divider()
                Label(message, systemImage: error != nil || library.lastError != nil ? "exclamationmark.triangle" : "info.circle")
                    .font(.callout).foregroundStyle(error != nil ? .orange : .secondary)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(10)
            }
        }
        .frame(minWidth: 520, minHeight: 520)
        .sheet(item: $editingDoc) { doc in DocumentEditor(library: library, document: doc) }
        .sheet(item: $viewingSkill) { skill in SkillViewer(library: library, skill: skill) }
        .sheet(item: $pasting) { kind in PasteDocumentSheet(library: library, kind: kind) }
    }

    private func documentRow(_ doc: InterviewLibraryIndex.Document) -> some View {
        HStack {
            Image(systemName: doc.original.isEmpty ? "text.alignleft" : "doc.text")
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(doc.title)
                Text("\(doc.chars.formatted()) characters" + (doc.original.isEmpty ? " · pasted" : ""))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Edit…") { editingDoc = doc }.buttonStyle(.borderless)
            Button(role: .destructive) { library.deleteDocument(doc.id) } label: { Image(systemName: "trash") }
                .buttonStyle(.borderless).help("Delete")
        }
    }

    private func skillRow(_ skill: InterviewLibraryIndex.Skill) -> some View {
        HStack {
            Image(systemName: "wand.and.stars").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(skill.title)
                Text("\(skill.files.count) file\(skill.files.count == 1 ? "" : "s") · \(skill.chars.formatted()) characters"
                     + (skill.chars > SkillFile.maxChars ? " · large" : ""))
                    .font(.caption).foregroundStyle(skill.chars > SkillFile.maxChars ? .orange : .secondary)
            }
            Spacer()
            Button("View…") { viewingSkill = skill }.buttonStyle(.borderless)
            Button(role: .destructive) { library.deleteSkill(skill.id) } label: { Image(systemName: "trash") }
                .buttonStyle(.borderless).help("Delete")
        }
    }

    private func importDocument(_ kind: InterviewLibraryIndex.DocumentKind) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.pdf, .plainText, UTType(filenameExtension: "md") ?? .plainText,
                                     UTType(filenameExtension: "docx") ?? .data]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let doc = try library.importDocument(from: url, kind: kind)
            error = nil; notice = "Imported “\(doc.title)”. Check the extracted text with Edit…"
            editingDoc = doc
        } catch let e {
            error = e.localizedDescription
        }
    }

    private func importSkill() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText, .folder]
        panel.message = "Choose a skill folder (with SKILL.md) or a single .md file"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let result = try library.importSkill(from: url)
            error = nil
            notice = result.ignored.isEmpty
                ? "Imported skill “\(result.skill.title)”."
                : "Imported “\(result.skill.title)”. Left out (Codex can't use them): \(result.ignored.joined(separator: ", "))"
        } catch let e {
            error = e.localizedDescription
        }
    }
}

extension InterviewLibraryIndex.DocumentKind: Identifiable {
    public var id: String { rawValue }
    var sectionTitle: String {
        switch self {
        case .cv: return "CVs"
        case .jd: return "Job descriptions"
        case .notes: return "Notes"
        }
    }
}

/// The extracted text, editable — it is exactly what gets sent (SPEC-13).
private struct DocumentEditor: View {
    @ObservedObject var library: InterviewLibrary
    let document: InterviewLibraryIndex.Document
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""
    @State private var kind: InterviewLibraryIndex.DocumentKind = .cv
    @State private var text = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                TextField("Title", text: $title).textFieldStyle(.roundedBorder)
                Picker("", selection: $kind) {
                    ForEach(InterviewLibraryIndex.DocumentKind.allCases) { Text($0.label).tag($0) }
                }
                .labelsHidden().frame(width: 160)
            }
            Text("This text is what the coach sees. Fix anything the import got wrong.")
                .font(.caption).foregroundStyle(.secondary)
            TextEditor(text: $text).font(.system(.body, design: .monospaced))
                .border(.quaternary)
            HStack {
                Text("\(text.count.formatted()) characters").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") {
                    var d = document; d.title = title; d.kind = kind
                    library.update(d)
                    library.setText(text, of: document.id)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(minWidth: 620, minHeight: 520)
        .onAppear { title = document.title; kind = document.kind; text = library.text(of: document.id) }
    }
}

private struct SkillViewer: View {
    @ObservedObject var library: InterviewLibrary
    let skill: InterviewLibraryIndex.Skill
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            TextField("Title", text: $title).textFieldStyle(.roundedBorder)
            ScrollView {
                Text(library.skillText(skill.id)).font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
            }
            .border(.quaternary)
            HStack {
                Text(skill.files.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Done") {
                    if title != skill.title, !title.isEmpty { library.renameSkill(skill.id, to: title) }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(minWidth: 620, minHeight: 520)
        .onAppear { title = skill.title }
    }
}

private struct PasteDocumentSheet: View {
    @ObservedObject var library: InterviewLibrary
    let kind: InterviewLibraryIndex.DocumentKind
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""
    @State private var text = ""
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Paste \(kind.label.lowercased())").font(.headline)
            TextField("Title", text: $title).textFieldStyle(.roundedBorder)
            TextEditor(text: $text).font(.body).border(.quaternary)
            if let error { Text(error).font(.caption).foregroundStyle(.orange) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Add") {
                    do { _ = try library.addPastedDocument(title: title, text: text, kind: kind); dismiss() }
                    catch let e { error = e.localizedDescription }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding()
        .frame(minWidth: 560, minHeight: 440)
    }
}
