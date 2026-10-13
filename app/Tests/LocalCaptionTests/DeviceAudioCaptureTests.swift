import XCTest
import CoreAudio
@testable import LocalCaption

/// Settings ▸ Captions listen to: the device list, and (opt-in, it plays a sound and may need the
/// System Audio Recording permission) a real tap on the default output.
final class DeviceAudioCaptureTests: XCTestCase {
    func testListsOutputDevicesWithStableIDs() {
        let devices = AudioOutputDevices.all()
        XCTAssertFalse(devices.isEmpty, "a Mac always has an output")
        XCTAssertEqual(Set(devices.map(\.id)).count, devices.count)
        XCTAssertFalse(devices.contains { $0.id.hasPrefix("com.livecaption.capture.") }, "our own capture device is hidden")
    }

    func testTapOnDefaultOutputHearsPlayback() async throws {
        guard ProcessInfo.processInfo.environment["LC_LIVE_TAP"] == "1" else { throw XCTSkip("set LC_LIVE_TAP=1") }
        guard #available(macOS 14.2, *) else { throw XCTSkip("needs macOS 14.2") }
        let uid = try XCTUnwrap(Self.defaultOutputUID())
        let samples = LockedValue<[Float]>([])
        let capture = DeviceAudioCapture(deviceUID: uid, onSamples: { s in samples.update { $0 += s } },
                                         onError: { XCTFail("\($0)") })
        try await capture.start()
        let play = Process()
        play.executableURL = URL(fileURLWithPath: "/usr/bin/afplay")
        play.arguments = ["/System/Library/Sounds/Glass.aiff"]
        try play.run(); play.waitUntilExit()
        try await Task.sleep(nanoseconds: 300_000_000)
        await capture.stop()
        let got = samples.get()
        print("tap samples:", got.count, "peak:", got.map(abs).max() ?? 0)
        XCTAssertGreaterThan(got.count, 8_000, "about a second of 16 kHz audio")
        XCTAssertGreaterThan(got.map(abs).max() ?? 0, 0.01, "the sound was heard, not silence")
    }

    private static func defaultOutputUID() -> String? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var id = AudioObjectID(0); var size = UInt32(MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr
        else { return nil }
        var uidAddress = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceUID,
                                                    mScope: kAudioObjectPropertyScopeGlobal,
                                                    mElement: kAudioObjectPropertyElementMain)
        var uid: Unmanaged<CFString>?; var uidSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &uidAddress, 0, nil, &uidSize, &uid) == noErr else { return nil }
        return uid?.takeRetainedValue() as String?
    }
}
