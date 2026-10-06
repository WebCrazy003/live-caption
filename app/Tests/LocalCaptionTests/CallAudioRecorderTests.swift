import XCTest
import AVFoundation
@testable import LocalCaption

final class CallAudioRecorderTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("lc-callrec-\(UUID().uuidString)", isDirectory: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    /// What ScreenCaptureKit delivers: 48 kHz stereo Float32, non-interleaved.
    private func tone(frames: AVAudioFrameCount, format: AVAudioFormat) -> AVAudioPCMBuffer {
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames)!
        buffer.frameLength = frames
        for ch in 0..<Int(format.channelCount) {
            for i in 0..<Int(frames) { buffer.floatChannelData![ch][i] = 0.2 * sin(Float(i) * 0.06) }
        }
        return buffer
    }

    private let scFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000,
                                         channels: 2, interleaved: false)!

    func testWritesPlayableAACOfTheCapturedLength() throws {
        let url = dir.appendingPathComponent("s.m4a")
        let recorder = try CallAudioRecorder(url: url)
        var errors: [String] = []
        recorder.onError = { errors.append($0) }
        // Two "captures" (a pause in between) — the file is one continuous recording.
        for _ in 0..<50 { recorder.append(tone(frames: 960, format: scFormat)) }
        recorder.pause()
        try recorder.resume()
        for _ in 0..<50 { recorder.append(tone(frames: 960, format: scFormat)) }
        XCTAssertTrue(recorder.finish())
        XCTAssertEqual(errors, [])

        XCTAssertTrue(RecordingCheck.isPlayable(url))
        let file = try AVAudioFile(forReading: url)
        XCTAssertEqual(file.fileFormat.streamDescription.pointee.mFormatID, kAudioFormatMPEG4AAC)
        XCTAssertEqual(file.fileFormat.channelCount, 2)
        let seconds = Double(file.length) / file.fileFormat.sampleRate
        XCTAssertEqual(seconds, 2.0, accuracy: 0.05, "100 × 20 ms buffers")
    }

    func testNothingRecordedLeavesNoFile() throws {
        let url = dir.appendingPathComponent("empty.m4a")
        let recorder = try CallAudioRecorder(url: url)
        XCTAssertFalse(recorder.finish())
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    func testFormatChangeStopsRecordingButKeepsWhatWasWritten() throws {
        let url = dir.appendingPathComponent("change.m4a")
        let recorder = try CallAudioRecorder(url: url)
        var errors: [String] = []
        recorder.onError = { errors.append($0) }
        for _ in 0..<25 { recorder.append(tone(frames: 960, format: scFormat)) }
        let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 1, interleaved: false)!
        recorder.append(tone(frames: 960, format: mono))
        recorder.append(tone(frames: 960, format: scFormat))   // ignored once stopped
        XCTAssertTrue(recorder.finish())
        XCTAssertEqual(errors.count, 1)
        XCTAssertTrue(RecordingCheck.isPlayable(url))
    }

    func testLateBuffersAfterFinishAreIgnored() throws {
        let url = dir.appendingPathComponent("late.m4a")
        let recorder = try CallAudioRecorder(url: url)
        recorder.append(tone(frames: 960, format: scFormat))
        XCTAssertTrue(recorder.finish())
        recorder.append(tone(frames: 960, format: scFormat))
        XCTAssertTrue(RecordingCheck.isPlayable(url))
    }

    func testUnclosedRecordingIsNotPlayable() throws {
        let url = dir.appendingPathComponent("crashed.m4a")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try Data(repeating: 0, count: 4096).write(to: url)
        XCTAssertFalse(RecordingCheck.isPlayable(url))
    }
}
