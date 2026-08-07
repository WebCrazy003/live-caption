import Foundation
import SwiftUI
import LocalCaptionKit

/// App-wide dependencies: the config store and the sqlite session store.
///
/// Bootstraps the App Support directory tree, loads (repairing if needed) the config,
/// and opens the database. `config` is `@Published`; any mutation persists atomically,
/// which is what gives Settings its two-way binding + live persistence.
@MainActor
final class AppEnvironment: ObservableObject {
    @Published var config: Config { didSet { persist() } }
    let store: Store

    /// True if the config on disk was corrupt and had to be repaired to defaults.
    let configWasRepaired: Bool

    init() {
        _ = try? AppPaths.bootstrap()

        let loaded = Config.loadOrRepair(from: AppPaths.configFile)
        self.config = loaded.config
        self.configWasRepaired = loaded.repaired

        do {
            self.store = try Store()
        } catch {
            // Last resort so the UI still launches; a real disk failure is surfaced elsewhere.
            let fallback = URL(fileURLWithPath: NSTemporaryDirectory())
                .appendingPathComponent("localcaption-fallback.db")
            self.store = try! Store(url: fallback)
        }
    }

    private func persist() {
        try? config.write(to: AppPaths.configFile)
    }
}
