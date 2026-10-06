import Foundation
import AVFoundation
import LocalCaptionKit

/// Saves a session's audio as AAC `.m4a` while it records (Settings → General → Recording).
/// The file only grows while capture runs, so pauses are cut out the same way the transcript
/// clock skips them, and caption timestamps line up with the call recording.
///
/// The file is written to `recordings/<session uuid>.m4a` and moved beside the transcript on
/// save. Recording never stops captioning: a failure is reported once and the session goes on.
protocol SessionAudioRecorder: AnyObject {
    var url: URL { get }
    var source: Config.RecordSource { get }
    /// Called once, from any thread, if the recording fails part-way.
    var onError: ((String) -> Void)? { get set }
    func pause()
    func resume() throws
    /// Close the file. Returns false (and removes the file) when nothing was recorded.
    @discardableResult func finish() -> Bool
}

extension SessionAudioRecorder {
    /// Close and delete — the session it belonged to was never started.
    func discard() {
        finish()
        try? FileManager.default.removeItem(at: url)
    }
}

/// Encoder settings shared by both recorders: AAC at 64 kbps per channel — plenty for speech.
private func aacSettings(sampleRate: Double, channels: AVAudioChannelCount) -> [String: Any] {
    [AVFormatIDKey: kAudioFormatMPEG4AAC,
     AVSampleRateKey: sampleRate,
     AVNumberOfChannelsKey: channels,
     AVEncoderBitRateKey: 64_000 * Int(channels)]
}

// MARK: - Call audio

/// Writes the system/call audio exactly as ScreenCaptureKit delivers it (48 kHz stereo), before
/// it is downsampled for Whisper. Each buffer is copied off the capture queue and encoded on a
/// queue of its own, so a slow disk can't hold up the captions. Pausing needs no action because
/// a paused session has no capture running.
final class CallAudioRecorder: SessionAudioRecorder, @unchecked Sendable {
    let url: URL
    let source = Config.RecordSource.call
    var onError: ((String) -> Void)?

    // Everything below is touched only on `queue`.
    private let queue = DispatchQueue(label: "callaudio.writer")
    private var file: AVAudioFile?
    private var frames: AVAudioFramePosition = 0
    private var stopped = false   // failed or finished: ignore late buffers

    init(url: URL) throws {
        self.url = url
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
    }

    /// Queue one capture buffer. `buffer` may wrap memory that is only valid during this call
    /// (`bufferListNoCopy`), so it is copied first.
    func append(_ buffer: AVAudioPCMBuffer) {
        guard buffer.frameLength > 0, let copy = Self.copy(buffer) else { return }
        queue.async { self.write(copy) }
    }

    func pause() {}
    func resume() throws {}

    /// Waits for every queued buffer to be written.
    @discardableResult func finish() -> Bool {
        queue.sync {
            stopped = true
            file = nil   // AVAudioFile writes the MP4 header when it is released
            if frames == 0 { try? FileManager.default.removeItem(at: url) }
            return frames > 0
        }
    }

    private func write(_ buffer: AVAudioPCMBuffer) {
        guard !stopped else { return }
        let format = buffer.format
        do {
            if file == nil {
                file = try AVAudioFile(forWriting: url,
                                       settings: aacSettings(sampleRate: format.sampleRate,
                                                             channels: format.channelCount),
                                       commonFormat: format.commonFormat,
                                       interleaved: format.isInterleaved)
            }
            guard let file else { return }
            let expected = file.processingFormat
            guard expected.sampleRate == format.sampleRate, expected.channelCount == format.channelCount,
                  expected.commonFormat == format.commonFormat, expected.isInterleaved == format.isInterleaved
            else { return fail("the call audio format changed mid-session") }
            try file.write(from: buffer)
            frames += AVAudioFramePosition(buffer.frameLength)
        } catch {
            fail(error.localizedDescription)
        }
    }

    /// What was written so far stays usable once the file is closed.
    private func fail(_ reason: String) {
        stopped = true
        file = nil
        onError?("Audio recording stopped: \(reason). Captions continue.")
    }

    private static func copy(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard let copy = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: buffer.frameLength)
        else { return nil }
        copy.frameLength = buffer.frameLength
        let src = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: buffer.audioBufferList))
        let dst = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
        for (from, to) in zip(src, dst) {
            guard let fromData = from.mData, let toData = to.mData else { return nil }
            memcpy(toData, fromData, Int(min(from.mDataByteSize, to.mDataByteSize)))
        }
        return copy
    }
}

// MARK: - Microphone

/// Records the default input device with `AVAudioRecorder`. Needs the Microphone permission;
/// the captions still come from the call audio.
final class MicrophoneRecorder: NSObject, SessionAudioRecorder, AVAudioRecorderDelegate, @unchecked Sendable {
    let url: URL
    let source = Config.RecordSource.microphone
    var onError: ((String) -> Void)?
    private let recorder: AVAudioRecorder
    private var finished = false

    enum Failure: LocalizedError {
        case permissionDenied, couldNotStart
        var errorDescription: String? {
            switch self {
            case .permissionDenied:
                return "Microphone recording is off: LocalCaption doesn't have the Microphone permission. "
                    + "Turn it on in System Settings ▸ Privacy & Security ▸ Microphone. Captions continue."
            case .couldNotStart:
                return "Microphone recording couldn't start (is an input device connected?). Captions continue."
            }
        }
    }

    init(url: URL) throws {
        self.url = url
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        recorder = try AVAudioRecorder(url: url, settings: aacSettings(sampleRate: 48_000, channels: 1))
        super.init()
        recorder.delegate = self
    }

    /// Ask for the Microphone permission if it hasn't been decided yet. True when granted.
    static func requestAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    func start() async throws {
        guard await Self.requestAccess() else { throw Failure.permissionDenied }
        guard recorder.prepareToRecord(), recorder.record() else { throw Failure.couldNotStart }
    }

    func pause() { if recorder.isRecording { recorder.pause() } }

    func resume() throws {
        guard !finished else { return }
        guard recorder.record() else { throw Failure.couldNotStart }
    }

    @discardableResult func finish() -> Bool {
        if !finished { finished = true; recorder.stop() }
        let duration = (try? AVAudioFile(forReading: url)).map { $0.length } ?? 0
        if duration == 0 { try? FileManager.default.removeItem(at: url) }
        return duration > 0
    }

    func audioRecorderEncodeErrorDidOccur(_ recorder: AVAudioRecorder, error: Error?) {
        onError?("Microphone recording stopped: \(error?.localizedDescription ?? "encoder error"). Captions continue.")
    }
}

// MARK: - Recovery

enum RecordingCheck {
    /// A recording left by a session that never reached Save is usable only if its file was
    /// closed (a crash leaves the MP4 header unwritten).
    static func isPlayable(_ url: URL) -> Bool {
        guard let file = try? AVAudioFile(forReading: url) else { return false }
        return file.length > 0
    }
}
