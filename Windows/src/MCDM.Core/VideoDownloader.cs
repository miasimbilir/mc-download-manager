using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MCDM;

/// yt-dlp wrapper. Prefers a user-updated copy in the app data folder, then the bundled one
/// (tools\ next to the executable), then whatever is on PATH.
public static class VideoDownloader
{
    public static string? LocateYtDlp(string custom) => Locate("yt-dlp", custom);

    public static string? LocateFfmpeg() => Locate("ffmpeg", null, userCopyFirst: false);

    /// deno is the JavaScript runtime yt-dlp uses to solve YouTube's n/signature challenges.
    /// Without it YouTube downloads fail or lose formats (see https://github.com/yt-dlp/yt-dlp/wiki/EJS).
    public static string? LocateDeno() => Locate("deno", null,
        extra: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deno", "bin"));

    private static string? Locate(string tool, string? custom, bool userCopyFirst = true, string? extra = null)
    {
        var exe = AppPaths.Exe(tool);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom.Trim().Trim('"'));
        var user = Path.Combine(AppPaths.BinDir, exe);
        var bundled = Path.Combine(AppPaths.BundledToolsDir, exe);
        if (userCopyFirst) { candidates.Add(user); candidates.Add(bundled); }
        else { candidates.Add(bundled); candidates.Add(user); }
        if (extra != null) candidates.Add(Path.Combine(extra, exe));
        if (!OperatingSystem.IsWindows())
        {
            candidates.Add("/opt/homebrew/bin/" + exe);
            candidates.Add("/usr/local/bin/" + exe);
        }
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static bool IsYouTube(Uri url) => HostMatches(url.Host, "youtube.com", "youtu.be", "youtube-nocookie.com");

    public static bool HostMatches(string? host, params string[] domains)
    {
        host = host?.ToLowerInvariant() ?? "";
        foreach (var d in domains) if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
        return false;
    }

    /// YouTube rejects the signed-in clients yt-dlp picks when cookies are passed
    /// ("The page needs to be reloaded", yt-dlp#17389); the same video usually works without cookies.
    public static bool IsSignedInClientFailure(string message) =>
        message.Contains("page needs to be reloaded", StringComparison.OrdinalIgnoreCase);

    public static void ApplyEnvironment(ProcessStartInfo psi)
    {
        var path = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH");
        var extra = new List<string> { AppPaths.BundledToolsDir, AppPaths.BinDir };
        if (!OperatingSystem.IsWindows()) extra.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, extra) + Path.PathSeparator + path;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
    }

    public static bool HasCookies(string? itemCookies, string? targetHost) =>
        (itemCookies != null && File.Exists(itemCookies)) || SiteCookies.Shared.AppliesTo(targetHost);

    /// Combines the per-download cookie file (from the browser extension) with the shared site cookie
    /// file into one temporary Netscape file. The site jar is only attached when the target host
    /// belongs to one of its domains.
    public static string? MergedCookieFile(string? itemCookies, string? targetHost)
    {
        var item = itemCookies != null && File.Exists(itemCookies) ? itemCookies : null;
        var site = SiteCookies.Shared.AppliesTo(targetHost) ? SiteCookies.FilePath : null;
        if (item == null) return site;
        if (site == null) return item;
        try
        {
            var body = File.ReadAllText(site) + "\n" + File.ReadAllText(item);
            Directory.CreateDirectory(AppPaths.CookiesDir);
            var tmp = Path.Combine(AppPaths.CookiesDir, $"merged-{Guid.NewGuid()}.txt");
            File.WriteAllText(tmp, body, new UTF8Encoding(false));
            return tmp;
        }
        catch
        {
            return item;
        }
    }

    public readonly record struct Progress(long Downloaded, long Total, double Speed);

    private static readonly Regex audioPart = new(@"\.f[0-9]+[-a-z0-9]*\.(m4a|webm|opus|mp3|aac|ogg)$", RegexOptions.CultureInvariant);
    private static readonly Regex anyPart = new(@"\.f[0-9]+[-a-z0-9]*\.[a-z0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex formatSuffix = new(@"\.f[0-9]+[-a-z0-9]*(?=\.[a-z0-9]+$)", RegexOptions.CultureInvariant);

    private static readonly Regex qualityTagPattern = new(@"^\d{3,4}p$", RegexOptions.CultureInvariant);
    // The end of a name made by our output template: "… [id]" or "… [id] 720p".
    private static readonly Regex templateStem = new(@" \[[^\[\]]+\]( \d{3,4}p)?$", RegexOptions.CultureInvariant);

    /// Strips yt-dlp's intermediate ".f137" format suffix from a file name.
    public static string StripFormatSuffix(string name) => formatSuffix.Replace(name, "", 1);

    // What follows "<title> [<id>]." in the files an unfinished run leaves behind: "f299.mp4.part",
    // "f140.m4a", "mp4.part", "mp4.ytdl", "f299.mp4.part-Frag12", "temp.mp4".
    private static readonly Regex leftover = new(
        @"^(f[0-9]+[-a-z0-9]*\.[a-z0-9]+(\.part(-Frag[0-9]+)?(\.part)?|\.ytdl)?|[a-z0-9]+(\.part(-Frag[0-9]+)?(\.part)?|\.ytdl)|temp\.[a-z0-9]+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// Deletes what a cancelled yt-dlp run leaves next to where the video would have gone (part files,
    /// already fetched single streams, fragment files). `fileName` is the item's name as shown in the
    /// list ("Title [id].mp4"); a finished file of that name is never touched. Returns false while a
    /// leftover could not be deleted (still held open by a process that is on its way out).
    public static bool DeletePartials(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(StripFormatSuffix(fileName));
        // Only names made by our output template ("… [id]") — never a bare host or title.
        if (stem.Length < 4 || !templateStem.IsMatch(stem)) return true;
        var clean = true;
        try
        {
            if (!Directory.Exists(directory)) return true;
            foreach (var f in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(f);
                if (name.Length <= stem.Length + 1 || name[stem.Length] != '.' || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (!leftover.IsMatch(name[(stem.Length + 1)..])) continue;
                try { File.Delete(f); } catch { clean = false; }
            }
        }
        catch { clean = false; }
        return clean;
    }

    /// Turns a yt-dlp log line into a user-facing phase description, if it marks a new phase.
    public static string? Stage(string line)
    {
        if (line.StartsWith("[download] Destination:", StringComparison.Ordinal))
        {
            var lower = line["[download] Destination:".Length..].Trim().ToLowerInvariant();
            // yt-dlp names intermediate files <title>.f<formatid>.<ext>
            if (audioPart.IsMatch(lower)) return Loc.T("Downloading audio…");
            if (anyPart.IsMatch(lower)) return Loc.T("Downloading video…");
            return Loc.T("Downloading…");
        }
        if (line.StartsWith("[Merger]", StringComparison.Ordinal)) return Loc.T("Merging video and audio (ffmpeg)…");
        if (line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal) || line.StartsWith("[VideoConvertor]", StringComparison.Ordinal))
            return Loc.T("Converting container (ffmpeg)…");
        if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)) return Loc.T("Extracting audio (ffmpeg)…");
        if (line.StartsWith("MCFMT|", StringComparison.Ordinal))
        {
            var p = line.Split('|');
            if (p.Length < 6) return null;
            var parts = new List<string>();
            if (p[1] != "NA" && p[1].Length > 0) parts.Add(p[1]);
            if (p[2] != "NA") parts.Add(p[2].ToUpperInvariant());
            if (p[3] != "NA" && p[3] != "none") parts.Add(p[3].Split('.')[0]);
            if (ParseNumber(p[5]) is > 0 and var n) parts.Add("~" + Fmt.Size((long)n));
            return Loc.T("Selected: {0}", string.Join(" · ", parts));
        }
        if (line.Contains("has already been downloaded", StringComparison.Ordinal)) return Loc.T("File already downloaded");
        return null;
    }

    public static double? ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;

    public static List<string> BuildArguments(Uri url, string outputDir, IEnumerable<string> formatArgs,
        IReadOnlyDictionary<string, string> headers, string? cookieFile, string? ffmpeg, string? deno, string? qualityTag = null)
    {
        // "Title [id] 720p.mp4" when the quality was picked for this download: without the tag a second
        // request for the same video in another quality would find "Title [id].mp4" and download nothing.
        var tag = qualityTag != null && qualityTagPattern.IsMatch(qualityTag) ? " " + qualityTag : "";
        var args = new List<string>
        {
            "--newline", "--no-playlist", "--no-colors", "--progress", "--no-simulate", "--no-mtime", "--no-quiet",
            "--encoding", "utf-8",
            "-N", "4",
            "-o", Path.Combine(outputDir, $"%(title).150B [%(id)s]{tag}.%(ext)s"),
            "--progress-template",
            "download:DMP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
            "--print", "after_move:filepath",
            "--print", "before_dl:MCFMT|%(resolution)s|%(ext)s|%(vcodec)s|%(acodec)s|%(filesize_approx)s|%(format_id)s|%(duration)s",
        };
        args.AddRange(formatArgs);
        if (ffmpeg != null) args.AddRange(["--ffmpeg-location", ffmpeg]);
        if (headers.TryGetValue("User-Agent", out var ua) && ua.Length > 0) args.AddRange(["--user-agent", ua]);
        if (headers.TryGetValue("Referer", out var referer) && referer.Length > 0) args.AddRange(["--referer", referer]);
        if (cookieFile != null) args.AddRange(["--cookies", cookieFile]);
        if (deno != null) args.AddRange(["--js-runtimes", "deno:" + deno]);
        // "--" keeps a URL that starts with a dash from being read as an option.
        args.Add("--");
        args.Add(url.AbsoluteUri);
        return args;
    }

    /// Starts yt-dlp. `onProgress`/`onLine` arrive on reader threads; `onFinish` gets the final file path
    /// (null when yt-dlp did not report one) or the error.
    public static Process? Run(string executable, Uri url, string outputDir, IEnumerable<string> formatArgs,
        IReadOnlyDictionary<string, string> headers, string? cookieFile, bool useCookies,
        Action<Progress> onProgress, Action<string> onLine, Action<string?, Exception?> onFinish, string? qualityTag = null)
    {
        var merged = useCookies ? MergedCookieFile(cookieFile, url.Host) : null;
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = outputDir,
        };
        foreach (var a in BuildArguments(url, outputDir, formatArgs, headers, merged, LocateFfmpeg(), LocateDeno(), qualityTag))
            psi.ArgumentList.Add(a);
        ApplyEnvironment(psi);

        var gate = new object();
        string? finalPath = null;
        string? lastError = null;

        void Handle(string raw, bool isError)
        {
            var line = raw.Trim();
            if (line.Length == 0) return;
            if (line.StartsWith("DMP|", StringComparison.Ordinal))
            {
                var parts = line.Split('|');
                if (parts.Length < 5) return;
                long Num(string s) => (long)(ParseNumber(s) ?? -1);
                var downloaded = Math.Max(0, Num(parts[1]));
                var total = Num(parts[2]);
                if (total <= 0) total = Num(parts[3]);
                onProgress(new Progress(downloaded, total, Math.Max(0, ParseNumber(parts[4]) ?? 0)));
                return;
            }
            if (!isError && Path.IsPathFullyQualified(line) && File.Exists(line)) lock (gate) finalPath = line;
            if (isError && line.StartsWith("ERROR", StringComparison.Ordinal)) lock (gate) lastError = line;
            onLine(line);
        }

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new DownloadFailure(Loc.T("yt-dlp not found"));
        }
        catch (Exception e)
        {
            onFinish(null, e);
            return null;
        }
        try { p.StandardInput.Close(); } catch { }

        var readers = new[]
        {
            Task.Run(() => ReadLines(p.StandardOutput.BaseStream, l => Handle(l, false))),
            Task.Run(() => ReadLines(p.StandardError.BaseStream, l => Handle(l, true))),
        };
        Task.Run(async () =>
        {
            int code;
            try
            {
                await p.WaitForExitAsync().ConfigureAwait(false);
                // A grandchild can keep the pipes open; do not wait for EOF forever.
                await Task.WhenAny(Task.WhenAll(readers), Task.Delay(3000)).ConfigureAwait(false);
                code = p.ExitCode;
            }
            catch (Exception e)
            {
                onFinish(null, e);
                return;
            }
            finally
            {
                if (merged != null && merged != cookieFile && merged != SiteCookies.FilePath) try { File.Delete(merged); } catch { }
            }
            string? path, err;
            lock (gate) { path = finalPath; err = lastError; }
            if (code == 0) onFinish(path, null);
            else onFinish(null, new DownloadFailure(err ?? Loc.T("yt-dlp exited with code {0}", code)));
        });
        return p;
    }

    /// Kills yt-dlp together with its children: the PyInstaller build of yt-dlp.exe runs the real
    /// program in a child process, and ffmpeg may be running underneath it.
    public static void Kill(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    private static readonly UTF8Encoding strictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> ansi = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch { return Encoding.Latin1; }
    });

    /// Lines are UTF-8 (`--encoding utf-8`); if a line is not valid UTF-8, fall back to the ANSI code page.
    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return strictUtf8.GetString(bytes); }
        catch (ArgumentException) { return ansi.Value.GetString(bytes); }
    }

    private static void ReadLines(Stream stream, Action<string> onLine)
    {
        var buffer = new byte[16 * 1024];
        var line = new MemoryStream();
        try
        {
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    // yt-dlp redraws its progress line with \r when stdout is not a terminal-aware pipe.
                    if (buffer[i] is (byte)'\n' or (byte)'\r')
                    {
                        if (line.Length > 0) onLine(Decode(line.GetBuffer().AsSpan(0, (int)line.Length)));
                        line.SetLength(0);
                    }
                    else line.WriteByte(buffer[i]);
                }
            }
        }
        catch { }
        if (line.Length > 0) onLine(Decode(line.GetBuffer().AsSpan(0, (int)line.Length)));
    }

    /// First line of `<tool> --version` (deno prints three lines; yt-dlp one).
    public static string? Version(string path, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");
            ApplyEnvironment(psi);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { Kill(p); return null; }
            if (!read.Wait(2000)) return null;
            var v = read.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }
}

/// Shared cookie jar for sites that need a signed-in account (YouTube age verification, members-only
/// videos): a Netscape cookies.txt the user exports from the browser and imports in Settings ▸ Video.
public sealed class SiteCookies
{
    public static SiteCookies Shared { get; } = new();

    public static string FilePath => Path.Combine(AppPaths.CookiesDir, "sites.txt");

    public int CookieCount { get; private set; }
    public DateTime? SavedAt { get; private set; }
    public List<string> Domains { get; private set; } = [];

    private SiteCookies() => LoadStatus();

    public bool AppliesTo(string? host) => Domains.Count > 0 && VideoDownloader.HostMatches(host, [.. Domains]);

    /// Copies a Netscape-format cookie file into the app data folder. Throws when it holds no cookie rows.
    public void Import(string sourcePath)
    {
        var text = File.ReadAllText(sourcePath);
        if (Rows(text).Count == 0) throw new DownloadFailure(Loc.T("This file does not look like a Netscape cookies.txt export."));
        Directory.CreateDirectory(AppPaths.CookiesDir);
        File.WriteAllText(FilePath, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        LoadStatus();
    }

    public void Clear()
    {
        try { File.Delete(FilePath); } catch { }
        LoadStatus();
    }

    private static List<string[]> Rows(string text)
    {
        var rows = new List<string[]>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // "#HttpOnly_" marks a cookie row; every other "#" line is a comment.
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line["#HttpOnly_".Length..];
            else if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length >= 7) rows.Add(cols);
        }
        return rows;
    }

    private void LoadStatus()
    {
        try
        {
            if (!File.Exists(FilePath)) { CookieCount = 0; SavedAt = null; Domains = []; return; }
            var rows = Rows(File.ReadAllText(FilePath));
            CookieCount = rows.Count;
            var hosts = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var parts = r[0].TrimStart('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                hosts.Add(parts.Length >= 2 ? parts[^2] + "." + parts[^1] : parts[0]);
            }
            Domains = [.. hosts];
            SavedAt = File.GetLastWriteTimeUtc(FilePath);
        }
        catch
        {
            CookieCount = 0; SavedAt = null; Domains = [];
        }
    }
}
