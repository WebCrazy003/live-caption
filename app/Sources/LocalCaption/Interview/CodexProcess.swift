import Foundation
import LocalCaptionKit

/// Newline-delimited text over a child process's stdio (SPEC-12 §Process & transport). A
/// protocol so engine tests can script the server without a real process.
protocol LineTransport: AnyObject, Sendable {
    /// Lines from stdout, in order. Finishes when the process exits.
    var lines: AsyncStream<String> { get }
    func send(_ line: String) throws
    func terminate()
}

/// `codex app-server` as a child process.
final class ProcessLineTransport: LineTransport, @unchecked Sendable {
    let lines: AsyncStream<String>
    private let continuation: AsyncStream<String>.Continuation
    private let process = Process()
    private let stdin = Pipe()
    private let stdout = Pipe()
    private let lock = NSLock()
    private var buffer = Data()

    init(executable: URL, arguments: [String], cwd: URL, environment: [String: String], stderrLog: URL?) throws {
        (lines, continuation) = AsyncStream.makeStream(of: String.self)
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = cwd
        process.environment = environment
        process.standardInput = stdin
        process.standardOutput = stdout
        if let stderrLog, let log = CodexProcessLog.handle(at: stderrLog) {
            process.standardError = log
        } else {
            process.standardError = FileHandle.nullDevice
        }
        let cont = continuation
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let chunk = handle.availableData
            guard let self else { return }
            if chunk.isEmpty { handle.readabilityHandler = nil; return }
            self.lock.lock()
            self.buffer.append(chunk)
            var out: [String] = []
            while let nl = self.buffer.firstIndex(of: 0x0A) {
                out.append(String(decoding: self.buffer[..<nl], as: UTF8.self))
                self.buffer.removeSubrange(...nl)
            }
            self.lock.unlock()
            for line in out { cont.yield(line) }
        }
        process.terminationHandler = { _ in cont.finish() }
        try process.run()
    }

    func send(_ line: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard process.isRunning else { throw EngineError.crashed }
        try stdin.fileHandleForWriting.write(contentsOf: Data((line + "\n").utf8))
    }

    func terminate() {
        try? stdin.fileHandleForWriting.close()
        let p = process
        DispatchQueue.global().asyncAfter(deadline: .now() + 2) { if p.isRunning { p.terminate() } }
    }
}

/// Codex's stderr goes to `interview/codex.log`, rotated at 5 MB (SPEC-12).
enum CodexProcessLog {
    static let maxBytes = 5 * 1024 * 1024

    static func handle(at url: URL) -> FileHandle? {
        let fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let size = (try? fm.attributesOfItem(atPath: url.path)[.size]) as? Int, size > maxBytes {
            let old = url.appendingPathExtension("1")
            try? fm.removeItem(at: old)
            try? fm.moveItem(at: url, to: old)
        }
        if !fm.fileExists(atPath: url.path) { fm.createFile(atPath: url.path, contents: nil) }
        let h = try? FileHandle(forWritingTo: url)
        _ = try? h?.seekToEnd()
        return h
    }
}

/// Finds the `codex` binary and checks its version (SPEC-12 §Finding codex). GUI apps don't
/// inherit the shell `PATH`, so known locations are searched explicitly.
enum CodexLocator {
    struct Found: Equatable {
        let url: URL
        let version: [Int]
        var versionString: String { version.map(String.init).joined(separator: ".") }
    }

    enum Problem: Error, Equatable { case notInstalled, tooOld(String) }

    static func candidates(configured: String) -> [String] {
        var paths: [String] = []
        let trimmed = (configured as NSString).expandingTildeInPath.trimmingCharacters(in: .whitespaces)
        if !trimmed.isEmpty { paths.append(trimmed) }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        paths += ["/opt/homebrew/bin/codex", "/usr/local/bin/codex",
                  "\(home)/.npm-global/bin/codex", "\(home)/.local/bin/codex", "\(home)/.volta/bin/codex"]
        return paths
    }

    static func locate(configured: String) -> Result<Found, Problem> {
        var seenTooOld: String?
        var paths = candidates(configured: configured)
        if let viaShell = loginShellLookup() { paths.append(viaShell) }
        for path in paths where FileManager.default.isExecutableFile(atPath: path) {
            let url = URL(fileURLWithPath: path)
            guard let out = run(url, ["--version"]), let v = CodexRPC.version(fromOutput: out) else { continue }
            if CodexRPC.isSupported(version: v) { return .success(Found(url: url, version: v)) }
            seenTooOld = v.map(String.init).joined(separator: ".")
        }
        return .failure(seenTooOld.map(Problem.tooOld) ?? .notInstalled)
    }

    /// `zsh -lc 'command -v codex'` — finds installs only the user's shell profile knows about.
    private static func loginShellLookup() -> String? {
        guard let out = run(URL(fileURLWithPath: "/bin/zsh"), ["-lc", "command -v codex"]) else { return nil }
        let path = out.trimmingCharacters(in: .whitespacesAndNewlines)
        return path.hasPrefix("/") ? path : nil
    }

    private static func run(_ exe: URL, _ args: [String]) -> String? {
        let p = Process()
        p.executableURL = exe
        p.arguments = args
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let deadline = Date().addingTimeInterval(5)
        while p.isRunning && Date() < deadline { usleep(20_000) }
        if p.isRunning { p.terminate(); return nil }
        return String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    }

    /// The child environment: the user's, plus a `PATH` that can find `node` for npm installs,
    /// and the dedicated `CODEX_HOME` (SPEC-12 §Lockdown).
    static func environment(codexURL: URL, codexHome: URL) -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        let extra = [codexURL.deletingLastPathComponent().path, "/opt/homebrew/bin", "/usr/local/bin"]
        env["PATH"] = (extra + [env["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        env["CODEX_HOME"] = codexHome.path
        return env
    }
}
