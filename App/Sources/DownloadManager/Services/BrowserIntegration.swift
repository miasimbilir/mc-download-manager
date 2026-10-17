import AppKit
import Foundation
import Security
import SafariServices

/// Browser extension helpers. Supported browsers for the guided setup: Safari, Google Chrome, Brave, Opera, Firefox.
enum BrowserIntegration {
    static let safariExtensionID = "com.mesutcevik.mcdownloadmanager.safari"
    /// Set this once the extension is published on the Chrome Web Store; the wizard then links to it.
    static let chromeWebStoreURL: URL? = nil

    enum Kind: String, CaseIterable, Identifiable {
        case safari, chrome, brave, opera, firefox
        var id: String { rawValue }

        var name: String {
            switch self {
            case .safari: return "Safari"
            case .chrome: return "Google Chrome"
            case .brave: return "Brave"
            case .opera: return "Opera"
            case .firefox: return "Firefox"
            }
        }
        var bundleID: String {
            switch self {
            case .safari: return "com.apple.Safari"
            case .chrome: return "com.google.Chrome"
            case .brave: return "com.brave.Browser"
            case .opera: return "com.operasoftware.Opera"
            case .firefox: return "org.mozilla.firefox"
            }
        }
        var extensionsPageURL: String {
            switch self {
            case .safari: return ""
            case .chrome: return "chrome://extensions"
            case .brave: return "brave://extensions"
            case .opera: return "opera://extensions"
            case .firefox: return "about:addons"
            }
        }
        var isChromium: Bool { self == .chrome || self == .brave || self == .opera }
        var appURL: URL? { NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) }
        var isInstalled: Bool { appURL != nil }
        var icon: NSImage? { appURL.map { NSWorkspace.shared.icon(forFile: $0.path) } }
    }

    /// Installed browsers in setup order.
    static var installedBrowsers: [Kind] { Kind.allCases.filter(\.isInstalled) }

    private static let queue = DispatchQueue(label: "dm.browser-integration", qos: .userInitiated)

    // MARK: Files

    /// Unpacked extension shipped inside the app.
    static var bundledExtensionFolder: URL {
        if let override = ProcessInfo.processInfo.environment["MCDM_EXTENSION_DIR"] { return URL(fileURLWithPath: override) }
        return Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/ChromeExtension")
    }

    /// ~/Downloads/MC Download Manager/Browser Plugins
    static var pluginsRoot: URL {
        URL(fileURLWithPath: AppSettings.defaultDownloadDir).appendingPathComponent("Browser Plugins", isDirectory: true)
    }

    static func pluginFolder(for kind: Kind) -> URL {
        pluginsRoot.appendingPathComponent(kind.name, isDirectory: true)
    }

    /// Copies the unpacked extension into the browser's plugin folder. Returns the folder.
    @discardableResult
    static func prepareFiles(for kind: Kind) throws -> URL {
        let fm = FileManager.default
        let dest = pluginFolder(for: kind)
        try fm.createDirectory(at: pluginsRoot, withIntermediateDirectories: true)
        if fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
        try fm.copyItem(at: bundledExtensionFolder, to: dest)
        let readme = """
        MC Download Manager — \(kind.name) extension

        1. Open \(kind.extensionsPageURL) in \(kind.name).
        2. Turn on "Developer mode" (top right).
        3. Click "Load unpacked" and select this folder.

        Do not move or delete this folder while the extension is installed.
        """
        try? readme.write(to: dest.appendingPathComponent("README.txt"), atomically: true, encoding: .utf8)
        return dest
    }

    static func filesPrepared(for kind: Kind) -> Bool {
        FileManager.default.fileExists(atPath: pluginFolder(for: kind).appendingPathComponent("manifest.json").path)
    }

    // MARK: Versions

    private static func manifestVersion(in folder: URL) -> String? {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent("manifest.json")),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return obj["version"] as? String
    }

    /// Version of the extension this app carries; every browser's extension should report the same.
    static let bundledExtensionVersion: String = manifestVersion(in: bundledExtensionFolder) ?? ServerController.version

    /// Version of the files in the browser's plugin folder (nil when not prepared).
    static func preparedVersion(for kind: Kind) -> String? { manifestVersion(in: pluginFolder(for: kind)) }

    static func filesUpToDate(for kind: Kind) -> Bool { preparedVersion(for: kind) == bundledExtensionVersion }

    /// After an app update, replaces plugin folders prepared by an older version. Browsers keep running the old
    /// code until the extension is reloaded; extensions that report their version to the bridge reload themselves
    /// when the app reports a different one (older ones need the reload icon once). Runs at launch, off the main thread.
    static func refreshOutdatedPluginFolders() {
        for kind in Kind.allCases where kind.isChromium && filesPrepared(for: kind) && !filesUpToDate(for: kind) {
            try? prepareFiles(for: kind)
        }
    }

    // MARK: Safari

    static func safariExtensionEnabled() async -> Bool? {
        await withCheckedContinuation { cont in
            SFSafariExtensionManager.getStateOfSafariExtension(withIdentifier: safariExtensionID) { state, _ in
                cont.resume(returning: state?.isEnabled)
            }
        }
    }

    /// True when the app is signed with an Apple-issued certificate (Developer ID or Apple Development),
    /// i.e. Safari loads the extension without "Allow Unsigned Extensions". Ad-hoc builds have no team.
    static var isSignedWithAppleCertificate: Bool {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(Bundle.main.bundleURL as CFURL, [], &code) == errSecSuccess, let code else { return false }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let dict = info as? [String: Any] else { return false }
        return (dict[kSecCodeInfoTeamIdentifier as String] as? String)?.isEmpty == false
    }

    static func openSafariExtensionPreferences() {
        SFSafariApplication.showPreferencesForExtension(withIdentifier: safariExtensionID) { error in
            guard error != nil else { return }
            DispatchQueue.main.async {
                if let url = URL(string: "x-apple.systempreferences:com.apple.Safari-Settings.extension") {
                    NSWorkspace.shared.open(url)
                }
            }
        }
    }

    /// Re-registers the app and its extension with the system. Runs in the background.
    static func repairSafariExtension(completion: (() -> Void)? = nil) {
        let appPath = Bundle.main.bundleURL.path
        let appex = Bundle.main.bundleURL.appendingPathComponent("Contents/PlugIns/MC Download Manager Extension.appex").path
        queue.async {
            run("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister", ["-f", appPath])
            run("/usr/bin/pluginkit", ["-a", appex])
            run("/usr/bin/pluginkit", ["-e", "use", "-i", safariExtensionID])
            DispatchQueue.main.async { completion?() }
        }
    }

    // MARK: Chromium

    /// Opens the browser's extensions page (same mechanism as `open -a <Browser> chrome://extensions`).
    static func openExtensionsPage(in kind: Kind) {
        guard kind != .safari else { return }
        open(kind.extensionsPageURL, in: kind)
    }

    private static func open(_ address: String, in kind: Kind) {
        guard let appURL = kind.appURL, let url = URL(string: address) else { return }
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.activates = true
        NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: cfg) { _, error in
            if error != nil { queue.async { run("/usr/bin/open", ["-a", appURL.path, address]) } }
        }
    }

    // MARK: Firefox

    /// Add-on signed by Mozilla (unlisted channel), bundled by scripts/build.sh. Release Firefox
    /// installs it permanently when the file is opened: Firefox shows its own “Add …?” prompt.
    static var signedFirefoxAddon: URL? { bundledResource("FirefoxExtension.xpi") }
    /// Unsigned build, only loadable temporarily via about:debugging (removed when Firefox quits).
    static var unsignedFirefoxAddon: URL? { bundledResource("FirefoxExtension-unsigned.xpi") }

    private static func bundledResource(_ name: String) -> URL? {
        let url = Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/\(name)")
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    /// Hands the signed add-on to Firefox, which asks the user to confirm the installation.
    static func installFirefoxAddon() {
        guard let xpi = signedFirefoxAddon, let appURL = Kind.firefox.appURL else { return }
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.activates = true
        NSWorkspace.shared.open([xpi], withApplicationAt: appURL, configuration: cfg) { _, error in
            if error != nil { queue.async { run("/usr/bin/open", ["-a", appURL.path, xpi.path]) } }
        }
    }

    /// Copies the unsigned add-on next to the other browser plugins for a temporary load.
    @discardableResult
    static func prepareTemporaryFirefoxAddon() throws -> URL {
        guard let src = unsignedFirefoxAddon else { throw CocoaError(.fileNoSuchFile) }
        let fm = FileManager.default
        let dir = pluginFolder(for: .firefox)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        let dest = dir.appendingPathComponent("mc-download-manager.xpi")
        if fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
        try fm.copyItem(at: src, to: dest)
        return dest
    }

    static func openFirefoxDebugging() {
        open("about:debugging#/runtime/this-firefox", in: .firefox)
    }

    static func revealInFinder(_ url: URL) {
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    static func copyToClipboard(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    @discardableResult
    private static func run(_ path: String, _ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return -1 }
        p.waitUntilExit()
        return p.terminationStatus
    }
}
