import Foundation
import Network

struct HTTPRequest {
    var method: String
    var path: String
    var headers: [String: String]
    var body: Data
}

struct HTTPResponse {
    var status: Int
    var json: Any

    static func json(_ obj: Any, status: Int = 200) -> HTTPResponse {
        HTTPResponse(status: status, json: obj)
    }
}

/// Decides which Origin values receive CORS headers (set by the owner of the server).
var localServerAllowedOrigin: (String?) -> Bool = { _ in false }

/// 127.0.0.1 üzerinde dinleyen küçük bir HTTP sunucusu. Tarayıcı eklentileri buraya POST atar.
final class LocalServer {
    typealias Handler = (HTTPRequest, @escaping (HTTPResponse) -> Void) -> Void

    private var listener: NWListener?
    private let queue = DispatchQueue(label: "dm.local-server")
    private let handler: Handler
    let port: UInt16
    var onStateChange: ((NWListener.State) -> Void)?

    init(port: UInt16, handler: @escaping Handler) {
        self.port = port
        self.handler = handler
    }

    func start() throws {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            throw DownloadFailure(message: String(localized: "Invalid port: \(String(port))"))
        }
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: "127.0.0.1", port: nwPort)
        let l = try NWListener(using: params)
        l.stateUpdateHandler = { [weak self] st in self?.onStateChange?(st) }
        l.newConnectionHandler = { [weak self] c in self?.accept(c) }
        l.start(queue: queue)
        listener = l
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func accept(_ conn: NWConnection) {
        conn.start(queue: queue)
        receive(conn, buffer: Data())
    }

    private func receive(_ conn: NWConnection, buffer: Data) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 1 << 16) { [weak self] data, _, isComplete, error in
            guard let self else { conn.cancel(); return }
            if error != nil { conn.cancel(); return }
            var buf = buffer
            if let data { buf.append(data) }
            if let req = Self.parse(buf) {
                self.handler(req) { resp in self.send(resp, origin: req.headers["origin"], on: conn) }
            } else if isComplete || buf.count > 16_000_000 {
                conn.cancel()
            } else {
                self.receive(conn, buffer: buf)
            }
        }
    }

    static func parse(_ data: Data) -> HTTPRequest? {
        guard let headerEnd = data.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        guard let head = String(data: data[data.startIndex..<headerEnd.lowerBound], encoding: .utf8) else { return nil }
        var lines = head.components(separatedBy: "\r\n")
        guard !lines.isEmpty else { return nil }
        let reqLine = lines.removeFirst().split(separator: " ")
        guard reqLine.count >= 2 else { return nil }
        var headers: [String: String] = [:]
        for l in lines {
            guard let c = l.firstIndex(of: ":") else { continue }
            headers[l[..<c].lowercased()] = l[l.index(after: c)...].trimmingCharacters(in: .whitespaces)
        }
        let length = Int(headers["content-length"] ?? "0") ?? 0
        let bodyStart = headerEnd.upperBound
        guard data.endIndex - bodyStart >= length else { return nil }
        let body = data.subdata(in: bodyStart..<(bodyStart + length))
        return HTTPRequest(method: String(reqLine[0]).uppercased(), path: String(reqLine[1]), headers: headers, body: body)
    }

    private func send(_ resp: HTTPResponse, origin: String?, on conn: NWConnection) {
        let body = (try? JSONSerialization.data(withJSONObject: resp.json)) ?? Data("{}".utf8)
        let reason = resp.status == 200 ? "OK" : "Error"
        var head = "HTTP/1.1 \(resp.status) \(reason)\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + "Content-Length: \(body.count)\r\n"
            + "Cache-Control: no-store\r\n"
            + "X-Content-Type-Options: nosniff\r\n"
        // CORS headers only for allowed extension origins; ordinary web pages get none.
        if let origin, localServerAllowedOrigin(origin) {
            head += "Access-Control-Allow-Origin: \(origin)\r\n"
                + "Vary: Origin\r\n"
                + "Access-Control-Allow-Headers: Content-Type, X-MCDM-Extension, X-MCDM-Extension-Id, X-MCDM-Extension-Version\r\n"
                + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
                // Chrome Private/Local Network Access preflights for loopback targets.
                + "Access-Control-Allow-Private-Network: true\r\n"
                + "Access-Control-Allow-Local-Network: true\r\n"
        }
        head += "Connection: close\r\n\r\n"
        var out = Data(head.utf8)
        out.append(body)
        conn.send(content: out, completion: .contentProcessed { _ in conn.cancel() })
    }
}
