using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCDM;

/// One resolution a video is offered in.
/// `Height` is the real pixel height (used to cap the fallback selection); `Nominal` is the number people
/// know it by: the site's own label, or the short side (a 1920×818 film and a 1080×1920 reel are both 1080p).
/// `Selector` names the exact yt-dlp formats behind the size shown, e.g. "137+140".
public sealed record VideoChoice(int Height, int Nominal, bool HighFrameRate, long? ApproxBytes, string Selector)
{
    public string Label => Nominal + "p" + (HighFrameRate ? " · 60 fps" : "");
}

public sealed record VideoInfo(string Title, double? Duration, List<VideoChoice> Choices, bool HasAudio, long? AudioBytes)
{
    /// Set when the address also (or only) names a playlist.
    public PlaylistInfo? Playlist { get; init; }
}

/// The playlist behind an address. `Only`: the address names nothing but the list
/// (youtube.com/playlist?list=…), as opposed to one video opened from a list (watch?v=…&list=…).
public sealed record PlaylistInfo(string Title, int Count, bool Only);

/// What the user picked. `MaxHeight`: a pixel height cap, -1 = audio only (M4A), -2 = audio only (MP3),
/// or `UseDefault` to go with Settings. `Nominal` is what "always use this" stores in Settings.
public sealed record VideoFormatAnswer(int MaxHeight, int Nominal, bool Remember, string? Selector = null)
{
    public const int UseDefault = int.MinValue;
    public static VideoFormatAnswer Default { get; } = new(UseDefault, UseDefault, false);

    /// A folder picked for this download; null = the usual place.
    public string? Directory { get; init; }
    /// Also fetch subtitles; null = as in Settings.
    public bool? Subtitles { get; init; }
    /// Download the whole playlist, not just the one video.
    public bool Playlist { get; init; }

    /// "Not above my target; if nothing is that small, the smallest there is" — the rule the default
    /// selection follows (`target` as in Settings: 0 = best).
    public static VideoChoice? Pick(IReadOnlyList<VideoChoice> choices, int target)
    {
        if (choices.Count == 0) return null;
        if (target <= 0) return choices.MaxBy(c => c.Nominal);
        return choices.Where(c => c.Nominal <= target).MaxBy(c => c.Nominal) ?? choices.MinBy(c => c.Nominal);
    }
}

public static class VideoFormats
{
    /// Asks yt-dlp which formats the page offers (`yt-dlp -J`), without downloading anything. When the
    /// address belongs to a playlist, the list's title and length are fetched alongside.
    public static async Task<(VideoInfo? Info, string? Error)> ProbeAsync(string executable, Uri url,
        IReadOnlyDictionary<string, string> headers, string? cookieFile, bool useCookies, string container, CancellationToken ct)
    {
        var merged = useCookies ? VideoDownloader.MergedCookieFile(cookieFile, url.Host) : null;
        try
        {
            var kind = Links.Playlist(url);
            var listing = kind == PlaylistKind.None
                ? Task.FromResult<PlaylistInfo?>(null)
                : ProbePlaylistAsync(executable, url, headers, merged, kind == PlaylistKind.Only, ct);
            // "-I 1": for an address that is only a playlist, look at its first video instead of all of them.
            var (json, error) = await RunJsonAsync(executable, ["-J", "--no-playlist", "-I", "1"], url, headers, merged, ct).ConfigureAwait(false);
            var playlist = await listing.ConfigureAwait(false);
            if (json == null) return (null, error);
            using var doc = JsonDocument.Parse(json);
            var (info, parts) = ParseDetailed(doc.RootElement, container);
            info = await MeasureAsync(doc.RootElement, info, parts, ct).ConfigureAwait(false);
            return (info with { Playlist = playlist }, null);
        }
        catch (OperationCanceledException)
        {
            return (null, ct.IsCancellationRequested ? "cancelled" : Loc.T("The server did not respond in time"));
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
        finally
        {
            if (merged != null && merged != cookieFile && merged != SiteCookies.FilePath) try { File.Delete(merged); } catch { }
        }
    }

    /// Title and length of a playlist (`--flat-playlist`: the videos themselves are not looked at).
    /// null when the address turns out not to be one, or the listing fails; the single video still works then.
    private static async Task<PlaylistInfo?> ProbePlaylistAsync(string executable, Uri url, IReadOnlyDictionary<string, string> headers,
        string? cookies, bool only, CancellationToken ct)
    {
        try
        {
            var (json, _) = await RunJsonAsync(executable, ["-J", "--flat-playlist", "--yes-playlist"], url, headers, cookies, ct).ConfigureAwait(false);
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            return ParsePlaylist(doc.RootElement, only);
        }
        catch
        {
            return null;
        }
    }

    public static PlaylistInfo? ParsePlaylist(JsonElement root, bool only)
    {
        if (Str(root, "_type") != "playlist") return null;
        var entries = root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
        var count = (int)(Num(root, "playlist_count") ?? entries);
        if (count <= 0) count = entries;
        return count > 0 ? new PlaylistInfo(Str(root, "title") ?? "", count, only) : null;
    }

    /// Runs yt-dlp for a JSON answer. Returns the JSON, or null and the reason.
    private static async Task<(string? Json, string? Error)> RunJsonAsync(string executable, string[] mode, Uri url,
        IReadOnlyDictionary<string, string> headers, string? cookies, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var args = new List<string>(mode) { "--no-warnings", "--encoding", "utf-8" };
        if (headers.TryGetValue("User-Agent", out var ua) && ua.Length > 0) args.AddRange(["--user-agent", ua]);
        if (headers.TryGetValue("Referer", out var referer) && referer.Length > 0) args.AddRange(["--referer", referer]);
        if (cookies != null) args.AddRange(["--cookies", cookies]);
        if (VideoDownloader.LocateDeno() is { } deno) args.AddRange(["--js-runtimes", "deno:" + deno]);
        args.Add("--");
        args.Add(url.AbsoluteUri);
        foreach (var a in args) psi.ArgumentList.Add(a);
        VideoDownloader.ApplyEnvironment(psi);

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null) return (null, Loc.T("yt-dlp not found"));
            try { p.StandardInput.Close(); } catch { }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json = await stdout.ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);
            if (p.ExitCode != 0 || json.Trim().Length == 0)
            {
                var line = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal));
                return (null, line ?? Loc.T("yt-dlp exited with code {0}", p.ExitCode));
            }
            return (json, null);
        }
        catch
        {
            VideoDownloader.Kill(p);
            throw;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static readonly Regex noteHeight = new(@"^(\d{3,4})p", RegexOptions.CultureInvariant);
    private static readonly int[] standardHeights = [144, 240, 270, 360, 480, 540, 720, 1080, 1440, 2160, 4320];

    private sealed record Format(JsonElement Json, string Id, string? VCodec, string? ACodec, string Ext, bool Https, double Tbr, long? Bytes)
    {
        public int Height, Nominal;
        public double Fps;
        /// yt-dlp reports "none" when a stream is absent and nothing at all when it does not know;
        /// a progressive file with unknown codecs has both.
        public bool HasAudio => ACodec != "none";
    }

    /// Turns yt-dlp's info JSON into the list shown to the user: one entry per resolution, largest first,
    /// each tied to the formats that would be downloaded for it in the given container.
    public static VideoInfo Parse(JsonElement root, string container) => ParseDetailed(root, container).Info;

    private static (VideoInfo Info, List<(Format Video, Format? Audio)> Parts) ParseDetailed(JsonElement root, string container)
    {
        // A playlist URL with --no-playlist can still come back wrapped; use its first entry.
        if (!root.TryGetProperty("formats", out _) && root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            root = entries[0];

        var title = Str(root, "title") ?? "";
        var duration = Num(root, "duration");
        var videos = new List<Format>();
        var audios = new List<Format>();

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var j in formats.EnumerateArray())
            {
                var id = Str(j, "format_id");
                if (string.IsNullOrEmpty(id)) continue;
                var tbr = Num(j, "tbr") ?? Num(j, "abr") ?? 0;
                var bytes = Num(j, "filesize") ?? Num(j, "filesize_approx");
                if (bytes is not > 0) bytes = tbr > 0 && duration is > 0 ? tbr * 125 * duration.Value : null;
                var f = new Format(j, id, Str(j, "vcodec"), Str(j, "acodec"), Str(j, "ext") ?? "",
                    (Str(j, "protocol") ?? "").StartsWith("http", StringComparison.Ordinal), tbr, bytes is > 0 ? (long)bytes : null);
                int width = (int)(Num(j, "width") ?? 0), height = (int)(Num(j, "height") ?? 0);
                if (f.VCodec == "none")
                {
                    if (f.ACodec != "none") audios.Add(f);      // both "none" = storyboard images
                    continue;
                }
                if (height <= 0) continue;                      // a format yt-dlp knows nothing about
                f.Height = height;
                f.Fps = Num(j, "fps") ?? 0;
                var note = noteHeight.Match(Str(j, "format_note") ?? "");
                if (note.Success) f.Nominal = int.Parse(note.Groups[1].Value);
                else
                {
                    var side = width > 0 ? Math.Min(width, height) : height;
                    f.Nominal = standardHeights.FirstOrDefault(s => Math.Abs(s - side) <= s * 0.03, side);
                }
                videos.Add(f);
            }
        }

        // Preference inside one resolution, mirroring what plays everywhere for the container:
        // a plain https file before an HLS playlist, then the container's codec, then bitrate.
        int CodecRank(Format f) => container switch
        {
            "mp4" => f.VCodec == null ? (f.Ext == "mp4" ? 2 : 0) : f.VCodec.StartsWith("avc1", StringComparison.Ordinal) ? 3 : f.Ext == "mp4" ? 1 : 0,
            "webm" => f.Ext == "webm" ? 3 : 0,
            _ => 0,
        };
        // Audio: the original language, no dynamic-range-compressed variant, the container's codec, bitrate.
        var audio = audios
            .OrderByDescending(a => Num(a.Json, "language_preference") ?? -1)
            .ThenBy(a => a.Id.Contains("drc", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => container == "mp4" ? (a.ACodec?.StartsWith("mp4a", StringComparison.Ordinal) == true || a.Ext is "m4a" or "mp4" ? 1 : 0)
                                 : container == "webm" ? (a.Ext == "webm" ? 1 : 0) : 0)
            .ThenByDescending(a => a.Https)
            .ThenByDescending(a => a.Tbr)
            .FirstOrDefault();

        var choices = new List<VideoChoice>();
        var parts = new List<(Format Video, Format? Audio)>();
        foreach (var group in videos.GroupBy(v => v.Nominal).OrderByDescending(g => g.Key))
        {
            var best = group.OrderByDescending(v => v.Https).ThenByDescending(CodecRank).ThenByDescending(v => v.Tbr).ThenByDescending(v => v.Bytes ?? 0).First();
            var needsAudio = !best.HasAudio && audio != null;
            var bytes = best.Bytes;
            if (bytes != null && needsAudio) bytes = audio!.Bytes is { } ab ? bytes + ab : null;
            choices.Add(new VideoChoice(group.Max(v => v.Height), group.Key, group.Any(v => v.Fps >= 50), bytes,
                needsAudio ? best.Id + "+" + audio!.Id : best.Id));
            parts.Add((best, needsAudio ? audio : null));
        }
        return (new VideoInfo(title, duration, choices, audio != null || videos.Any(v => v.HasAudio), audio?.Bytes), parts);
    }

    private static readonly HttpClient http = new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// Replaces listed sizes by measured ones where the listing cannot be trusted.
    /// YouTube reports real file sizes. X reports bitrate × duration at the nominal bitrate (a 270p clip
    /// listed as 5.6 MB is 2.1 MB on the wire) and Instagram reports nothing; their files are plain
    /// https downloads, so one ranged request per file tells the real size.
    private static async Task<VideoInfo> MeasureAsync(JsonElement root, VideoInfo info, List<(Format Video, Format? Audio)> parts, CancellationToken ct)
    {
        if (!root.TryGetProperty("formats", out _) && root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            root = entries[0];
        if (string.Equals(Str(root, "extractor_key"), "Youtube", StringComparison.OrdinalIgnoreCase) || parts.Count == 0 || parts.Count > 12) return info;

        var wanted = parts.SelectMany(p => new[] { p.Video, p.Audio }).Where(f => f is { Https: true } && Str(f.Json, "url") != null)
            .Select(f => f!).DistinctBy(f => f.Id).ToList();
        if (wanted.Count == 0) return info;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var measured = new Dictionary<string, long>();
        await Task.WhenAll(wanted.Select(async f =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Str(f.Json, "url"));
                if (f.Json.TryGetProperty("http_headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
                    foreach (var h in headers.EnumerateObject())
                        if (h.Value.ValueKind == JsonValueKind.String && !h.Name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                            req.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString());
                req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                long? total = (int)resp.StatusCode == 206 ? resp.Content.Headers.ContentRange?.Length
                    : resp.IsSuccessStatusCode ? resp.Content.Headers.ContentLength : null;
                if (total is > 0) lock (measured) measured[f.Id] = total.Value;
            }
            catch { }
        })).ConfigureAwait(false);
        if (measured.Count == 0) return info;

        long? Bytes(Format f) => measured.TryGetValue(f.Id, out var m) ? m : f.Bytes;
        var choices = info.Choices.Select((c, i) =>
        {
            var (video, audio) = parts[i];
            var bytes = Bytes(video);
            if (bytes != null && audio != null) bytes = Bytes(audio) is { } ab ? bytes + ab : null;
            return c with { ApproxBytes = bytes };
        }).ToList();
        var audioBytes = parts.Select(p => p.Audio).FirstOrDefault(a => a != null) is { } track ? Bytes(track) : info.AudioBytes;
        return info with { Choices = choices, AudioBytes = audioBytes };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
