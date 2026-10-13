import Foundation
import AVFoundation
import CoreAudio

/// Captures what plays through one output device — the call's headphones, say — with a Core Audio
/// process tap (macOS 14.2+), so sounds sent to other outputs aren't captioned (owner, 2026-10-13).
/// Delivers 16 kHz mono Float32 chunks like `SystemAudioCapture`. Needs the System Audio
/// Recording permission, which macOS asks for on first use.
@available(macOS 14.2, *)
final class DeviceAudioCapture: CallAudioCapture, @unchecked Sendable {
    private let deviceUID: String
    private let recorder: CallAudioRecorder?
    private let onSamples: ([Float]) -> Void
    private let onError: (Error) -> Void
    private let resampler = MonoResampler()
    private let audioQueue = DispatchQueue(label: "deviceaudio.audio")
    // Accessed only on audioQueue. The fence in stop defines the capture cutoff.
    private var acceptingAudio = true

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?

    init(deviceUID: String, recorder: CallAudioRecorder? = nil,
         onSamples: @escaping ([Float]) -> Void, onError: @escaping (Error) -> Void) {
        self.deviceUID = deviceUID
        self.recorder = recorder
        self.onSamples = onSamples
        self.onError = onError
    }

    func start() async throws {
        guard AudioOutputDevices.all().contains(where: { $0.id == deviceUID }) else {
            throw CaptureError("The output device chosen in Settings ▸ General ▸ Captions listen to isn't "
                               + "connected. Connect it, or choose All sound, then press Start.")
        }
        do { try open() } catch { close(); throw error }
    }

    private func open() throws {
        // Everything sent to the device except this app's own sound.
        let own = AudioOutputDevices.processObject(pid: ProcessInfo.processInfo.processIdentifier)
        let tap = CATapDescription(__excludingProcesses: own.map { [NSNumber(value: $0)] } ?? [],
                                   andDeviceUID: deviceUID, withStream: 0)
        tap.uuid = UUID()
        tap.name = "LocalCaption"
        tap.isPrivate = true
        tap.muteBehavior = .unmuted
        try check(AudioHardwareCreateProcessTap(tap, &tapID), "create the audio tap")

        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "LocalCaption capture",
            kAudioAggregateDeviceUIDKey: "com.livecaption.capture.\(UUID().uuidString)",
            kAudioAggregateDeviceMainSubDeviceKey: deviceUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: deviceUID]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true,
                                               kAudioSubTapUIDKey: tap.uuid.uuidString]],
        ]
        try check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &aggregateID),
                  "create the capture device")

        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        try check(AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &asbd), "read the tap's format")
        guard let format = AVAudioFormat(streamDescription: &asbd) else {
            throw CaptureError("The output device's audio format isn't supported.")
        }

        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, audioQueue) {
            [weak self] _, input, _, _, _ in
            self?.receive(input, format: format)
        }, "start listening")
        try check(AudioDeviceStart(aggregateID, procID), "start the capture device")
    }

    /// The aggregate device's input is the output device's own inputs (a headset's mic, say)
    /// followed by the tap; the tap's buffer is the last.
    private func receive(_ input: UnsafePointer<AudioBufferList>, format: AVAudioFormat) {
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
        guard acceptingAudio, let tapped = buffers.last,
              tapped.mNumberChannels == format.channelCount else { return }
        var single = AudioBufferList(mNumberBuffers: 1, mBuffers: tapped)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: &single),
              buffer.frameLength > 0 else { return }
        recorder?.append(buffer)
        do {
            let floats = try resampler.convert(buffer)
            if !floats.isEmpty { onSamples(floats) }
        } catch { onError(error) }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            audioQueue.async {
                self.acceptingAudio = false
                continuation.resume()
            }
        }
        close()
    }

    private func close() {
        if aggregateID != kAudioObjectUnknown {
            if let procID {
                AudioDeviceStop(aggregateID, procID)
                AudioDeviceDestroyIOProcID(aggregateID, procID)
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        procID = nil
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        tapID = AudioObjectID(kAudioObjectUnknown)
    }

    private func check(_ status: OSStatus, _ what: String) throws {
        guard status != noErr else { return }
        throw CaptureError("Couldn't \(what) for the chosen output device (error \(status)). If macOS asked "
                           + "about recording system audio, allow LocalCaption in System Settings ▸ Privacy & "
                           + "Security ▸ Screen & System Audio Recording, then press Start.")
    }
}

struct CaptureError: LocalizedError, CustomStringConvertible {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
    var description: String { message }
}

/// The Mac's output devices (speakers, headphones, virtual devices), for Settings.
enum AudioOutputDevices {
    struct Device: Identifiable, Hashable {
        /// The device UID: stable across reboots and reconnects.
        let id: String
        let name: String
    }

    static func all() -> [Device] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        let system = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard hasOutput(id), let uid = string(id, kAudioDevicePropertyDeviceUID),
                  !uid.hasPrefix("com.livecaption.capture.") else { return nil }
            return Device(id: uid, name: string(id, kAudioObjectPropertyName) ?? uid)
        }
    }

    /// This app's Core Audio process object, so the tap can leave its own sound out.
    static func processObject(pid: pid_t) -> AudioObjectID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var pid = pid
        var object = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                                UInt32(MemoryLayout<pid_t>.size), &pid, &size, &object)
        return status == noErr && object != kAudioObjectUnknown ? object : nil
    }

    private static func hasOutput(_ id: AudioObjectID) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams,
                                                 mScope: kAudioObjectPropertyScopeOutput,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr,
              let s = value?.takeRetainedValue() else { return nil }
        return s as String
    }
}
