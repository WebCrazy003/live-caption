import Foundation

/// Wait for a condition instead of sleeping a fixed time — fixed sleeps fail on a busy machine.
/// Returns when `condition` holds or after `timeout` (the test's own assertion then reports it).
func waitUntil(timeout: TimeInterval = 5, _ condition: () -> Bool) async {
    let deadline = Date().addingTimeInterval(timeout)
    while !condition(), Date() < deadline { try? await Task.sleep(nanoseconds: 10_000_000) }
}

@MainActor
func waitUntilOnMain(timeout: TimeInterval = 5, _ condition: @MainActor () -> Bool) async {
    let deadline = Date().addingTimeInterval(timeout)
    while !condition(), Date() < deadline { try? await Task.sleep(nanoseconds: 10_000_000) }
}
