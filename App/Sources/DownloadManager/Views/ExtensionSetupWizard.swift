import SwiftUI
import AppKit

/// Guided, one-browser-at-a-time setup of the browser extensions (Safari, Chrome, Brave, Opera, Firefox).
struct ExtensionSetupWizard: View {
    @EnvironmentObject var server: ServerController
    let browsers: [BrowserIntegration.Kind]
    let onFinish: () -> Void

    @State private var browserIndex = 0
    @State private var step = 0
    @State private var safariEnabled: Bool?
    @State private var prepared: [BrowserIntegration.Kind: URL] = [:]
    @State private var error: String?
    @State private var copied = false

    private var browser: BrowserIntegration.Kind { browsers[browserIndex] }
    private var stepCount: Int {
        switch browser {
        case .safari: return 3
        case .firefox: return 2
        default: return 4
        }
    }
    private var isLastStep: Bool { step >= stepCount - 1 }
    private var isLastBrowser: Bool { browserIndex >= browsers.count - 1 }
    private var connected: Bool { server.isExtensionConnected(browser.rawValue) }
    private var outdated: Bool { connected && server.isExtensionOutdated(browser.rawValue) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    switch browser {
                    case .safari: safariStep
                    case .firefox: firefoxStep
                    default: chromiumStep
                    }
                    if let error { Text(error).font(.caption).foregroundStyle(.red) }
                }
                .padding(22)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Divider()
            footer
        }
        .frame(width: 620, height: 460)
        .onAppear { refreshSafari() }
        .onReceive(Timer.publish(every: 2, on: .main, in: .common).autoconnect()) { _ in
            if browser == .safari && step == 1 { refreshSafari() }
        }
    }

    // MARK: Header / footer

    private var header: some View {
        HStack(spacing: 12) {
            if let icon = browser.icon {
                Image(nsImage: icon).resizable().frame(width: 40, height: 40)
            } else {
                Image(systemName: "puzzlepiece.extension").font(.system(size: 32))
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(browser.name).font(.title2.bold())
                Text("Browser \(browserIndex + 1) of \(browsers.count) · Step \(step + 1) of \(stepCount)")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 6) {
                ForEach(0..<stepCount, id: \.self) { i in
                    Circle().fill(i <= step ? Color.accentColor : Color.gray.opacity(0.3)).frame(width: 8, height: 8)
                }
            }
        }
        .padding(.horizontal, 22).padding(.vertical, 14)
    }

    private var footer: some View {
        HStack {
            Button("Back") { back() }.disabled(step == 0 && browserIndex == 0)
            Spacer()
            if !isLastBrowser || !isLastStep {
                Button("Skip This Browser") { skipBrowser() }
            }
            Button(isLastStep ? (isLastBrowser ? "Finish" : "Next Browser") : "Next") { next() }
                .keyboardShortcut(.defaultAction)
        }
        .padding(.horizontal, 22).padding(.vertical, 12)
    }

    private func next() {
        error = nil
        copied = false
        if isLastStep {
            if isLastBrowser { onFinish() } else { browserIndex += 1; step = 0 }
        } else {
            step += 1
        }
    }

    private func back() {
        error = nil
        if step > 0 { step -= 1 } else if browserIndex > 0 { browserIndex -= 1; step = 0 }
    }

    private func skipBrowser() {
        error = nil
        if isLastBrowser { onFinish() } else { browserIndex += 1; step = 0 }
    }

    // MARK: Safari

    @ViewBuilder
    private var safariStep: some View {
        switch step {
        case 0:
            stepTitle("The Safari extension is built into the app")
            Text("Nothing needs to be downloaded. The extension only has to be enabled once in Safari's settings. This button registers the extension with macOS and opens the Safari extension settings.")
            Button {
                BrowserIntegration.repairSafariExtension { BrowserIntegration.openSafariExtensionPreferences() }
            } label: { Label("Open Safari Extension Settings", systemImage: "safari") }
            if !BrowserIntegration.isSignedWithAppleCertificate {
                Text("Builds without a Developer ID signature also need Safari ▸ Develop ▸ Allow Unsigned Extensions. If the Develop menu is hidden: Safari ▸ Settings ▸ Advanced ▸ “Show features for web developers”.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        case 1:
            stepTitle("Enable “MC Download Manager” in Safari")
            Text("In Safari ▸ Settings ▸ Extensions, tick the checkbox next to MC Download Manager. When Safari asks for website access, choose “Always Allow on Every Website” so downloads on all sites are captured.")
            statusRow(ok: safariEnabled == true,
                      okText: "Extension is enabled in Safari",
                      pendingText: safariEnabled == nil ? "Checking…" : "Not enabled yet — this updates automatically")
            Button("Open Safari Extension Settings Again") { BrowserIntegration.openSafariExtensionPreferences() }
        default:
            stepTitle("Safari is ready")
            Text("Download a file in Safari or click the MC Download Manager toolbar button to see captured videos. The toolbar button's popup should show “Connected”.")
            statusRow(ok: connected, okText: "Extension has connected to the app", pendingText: "Waiting for the extension to connect (open the popup once)")
        }
    }

    // MARK: Chrome / Brave / Opera

    @ViewBuilder
    private var chromiumStep: some View {
        let folder = BrowserIntegration.pluginFolder(for: browser)
        switch step {
        case 0:
            stepTitle("Prepare the extension files")
            Text("\(browser.name) does not allow apps to install extensions automatically, so the extension is loaded from a folder. The files are placed in:")
            pathBox(folder.path)
            HStack {
                Button {
                    do { prepared[browser] = try BrowserIntegration.prepareFiles(for: browser); error = nil }
                    catch { self.error = error.localizedDescription }
                } label: { Label("Prepare Files", systemImage: "folder.badge.plus") }
                Button("Show in Finder") { BrowserIntegration.revealInFinder(folder) }
                    .disabled(!BrowserIntegration.filesPrepared(for: browser))
            }
            statusRow(ok: BrowserIntegration.filesUpToDate(for: browser),
                      okText: "Files are ready (version \(BrowserIntegration.bundledExtensionVersion))",
                      pendingText: BrowserIntegration.filesPrepared(for: browser)
                        ? "The folder holds an older version — click “Prepare Files”" : "Click “Prepare Files”")
            Text("Keep this folder in place; the browser loads the extension from it.").font(.caption).foregroundStyle(.secondary)
        case 1:
            stepTitle("Open the extensions page and turn on Developer mode")
            Text("Click the button below. On the page that opens, switch on “Developer mode” in the top right corner.")
            Button { BrowserIntegration.openExtensionsPage(in: browser) } label: {
                Label("Open \(browser.extensionsPageURL)", systemImage: "globe")
            }
            if let store = BrowserIntegration.chromeWebStoreURL {
                Link("Or install from the Chrome Web Store", destination: store)
            }
        case 2:
            stepTitle("Load the extension folder")
            Text("Click “Load unpacked” on the extensions page. In the file dialog press ⇧⌘G, paste the folder path below and press Return, then click “Select”.")
            pathBox(folder.path)
            HStack {
                Button {
                    BrowserIntegration.copyToClipboard(folder.path); copied = true
                } label: { Label(copied ? "Copied" : "Copy Folder Path", systemImage: "doc.on.doc") }
                Button("Show in Finder") { BrowserIntegration.revealInFinder(folder) }
            }
        default:
            stepTitle("\(browser.name) is ready")
            Text("Pin MC Download Manager to the toolbar (extensions icon ▸ pin). Its popup should show “Connected”. Downloads of the captured file types are now handed to the app.")
            connectionStatus
            if outdated {
                Text("The browser still runs an older version. Click the reload icon (↻) of MC Download Manager on the extensions page, or restart the browser.")
                    .font(.caption).foregroundStyle(.secondary)
                Button { BrowserIntegration.openExtensionsPage(in: browser) } label: {
                    Label("Open \(browser.extensionsPageURL)", systemImage: "globe")
                }
            }
        }
    }

    // MARK: Firefox

    @ViewBuilder
    private var firefoxStep: some View {
        switch step {
        case 0:
            if BrowserIntegration.signedFirefoxAddon != nil {
                stepTitle("Add the extension to Firefox")
                Text("The Firefox add-on is included in the app and signed by Mozilla. Click the button below; Firefox opens and asks “Add MC Download Manager?”. Click “Add”.")
                Button { BrowserIntegration.installFirefoxAddon() } label: {
                    Label("Add to Firefox", systemImage: "puzzlepiece.extension")
                }
                Text("To capture downloads in private windows too, tick “Allow this extension to run in Private Windows” in the confirmation that follows.")
                    .font(.caption).foregroundStyle(.secondary)
            } else if BrowserIntegration.unsignedFirefoxAddon != nil {
                stepTitle("Load the extension temporarily")
                Text("This build contains an unsigned Firefox add-on. Firefox only loads it temporarily: it is removed when Firefox quits. On the page that opens, click “Load Temporary Add-on…” and select the file below.")
                HStack {
                    Button {
                        do { prepared[browser] = try BrowserIntegration.prepareTemporaryFirefoxAddon(); error = nil }
                        catch { self.error = error.localizedDescription }
                        BrowserIntegration.openFirefoxDebugging()
                    } label: { Label("Open about:debugging", systemImage: "globe") }
                    if let file = prepared[browser] {
                        Button("Show in Finder") { BrowserIntegration.revealInFinder(file) }
                    }
                }
                if let file = prepared[browser] { pathBox(file.path) }
            } else {
                stepTitle("Firefox add-on not included")
                Text("This build of the app does not contain the Firefox add-on. Build it with scripts/pack-firefox.sh, or install the release version of the app.")
            }
        default:
            stepTitle("Firefox is ready")
            Text("Pin MC Download Manager to the toolbar (puzzle icon ▸ gear ▸ Pin to Toolbar). Its popup should show “Connected”. Downloads of the captured file types are now handed to the app.")
            connectionStatus
            if outdated {
                Button { BrowserIntegration.installFirefoxAddon() } label: {
                    Label("Add to Firefox", systemImage: "puzzlepiece.extension")
                }
            }
        }
    }

    // MARK: Pieces

    /// Connected and running the version this app carries; an older extension counts as not done.
    @ViewBuilder
    private var connectionStatus: some View {
        if outdated {
            HStack(spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text("Connected, but the extension is out of date (\(server.extensionVersions[browser.rawValue].flatMap { $0.isEmpty ? nil : $0 } ?? "?") → \(BrowserIntegration.bundledExtensionVersion))")
            }
        } else {
            statusRow(ok: connected, okText: "Extension has connected to the app", pendingText: "Waiting for the extension to connect (it connects right after loading)")
        }
    }

    private func stepTitle(_ key: LocalizedStringKey) -> some View {
        Text(key).font(.headline)
    }

    private func pathBox(_ path: String) -> some View {
        Text(path)
            .font(.system(.callout, design: .monospaced))
            .textSelection(.enabled)
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(nsColor: .textBackgroundColor))
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
    }

    private func statusRow(ok: Bool, okText: LocalizedStringKey, pendingText: LocalizedStringKey) -> some View {
        HStack(spacing: 8) {
            Image(systemName: ok ? "checkmark.circle.fill" : "circle.dashed")
                .foregroundStyle(ok ? .green : .secondary)
            if ok { Text(okText) } else { Text(pendingText).foregroundStyle(.secondary) }
        }
    }

    private func refreshSafari() {
        Task {
            let state = await BrowserIntegration.safariExtensionEnabled()
            await MainActor.run { safariEnabled = state }
        }
    }
}

/// Hosts the wizard in its own window so it can be opened from onboarding and Settings alike.
final class ExtensionWizardWindow {
    static let shared = ExtensionWizardWindow()
    private var window: NSWindow?

    func show(server: ServerController) {
        if let window { window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return }
        let browsers = BrowserIntegration.installedBrowsers
        let root = ExtensionSetupWizard(browsers: browsers.isEmpty ? [.safari] : browsers) { [weak self] in self?.close() }
            .environmentObject(server)
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 620, height: 460),
                         styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        w.title = String(localized: "Browser Extension Setup")
        w.contentView = NSHostingView(rootView: root)
        w.isReleasedWhenClosed = false
        w.center()
        // Normal level: Safari / System Settings opened from the wizard must come in front of it.
        window = w
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func close() {
        window?.close()
        window = nil
    }
}
