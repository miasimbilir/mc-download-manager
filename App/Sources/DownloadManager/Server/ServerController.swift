import Foundation
import Combine
import Network

/// Yerel HTTP köprüsünü yönetir ve istekleri DownloadStore'a çevirir.
final class ServerController: ObservableObject {
    @Published var statusText = String(localized: "Not started")
    @Published var running = false
    /// Last time each browser's extension talked to the bridge (keys: safari, chrome, brave, firefox, other).
    @Published var extensionConnections: [String: Date] = [:]

    func isExtensionConnected(_ browser: String, within seconds: TimeInterval = 120) -> Bool {
        guard let d = extensionConnections[browser] else { return false }
        return Date().timeIntervalSince(d) < seconds
    }

    private func noteConnection(_ req: HTTPRequest) {
        var browser = "other"
        if let origin = req.headers["origin"] {
            if origin.hasPrefix("safari-web-extension://") { browser = "safari" }
            else if origin.hasPrefix("chrome-extension://") { browser = "chrome" }
            else if origin.hasPrefix("moz-extension://") { browser = "firefox" }
        }
        if let h = req.headers["x-mcdm-extension"]?.lowercased(), ["safari", "chrome", "brave", "edge", "firefox"].contains(h) { browser = h }
        if let q = req.path.split(separator: "?", maxSplits: 1).last, req.path.contains("?") {
            for pair in q.split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1)
                if kv.count == 2, kv[0] == "browser" { browser = String(kv[1]).lowercased() }
            }
        }
        extensionConnections[browser] = Date()
        // Chromium extension ids may call the native host that launches the app (see NativeHostRegistry).
        var extensionID = req.headers["x-mcdm-extension-id"]
        if let origin = req.headers["origin"], origin.hasPrefix("chrome-extension://") {
            extensionID = String(origin.dropFirst("chrome-extension://".count).prefix { $0 != "/" })
        }
        if let id = extensionID?.lowercased(), NativeHostRegistry.isValidChromiumID(id) {
            DispatchQueue.global(qos: .utility).async { NativeHostRegistry.noteExtensionID(id) }
        }
    }

    private var server: LocalServer?
    private let store: DownloadStore
    private let settings: AppSettings
    private var cancellable: AnyCancellable?

    static let version = "1.3.0"

    init(store: DownloadStore, settings: AppSettings) {
        self.store = store
        self.settings = settings
        localServerAllowedOrigin = { Self.originAllowed($0) }
        start()
        cancellable = settings.$serverPort
            .dropFirst()
            .removeDuplicates()
            .debounce(for: .seconds(1), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in self?.start() }
    }

    func start() {
        server?.stop()
        running = false
        let port = UInt16(clamping: max(1024, min(65535, settings.serverPort)))
        let s = LocalServer(port: port) { [weak self] req, done in
            DispatchQueue.main.async {
                done(self?.handle(req) ?? .json(["ok": false, "error": "shutting down"], status: 500))
            }
        }
        s.onStateChange = { [weak self] st in
            DispatchQueue.main.async {
                switch st {
                case .ready:
                    self?.running = true
                    self?.statusText = String(localized: "Listening on 127.0.0.1:\(String(port))")
                case .failed(let e):
                    self?.running = false
                    self?.statusText = String(localized: "Error: \(e.localizedDescription) (port \(String(port)) may be in use)")
                case .cancelled:
                    self?.running = false
                    self?.statusText = String(localized: "Stopped")
                default:
                    break
                }
            }
        }
        do {
            try s.start()
            server = s
        } catch {
            statusText = String(localized: "Could not start: \(error.localizedDescription)")
        }
    }

    /// Only browser extensions may talk to the bridge. Requests without an Origin, with an
    /// empty Origin or with the opaque "null" origin (sandboxed iframes, data: pages) are rejected,
    /// so ordinary web pages cannot drive the app.
    static func originAllowed(_ origin: String?) -> Bool {
        guard let origin, !origin.isEmpty, origin != "null" else { return false }
        return origin.hasPrefix("chrome-extension://")
            || origin.hasPrefix("safari-web-extension://")
            || origin.hasPrefix("moz-extension://")
    }

    /// Extension requests either carry an extension Origin (Safari, Chrome POST) or — when Chrome omits the
    /// Origin header for GETs covered by host permissions — no Origin at all plus the X-MCDM-Extension header.
    /// A web page cannot produce the second form: custom headers force a CORS preflight, and the preflight's
    /// Origin is a web origin that is rejected here.
    static func extensionRequest(_ req: HTTPRequest) -> Bool {
        let origin = req.headers["origin"]
        if originAllowed(origin) { return true }
        let hasHeader = !(req.headers["x-mcdm-extension"] ?? "").isEmpty
        return origin == nil && hasHeader
    }

    private func hostAllowed(_ host: String?) -> Bool {
        guard let host = host?.lowercased() else { return false }
        let port = UInt16(clamping: max(1024, min(65535, settings.serverPort)))
        return host == "127.0.0.1:\(port)" || host == "localhost:\(port)" || host == "[::1]:\(port)"
    }

    private func handle(_ req: HTTPRequest) -> HTTPResponse {
        guard Self.extensionRequest(req), hostAllowed(req.headers["host"]) else {
            return .json(["ok": false, "error": "forbidden"], status: 403)
        }
        if req.method == "OPTIONS" { return .json(["ok": true]) }
        noteConnection(req)
        let path = req.path.split(separator: "?", maxSplits: 1).first.map(String.init) ?? req.path

        switch (req.method, path) {
        case ("GET", "/ping"):
            return .json([
                "ok": true,
                "app": "MCDownloadManager",
                "version": Self.version,
                "ytdlp": VideoDownloader.locateYtDlp(custom: settings.ytdlpPath) != nil,
                "active": store.items.filter { $0.state.isActive }.count,
                "captureExtensions": settings.captureExtensions,
            ])

        case ("GET", "/downloads"):
            let list: [[String: Any]] = store.items.prefix(50).map {
                [
                    "id": $0.id.uuidString,
                    "name": $0.fileName,
                    "state": $0.state.rawValue,
                    "progress": $0.progress,
                    "speed": $0.speed,
                    "total": $0.totalBytes,
                    "received": $0.receivedBytes,
                    "stage": $0.stage,
                ]
            }
            return .json(["ok": true, "items": list])

        case ("POST", "/download"):
            guard let obj = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any],
                  let urlStr = obj["url"] as? String,
                  let url = URL(string: urlStr),
                  let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme) else {
                return .json(["ok": false, "error": "invalid url"], status: 400)
            }
            var r = NewDownloadRequest(url: url)
            r.fileName = obj["filename"] as? String
            r.referer = obj["referer"] as? String
            r.userAgent = obj["userAgent"] as? String
            r.cookieHeader = obj["cookieHeader"] as? String
            r.cookies = obj["cookies"] as? [[String: Any]]
            r.kind = (obj["kind"] as? String) == "video" ? .video : .file
            if let h = obj["headers"] as? [String: String] { r.extraHeaders = h }
            if let s = obj["segments"] as? Int { r.segments = s }
            let item = store.add(r)
            return .json(["ok": true, "id": item.id.uuidString])

        case ("POST", "/action"):
            guard let obj = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any],
                  let idStr = obj["id"] as? String, let id = UUID(uuidString: idStr),
                  let action = obj["action"] as? String else {
                return .json(["ok": false, "error": "id and action required"], status: 400)
            }
            switch action {
            case "pause": store.pause(id)
            case "resume": store.resume(id)
            case "cancel": store.cancel(id)
            case "remove": store.remove(id)
            default: return .json(["ok": false, "error": "unknown action"], status: 400)
            }
            return .json(["ok": true])

        default:
            return .json(["ok": false, "error": "not found"], status: 404)
        }
    }
}
