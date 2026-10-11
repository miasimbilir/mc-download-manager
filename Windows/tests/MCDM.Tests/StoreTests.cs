using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MCDM.Tests;

public class StoreTests
{
    private const int TestPort = 48931;

    private sealed class Rig : IDisposable
    {
        public readonly string Dir = Setup.TempDir();
        public AppSettings Settings;
        public DownloadStore Store = null!;
        public string StoreFile => Path.Combine(Dir, "downloads.json");

        public Rig(Action<AppSettings>? configure = null)
        {
            Settings = AppSettings.Load(Path.Combine(Dir, "settings.json"));
            Settings.DownloadDirectory = Path.Combine(Dir, "dl");
            Settings.ShowDetailOnStart = false;
            Settings.ServerPort = TestPort;      // not 48213: a running copy of the app and real browser extensions use that
            configure?.Invoke(Settings);
            Open();
        }

        public void Open() => Store = MainThread.Run(() => new DownloadStore(Settings, StoreFile)).Result;
        public T On<T>(Func<DownloadStore, T> f) => MainThread.Run(() => f(Store)).Result;
        public void On(Action<DownloadStore> f) => MainThread.Run(() => f(Store)).Wait();
        public DownloadItem Add(string url, DownloadKind kind = DownloadKind.file, string? name = null) =>
            On(s => s.Add(new NewDownloadRequest(new Uri(url)) { Kind = kind, FileName = name }));
        public Task Wait(DownloadItem item, DownloadState state, int seconds = 30) =>
            Setup.Until(() => item.State == state, seconds, $"{item.FileName} to become {state} (is {item.State}: {item.ErrorMessage})");
        public void Dispose() => On(s => s.Shutdown());
    }

    [Fact]
    public async Task Downloads_into_a_category_folder_and_names_the_file_from_the_server()
    {
        using var server = new TestServer(700_000);
        using var rig = new Rig();
        var item = rig.Add(server.Url("/named"));
        await rig.Wait(item, DownloadState.completed);
        Assert.Equal("report 2026.pdf", item.FileName);
        Assert.Equal(Path.Combine(rig.Settings.DownloadDirectory, "Documents", "report 2026.pdf"), item.FinalPath);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath!));
        Assert.False(File.Exists(item.PartPath));
        Assert.Equal(item.TotalBytes, item.ReceivedBytes);

        // The same file again gets a numbered name instead of overwriting.
        var again = rig.Add(server.Url("/named"));
        await rig.Wait(again, DownloadState.completed);
        Assert.Equal("report 2026 (2).pdf", again.FileName);
    }

    [Fact]
    public async Task Adds_an_extension_from_the_content_type_when_the_url_has_none()
    {
        using var server = new TestServer(100_000);
        using var rig = new Rig(s => s.OrganizeByCategory = false);
        var item = rig.Add(server.Url("/typed"));
        await rig.Wait(item, DownloadState.completed);
        Assert.Equal("typed.pdf", item.FileName);
        Assert.Equal(Path.Combine(rig.Settings.DownloadDirectory, "typed.pdf"), item.FinalPath);
    }

    [Fact]
    public async Task Pause_survives_an_app_restart_and_resume_fetches_only_the_rest()
    {
        // Slow enough (about a second) that the pause always lands mid-download; see DownloaderTests.Stop_keeps_…
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 60 };
        var rig = new Rig(s => s.SegmentCount = 4);
        var item = rig.Add(server.Url("/slow"));
        await Setup.Until(() => item.ReceivedBytes > 400_000, what: "progress");
        rig.On(s => s.Pause(item.Id));
        Assert.Equal(DownloadState.paused, item.State);
        await Task.Delay(300);
        var pausedAt = item.ReceivedBytes;
        Assert.InRange(pausedAt, 400_000, server.Data.Length - 1);
        await Task.Delay(300);
        Assert.Equal(pausedAt, item.ReceivedBytes);         // nothing keeps downloading behind a pause
        rig.Dispose();

        // "Restart": a new store reads downloads.json
        server.ChunkDelayMs = 0;
        var seen = server.Requests.Count;
        rig.Open();
        var restored = rig.On(s => s.Items.Single());
        Assert.Equal(DownloadState.paused, restored.State);
        Assert.Equal(pausedAt, restored.ReceivedBytes);
        Assert.Equal(4, restored.Segments.Count);
        rig.On(s => s.Resume(restored.Id));
        await rig.Wait(restored, DownloadState.completed);
        Assert.Equal(server.Data, File.ReadAllBytes(restored.FinalPath!));
        var ranges = server.Requests.Skip(seen).Select(q => q.Headers["Range"]).ToList();
        Assert.Equal(4, ranges.Count);
        Assert.DoesNotContain(ranges, r => r.StartsWith("bytes=0-"));
        rig.Dispose();
    }

    [Fact]
    public async Task An_app_that_was_killed_mid_download_comes_back_paused()
    {
        using var server = new TestServer(8 * 1024 * 1024) { ChunkDelayMs = 60 };
        var rig = new Rig(s => s.SegmentCount = 4);
        var item = rig.Add(server.Url("/slow"));
        await Setup.Until(() => item.ReceivedBytes > 200_000, what: "progress");
        await Task.Delay(700);                              // lets the debounced save run while it is still downloading
        Assert.Contains("\"State\":\"downloading\"", File.ReadAllText(rig.StoreFile));
        var copy = Path.Combine(rig.Dir, "killed.json");
        File.Copy(rig.StoreFile, copy);
        rig.Dispose();

        var settings = AppSettings.Load(Path.Combine(rig.Dir, "settings.json"));
        var store = await MainThread.Run(() => new DownloadStore(settings, copy));
        Assert.Equal(DownloadState.paused, store.Items.Single().State);
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task Cancel_removes_the_part_file_and_retry_starts_over()
    {
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 60 };
        using var rig = new Rig(s => s.SegmentCount = 4);
        var item = rig.Add(server.Url("/slow"));
        await Setup.Until(() => item.ReceivedBytes > 200_000, what: "progress");
        Assert.True(File.Exists(item.PartPath));
        rig.On(s => s.Cancel(item.Id));
        Assert.Equal(DownloadState.cancelled, item.State);
        Assert.Equal(0, item.ReceivedBytes);
        await Setup.Until(() => !File.Exists(item.PartPath), 5, "the part file to go away");

        server.ChunkDelayMs = 0;
        rig.On(s => s.Resume(item.Id));
        await rig.Wait(item, DownloadState.completed);
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath!));
    }

    [Fact]
    public async Task Runs_at_most_the_configured_number_of_downloads_at_once()
    {
        using var server = new TestServer(600_000) { ChunkDelayMs = 20 };
        using var rig = new Rig(s => { s.MaxConcurrent = 2; s.SegmentCount = 1; });
        var items = Enumerable.Range(0, 5).Select(i => rig.Add(server.Url("/slow?" + i))).ToList();
        var peak = 0;
        while (items.Any(i => i.State != DownloadState.completed))
        {
            peak = Math.Max(peak, rig.On(s => s.ActiveCount));
            Assert.DoesNotContain(items, i => i.State == DownloadState.failed);
            await Task.Delay(20);
        }
        Assert.Equal(2, peak);
        // Oldest first
        Assert.True(items[0].CompletedAt <= items[4].CompletedAt);
    }

    [Fact]
    public async Task A_failed_download_says_why_and_can_be_removed()
    {
        using var server = new TestServer(1000);
        using var rig = new Rig();
        var item = rig.Add(server.Url("/missing"));
        await rig.Wait(item, DownloadState.failed);
        Assert.Contains("404", item.ErrorMessage);
        rig.On(s => s.Remove(item.Id));
        Assert.Empty(rig.On(s => s.Items.ToList()));
    }

    [Fact]
    public void Queues_without_starting_when_auto_start_is_off()
    {
        using var rig = new Rig(s => s.AutoStart = false);
        var item = rig.Add("http://127.0.0.1:9/x.zip");
        Assert.Equal(DownloadState.queued, item.State);
        Assert.Equal(FileCategory.compressed, item.Category);
        Assert.Equal(2, rig.On(s => s.AddUrls("https://a.example/one.iso\nnot a url\nftp://x/y\n  https://b.example/two.mp3  ", DownloadKind.file)));
        Assert.Equal(3, rig.On(s => s.Items.Count));
    }

    // ---- Bridge ----

    private static HttpRequest Req(string method, string path, object? body = null, string? origin = "chrome-extension://abcdefghijklmnop", string host = "127.0.0.1:48931", string? ext = null)
    {
        var headers = new Dictionary<string, string> { ["host"] = host };
        if (origin != null) headers["origin"] = origin;
        if (ext != null) headers["x-mcdm-extension"] = ext;
        return new HttpRequest(method, path, headers, body == null ? [] : JsonSerializer.SerializeToUtf8Bytes(body));
    }

    [Fact]
    public void The_bridge_only_answers_browser_extensions()
    {
        using var rig = new Rig(s => s.AutoStart = false);
        var server = new ServerController(rig.Store, rig.Settings);
        try
        {
            int Status(HttpRequest r) => MainThread.Run(() => server.Handle(r)).Result.Status;
            Assert.Equal(200, Status(Req("GET", "/ping")));
            Assert.Equal(200, Status(Req("GET", "/ping", origin: null, ext: "chrome")));     // Chrome omits Origin on GETs
            Assert.Equal(403, Status(Req("GET", "/ping", origin: null)));                    // curl, other local programs
            Assert.Equal(403, Status(Req("GET", "/ping", origin: "https://evil.example")));
            Assert.Equal(403, Status(Req("GET", "/ping", origin: "https://evil.example", ext: "chrome")));
            Assert.Equal(403, Status(Req("GET", "/ping", origin: "null", ext: "chrome")));
            Assert.Equal(403, Status(Req("POST", "/download", new { url = "https://a.example/f.zip" }, origin: "http://127.0.0.1:48213")));
            Assert.Equal(403, Status(Req("GET", "/ping", host: "attacker.example:48213")));  // DNS rebinding
            Assert.Equal(404, Status(Req("GET", "/nope")));
            Assert.Empty(rig.Store.Items);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void The_bridge_adds_and_controls_downloads()
    {
        using var rig = new Rig(s => s.AutoStart = false);
        var server = new ServerController(rig.Store, rig.Settings);
        try
        {
            JsonElement Call(HttpRequest r) => JsonDocument.Parse(MainThread.Run(() => server.Handle(r)).Result.Json.ToJsonString()).RootElement;

            var ping = Call(Req("GET", "/ping?browser=edge", ext: "edge"));
            Assert.True(ping.GetProperty("ok").GetBoolean());
            Assert.Equal(AppInfo.Version, ping.GetProperty("version").GetString());
            Assert.Contains("zip", ping.GetProperty("captureExtensions").EnumerateArray().Select(e => e.GetString()));
            Assert.True(server.IsExtensionConnected("edge"));
            Assert.False(server.IsExtensionConnected("brave"));

            // Opera runs the Chrome extension, which calls itself "chrome"; the User-Agent tells the two apart.
            var opera = Req("GET", "/ping", origin: null, ext: "chrome");
            opera.Headers["user-agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 OPR/136.0.0.0";
            Call(opera);
            Assert.True(server.IsExtensionConnected("opera"));
            Assert.False(server.IsExtensionConnected("chrome"));

            var added = Call(Req("POST", "/download", new
            {
                url = "https://files.example/İndirilen Dosya.ZIP", filename = "evrak:2026?.zip", referer = "https://files.example/page",
                userAgent = "UA/1.0", cookieHeader = "a=1; b=2", kind = "file", segments = 99,
            }));
            Assert.True(added.GetProperty("ok").GetBoolean());
            var item = rig.Store.Items.Single();
            Assert.Equal(Guid.Parse(added.GetProperty("id").GetString()!), item.Id);
            Assert.Equal("evrak_2026_.zip", item.FileName);
            Assert.Equal("https://files.example/page", item.Headers["Referer"]);
            Assert.Equal("UA/1.0", item.Headers["user-agent"]);
            Assert.Equal("a=1; b=2", item.Headers["Cookie"]);
            Assert.Equal(16, item.RequestedSegments);

            Assert.Equal(400, MainThread.Run(() => server.Handle(Req("POST", "/download", new { url = "file:///C:/Windows/win.ini" }))).Result.Status);
            Assert.Equal(400, MainThread.Run(() => server.Handle(Req("POST", "/download", new { url = "javascript:alert(1)" }))).Result.Status);
            Assert.Single(rig.Store.Items);

            var list = Call(Req("GET", "/downloads", ext: "chrome")).GetProperty("items");
            Assert.Equal("queued", list[0].GetProperty("state").GetString());
            Assert.Equal("evrak_2026_.zip", list[0].GetProperty("name").GetString());

            Call(Req("POST", "/action", new { id = item.Id.ToString(), action = "pause" }));
            Assert.Equal(DownloadState.paused, item.State);
            Call(Req("POST", "/action", new { id = item.Id.ToString(), action = "remove" }));
            Assert.Empty(rig.Store.Items);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void Video_requests_keep_the_browser_cookies_in_a_netscape_file()
    {
        using var rig = new Rig(s => s.AutoStart = false);
        var server = new ServerController(rig.Store, rig.Settings);
        try
        {
            MainThread.Run(() => server.Handle(Req("POST", "/download", new
            {
                url = "https://www.youtube.com/watch?v=abc", kind = "video",
                cookies = new object[]
                {
                    new { name = "SID", value = "xyz", domain = ".youtube.com", path = "/", secure = true, expirationDate = 1893456000.5 },
                    new { name = "PREF", value = "tz=Europe.Istanbul", domain = "www.youtube.com", path = "/", secure = false, expirationDate = 0 },
                    new { name = "bad", value = "line\nbreak", domain = ".youtube.com", path = "/" },
                },
            }))).Wait();
            var item = rig.Store.Items.Single();
            Assert.Equal(DownloadKind.video, item.Kind);
            Assert.Equal(FileCategory.video, item.Category);
            var lines = File.ReadAllLines(item.CookieFile!);
            Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
            Assert.Equal(".youtube.com\tTRUE\t/\tTRUE\t1893456000\tSID\txyz", lines[1]);
            Assert.Equal("www.youtube.com\tFALSE\t/\tFALSE\t0\tPREF\ttz=Europe.Istanbul", lines[2]);
            Assert.Equal(3, lines.Length);
            var cookieFile = item.CookieFile!;
            MainThread.Run(() => rig.Store.Remove(item.Id)).Wait();
            Assert.False(File.Exists(cookieFile));
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task The_bridge_speaks_http_on_loopback_and_downloads_end_to_end()
    {
        using var origin = new TestServer(500_000);
        using var rig = new Rig(s => s.ServerPort = 48931);
        var server = new ServerController(rig.Store, rig.Settings);
        try
        {
            Assert.True(server.Running, server.StatusText);
            using var http = new HttpClient();

            var bare = await http.GetAsync("http://127.0.0.1:48931/ping");
            Assert.Equal(403, (int)bare.StatusCode);
            Assert.False(bare.Headers.Contains("Access-Control-Allow-Origin"));

            var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:48931/download")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { url = origin.Url("/file"), filename = "paket.zip", kind = "file" }), Encoding.UTF8),
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            req.Headers.Add("X-MCDM-Extension", "chrome");
            var resp = await http.SendAsync(req);
            Assert.Equal(200, (int)resp.StatusCode);
            Assert.Equal("chrome-extension://abcdefghijklmnop", resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.True(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean());

            var item = rig.On(s => s.Items.Single());
            await rig.Wait(item, DownloadState.completed);
            Assert.Equal(Path.Combine(rig.Settings.DownloadDirectory, "Archives", "paket.zip"), item.FinalPath);
            Assert.Equal(origin.Data, File.ReadAllBytes(item.FinalPath!));

            // A second instance on the same port reports the conflict instead of crashing.
            var clash = new ServerController(rig.Store, rig.Settings);
            Assert.False(clash.Running);
            Assert.Contains("48931", clash.StatusText);
        }
        finally { server.Stop(); }
    }
}
