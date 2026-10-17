using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MCDM;

public sealed record ProbeResult(long TotalBytes, bool SupportsRanges, string? SuggestedFileName, string? ContentType, Uri? FinalUrl);

/// Downloads one file with N parallel HTTP Range requests into a single part file.
/// Shared state (segment progress, the retry queue) is guarded by `gate`; file writes are positional.
public sealed class SegmentedDownloader
{
    public sealed class Configuration
    {
        public required Uri Url { get; init; }
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public required string PartPath { get; init; }
        public int SegmentCount { get; init; } = 8;
        public long MinSegmentSize { get; init; } = 512 * 1024;
        public List<SegmentInfo> ResumeSegments { get; init; } = [];
        public long ResumeTotal { get; init; } = -1;
    }

    /// All three are raised on thread-pool threads.
    public Action<ProbeResult>? OnProbe;
    public Action<long, SegmentInfo[]>? OnProgress;
    /// null = success. Not raised after Stop().
    public Action<Exception?>? OnFinish;

    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    internal static double BackoffScale { get; set; } = 1;      // tests shorten the waits
    private const int MaxAttempts = 12;
    private const int MaxRedirects = 10;

    private readonly Configuration config;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stopCts = new();
    private readonly object gate = new();
    private Uri requestUrl;
    private SegmentInfo[] segments = [];
    private SafeFileHandle? file;
    private bool single;
    private long singleReceived;
    private volatile bool stopped;
    private long lastReport;

    public SegmentedDownloader(Configuration configuration)
    {
        config = configuration;
        requestUrl = configuration.Url;
        http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,              // redirects are followed by hand so credentials can be dropped
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
            ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // ---- Control ----

    public void Start() => Task.Run(RunAsync);

    /// Pause or cancel. The last segment state is reported through `OnProgress`.
    public void Stop()
    {
        stopped = true;
        try { stopCts.Cancel(); } catch { }
    }

    private async Task RunAsync()
    {
        Exception? error = null;
        try
        {
            var ct = stopCts.Token;
            var canResume = config.ResumeTotal > 0 && config.ResumeSegments.Count > 0 && File.Exists(config.PartPath);
            if (canResume)
            {
                segments = [.. config.ResumeSegments];
                OpenFile(create: false, preallocate: null);
                await RunMultiAsync(ct).ConfigureAwait(false);
            }
            else
            {
                ProbeResult? probe = null;
                try { probe = await ProbeAsync(ct).ConfigureAwait(false); }
                // Some servers (signed / one-shot links, anti download-manager checks) refuse a Range request
                // but serve the plain GET a browser would send: try that once before giving up.
                catch (HttpStatusError e) when (!IsThrottle(e.Status)) { }
                if (probe == null)
                {
                    await RunSingleAsync(ct, reportProbe: true).ConfigureAwait(false);
                }
                else
                {
                    OnProbe?.Invoke(probe);
                    var count = probe.SupportsRanges
                        ? (int)Math.Max(1, Math.Min(config.SegmentCount, probe.TotalBytes / config.MinSegmentSize))
                        : 1;
                    if (probe.SupportsRanges && count > 1)
                    {
                        segments = MakeSegments(probe.TotalBytes, count);
                        OpenFile(create: true, preallocate: probe.TotalBytes);
                        await RunMultiAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunSingleAsync(ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            CloseFile();
            http.Dispose();
        }

        var (received, snapshot) = Snapshot();
        OnProgress?.Invoke(received, snapshot);
        if (!stopped) OnFinish?.Invoke(error);
    }

    // ---- Helpers ----

    private (long, SegmentInfo[]) Snapshot()
    {
        lock (gate)
        {
            long received = single ? singleReceived : 0;
            if (!single) foreach (var s in segments) received += s.Received;
            return (received, (SegmentInfo[])segments.Clone());
        }
    }

    private void ReportProgress()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastReport);
        if (now - last < 200 || Interlocked.CompareExchange(ref lastReport, now, last) != last) return;
        var (received, snapshot) = Snapshot();
        OnProgress?.Invoke(received, snapshot);
    }

    private static readonly HashSet<string> credentialHeaders = new(StringComparer.OrdinalIgnoreCase) { "Cookie", "Authorization", "Proxy-Authorization" };

    /// Credentials (Cookie/Authorization) are only sent to the host the download was requested for,
    /// never to a host reached through a redirect.
    private bool SameOrigin(Uri url) =>
        string.Equals(url.Host, config.Url.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(url.Scheme, config.Url.Scheme, StringComparison.OrdinalIgnoreCase)
        && url.Port == config.Url.Port;

    private HttpRequestMessage MakeRequest(Uri url, string? range)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        var allowCredentials = SameOrigin(url);
        foreach (var (k, v) in config.Headers)
        {
            if (!allowCredentials && credentialHeaders.Contains(k)) continue;
            if (k.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) || k.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
            r.Headers.TryAddWithoutValidation(k, v);
        }
        r.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        r.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        if (range != null) r.Headers.TryAddWithoutValidation("Range", range);
        return r;
    }

    /// Sends a GET and follows redirects. Returns as soon as the response headers are in.
    private async Task<(HttpResponseMessage, Uri)> SendAsync(string? range, CancellationToken ct)
    {
        var url = requestUrl;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(IdleTimeout);
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(MakeRequest(url, range), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Loc.T("The server did not respond in time"));
            }
            var status = (int)resp.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } location)
            {
                resp.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                    throw new DownloadFailure(Loc.T("Invalid server response"));
                url = next;
                continue;
            }
            return (resp, url);
        }
        throw new DownloadFailure(Loc.T("Too many redirects"));
    }

    private static string? Header(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var v) || resp.Content.Headers.TryGetValues(name, out v))
            return v.FirstOrDefault();
        return null;
    }

    /// One request for the headers alone: size, the server's name for the file, whether ranges work.
    /// Used to show these before a download is confirmed; nothing is written anywhere.
    public static async Task<ProbeResult> ProbeOnceAsync(Uri url, Dictionary<string, string> headers, CancellationToken ct)
    {
        var dl = new SegmentedDownloader(new Configuration { Url = url, Headers = headers, PartPath = "" });
        try { return await dl.ProbeAsync(ct).ConfigureAwait(false); }
        finally { dl.http.Dispose(); }
    }

    private async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        var (resp, finalUrl) = await SendAsync("bytes=0-0", ct).ConfigureAwait(false);
        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (status is < 200 or > 299) throw new HttpStatusError(status);

            long total = -1;
            bool ranges = false;
            var contentRange = Header(resp, "Content-Range");
            if (status == 206 && contentRange != null && contentRange.LastIndexOf('/') is var slash and >= 0
                && long.TryParse(contentRange[(slash + 1)..].Trim(), out var t))
            {
                total = t;
                ranges = true;
            }
            else
            {
                if (resp.Content.Headers.ContentLength is { } n) total = n;
                var accept = Header(resp, "Accept-Ranges")?.ToLowerInvariant() ?? "";
                ranges = total > 0 && accept.Contains("bytes");
            }
            requestUrl = finalUrl;

            var name = FileNameFromContentDisposition(Header(resp, "Content-Disposition"));
            var ctype = Header(resp, "Content-Type")?.Split(';')[0].Trim();
            return new ProbeResult(total, ranges, name, ctype, finalUrl);
        }
    }

    public static SegmentInfo[] MakeSegments(long total, int count)
    {
        var size = total / count;
        var list = new SegmentInfo[count];
        for (int i = 0; i < count; i++)
        {
            var start = i * size;
            var end = i == count - 1 ? total - 1 : start + size - 1;
            list[i] = new SegmentInfo(start, end);
        }
        return list;
    }

    // ---- Part file ----

    private void OpenFile(bool create, long? preallocate)
    {
        try
        {
            var dir = Path.GetDirectoryName(config.PartPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (create && File.Exists(config.PartPath)) File.Delete(config.PartPath);
            // Delete sharing lets a cancelled download remove its part file while a connection is still unwinding.
            var handle = File.OpenHandle(config.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (preallocate is > 0)
            {
                // NTFS zero-fills everything before the first write at a high offset; a sparse file avoids that stall.
                TryMarkSparse(handle);
                RandomAccess.SetLength(handle, preallocate.Value);
            }
            lock (gate) file = handle;
        }
        catch (Exception e) when (e is not DownloadFailure)
        {
            throw new DownloadFailure(Loc.T("Could not create file: {0}", config.PartPath) + " — " + e.Message, local: true);
        }
    }

    private void CloseFile()
    {
        SafeFileHandle? h;
        lock (gate) { h = file; file = null; }
        try { h?.Dispose(); } catch { }
    }

    private void Write(byte[] buffer, int count, long offset)
    {
        var h = file ?? throw new OperationCanceledException();
        try
        {
            RandomAccess.Write(h, buffer.AsSpan(0, count), offset);
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DiskWriteException(e.Message, e);
        }
    }

    private sealed class DiskWriteException(string message, Exception inner) : Exception(message, inner);

    private static void TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const uint FSCTL_SET_SPARSE = 0x000900C4;
            DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    /// Reads a response body into the part file. `next` yields the file offset and how many bytes are still wanted.
    private async Task CopyAsync(HttpResponseMessage resp, Func<(long offset, long remaining)> next, Action<int> advance, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                var (offset, remaining) = next();
                if (remaining <= 0) return;
                idle.CancelAfter(IdleTimeout);
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(RateLimiter.ChunkSize(buffer.Length), remaining)), idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(Loc.T("The server did not respond in time"));
                }
                if (n == 0) return;
                Write(buffer, n, offset);
                advance(n);
                ReportProgress();
                if (RateLimiter.Limited)
                {
                    // Speed limit: the idle timer must not run while this connection waits for its turn.
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    await RateLimiter.WaitAsync(n, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- Multi-connection mode ----

    private async Task RunMultiAsync(CancellationToken stop)
    {
        using var multi = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var ct = multi.Token;
        var pending = new LinkedList<int>();        // segment indexes waiting to be started
        for (int i = 0; i < segments.Length; i++) if (!segments[i].IsDone) pending.AddLast(i);
        var retries = new int[segments.Length];
        var limit = Math.Max(1, config.SegmentCount);
        var activeLimit = limit;                    // open-connection cap; drops on 429/503
        var active = 0;
        var throttledUntil = DateTime.MinValue;
        Exception? fatal = null;
        var fallback = false;
        var wake = new SemaphoreSlim(0);
        var tasks = new List<Task>();

        void Fatal(Exception e) { fatal ??= e; multi.Cancel(); }

        // Server-side limiting (429/503): lower the connection count and put the segment back in front.
        void Throttle(int i, int status)
        {
            activeLimit = Math.Max(1, Math.Min(activeLimit, active - 1) - 1);
            var attempt = ++retries[i];
            if (attempt > MaxAttempts) { Fatal(new HttpStatusError(status)); return; }
            throttledUntil = DateTime.UtcNow.AddSeconds(Math.Min(15, 1.5 * attempt) * BackoffScale);
            if (!pending.Contains(i)) pending.AddFirst(i);
        }

        void Retry(int i, Exception? error)
        {
            var attempt = ++retries[i];
            if (attempt > MaxAttempts)
            {
                Fatal(error is null or OperationCanceledException ? new DownloadFailure(Loc.T("Segment {0} could not be completed", i + 1)) : error);
                return;
            }
            var until = DateTime.UtcNow.AddSeconds(Math.Min(10, attempt) * BackoffScale);
            if (until > throttledUntil) throttledUntil = until;
            if (!pending.Contains(i)) pending.AddLast(i);
        }

        async Task Segment(int i)
        {
            try
            {
                string range;
                lock (gate) range = $"bytes={segments[i].Start + segments[i].Received}-{segments[i].End}";
                var (resp, _) = await SendAsync(range, ct).ConfigureAwait(false);
                using (resp)
                {
                    var status = (int)resp.StatusCode;
                    if (status == 206)
                    {
                        lock (gate) retries[i] = 0;
                        await CopyAsync(resp,
                            () => { lock (gate) return (segments[i].Start + segments[i].Received, segments[i].Length - segments[i].Received); },
                            n => { lock (gate) segments[i].Received += n; },
                            ct).ConfigureAwait(false);
                        lock (gate)
                        {
                            if (segments[i].IsDone) activeLimit = Math.Min(limit, activeLimit + 1);
                            else Retry(i, null);
                        }
                    }
                    else if (status == 200)
                    {
                        // The server ignored Range: start over as a single stream.
                        lock (gate) { fallback = true; multi.Cancel(); }
                    }
                    else if (IsThrottle(status))
                    {
                        lock (gate) Throttle(i, status);
                    }
                    else
                    {
                        // 401/403/410…: the server answered the probe but refuses parallel connections — continue on one,
                        // the way a browser downloads.
                        lock (gate) { fallback = true; multi.Cancel(); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (DiskWriteException e)
            {
                lock (gate) Fatal(new DownloadFailure(e.Message, local: true));
            }
            catch (Exception e)
            {
                lock (gate) { if (!ct.IsCancellationRequested) Retry(i, e); }
            }
            finally
            {
                lock (gate) active--;
                wake.Release();
            }
        }

        while (true)
        {
            var wait = Timeout.InfiniteTimeSpan;
            lock (gate)
            {
                if (fatal != null || fallback || ct.IsCancellationRequested) break;
                var now = DateTime.UtcNow;
                if (now >= throttledUntil)
                {
                    while (active < activeLimit && pending.First is { } node)
                    {
                        pending.RemoveFirst();
                        var index = node.Value;
                        if (segments[index].IsDone) continue;
                        active++;
                        tasks.Add(Task.Run(() => Segment(index)));
                    }
                }
                else
                {
                    wait = throttledUntil - now + TimeSpan.FromMilliseconds(50);
                }
                if (active == 0 && pending.Count == 0) break;
            }
            try { await wake.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        stop.ThrowIfCancellationRequested();
        if (fatal != null) throw fatal;
        if (fallback)
        {
            // The part file and the segment state stay as they are until the single stream is actually served:
            // a refused resume (an expired link) can still be resumed later with fresh cookies.
            await RunSingleAsync(stop).ConfigureAwait(false);
            return;
        }
        lock (gate)
        {
            if (segments.Any(s => !s.IsDone)) throw new DownloadFailure(Loc.T("Segment {0} could not be completed", Array.FindIndex(segments, s => !s.IsDone) + 1));
        }
    }

    /// Statuses that mean "slow down / try again later" rather than "no".
    internal static bool IsThrottle(int status) => status is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    // ---- Single stream (no Range support) ----

    /// `reportProbe`: the Range probe was refused, so this plain GET also reports what a probe would have.
    private async Task RunSingleAsync(CancellationToken ct, bool reportProbe = false)
    {
        var (resp, finalUrl) = await SendAsync(null, ct).ConfigureAwait(false);
        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (status is < 200 or > 299) throw new HttpStatusError(status);
            // From here on the download starts over as one stream into a fresh part file.
            CloseFile();
            lock (gate) { segments = []; single = true; singleReceived = 0; }
            OpenFile(create: true, preallocate: null);
            // The real GET may differ from the probe (dynamic responses); trust its Content-Length.
            var total = resp.Content.Headers.ContentLength is > 0 and var n ? n : -1;
            if (reportProbe)
            {
                requestUrl = finalUrl;
                OnProbe?.Invoke(new ProbeResult(total, false, FileNameFromContentDisposition(Header(resp, "Content-Disposition")),
                    Header(resp, "Content-Type")?.Split(';')[0].Trim(), finalUrl));
            }
            try
            {
                await CopyAsync(resp,
                    () => { lock (gate) return (singleReceived, total > 0 ? total - singleReceived : long.MaxValue); },
                    n => { lock (gate) singleReceived += n; },
                    ct).ConfigureAwait(false);
            }
            catch (DiskWriteException e)
            {
                throw new DownloadFailure(e.Message, local: true);
            }
            long got;
            lock (gate) got = singleReceived;
            if (total > 0 && got < total) throw new DownloadFailure(Loc.T("Connection closed early ({0}/{1} bytes)", got, total));
        }
    }

    // ---- Content-Disposition ----

    public static string? FileNameFromContentDisposition(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        // RFC 5987: filename*=UTF-8''file%20name.zip
        var m = Regex.Match(header, @"filename\*\s*=\s*[^']*'[^']*'([^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            try
            {
                var decoded = Uri.UnescapeDataString(m.Groups[1].Value.Trim()).Trim('"');
                if (decoded.Length > 0) return decoded;
            }
            catch { }
        }
        m = Regex.Match(header, "filename\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success) return m.Groups[1].Value.Length > 0 ? RepairUtf8(m.Groups[1].Value) : null;
        m = Regex.Match(header, @"filename\s*=\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (m.Success)
        {
            var v = m.Groups[1].Value.Trim();
            return v.Length > 0 ? RepairUtf8(v) : null;
        }
        return null;
    }

    /// Headers are decoded as Latin-1, but many servers put raw UTF-8 into `filename="…"`.
    /// If the bytes form valid UTF-8, use that reading ("Ã¶zet.pdf" → "özet.pdf").
    private static string RepairUtf8(string s)
    {
        if (s.All(c => c < 0x80) || s.Any(c => c > 0xFF)) return s;
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return strict.GetString(Encoding.Latin1.GetBytes(s));
        }
        catch (ArgumentException)
        {
            return s;
        }
    }
}
