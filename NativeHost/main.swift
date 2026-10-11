import Foundation

// Native messaging host for Chrome, Brave, Edge and Firefox (Safari uses its app extension instead).
// The browser starts this tool when the extension calls runtime.sendNativeMessage; it understands one
// request, {"action": "launchApp"}, which starts MC Download Manager in the background so the extension
// can hand over a download while the app was not running. Nothing else (URLs, cookies) passes through here.
//
// Protocol: each message is a 32-bit length in native byte order followed by UTF-8 JSON, on stdin/stdout.

/// …/MC Download Manager.app/Contents/Helpers/mcdm-native-host → …/MC Download Manager.app
let appURL: URL = {
    let exe = (Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0])).resolvingSymlinksInPath()
    return exe.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
}()

func readMessage() -> [String: Any]? {
    let input = FileHandle.standardInput
    let header = input.readData(ofLength: 4)
    guard header.count == 4 else { return nil }
    let length = header.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }
    guard length > 0, length <= 1 << 20 else { return nil }
    let body = input.readData(ofLength: Int(length))
    guard body.count == Int(length) else { return nil }
    return (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
}

func send(_ object: [String: Any]) {
    guard let body = try? JSONSerialization.data(withJSONObject: object) else { return }
    var length = UInt32(body.count)
    var out = Data(bytes: &length, count: 4)
    out.append(body)
    FileHandle.standardOutput.write(out)
}

/// `open -g` starts the app without bringing it to the front (it is a menu bar app); a running app is left as is.
func launchApp() -> Bool {
    guard appURL.pathExtension == "app", FileManager.default.fileExists(atPath: appURL.path) else { return false }
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
    p.arguments = ["-g", appURL.path]
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    guard (try? p.run()) != nil else { return false }
    p.waitUntilExit()
    return p.terminationStatus == 0
}

// sendNativeMessage sends one message and closes stdin; connectNative keeps it open. Serve until EOF.
while let message = readMessage() {
    switch message["action"] as? String {
    case "launchApp":
        send(["ok": launchApp()])
    case "ping":
        send(["ok": true])
    default:
        send(["ok": false, "error": "unknown action"])
    }
}
