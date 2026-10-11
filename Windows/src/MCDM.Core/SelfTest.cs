using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MCDM;

/// `MCDownloadManager.exe --selftest [report.txt]` exercises the platform-dependent parts without the UI
/// and writes a report (default: selftest.txt in the app data folder). Exit code 0 = every check passed.
public static class SelfTest
{
    public static int Run(string? reportPath)
    {
        reportPath ??= Path.Combine(AppPaths.DataDir, "selftest.txt");
        var report = new StringBuilder();
        var failures = 0;

        void Line(string s) { report.AppendLine(s); Console.WriteLine(s); }

        void Check(string name, Func<string> body)
        {
            try { Line($"[ OK ] {name}: {body()}"); }
            catch (Exception e) { failures++; Line($"[FAIL] {name}: {e.GetType().Name}: {e.Message}"); }
        }

        void Info(string name, Func<string> body)
        {
            try { Line($"[info] {name}: {body()}"); }
            catch (Exception e) { Line($"[info] {name}: (unavailable: {e.Message})"); }
        }

        Line($"{AppInfo.Name} {AppInfo.Version} self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Info("OS", () => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        Info("Culture", () => $"{System.Globalization.CultureInfo.CurrentCulture.Name} / UI {System.Globalization.CultureInfo.CurrentUICulture.Name}, Turkish UI: {Loc.Turkish}");
        Info("App folder", () => AppPaths.AppDir);
        Info("Data folder", () => AppPaths.DataDir);
        Info("Download folder", () => AppPaths.DefaultDownloadDir);

        var work = Path.Combine(AppPaths.DataDir, "selftest-" + Guid.NewGuid().ToString("N")[..6]);

        Check("Data folder is writable", () =>
        {
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, "probe.txt"), "ok");
            return work;
        });

        using var origin = new LoopbackOrigin(5 * 1024 * 1024 + 123);

        Check("Segmented download (8 connections, loopback)", () =>
        {
            var part = Path.Combine(work, ".dm-test.part");
            var segments = Fetch(origin.Url, part, 8, out _);
            if (!SHA256.HashData(File.ReadAllBytes(part)).AsSpan().SequenceEqual(SHA256.HashData(origin.Data))) throw new Exception("content differs");
            return $"{origin.Data.Length} bytes in {segments} segments, content verified";
        });

        Check("Finished file: move, unique name, Mark of the Web", () =>
        {
            var dest = FileNames.UniqueDestination(Path.Combine(work, FileNames.Sanitize("test: dosya?.bin")));
            File.Move(Path.Combine(work, ".dm-test.part"), dest);
            Shell.MarkAsDownloaded(dest, "https://example.com/test.bin", null);
            var zone = OperatingSystem.IsWindows() && File.Exists(dest + ":Zone.Identifier") ? "Zone.Identifier written" : "no Zone.Identifier (not NTFS or not Windows)";
            return $"{Path.GetFileName(dest)}, {zone}";
        });

        Check("Pause and resume", () =>
        {
            var part = Path.Combine(work, ".dm-resume.part");
            origin.ChunkDelayMs = 20;
            long received = 0;
            SegmentInfo[] segs = [];
            var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration { Url = new Uri(origin.Url), PartPath = part, SegmentCount = 4 })
            {
                OnProgress = (r, s) => { received = r; segs = s; },
            };
            dl.Start();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Volatile.Read(ref received) < 500_000 && DateTime.UtcNow < deadline) Thread.Sleep(20);
            dl.Stop();
            Thread.Sleep(400);
            var pausedAt = received;
            if (pausedAt < 500_000 || pausedAt >= origin.Data.Length) throw new Exception($"unexpected pause point {pausedAt}");
            origin.ChunkDelayMs = 0;
            var done = new ManualResetEventSlim();
            Exception? error = null;
            var resumed = new SegmentedDownloader(new SegmentedDownloader.Configuration
            {
                Url = new Uri(origin.Url), PartPath = part, SegmentCount = 4, ResumeSegments = [.. segs], ResumeTotal = origin.Data.Length,
            }) { OnFinish = e => { error = e; done.Set(); } };
            resumed.Start();
            if (!done.Wait(30000)) throw new Exception("resume timed out");
            if (error != null) throw error;
            if (!File.ReadAllBytes(part).AsSpan().SequenceEqual(origin.Data)) throw new Exception("content differs after resume");
            return $"paused at {Fmt.Size(pausedAt)}, resumed to {Fmt.Size(origin.Data.Length)}, content verified";
        });

        Check("Cancel deletes a part file that is still open", () =>
        {
            var part = Path.Combine(work, ".dm-cancel.part");
            origin.ChunkDelayMs = 20;
            long received = 0;
            var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration { Url = new Uri(origin.Url), PartPath = part, SegmentCount = 4 })
            {
                OnProgress = (r, _) => received = r,
            };
            dl.Start();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Volatile.Read(ref received) < 200_000 && DateTime.UtcNow < deadline) Thread.Sleep(20);
            dl.Stop();
            File.Delete(part);
            Thread.Sleep(500);
            origin.ChunkDelayMs = 0;
            if (File.Exists(part)) throw new Exception("part file still exists");
            return "deleted";
        });

        Check("Recycle Bin", () =>
        {
            var f = Path.Combine(work, "recycle-me.txt");
            File.WriteAllText(f, "x");
            if (!Shell.MoveToRecycleBin(f) || File.Exists(f)) throw new Exception("file was not moved");
            return "moved";
        });

        Check("Bridge port", () =>
        {
            var server = new LocalServer(AppInfo.DefaultPort, _ => Task.FromResult(HttpResponse.Ok(new() { ["ok"] = true })), ServerController.OriginAllowed);
            try { server.Start(); }
            catch (SocketException) { return $"127.0.0.1:{AppInfo.DefaultPort} is in use — fine if the app is running right now"; }
            server.Stop();
            return $"127.0.0.1:{AppInfo.DefaultPort} is free";
        });

        Check("HTTPS download (github.com)", () =>
        {
            var part = Path.Combine(work, ".dm-https.part");
            Fetch("https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS", part, 2, out var probe);
            var text = File.ReadAllText(part);
            if (!text.Contains("yt-dlp.exe")) throw new Exception("unexpected content");
            return $"{text.Length} bytes via {probe?.FinalUrl?.Host}";
        });

        Check("Sign-in startup registry key", () =>
            Shell.StartupSupported ? $"readable, currently {(Shell.StartupEnabled ? "enabled" : "disabled")}" : "not Windows — skipped");

        Check("Clipboard counter", () => Shell.StartupSupported ? "sequence " + Shell.ClipboardSequence : "not Windows — skipped");

        Info("Browsers", () => string.Join(", ", BrowserKind.All.Select(b => $"{b.Name}: {b.ExecutablePath ?? "not found"}")));
        Info("Extension files", () => File.Exists(Path.Combine(BrowserIntegration.BundledExtensionFolder, "manifest.json"))
            ? $"{BrowserIntegration.BundledExtensionFolder} (v{BrowserIntegration.BundledVersion})" : $"MISSING at {BrowserIntegration.BundledExtensionFolder}");
        Info("Native messaging host", NativeHostRegistry.Describe);
        Info("Firefox add-on", () => BrowserIntegration.FirefoxAddon ?? "not included (FirefoxExtension.xpi next to the executable)");

        foreach (var (tool, path) in new[] { ("yt-dlp", VideoDownloader.LocateYtDlp("")), ("ffmpeg", VideoDownloader.LocateFfmpeg()), ("deno", VideoDownloader.LocateDeno()) })
        {
            if (path == null) { Line($"[info] {tool}: not found" + (tool == "deno" ? " (the app offers to install it on the first YouTube download)" : "")); continue; }
            if (tool == "ffmpeg") { Line($"[info] ffmpeg: {path}"); continue; }
            Check(tool, () => $"{VideoDownloader.Version(path, 90000) ?? throw new Exception("did not run: " + path)} — {path}");
        }

        try { Directory.Delete(work, true); } catch { }
        Line(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} check(s) failed");
        try { File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8); } catch { }
        return failures == 0 ? 0 : 1;
    }

    private static int Fetch(string url, string part, int segments, out ProbeResult? probe)
    {
        var done = new ManualResetEventSlim();
        Exception? error = null;
        SegmentInfo[] segs = [];
        ProbeResult? p = null;
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(url), PartPath = part, SegmentCount = segments,
            Headers = new(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = DownloadStore.DefaultUserAgent },
        })
        {
            OnProbe = r => p = r,
            OnProgress = (_, s) => segs = s,
            OnFinish = e => { error = e; done.Set(); },
        };
        dl.Start();
        if (!done.Wait(60000)) { dl.Stop(); throw new TimeoutException("download timed out"); }
        probe = p;
        if (error != null) throw error;
        return Math.Max(1, segs.Length);
    }

    /// Minimal range-capable HTTP server on a random loopback port.
    private sealed class LoopbackOrigin : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public byte[] Data { get; }
        public int ChunkDelayMs;
        public string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/selftest.bin";

        public LoopbackOrigin(int size)
        {
            Data = new byte[size];
            new Random(7).NextBytes(Data);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var s = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await s.ReadAsync(one) == 0) return;
                        head.Append((char)one[0]);
                    }
                    long start = 0, end = Data.Length - 1;
                    var range = head.ToString().Split("\r\n").FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                    if (range != null)
                    {
                        var parts = range[(range.IndexOf('=') + 1)..].Split('-');
                        start = long.Parse(parts[0]);
                        if (parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
                    }
                    var header = (range != null ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{Data.Length}\r\n" : "HTTP/1.1 200 OK\r\n")
                        + $"Content-Length: {end - start + 1}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(header));
                    for (long pos = start; pos <= end;)
                    {
                        var n = (int)Math.Min(64 * 1024, end - pos + 1);
                        await s.WriteAsync(Data.AsMemory((int)pos, n));
                        pos += n;
                        if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
                    }
                }
                catch { }
            }
        }

        public void Dispose() => listener.Stop();
    }
}
