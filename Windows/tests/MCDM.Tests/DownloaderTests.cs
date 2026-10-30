using System.Security.Cryptography;
using Xunit;

namespace MCDM.Tests;

public class DownloaderTests
{
    private sealed record Outcome(Exception? Error, ProbeResult? Probe, long Received, SegmentInfo[] Segments, string Path);

    private static async Task<Outcome> Download(string url, int segments = 8, Dictionary<string, string>? headers = null)
    {
        var part = Path.Combine(Setup.TempDir(), "file.part");
        var done = new TaskCompletionSource<Exception?>();
        ProbeResult? probe = null;
        long received = 0;
        SegmentInfo[] segs = [];
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(url), PartPath = part, SegmentCount = segments, MinSegmentSize = 64 * 1024,
            Headers = headers ?? new(),
        })
        {
            OnProbe = p => probe = p,
            OnProgress = (r, s) => { received = r; segs = s; },
            OnFinish = e => done.TrySetResult(e),
        };
        dl.Start();
        var error = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        return new Outcome(error, probe, received, segs, part);
    }

    private static void AssertSame(byte[] expected, string path) =>
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    [Fact]
    public async Task Splits_a_file_into_parallel_range_requests()
    {
        using var server = new TestServer();
        var r = await Download(server.Url("/file"));
        Assert.Null(r.Error);
        Assert.True(r.Probe!.SupportsRanges);
        Assert.Equal(server.Data.Length, r.Probe.TotalBytes);
        Assert.True(r.Segments.Length >= 8);        // the eight parts, and the splits of them free connections took over
        Assert.All(r.Segments, s => Assert.True(s.IsDone));
        Assert.Equal(server.Data.Length, r.Segments.Sum(s => s.Length));
        Assert.Equal(server.Data.Length, r.Received);
        AssertSame(server.Data, r.Path);
        // probe + one request per part
        Assert.Equal(1 + r.Segments.Length, server.Requests.Count(q => q.Headers.ContainsKey("Range")));
    }

    [Fact]
    public async Task Falls_back_to_one_stream_when_the_server_has_no_range_support()
    {
        using var server = new TestServer(900_000);
        var r = await Download(server.Url("/norange"));
        Assert.Null(r.Error);
        Assert.False(r.Probe!.SupportsRanges);
        Assert.Empty(r.Segments);
        Assert.Equal(server.Data.Length, r.Received);
        AssertSame(server.Data, r.Path);
    }

    [Fact]
    public async Task Restarts_as_one_stream_when_range_requests_get_a_200()
    {
        using var server = new TestServer(900_000);
        var r = await Download(server.Url("/lie"));
        Assert.Null(r.Error);
        // The probe said "ranges"; the single stream reports again, so the item does not keep that claim.
        Assert.False(r.Probe!.SupportsRanges);
        AssertSame(server.Data, r.Path);
    }

    [Fact]
    public async Task Lowers_the_connection_count_when_the_server_answers_429()
    {
        using var server = new TestServer(3 * 1024 * 1024) { MaxConcurrent = 2, ChunkDelayMs = 4 };
        var r = await Download(server.Url("/throttle"));
        Assert.Null(r.Error);
        AssertSame(server.Data, r.Path);
        Assert.True(server.Throttled > 0, "the server should have rejected some connections");
        Assert.True(server.PeakConcurrent <= 2);
    }

    [Fact]
    public async Task Resumes_segments_whose_connection_was_cut()
    {
        using var server = new TestServer(2 * 1024 * 1024) { DropFirst = 5 };
        var r = await Download(server.Url("/flaky"));
        Assert.Null(r.Error);
        AssertSame(server.Data, r.Path);
        // The retries continue from where each segment stopped instead of starting over.
        var starts = server.Requests.Where(q => q.Headers.ContainsKey("Range")).Select(q => q.Headers["Range"]).ToList();
        Assert.True(starts.Count > 9);
        Assert.True(starts.Distinct().Count() == starts.Count, "no range should be requested twice");
    }

    [Fact]
    public async Task Reports_http_errors()
    {
        using var server = new TestServer(1000);
        var r = await Download(server.Url("/missing"));
        var e = Assert.IsType<HttpStatusError>(r.Error);
        Assert.Equal(404, e.Status);
    }

    [Fact]
    public async Task Fails_cleanly_when_nothing_listens()
    {
        var r = await Download("http://127.0.0.1:9/file");
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Keeps_credentials_on_the_original_host_only()
    {
        var headers = new Dictionary<string, string> { ["Cookie"] = "session=secret", ["Authorization"] = "Bearer x", ["Referer"] = "https://example.com/page" };

        using (var server = new TestServer(300_000))
        {
            var r = await Download(server.Url("/redirect-cross"), 4, headers);
            Assert.Null(r.Error);
            AssertSame(server.Data, r.Path);
            var redirected = server.Requests.Where(q => q.Path == "/file").ToList();
            Assert.NotEmpty(redirected);
            Assert.All(redirected, q =>
            {
                Assert.False(q.Headers.ContainsKey("Cookie"), "Cookie leaked to the redirect target");
                Assert.False(q.Headers.ContainsKey("Authorization"), "Authorization leaked to the redirect target");
                Assert.Equal("https://example.com/page", q.Headers["Referer"]);
            });
            Assert.All(server.Requests.Where(q => q.Path == "/redirect-cross"), q => Assert.Equal("session=secret", q.Headers["Cookie"]));
        }

        using (var server = new TestServer(300_000))
        {
            var r = await Download(server.Url("/redirect-same"), 4, headers);
            Assert.Null(r.Error);
            Assert.All(server.Requests.Where(q => q.Path == "/file"), q => Assert.Equal("session=secret", q.Headers["Cookie"]));
        }
    }

    [Fact]
    public async Task Stop_keeps_the_part_file_and_reports_the_segment_state()
    {
        // 4 segments of 16 chunks each: at 15 ms a chunk the whole file is through in about 250 ms, barely more
        // than the 200 ms between progress reports, and now and then it finished before Stop() landed (seen on
        // Windows). 60 ms keeps it running for about a second.
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 60 };
        var part = Path.Combine(Setup.TempDir(), "file.part");
        long received = 0;
        SegmentInfo[] segs = [];
        var finished = false;
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration { Url = new Uri(server.Url("/slow")), PartPath = part, SegmentCount = 4, MinSegmentSize = 64 * 1024 })
        {
            OnProgress = (r, s) => { received = r; segs = s; },
            OnFinish = _ => finished = true,
        };
        dl.Start();
        await Setup.Until(() => Volatile.Read(ref received) > 300_000, what: "some progress");
        dl.Stop();
        await Task.Delay(400);
        Assert.False(finished);
        Assert.True(File.Exists(part));
        Assert.Equal(4, segs.Length);
        Assert.InRange(received, 300_000, server.Data.Length - 1);
        // Everything reported as received is really on disk.
        var bytes = File.ReadAllBytes(part);
        foreach (var s in segs)
            Assert.True(bytes.AsSpan((int)s.Start, (int)s.Received).SequenceEqual(server.Data.AsSpan((int)s.Start, (int)s.Received)));

        // A second downloader picks up from that state and only fetches the rest.
        server.ChunkDelayMs = 0;
        var before = server.Requests.Count;
        var done = new TaskCompletionSource<Exception?>();
        var resumed = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(server.Url("/slow")), PartPath = part, SegmentCount = 4, MinSegmentSize = 64 * 1024,
            ResumeSegments = [.. segs], ResumeTotal = server.Data.Length,
        }) { OnFinish = e => done.TrySetResult(e) };
        resumed.Start();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertSame(server.Data, part);
        var later = server.Requests.Skip(before).Select(q => q.Headers["Range"]).ToList();
        Assert.DoesNotContain("bytes=0-0", later);
        Assert.DoesNotContain(later, r => r.StartsWith("bytes=0-"));
    }

    // ---- Dynamic segmentation, resume validation, connection memory ----

    [Fact]
    public async Task A_free_connection_takes_over_half_of_the_slowest_part()
    {
        // Two parts; the one from the start of the file comes slowly (16 chunks × 80 ms ≈ 1.3 s on its own).
        using var server = new TestServer(2 * 1024 * 1024) { ChunkDelayMs = 80 };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await Download(server.Url("/slow-start"), segments: 2);
        Assert.Null(r.Error);
        AssertSame(server.Data, r.Path);
        Assert.True(r.Segments.Length > 2, $"expected splits, got {r.Segments.Length} parts");
        Assert.Equal(server.Data.Length, r.Segments.Sum(s => s.Length));
        Assert.Equal(server.Data.Length, r.Received);
        // The parts tile the file: sorted by start, each begins where the previous one ends.
        var sorted = r.Segments.OrderBy(s => s.Start).ToList();
        for (int i = 1; i < sorted.Count; i++) Assert.Equal(sorted[i - 1].End + 1, sorted[i].Start);
        // A split part was requested from inside the slow first half.
        Assert.Contains(server.Requests, q => q.Headers.TryGetValue("Range", out var range)
            && long.Parse(range[6..].Split('-')[0]) is > 0 and < 1024 * 1024);
        Assert.True(watch.ElapsedMilliseconds < 1200, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task One_connection_stays_one_connection()
    {
        using var server = new TestServer(1024 * 1024);
        var r = await Download(server.Url("/file"), segments: 1);
        Assert.Null(r.Error);
        AssertSame(server.Data, r.Path);
        Assert.True(server.Requests.Count <= 2);
    }

    /// Starts a download, stops it part-way and returns the state a later resume starts from.
    private static async Task<(string Part, SegmentInfo[] Segments, ProbeResult Probe)> Interrupted(TestServer server, string path)
    {
        var part = Path.Combine(Setup.TempDir(), "file.part");
        long received = 0;
        SegmentInfo[] segs = [];
        ProbeResult? probe = null;
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration { Url = new Uri(server.Url(path)), PartPath = part, SegmentCount = 4, MinSegmentSize = 64 * 1024 })
        {
            OnProbe = p => probe = p,
            OnProgress = (r, s) => { received = r; segs = s; },
        };
        dl.Start();
        await Setup.Until(() => Volatile.Read(ref received) > 300_000, what: "some progress");
        dl.Stop();
        await Task.Delay(300);
        return (part, segs, probe!);
    }

    private static async Task<(Exception? Error, int Restarts)> Resume(TestServer server, string path, string part, SegmentInfo[] segs, ProbeResult probe, long total)
    {
        var done = new TaskCompletionSource<Exception?>();
        var restarts = 0;
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(server.Url(path)), PartPath = part, SegmentCount = 4, MinSegmentSize = 64 * 1024,
            ResumeSegments = [.. segs], ResumeTotal = total, ResumeETag = probe.ETag, ResumeLastModified = probe.LastModified,
        })
        {
            OnRestart = () => Interlocked.Increment(ref restarts),
            OnFinish = e => done.TrySetResult(e),
        };
        dl.Start();
        var error = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (error, restarts);
    }

    [Fact]
    public async Task A_resume_asks_for_the_same_file_and_continues_when_it_is()
    {
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 60 };
        var (part, segs, probe) = await Interrupted(server, "/etag");
        Assert.Equal("\"v1\"", probe.ETag);
        Assert.NotNull(probe.LastModified);
        server.ChunkDelayMs = 0;
        var before = server.Requests.Count;
        var (error, restarts) = await Resume(server, "/etag", part, segs, probe, server.Data.Length);
        Assert.Null(error);
        Assert.Equal(0, restarts);
        AssertSame(server.Data, part);
        var later = server.Requests.Skip(before).ToList();
        Assert.All(later, q => Assert.Equal("\"v1\"", q.Headers["If-Range"]));
        Assert.DoesNotContain(later, q => q.Headers["Range"].StartsWith("bytes=0-"));
    }

    [Fact]
    public async Task A_resume_starts_over_when_the_file_on_the_server_has_changed()
    {
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 60 };
        var (part, segs, probe) = await Interrupted(server, "/etag");
        // Same size, other content: only the ETag tells.
        var replaced = new byte[server.Data.Length];
        new Random(7).NextBytes(replaced);
        server.Data = replaced;
        server.Version = 2;
        server.ChunkDelayMs = 0;
        var (error, restarts) = await Resume(server, "/etag", part, segs, probe, replaced.Length);
        Assert.Null(error);
        Assert.Equal(1, restarts);
        AssertSame(replaced, part);
    }

    [Fact]
    public async Task A_resume_starts_over_when_the_file_has_another_size()
    {
        using var server = new TestServer(4 * 1024 * 1024) { ChunkDelayMs = 60 };
        var (part, segs, _) = await Interrupted(server, "/slow");
        var oldLength = server.Data.Length;
        var replaced = new byte[3 * 1024 * 1024];
        new Random(9).NextBytes(replaced);
        server.Data = replaced;
        server.ChunkDelayMs = 0;
        // No validators (a server without ETag / Last-Modified): the size in Content-Range still gives it away.
        var (error, restarts) = await Resume(server, "/slow", part, segs, new ProbeResult(oldLength, true, null, null, null), oldLength);
        Assert.Null(error);
        Assert.Equal(1, restarts);
        AssertSame(replaced, part);
    }

    [Fact]
    public async Task Reports_how_many_connections_a_throttling_server_accepted()
    {
        using var server = new TestServer(3 * 1024 * 1024) { MaxConcurrent = 2, ChunkDelayMs = 4 };
        int? accepted = null;
        var done = new TaskCompletionSource<Exception?>();
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(server.Url("/throttle")), PartPath = Path.Combine(Setup.TempDir(), "file.part"), SegmentCount = 8, MinSegmentSize = 64 * 1024,
        })
        {
            OnConnectionLimit = n => accepted = n,
            OnFinish = e => done.TrySetResult(e),
        };
        dl.Start();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.NotNull(accepted);
        Assert.InRange(accepted!.Value, 1, 2);
    }

    [Fact]
    public async Task Reports_one_connection_for_a_server_that_refuses_parallel_requests()
    {
        using var server = new TestServer(900_000);
        int? accepted = null;
        var done = new TaskCompletionSource<Exception?>();
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(server.Url("/no-parallel")), PartPath = Path.Combine(Setup.TempDir(), "file.part"), SegmentCount = 4, MinSegmentSize = 64 * 1024,
        })
        {
            OnConnectionLimit = n => accepted = n,
            OnFinish = e => done.TrySetResult(e),
        };
        dl.Start();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, accepted);
    }

    [Fact]
    public async Task No_connection_count_is_reported_when_the_server_took_them_all()
    {
        using var server = new TestServer(1024 * 1024);
        int? accepted = null;
        var done = new TaskCompletionSource<Exception?>();
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(server.Url("/file")), PartPath = Path.Combine(Setup.TempDir(), "file.part"), SegmentCount = 4, MinSegmentSize = 64 * 1024,
        })
        {
            OnConnectionLimit = n => accepted = n,
            OnFinish = e => done.TrySetResult(e),
        };
        dl.Start();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(accepted);
    }

    // ---- The probe behind the question ----

    [Fact]
    public async Task The_question_asks_with_HEAD_and_spends_nothing()
    {
        using var server = new TestServer(700_000);
        var p = await SegmentedDownloader.ProbeOnceAsync(new Uri(server.Url("/named")), new(), CancellationToken.None);
        Assert.Equal(700_000, p.TotalBytes);
        Assert.True(p.SupportsRanges);
        Assert.Equal("report 2026.pdf", p.SuggestedFileName);
        Assert.Equal("HEAD", Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task The_question_knows_the_size_of_a_file_whose_server_refuses_ranges()
    {
        using var server = new TestServer(700_000);
        var p = await SegmentedDownloader.ProbeOnceAsync(new Uri(server.Url("/no-range-requests")), new(), CancellationToken.None);
        Assert.Equal(700_000, p.TotalBytes);
        Assert.False(p.SupportsRanges);
    }

    [Fact]
    public async Task The_question_falls_back_to_a_range_probe_when_HEAD_is_refused()
    {
        using var server = new TestServer(700_000);
        var p = await SegmentedDownloader.ProbeOnceAsync(new Uri(server.Url("/head-refused")), new(), CancellationToken.None);
        Assert.Equal(700_000, p.TotalBytes);
        Assert.Equal(["HEAD", "GET"], server.Requests.Select(q => q.Method).ToArray());
        Assert.Equal("bytes=0-0", server.Requests.Last().Headers["Range"]);
    }

    [Fact]
    public async Task The_question_hears_a_refusal()
    {
        using var server = new TestServer(1000);
        var e = await Assert.ThrowsAsync<HttpStatusError>(() => SegmentedDownloader.ProbeOnceAsync(new Uri(server.Url("/missing")), new(), CancellationToken.None));
        Assert.Equal(404, e.Status);
    }

    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=report.pdf", "report.pdf")]
    [InlineData("attachment; filename=\"fallback.zip\"; filename*=UTF-8''%C3%B6zet%20dosyas%C4%B1.zip", "özet dosyası.zip")]
    [InlineData("attachment; filename*=utf-8'tr'%C4%B0ndirme.rar", "İndirme.rar")]
    [InlineData("inline", null)]
    [InlineData(null, null)]
    public void Reads_file_names_from_Content_Disposition(string? header, string? expected) =>
        Assert.Equal(expected, SegmentedDownloader.FileNameFromContentDisposition(header));

    [Fact]
    public void Repairs_utf8_file_names_that_arrive_as_latin1()
    {
        // What .NET hands over when a server writes raw UTF-8 bytes into the header
        var raw = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("attachment; filename=\"dilekçe örneği.pdf\""));
        Assert.Equal("dilekçe örneği.pdf", SegmentedDownloader.FileNameFromContentDisposition(raw));
        // Genuine Latin-1 stays as it is
        Assert.Equal("café.pdf", SegmentedDownloader.FileNameFromContentDisposition("attachment; filename=\"café.pdf\""));
    }

    [Theory]
    [InlineData(1000, 4)]
    [InlineData(1003, 8)]
    [InlineData(7, 3)]
    public void Segments_cover_the_file_exactly(long total, int count)
    {
        var segs = SegmentedDownloader.MakeSegments(total, count);
        Assert.Equal(0, segs[0].Start);
        Assert.Equal(total - 1, segs[^1].End);
        for (int i = 1; i < segs.Length; i++) Assert.Equal(segs[i - 1].End + 1, segs[i].Start);
        Assert.Equal(total, segs.Sum(s => s.Length));
    }
}
