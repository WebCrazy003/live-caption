import Foundation
import AVFoundation
import CoreMedia
import ScreenCaptureKit

/// Thread-safe FIFO of audio samples shared between the capture callback and the loop.
final class SampleBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var data: [Float] = []
    func append(_ s: [Float]) { lock.lock(); data.append(contentsOf: s); lock.unlock() }
    func drain() -> [Float] {
        lock.lock(); defer { lock.unlock() }
        let d = data; data.removeAll(keepingCapacity: true); return d
    }
}

/// Captures system (speaker) audio via ScreenCaptureKit and delivers it as
/// 16 kHz mono Float32 chunks. Requires the Screen Recording permission.
@available(macOS 13.0, *)
final class SystemAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private var stream: SCStream?
    private let onSamples: ([Float]) -> Void
    private let onError: (Error) -> Void
    private var converter: AVAudioConverter?
    private let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                             sampleRate: 16000, channels: 1, interleaved: false)!
    private let audioQueue = DispatchQueue(label: "systemaudio.audio")
    private let screenQueue = DispatchQueue(label: "systemaudio.screen")

    init(onSamples: @escaping ([Float]) -> Void, onError: @escaping (Error) -> Void) {
        self.onSamples = onSamples
        self.onError = onError
    }

    func start() async throws {
        // Throws / returns empty if Screen Recording permission is not granted.
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first else {
            throw NSError(domain: "SystemAudioCapture", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "No display available to attach the audio stream to."])
        }
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])

        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.excludesCurrentProcessAudio = true   // don't capture our own app's sound
        config.sampleRate = 48000
        config.channelCount = 2
        config.width = 100                            // minimal video (SCStream needs a size)
        config.height = 100
        config.minimumFrameInterval = CMTime(value: 1, timescale: 4)

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: screenQueue)
        self.stream = stream
        try await stream.startCapture()
    }

    func stop() async {
        try? await stream?.stopCapture()
        stream = nil
    }

    // MARK: SCStreamDelegate
    func stream(_ stream: SCStream, didStopWithError error: Error) { onError(error) }

    // MARK: SCStreamOutput
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid, sampleBuffer.numSamples > 0 else { return }
        if let floats = convertToMono16k(sampleBuffer), !floats.isEmpty { onSamples(floats) }
    }

    private func convertToMono16k(_ sampleBuffer: CMSampleBuffer) -> [Float]? {
        guard let fmtDesc = sampleBuffer.formatDescription,
              var asbd = fmtDesc.audioStreamBasicDescription,
              let srcFormat = AVAudioFormat(streamDescription: &asbd) else { return nil }

        var out: [Float]?
        do {
            try sampleBuffer.withAudioBufferList { abl, _ in
                guard let srcBuffer = AVAudioPCMBuffer(pcmFormat: srcFormat,
                                                       bufferListNoCopy: abl.unsafePointer) else { return }
                if converter == nil { converter = AVAudioConverter(from: srcFormat, to: targetFormat) }
                guard let converter else { return }

                let ratio = targetFormat.sampleRate / srcFormat.sampleRate
                let capacity = AVAudioFrameCount(Double(srcBuffer.frameLength) * ratio) + 32
                guard let outBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }

                var provided = false
                var convErr: NSError?
                converter.convert(to: outBuffer, error: &convErr) { _, statusPtr in
                    if provided { statusPtr.pointee = .noDataNow; return nil }
                    provided = true
                    statusPtr.pointee = .haveData
                    return srcBuffer
                }
                if let convErr { onError(convErr); return }
                if let ch = outBuffer.floatChannelData {
                    out = Array(UnsafeBufferPointer(start: ch[0], count: Int(outBuffer.frameLength)))
                }
            }
        } catch {
            onError(error); return nil
        }
        return out
    }
}
