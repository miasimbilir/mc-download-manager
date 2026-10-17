import CryptoKit
import Foundation

/// Registers the bundled native messaging host (Contents/Helpers/mcdm-native-host) with Chromium browsers and
/// Firefox, so their extension can start the app when a download arrives while it is not running.
/// Each browser reads `<name>.json` from its NativeMessagingHosts folder; the file names the host binary and the
/// extensions allowed to call it. Rewritten at every launch, so moving the app keeps the path current.
enum NativeHostRegistry {
    static let hostName = "com.mesutcevik.mcdownloadmanager"
    static let firefoxAddonID = "mc-download-manager@mstcvk.github.io"

    private static let idsKey = "chromiumExtensionIDs"

    static var hostBinary: URL { VideoDownloader.helpersDirectory.appendingPathComponent("mcdm-native-host") }

    private enum Family { case chromium, firefox }

    /// Browser profile roots under ~/Library/Application Support. Manifests are only written for browsers whose
    /// folder exists (i.e. that have been run at least once).
    private static let browsers: [(root: String, family: Family)] = [
        ("Google/Chrome", .chromium),
        ("Google/Chrome Beta", .chromium),
        ("BraveSoftware/Brave-Browser", .chromium),
        ("Microsoft Edge", .chromium),
        ("com.operasoftware.Opera", .chromium),
        ("Chromium", .chromium),
        ("Mozilla", .firefox),
    ]

    /// Chromium extension ids reported by the extension itself (see `noteExtensionID`).
    private static var reportedIDs: Set<String> {
        get { Set(UserDefaults.standard.stringArray(forKey: idsKey) ?? []) }
        set { UserDefaults.standard.set(Array(newValue).sorted(), forKey: idsKey) }
    }

    /// An unpacked extension without a "key" gets its id from its folder path: the first 128 bits of
    /// SHA-256(path), hex digits mapped 0-f → a-p. Known in advance for the folders the setup wizard prepares.
    static func unpackedExtensionID(path: String) -> String {
        let hex = SHA256.hash(data: Data(path.utf8)).map { String(format: "%02x", $0) }.joined().prefix(32)
        return String(hex.map { c in Character(UnicodeScalar(UInt8(ascii: "a") + UInt8(c.hexDigitValue ?? 0))) })
    }

    static var allowedChromiumIDs: [String] {
        var ids = reportedIDs
        for kind in BrowserIntegration.Kind.allCases where kind.isChromium {
            ids.insert(unpackedExtensionID(path: BrowserIntegration.pluginFolder(for: kind).path))
        }
        return ids.sorted()
    }

    static func isValidChromiumID(_ id: String) -> Bool {
        id.count == 32 && id.allSatisfy { ("a"..."p").contains($0) }
    }

    /// Called by the bridge with the id of a Chromium extension that talked to it. Rewrites the manifests
    /// when the id is new (e.g. a store install or a folder in another place).
    static func noteExtensionID(_ id: String) {
        guard isValidChromiumID(id), !reportedIDs.contains(id) else { return }
        reportedIDs.insert(id)
        register()
    }

    /// Writes the host manifest for every installed browser. Skipped when the app carries no host binary.
    static func register() {
        let fm = FileManager.default
        guard fm.isExecutableFile(atPath: hostBinary.path),
              let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else { return }
        let chromiumOrigins = allowedChromiumIDs.map { "chrome-extension://\($0)/" }
        for browser in browsers {
            let root = support.appendingPathComponent(browser.root, isDirectory: true)
            guard fm.fileExists(atPath: root.path) || (browser.family == .firefox && firefoxInstalled(support: support)) else { continue }
            var manifest: [String: Any] = [
                "name": hostName,
                "description": "MC Download Manager launcher",
                "path": hostBinary.path,
                "type": "stdio",
            ]
            switch browser.family {
            case .chromium: manifest["allowed_origins"] = chromiumOrigins
            case .firefox: manifest["allowed_extensions"] = [firefoxAddonID]
            }
            let dir = root.appendingPathComponent("NativeMessagingHosts", isDirectory: true)
            let file = dir.appendingPathComponent("\(hostName).json")
            guard let data = try? JSONSerialization.data(withJSONObject: manifest, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { continue }
            if (try? Data(contentsOf: file)) == data { continue }
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            try? data.write(to: file, options: .atomic)
        }
    }

    /// Firefox keeps profiles in ~/Library/Application Support/Firefox but reads native hosts from …/Mozilla.
    private static func firefoxInstalled(support: URL) -> Bool {
        FileManager.default.fileExists(atPath: support.appendingPathComponent("Firefox").path)
            || BrowserIntegration.Kind.firefox.isInstalled
    }
}
