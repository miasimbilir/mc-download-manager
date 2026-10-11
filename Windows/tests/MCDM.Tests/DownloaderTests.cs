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
        Assert.Equal(8, r.Segments.Length);
        Assert.All(r.Segments, s => Assert.True(s.IsDone));
        Assert.Equal(server.Data.Length, r.Received);
        AssertSame(server.Data, r.Path);
        // probe + one request per segment
        Assert.Equal(9, server.Requests.Count(q => q.Headers.ContainsKey("Range")));
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
        Assert.True(r.Probe!.SupportsRanges);
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
