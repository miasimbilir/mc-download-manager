using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MCDM.Tests;

/// A deliberately awkward origin server: ranges, no ranges, rate limits, dropped connections, redirects.
public sealed class TestServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cts = new();
    public int Port { get; }
    public byte[] Data { get; }

    public ConcurrentQueue<(string Path, Dictionary<string, string> Headers)> Requests { get; } = new();
    public int MaxConcurrent;           // /throttle: answer 429 above this many open transfers
    public int DropFirst;               // /flaky: cut this many range responses short
    public int ChunkDelayMs;            // /slow: pause between chunks
    private int concurrent;
    public int PeakConcurrent;
    public int Throttled;

    public TestServer(int size = 6 * 1024 * 1024)
    {
        Data = new byte[size];
        new Random(42).NextBytes(Data);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    public string Url(string path, string host = "127.0.0.1") => $"http://{host}:{Port}{path}";

    private async Task Accept()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    var head = await ReadHead(stream);
                    if (head == null) return;
                    var lines = head.Split("\r\n");
                    var path = lines[0].Split(' ')[1];
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in lines.Skip(1))
                    {
                        var i = l.IndexOf(':');
                        if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
                    }
                    Requests.Enqueue((path, headers));
                    if (!await Respond(stream, path, headers)) return;
                }
            }
            catch { }
        }
    }

    private static async Task<string?> ReadHead(NetworkStream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one) == 0) return null;
            buf.Add(one[0]);
            var n = buf.Count;
            if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10)
                return Encoding.ASCII.GetString(buf.ToArray(), 0, n - 4);
        }
    }

    private static Task Head(NetworkStream s, string status, params string[] headers) =>
        s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n" + string.Concat(headers.Select(h => h + "\r\n")) + "\r\n")).AsTask();

    private static (long, long)? Range(Dictionary<string, string> headers, long total)
    {
        if (!headers.TryGetValue("Range", out var r) || !r.StartsWith("bytes=")) return null;
        var parts = r[6..].Split('-');
        var start = long.Parse(parts[0]);
        var end = parts[1].Length > 0 ? Math.Min(long.Parse(parts[1]), total - 1) : total - 1;
        return (start, end);
    }

    /// Returns false when the connection must be closed.
    private async Task<bool> Respond(NetworkStream s, string path, Dictionary<string, string> headers)
    {
        var route = path.Split('?')[0];
        var total = Data.LongLength;
        var range = Range(headers, total);

        switch (route)
        {
            case "/redirect-cross":
                // 127.0.0.1 -> localhost is a different origin for the credential rule
                await Head(s, "302 Found", $"Location: http://localhost:{Port}/file", "Content-Length: 0");
                return true;
            case "/redirect-same":
                await Head(s, "302 Found", "Location: /file", "Content-Length: 0");
                return true;
            case "/missing":
                await Head(s, "404 Not Found", "Content-Length: 0");
                return true;
            case "/norange":
                range = null;
                break;
            case "/no-range-requests":
                // Anti download-manager check: any Range request is refused, the plain GET a browser sends is served.
                if (range != null) { await Head(s, "403 Forbidden", "Content-Length: 0"); return true; }
                break;
            case "/no-parallel":
                // Answers the probe, refuses the parallel segment requests.
                if (range is not null and not (0, 0)) { await Head(s, "403 Forbidden", "Content-Length: 0"); return true; }
                break;
            case "/late-refusal":
                // Serves the probe and the first segment, then refuses the rest (a link that expires mid-download).
                if (range is (0, 0) || range is (0, _)) break;
                if (range != null) { await Task.Delay(400); await Head(s, "403 Forbidden", "Content-Length: 0"); return true; }
                break;
            case "/forbidden":
                await Head(s, "403 Forbidden", "Content-Length: 0");
                return true;
            case "/lie":
                // Advertises ranges on the probe, then ignores them
                if (range is (0, 0)) break;
                range = null;
                break;
        }

        if (route == "/throttle" && range is not (0, 0))
        {
            if (Interlocked.Increment(ref concurrent) > MaxConcurrent)
            {
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref Throttled);
                await Head(s, "429 Too Many Requests", "Content-Length: 0", "Connection: close");
                return false;
            }
            InterlockedMax(ref PeakConcurrent, concurrent);
        }

        try
        {
            var extra = new List<string>();
            if (route is "/named" or "/no-range-requests") extra.Add("Content-Disposition: attachment; filename=\"report 2026.pdf\"");
            if (route == "/typed") extra.Add("Content-Type: application/pdf");
            else extra.Add("Content-Type: application/octet-stream");

            long start = 0, end = total - 1;
            if (range is { } r)
            {
                (start, end) = r;
                await Head(s, "206 Partial Content", [$"Content-Range: bytes {start}-{end}/{total}", $"Content-Length: {end - start + 1}", "Accept-Ranges: bytes", .. extra]);
            }
            else
            {
                await Head(s, "200 OK", [$"Content-Length: {total}", .. extra]);
            }

            var length = end - start + 1;
            var cut = route == "/flaky" && length > 1 && Interlocked.Decrement(ref DropFirst) >= 0 ? length / 2 : -1;
            const int chunk = 64 * 1024;
            for (long sent = 0; sent < length;)
            {
                if (cut >= 0 && sent >= cut) return false;       // drop the connection mid-body
                var n = (int)Math.Min(chunk, length - sent);
                await s.WriteAsync(Data.AsMemory((int)(start + sent), n));
                sent += n;
                if (ChunkDelayMs > 0 && (route == "/slow" || route == "/throttle")) await Task.Delay(ChunkDelayMs);
            }
            return true;
        }
        finally
        {
            if (route == "/throttle" && range is not (0, 0)) Interlocked.Decrement(ref concurrent);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) break;
    }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
    }
}
