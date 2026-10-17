using System.Text.Json;
using Xunit;

namespace MCDM.Tests;

/// Servers that refuse download managers: one connection instead of many, then the browser as a last resort.
public class HandOffTests
{
    private sealed class HandOffUi(HandOffAnswer answer) : IStoreUi
    {
        public readonly List<(Guid Id, string Site)> Asked = [];
        public void ShowDetail(Guid id) { }
        public void DetailCompleted(Guid id, bool autoClose) { }
        public void NotifyCompleted(DownloadItem item) { }
        public Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync() => Task.FromResult((false, false));
        public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) => Task.FromResult<VideoFormatAnswer?>(VideoFormatAnswer.Default);
        public Task<HandOffAnswer> AskHandOffAsync(DownloadItem item, string site)
        {
            lock (Asked) Asked.Add((item.Id, site));
            return Task.FromResult(answer);
        }
    }

    private static async Task<(Exception? Error, ProbeResult? Probe, string Part)> Download(string url, string? part = null,
        List<SegmentInfo>? resume = null, long resumeTotal = -1)
    {
        part ??= Path.Combine(Setup.TempDir(), "file.part");
        var done = new TaskCompletionSource<Exception?>();
        ProbeResult? probe = null;
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(url), PartPath = part, SegmentCount = 8, MinSegmentSize = 64 * 1024,
            ResumeSegments = resume ?? [], ResumeTotal = resumeTotal,
        })
        {
            OnProbe = p => probe = p,
            OnFinish = e => done.TrySetResult(e),
        };
        dl.Start();
        return (await done.Task.WaitAsync(TimeSpan.FromSeconds(60)), probe, part);
    }

    [Fact]
    public async Task A_refused_range_probe_is_followed_by_the_plain_request_a_browser_sends()
    {
        using var server = new TestServer(900_000);
        var r = await Download(server.Url("/no-range-requests"));
        Assert.Null(r.Error);
        Assert.Equal(server.Data, File.ReadAllBytes(r.Part));
        Assert.NotNull(r.Probe);
        Assert.False(r.Probe!.SupportsRanges);
        Assert.Equal(900_000, r.Probe.TotalBytes);
        Assert.Equal("report 2026.pdf", r.Probe.SuggestedFileName);
    }

    [Fact]
    public async Task Refused_parallel_connections_continue_on_one()
    {
        using var server = new TestServer(2_000_000);
        var r = await Download(server.Url("/no-parallel"));
        Assert.Null(r.Error);
        Assert.Equal(server.Data, File.ReadAllBytes(r.Part));
        Assert.Contains(server.Requests, q => q.Path == "/no-parallel" && !q.Headers.ContainsKey("Range"));
    }

    [Fact]
    public async Task A_refusal_after_data_arrived_does_not_start_over()
    {
        using var server = new TestServer(2_000_000);
        var r = await Download(server.Url("/late-refusal"));
        // Starting over on one connection could replace what is on disk with a login page: stop with the status.
        Assert.Equal(403, Assert.IsType<HttpStatusError>(r.Error).Status);
        Assert.DoesNotContain(server.Requests, q => q.Path == "/late-refusal" && !q.Headers.ContainsKey("Range"));
    }

    [Fact]
    public async Task A_server_that_refuses_everything_still_fails_with_its_status()
    {
        using var server = new TestServer(1000);
        var r = await Download(server.Url("/forbidden"));
        Assert.Equal(403, Assert.IsType<HttpStatusError>(r.Error).Status);
        Assert.Equal(2, server.Requests.Count);         // the probe and one plain GET, no more
    }

    [Fact]
    public async Task A_refused_resume_keeps_the_part_file_for_a_later_attempt()
    {
        using var server = new TestServer(1000);
        var part = Path.Combine(Setup.TempDir(), "file.part");
        File.WriteAllBytes(part, new byte[600_000]);
        List<SegmentInfo> segs = [new(0, 299_999, 100_000), new(300_000, 599_999, 50_000)];
        var r = await Download(server.Url("/forbidden"), part, segs, 600_000);
        Assert.Equal(403, Assert.IsType<HttpStatusError>(r.Error).Status);
        Assert.Equal(600_000, new FileInfo(part).Length);
    }

    [Fact]
    public void Problems_on_this_computer_or_the_network_are_not_offered_to_the_browser()
    {
        Assert.True(DownloadStore.BrowserMightHelp(new HttpStatusError(403)));
        Assert.True(DownloadStore.BrowserMightHelp(new DownloadFailure("Too many redirects")));
        Assert.False(DownloadStore.BrowserMightHelp(new DownloadFailure("disk full", local: true)));
        Assert.False(DownloadStore.BrowserMightHelp(new HttpRequestException(HttpRequestError.NameResolutionError)));
        Assert.False(DownloadStore.BrowserMightHelp(new HttpRequestException(HttpRequestError.ConnectionError)));
        Assert.True(DownloadStore.BrowserMightHelp(new HttpRequestException(HttpRequestError.SecureConnectionError)));
        Assert.False(DownloadStore.BrowserMightHelp(new TimeoutException()));
        Assert.False(DownloadStore.BrowserMightHelp(new OperationCanceledException()));
    }

    [Fact]
    public async Task A_refused_download_is_handed_back_to_the_browser_it_came_from()
    {
        using var server = new TestServer(1000);
        var ui = new HandOffUi(new HandOffAnswer(HandOffDecision.Browser, Always: true));
        using var rig = new StoreTests.Rig(ui: ui);
        var opened = new List<(Uri Url, string? Browser)>();
        BrowserIntegration.OpenInBrowserHook = (u, b) => { lock (opened) opened.Add((u, b)); };
        try
        {
            var item = rig.On(s => s.Add(new NewDownloadRequest(new Uri(server.Url("/forbidden")))
            {
                Confirm = true, SourceBrowser = "edge", Referer = server.Url("/page", host: "localhost"),
            }));
            await Setup.Until(() => rig.On(s => s.Find(item.Id)) == null, what: "the download to be handed off");
            Assert.Equal(item.Id, Assert.Single(ui.Asked).Id);
            Assert.Equal("localhost", ui.Asked[0].Site);                   // the page it was started from
            // 403 on a signed link: the page is reopened so the site can issue a fresh one.
            var (url, browser) = Assert.Single(opened);
            Assert.Equal(server.Url("/page", host: "localhost"), url.AbsoluteUri);
            Assert.Equal("edge", browser);
            Assert.Equal(["localhost"], rig.Settings.BrowserOnlySites);    // "always" was ticked
            Assert.Equal(["127.0.0.1", "localhost"], rig.On(s => s.BypassHosts));
        }
        finally { BrowserIntegration.OpenInBrowserHook = null; }
    }

    [Fact]
    public async Task Closing_the_question_leaves_the_download_failed()
    {
        using var server = new TestServer(1000);
        var ui = new HandOffUi(HandOffAnswer.Close);
        using var rig = new StoreTests.Rig(ui: ui);
        var item = rig.Add(server.Url("/forbidden"));
        await rig.Wait(item, DownloadState.failed);
        await Setup.Until(() => { lock (ui.Asked) return ui.Asked.Count == 1; });
        Assert.Equal(403, item.LastHttpStatus);
        Assert.Empty(rig.Settings.BrowserOnlySites);
        Assert.Empty(rig.On(s => s.BypassHosts));
        rig.On(s => s.Resume(item.Id));
        Assert.Null(item.LastHttpStatus);
    }

    [Fact]
    public void The_bridge_keeps_the_site_list_and_reports_the_extension_version()
    {
        var bundled = Path.Combine(Setup.TempDir(), "ChromeExtension");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "manifest.json"), """{"name":"x","version":"1.4.1"}""");
        var before = Environment.GetEnvironmentVariable("MCDM_EXTENSION_DIR");
        Environment.SetEnvironmentVariable("MCDM_EXTENSION_DIR", bundled);
        using var rig = new StoreTests.Rig(s => s.AutoStart = false);
        var server = new ServerController(rig.Store, rig.Settings);
        try
        {
            JsonElement Call(HttpRequest r) => JsonDocument.Parse(MainThread.Run(() => server.Handle(r)).Result.Json.ToJsonString()).RootElement;
            int Status(HttpRequest r) => MainThread.Run(() => server.Handle(r)).Result.Status;
            HttpRequest Ping(string browser, string? version)
            {
                var r = StoreTests.Req("GET", "/ping", origin: null, ext: browser);
                r.Headers["x-mcdm-extension-id"] = "abcdefghijklmnopabcdefghijklmnop";
                if (version != null) r.Headers["x-mcdm-extension-version"] = version;
                return r;
            }

            var ping = Call(Ping("opera", "1.4.0"));
            Assert.Equal("1.4.1", ping.GetProperty("extensionVersion").GetString());
            Assert.Empty(ping.GetProperty("browserOnlySites").EnumerateArray());
            Assert.True(server.IsExtensionConnected("opera"));             // 1.4 extensions call Opera by its name
            Assert.True(server.IsExtensionOutdated("opera"));
            Assert.Equal("1.4.0", server.ExtensionVersion("opera"));
            Call(Ping("opera", "1.4.1"));                                  // it reloaded itself
            Assert.False(server.IsExtensionOutdated("opera"));
            Call(Ping("edge", null));                                      // 1.3.0 – 1.4.0 send no version
            Assert.True(server.IsExtensionOutdated("edge"));
            Assert.Equal("", server.ExtensionVersion("edge"));

            var set = Call(StoreTests.Req("POST", "/site", new { host = "WWW.WeTransfer.com", browserOnly = true }));
            Assert.Equal(["wetransfer.com"], set.GetProperty("browserOnlySites").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(["wetransfer.com"], rig.Settings.BrowserOnlySites);
            Assert.Equal(["wetransfer.com"], Call(Ping("chrome", "1.4.1")).GetProperty("browserOnlySites").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(400, Status(StoreTests.Req("POST", "/site", new { host = "javascript:alert(1)", browserOnly = true })));
            Assert.Equal(400, Status(StoreTests.Req("POST", "/site", new { host = "a.example", browserOnly = "yes" })));
            Assert.Equal(403, Status(StoreTests.Req("POST", "/site", new { host = "a.example", browserOnly = true }, origin: "https://evil.example")));
            Call(StoreTests.Req("POST", "/site", new { host = "wetransfer.com", browserOnly = false }));
            Assert.Empty(rig.Settings.BrowserOnlySites);

            // Downloads remember which browser sent them.
            Call(StoreTests.Req("POST", "/download", new { url = "https://files.example/a.zip" }, origin: "moz-extension://1234"));
            Assert.Equal("firefox", rig.Store.Items.Single().SourceBrowser);
        }
        finally
        {
            server.Stop();
            Environment.SetEnvironmentVariable("MCDM_EXTENSION_DIR", before);
        }
    }

    [Fact]
    public async Task An_extension_that_stays_outdated_is_reported_once()
    {
        var bundled = Path.Combine(Setup.TempDir(), "ChromeExtension");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "manifest.json"), """{"name":"x","version":"1.4.1"}""");
        var before = Environment.GetEnvironmentVariable("MCDM_EXTENSION_DIR");
        Environment.SetEnvironmentVariable("MCDM_EXTENSION_DIR", bundled);
        ServerController.OutdatedGrace = TimeSpan.FromMilliseconds(200);
        using var rig = new StoreTests.Rig(s => s.AutoStart = false);
        var server = new ServerController(rig.Store, rig.Settings);
        var told = new List<string>();
        server.ExtensionOutdated += b => { lock (told) told.Add(b); };
        try
        {
            HttpRequest Ping(string browser, string version)
            {
                var r = StoreTests.Req("GET", "/ping", origin: null, ext: browser);
                r.Headers["x-mcdm-extension-id"] = "abcdefghijklmnopabcdefghijklmnop";
                r.Headers["x-mcdm-extension-version"] = version;
                return r;
            }
            MainThread.Run(() => server.Handle(Ping("brave", "1.4.0"))).Wait();
            MainThread.Run(() => server.Handle(Ping("brave", "1.4.0"))).Wait();
            MainThread.Run(() => server.Handle(Ping("edge", "1.4.0"))).Wait();
            MainThread.Run(() => server.Handle(Ping("edge", "1.4.1"))).Wait();     // reloaded itself in time
            await Task.Delay(800);
            lock (told) Assert.Equal(["brave"], told);
        }
        finally
        {
            server.Stop();
            ServerController.OutdatedGrace = TimeSpan.FromSeconds(12);
            Environment.SetEnvironmentVariable("MCDM_EXTENSION_DIR", before);
        }
    }
}
