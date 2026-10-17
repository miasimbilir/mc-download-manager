using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCDM;

public sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

public sealed record HttpResponse(int Status, JsonNode Json)
{
    public static HttpResponse Ok(JsonObject obj) => new(200, obj);
    public static HttpResponse Error(int status, string message) => new(status, new JsonObject { ["ok"] = false, ["error"] = message });
}

/// A small HTTP server listening on 127.0.0.1. Browser extensions POST to it.
public sealed class LocalServer(int port, Func<HttpRequest, Task<HttpResponse>> handler, Func<string?, bool> allowedOrigin)
{
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private const int MaxRequestBytes = 16_000_000;

    public int Port { get; } = port;

    /// Throws SocketException when the port is taken.
    public void Start()
    {
        var l = new TcpListener(IPAddress.Loopback, Port);
        l.Start();
        listener = l;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(() => AcceptLoop(l, token));
    }

    public void Stop()
    {
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        listener = null;
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk = new byte[1 << 16];
                HttpRequest? req = null;
                while (req == null)
                {
                    var n = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    buffer.Write(chunk, 0, n);
                    req = Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                    if (req == null && buffer.Length > MaxRequestBytes) return;
                }
                HttpResponse resp;
                try { resp = await handler(req).ConfigureAwait(false); }
                catch { resp = HttpResponse.Error(500, "internal error"); }
                var bytes = Render(resp, req.Headers.GetValueOrDefault("origin"));
                await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    public static HttpRequest? Parse(ReadOnlySpan<byte> data)
    {
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return null;
        var head = Encoding.UTF8.GetString(data[..headerEnd]);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lines.Skip(1))
        {
            var c = l.IndexOf(':');
            if (c <= 0) continue;
            headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        var length = 0;
        if (headers.TryGetValue("content-length", out var cl) && (!int.TryParse(cl, out length) || length < 0)) length = 0;
        var bodyStart = headerEnd + 4;
        if (data.Length - bodyStart < length) return null;
        return new HttpRequest(requestLine[0].ToUpperInvariant(), requestLine[1], headers, data.Slice(bodyStart, length).ToArray());
    }

    private byte[] Render(HttpResponse resp, string? origin)
    {
        var body = Encoding.UTF8.GetBytes(resp.Json.ToJsonString());
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(resp.Status).Append(resp.Status == 200 ? " OK" : " Error").Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n");
        // CORS headers only for allowed extension origins; ordinary web pages get none.
        if (origin != null && allowedOrigin(origin) && origin.IndexOfAny(['\r', '\n']) < 0)
        {
            head.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n")
                .Append("Vary: Origin\r\n")
                .Append("Access-Control-Allow-Headers: Content-Type, X-MCDM-Extension, X-MCDM-Extension-Id, X-MCDM-Extension-Version\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n")
                // Chrome Private/Local Network Access preflights for loopback targets.
                .Append("Access-Control-Allow-Private-Network: true\r\n")
                .Append("Access-Control-Allow-Local-Network: true\r\n");
        }
        head.Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        var result = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(result, 0);
        body.CopyTo(result, headBytes.Length);
        return result;
    }
}

/// Runs the local HTTP bridge and turns its requests into DownloadStore calls.
public sealed class ServerController : INotifyPropertyChanged
{
    public string StatusText { get; private set; } = Loc.T("Not started");
    public bool Running { get; private set; }

    /// Last time each browser's extension talked to the bridge (keys: chrome, edge, brave, opera, firefox, other).
    private readonly Dictionary<string, DateTime> extensionConnections = new(StringComparer.Ordinal);

    public bool IsExtensionConnected(string browser, double withinSeconds = 120) =>
        extensionConnections.TryGetValue(browser, out var d) && (DateTime.UtcNow - d).TotalSeconds < withinSeconds;

    /// Browsers whose extension runs another version than the one this app carries (since 1.4.1 every
    /// extension reports its version; older ones send no version header, before 1.3.0 not even their id).
    /// The files on disk may well be current — a Chromium browser keeps running the old code until the
    /// extension reloads (1.4.1 and later do that by themselves) or “Reload” is clicked on its extensions page.
    private readonly HashSet<string> outdatedExtensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> extensionVersions = new(StringComparer.Ordinal);
    private readonly HashSet<string> outdatedNoticed = new(StringComparer.Ordinal);

    public bool IsExtensionOutdated(string browser) => outdatedExtensions.Contains(browser);

    /// Version the browser's extension reported last ("" when it is too old to report one, null when it never spoke).
    public string? ExtensionVersion(string browser) => extensionVersions.GetValueOrDefault(browser);

    /// A browser keeps running an outdated extension a little while after it first spoke (an extension that
    /// reloads itself reports the new version within a ping or two): once per browser and launch.
    public event Action<string>? ExtensionOutdated;

    /// How long an outdated extension gets to reload itself before the user is told.
    public static TimeSpan OutdatedGrace { get; set; } = TimeSpan.FromSeconds(12);

    private LocalServer? server;
    private readonly DownloadStore store;
    private readonly AppSettings settings;
    private Timer? restart, retry;
    private bool stopped;

    /// How long to wait before trying a busy port again.
    public static TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerController(DownloadStore store, AppSettings settings)
    {
        this.store = store;
        this.settings = settings;
        Start();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppSettings.ServerPort)) return;
            // Debounced: the port field changes with every keystroke.
            restart?.Dispose();
            restart = new Timer(_ => MainThread.Post(Start), null, 1000, Timeout.Infinite);
        };
    }

    private int Port => Math.Clamp(settings.ServerPort, 1024, 65535);

    public void Start()
    {
        retry?.Dispose();
        retry = null;
        if (stopped) return;
        server?.Stop();
        server = null;
        var wasRunning = Running;
        var before = StatusText;
        Running = false;
        var port = Port;
        var s = new LocalServer(port, req => MainThread.Run(() => Handle(req)), OriginAllowed);
        try
        {
            s.Start();
            server = s;
            Running = true;
            StatusText = Loc.T("Listening on 127.0.0.1:{0}", port);
        }
        catch (SocketException e)
        {
            StatusText = Loc.T("Error: {0} (port {1} may be in use)", e.Message, port);
        }
        catch (Exception e)
        {
            StatusText = Loc.T("Could not start: {0}", e.Message);
        }
        // Whatever holds the port (another program, a copy of the app that is just closing) may let go
        // of it; without the bridge the extensions cannot reach the app, so keep trying.
        if (!Running) retry = new Timer(_ => MainThread.Post(() => { if (!Running) Start(); }), null, RetryInterval, Timeout.InfiniteTimeSpan);
        if (Running != wasRunning || StatusText != before) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Stop()
    {
        stopped = true;
        retry?.Dispose();
        restart?.Dispose();
        server?.Stop();
        server = null;
        Running = false;
    }

    /// Which browser's extension sent the request (chrome, edge, brave, opera, firefox, other).
    public static string BrowserName(HttpRequest req)
    {
        var browser = "other";
        if (req.Headers.TryGetValue("origin", out var origin))
        {
            if (origin.StartsWith("chrome-extension://", StringComparison.Ordinal)) browser = "chrome";
            else if (origin.StartsWith("moz-extension://", StringComparison.Ordinal)) browser = "firefox";
        }
        if (req.Headers.TryGetValue("x-mcdm-extension", out var h) && h.ToLowerInvariant() is "chrome" or "brave" or "opera" or "edge" or "safari" or "firefox")
            browser = h.ToLowerInvariant();
        var q = req.Path.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in req.Path[(q + 1)..].Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "browser" && kv[1].Length is > 0 and < 20) browser = kv[1].ToLowerInvariant();
            }
        }
        // Opera runs the Chrome extension, which introduces itself as "chrome"; only the User-Agent tells them apart.
        if (browser == "chrome" && req.Headers.TryGetValue("user-agent", out var agent) && agent.Contains(" OPR/", StringComparison.Ordinal))
            browser = "opera";
        return browser;
    }

    private void NoteConnection(HttpRequest req, string browser)
    {
        req.Headers.TryGetValue("origin", out var origin);
        // A Chromium extension that reaches the bridge may also call the native host that starts the app.
        req.Headers.TryGetValue("x-mcdm-extension-id", out var extensionId);
        if (origin != null && origin.StartsWith("chrome-extension://", StringComparison.Ordinal))
            extensionId = origin["chrome-extension://".Length..].Split('/')[0];
        if (NativeHostRegistry.NoteExtensionId(settings, extensionId)) Task.Run(() => NativeHostRegistry.Register(settings));
        var first = !IsExtensionConnected(browser);
        extensionConnections[browser] = DateTime.UtcNow;
        var version = req.Headers.GetValueOrDefault("x-mcdm-extension-version") ?? "";
        var versionChanged = extensionVersions.GetValueOrDefault(browser) != version;
        extensionVersions[browser] = version;
        var outdated = IsOutdated(req.Headers.ContainsKey("x-mcdm-extension-id"), version, BrowserIntegration.BundledVersion);
        var changed = outdated ? outdatedExtensions.Add(browser) : outdatedExtensions.Remove(browser);
        if (first || changed || versionChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        if (outdated && browser != "other" && outdatedNoticed.Add(browser))
        {
            Timer? t = null;
            t = new Timer(_ => MainThread.Post(() =>
            {
                t?.Dispose();
                if (IsExtensionOutdated(browser)) ExtensionOutdated?.Invoke(browser);
                else outdatedNoticed.Remove(browser);   // it reloaded itself; a later outdated copy is noticed again
            }), null, OutdatedGrace, Timeout.InfiniteTimeSpan);
        }
    }

    /// `bundled`: the version this app carries (null when its extension files are missing — then only extensions
    /// from before 1.3.0, which send no id, count as outdated).
    internal static bool IsOutdated(bool sentId, string version, string? bundled) =>
        !sentId || (bundled != null && version != bundled);

    /// Only browser extensions may talk to the bridge. Requests without an Origin, with an empty Origin or
    /// with the opaque "null" origin (sandboxed iframes, data: pages) are rejected, so ordinary web pages
    /// cannot drive the app.
    public static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin == "null") return false;
        return origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
            || origin.StartsWith("safari-web-extension://", StringComparison.Ordinal)
            || origin.StartsWith("moz-extension://", StringComparison.Ordinal);
    }

    /// Extension requests either carry an extension Origin (Chrome POST) or — when Chrome omits the Origin
    /// header for GETs covered by host permissions — no Origin at all plus the X-MCDM-Extension header.
    /// A web page cannot produce the second form: custom headers force a CORS preflight, and the preflight's
    /// Origin is a web origin that is rejected here.
    public static bool ExtensionRequest(HttpRequest req)
    {
        req.Headers.TryGetValue("origin", out var origin);
        if (OriginAllowed(origin)) return true;
        var hasHeader = req.Headers.TryGetValue("x-mcdm-extension", out var x) && x.Length > 0;
        return origin == null && hasHeader;
    }

    private bool HostAllowed(string? host)
    {
        host = host?.ToLowerInvariant();
        var port = Port;
        return host == $"127.0.0.1:{port}" || host == $"localhost:{port}" || host == $"[::1]:{port}";
    }

    public HttpResponse Handle(HttpRequest req)
    {
        if (!ExtensionRequest(req) || !HostAllowed(req.Headers.GetValueOrDefault("host")))
            return HttpResponse.Error(403, "forbidden");
        if (req.Method == "OPTIONS") return HttpResponse.Ok(new JsonObject { ["ok"] = true });
        var browser = BrowserName(req);
        NoteConnection(req, browser);
        var q = req.Path.IndexOf('?');
        var path = q >= 0 ? req.Path[..q] : req.Path;

        switch (req.Method, path)
        {
            case ("GET", "/ping"):
            {
                var exts = new JsonArray();
                foreach (var e in settings.CaptureExtensions) exts.Add(e);
                var sites = new JsonArray();
                foreach (var e in settings.BrowserOnlySites) sites.Add(e);
                var bypass = new JsonArray();
                foreach (var e in store.BypassHosts) bypass.Add(e);
                return HttpResponse.Ok(new JsonObject
                {
                    ["ok"] = true,
                    ["app"] = "MCDownloadManager",
                    ["platform"] = "windows",
                    ["version"] = AppInfo.Version,
                    ["ytdlp"] = VideoDownloader.LocateYtDlp(settings.YtdlpPath) != null,
                    ["active"] = store.ActiveCount,
                    ["captureExtensions"] = exts,
                    // Extensions compare this with their own version and reload themselves (unpacked) when it differs.
                    ["extensionVersion"] = BrowserIntegration.BundledVersion,
                    // Downloads from these sites stay in the browser: permanently, or for a few minutes after a hand-off.
                    ["browserOnlySites"] = sites,
                    ["bypassHosts"] = bypass,
                });
            }

            case ("GET", "/downloads"):
            {
                var list = new JsonArray();
                foreach (var i in store.Items.Take(50))
                {
                    list.Add(new JsonObject
                    {
                        ["id"] = i.Id.ToString().ToUpperInvariant(),
                        ["name"] = i.FileName,
                        ["state"] = i.State.ToString(),
                        ["progress"] = i.Progress,
                        ["speed"] = i.Speed,
                        ["total"] = i.TotalBytes,
                        ["received"] = i.ReceivedBytes,
                        ["stage"] = i.Stage,
                        ["error"] = i.ErrorMessage,
                    });
                }
                return HttpResponse.Ok(new JsonObject { ["ok"] = true, ["items"] = list });
            }

            case ("POST", "/download"):
            {
                JsonElement obj;
                try { obj = JsonDocument.Parse(req.Body).RootElement; }
                catch { return HttpResponse.Error(400, "invalid json"); }
                if (obj.ValueKind != JsonValueKind.Object || Links.ParseHttp(Str(obj, "url")) is not { } url)
                    return HttpResponse.Error(400, "invalid url");
                var r = new NewDownloadRequest(url)
                {
                    FileName = Str(obj, "filename"),
                    Referer = Str(obj, "referer"),
                    UserAgent = Str(obj, "userAgent"),
                    CookieHeader = Str(obj, "cookieHeader"),
                    Kind = Str(obj, "kind") == "video" ? DownloadKind.video : DownloadKind.file,
                    Confirm = true,
                    SourceBrowser = browser == "other" ? null : browser,
                };
                if (obj.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
                {
                    r.Cookies = [];
                    foreach (var c in cookies.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        var name = Str(c, "name"); var domain = Str(c, "domain");
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain)) continue;
                        r.Cookies.Add(new BrowserCookie(name, Str(c, "value") ?? "", domain, Str(c, "path") ?? "/",
                            c.TryGetProperty("secure", out var sec) && sec.ValueKind == JsonValueKind.True,
                            c.TryGetProperty("expirationDate", out var exp) && exp.ValueKind == JsonValueKind.Number ? exp.GetDouble() : 0));
                    }
                }
                if (obj.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
                    foreach (var h in hs.EnumerateObject())
                        if (h.Value.ValueKind == JsonValueKind.String) r.ExtraHeaders[h.Name] = h.Value.GetString()!;
                if (obj.TryGetProperty("segments", out var seg) && seg.ValueKind == JsonValueKind.Number && seg.TryGetInt32(out var sn))
                    r.Segments = Math.Clamp(sn, 1, 16);
                var item = store.Add(r);
                return HttpResponse.Ok(new JsonObject { ["ok"] = true, ["id"] = item.Id.ToString().ToUpperInvariant() });
            }

            case ("POST", "/site"):
            {
                JsonElement obj;
                try { obj = JsonDocument.Parse(req.Body).RootElement; }
                catch { return HttpResponse.Error(400, "invalid json"); }
                if (obj.ValueKind != JsonValueKind.Object || Sites.Key(Str(obj, "host")) is not { } host
                    || !obj.TryGetProperty("browserOnly", out var on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return HttpResponse.Error(400, "host and browserOnly required");
                settings.SetBrowserOnly(host, on.ValueKind == JsonValueKind.True);
                var list = new JsonArray();
                foreach (var e in settings.BrowserOnlySites) list.Add(e);
                return HttpResponse.Ok(new JsonObject { ["ok"] = true, ["browserOnlySites"] = list });
            }

            case ("POST", "/action"):
            {
                JsonElement obj;
                try { obj = JsonDocument.Parse(req.Body).RootElement; }
                catch { return HttpResponse.Error(400, "invalid json"); }
                if (obj.ValueKind != JsonValueKind.Object || !Guid.TryParse(Str(obj, "id"), out var id) || Str(obj, "action") is not { } action)
                    return HttpResponse.Error(400, "id and action required");
                switch (action)
                {
                    case "pause": store.Pause(id); break;
                    case "resume": store.Resume(id); break;
                    case "cancel": store.Cancel(id); break;
                    case "remove": store.Remove(id); break;
                    default: return HttpResponse.Error(400, "unknown action");
                }
                return HttpResponse.Ok(new JsonObject { ["ok"] = true });
            }

            default:
                return HttpResponse.Error(404, "not found");
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
