using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MCDM;

[JsonConverter(typeof(JsonStringEnumConverter<DownloadKind>))]
public enum DownloadKind { file, video }

[JsonConverter(typeof(JsonStringEnumConverter<DownloadState>))]
public enum DownloadState { queued, probing, downloading, paused, completed, failed, cancelled }

public static class DownloadStateExtensions
{
    public static bool IsActive(this DownloadState s) => s is DownloadState.probing or DownloadState.downloading;

    public static string Label(this DownloadState s) => s switch
    {
        DownloadState.queued => Loc.T("Queued"),
        DownloadState.probing => Loc.T("Connecting"),
        DownloadState.downloading => Loc.T("Downloading"),
        DownloadState.paused => Loc.T("Paused"),
        DownloadState.completed => Loc.T("Completed"),
        DownloadState.failed => Loc.T("Error"),
        _ => Loc.T("Cancelled"),
    };
}

public struct SegmentInfo
{
    public long Start { get; set; }
    public long End { get; set; }          // inclusive
    public long Received { get; set; }

    public SegmentInfo(long start, long end, long received = 0) { Start = start; End = end; Received = received; }

    [JsonIgnore] public readonly long Length => End - Start + 1;
    [JsonIgnore] public readonly bool IsDone => Received >= Length;
}

/// One row of the download list. Mutated only on the main thread; `Touch()` refreshes every binding.
public sealed class DownloadItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Directory { get; set; } = "";
    public DownloadKind Kind { get; set; } = DownloadKind.file;
    public DownloadState State { get; set; } = DownloadState.queued;
    public long TotalBytes { get; set; } = -1;
    public long ReceivedBytes { get; set; }
    public bool SupportsRanges { get; set; }
    public List<SegmentInfo> Segments { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CookieFile { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? FinalPath { get; set; }
    public int RequestedSegments { get; set; }      // 0 = the value from Settings
    /// yt-dlp format arguments picked for this video; null = the defaults from Settings.
    public List<string>? FormatArgs { get; set; }
    /// Quality picked in the question ("720p"); becomes part of the file name.
    public string? QualityTag { get; set; }
    /// `Directory` was picked for this download: the file goes straight into it, no category subfolder.
    public bool ExactDirectory { get; set; }
    /// The name was typed by the user: whatever the server suggests does not replace it.
    public bool NameLocked { get; set; }
    /// A whole playlist: yt-dlp writes one file per video into a folder named after the list.
    public bool Playlist { get; set; }
    /// Subtitles for this video; null = as in Settings.
    public bool? Subtitles { get; set; }
    /// Shown after "Completed" when the result needs a remark (a playlist with videos that could not be fetched).
    public string? Note { get; set; }
    /// yt-dlp: the file being written right now (a playlist moves on from one to the next). Kept so that
    /// cancelling after a restart still finds the part files.
    public string? WorkingPath { get; set; }
    /// The browser whose extension handed this over (chrome, edge, brave, opera, firefox): a download the app
    /// cannot get is handed back to that browser.
    public string? SourceBrowser { get; set; }

    // Not persisted
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public string Log { get; set; } = "";
    [JsonIgnore] public string Stage { get; set; } = "";     // yt-dlp: current phase (downloading video/audio, merging…)
    /// Waiting for the user's answer to "download this file?".
    [JsonIgnore] public bool AwaitingConfirmation { get; set; }
    /// The server has been asked about the file (or could not be): an unknown size is final, not pending.
    [JsonIgnore] public bool Probed { get; set; }
    /// HTTP status of the last refusal (403, 410…), if the download failed with one.
    [JsonIgnore] public int? LastHttpStatus { get; set; }

    [JsonIgnore] public Uri Uri => new(Url);
    [JsonIgnore] public string? Host { get { try { return Uri.Host; } catch { return null; } } }
    [JsonIgnore] public string? Referer => Headers.GetValueOrDefault("Referer");

    /// Part file: id based so it stays put when the file name changes after the probe.
    [JsonIgnore] public string PartPath => Path.Combine(Directory, $".dm-{Id}.part");

    [JsonIgnore] public double Progress => TotalBytes > 0 ? Math.Min(1, (double)ReceivedBytes / TotalBytes) : 0;
    [JsonIgnore] public double? Eta => TotalBytes > 0 && Speed > 1 ? (TotalBytes - ReceivedBytes) / Speed : null;
    [JsonIgnore] public FileCategory Category => FileCategories.Detect(FileName, Kind);
    [JsonIgnore] public bool IsFinished => State == DownloadState.completed;

    // ---- Display helpers (list columns, download window) ----

    [JsonIgnore] public string CategoryLabel => Category.Label();
    [JsonIgnore] public string SizeText => TotalBytes > 0 ? Fmt.Size(TotalBytes) : ReceivedBytes > 0 ? Fmt.Size(ReceivedBytes) : "—";
    [JsonIgnore] public string SpeedText => State == DownloadState.downloading && Speed > 0 ? Fmt.Speed(Speed) : "";
    [JsonIgnore] public string EtaText => State == DownloadState.downloading && Eta is { } e ? Fmt.Duration(e) : "";
    [JsonIgnore] public string CreatedText => Fmt.Relative(CreatedAt);
    [JsonIgnore] public int Percent => (int)(Progress * 100);
    [JsonIgnore] public bool IsFailed => State == DownloadState.failed;
    [JsonIgnore] public bool IsPaused => State == DownloadState.paused;

    [JsonIgnore] public bool ShowsBar =>
        State is DownloadState.downloading or DownloadState.paused or DownloadState.probing
        || (State == DownloadState.queued && ReceivedBytes > 0);

    [JsonIgnore] public string StatusText
    {
        get
        {
            switch (State)
            {
                case DownloadState.completed: return string.IsNullOrEmpty(Note) ? Loc.T("Completed") : Loc.T("Completed") + " — " + Note;
                case DownloadState.queued: return AwaitingConfirmation ? Loc.T("Waiting for your answer…") : Loc.T("Added");
                case DownloadState.failed: return Loc.T("Error: {0}", ErrorMessage ?? "");
                case DownloadState.cancelled: return Loc.T("Cancelled");
                default:
                    var pct = TotalBytes > 0 ? $"{Percent}% " : "";
                    if (Kind == DownloadKind.video && State == DownloadState.downloading && Stage.Length > 0) return pct + Stage;
                    return pct + State.Label();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Raises a change notification for all properties (bindings re-read what they show).
    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// `Local`: a disk or file problem on this computer — a browser would not do better.
public class DownloadFailure(string message, bool local = false) : Exception(message)
{
    public bool Local { get; } = local;
}

public sealed class HttpStatusError(int status) : DownloadFailure(Loc.T("Server returned HTTP {0}", status))
{
    public int Status { get; } = status;
}

// ---- Categories ----

public enum FileCategory { image, music, video, apps, document, compressed, other }

public static class FileCategories
{
    public static readonly FileCategory[] All = Enum.GetValues<FileCategory>();

    public static string Label(this FileCategory c) => c switch
    {
        FileCategory.image => Loc.T("Images"),
        FileCategory.music => Loc.T("Music"),
        FileCategory.video => Loc.T("Video"),
        FileCategory.apps => Loc.T("Apps"),
        FileCategory.document => Loc.T("Documents"),
        FileCategory.compressed => Loc.T("Archives"),
        _ => Loc.T("Other"),
    };

    private static readonly Dictionary<string, FileCategory> table = Build(new()
    {
        [FileCategory.image] = ["jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "tiff", "tif", "svg", "psd", "ai", "raw", "cr2", "nef", "ico"],
        [FileCategory.music] = ["mp3", "m4a", "aac", "flac", "wav", "ogg", "opus", "wma", "aiff", "alac"],
        [FileCategory.video] = ["mp4", "m4v", "mkv", "webm", "mov", "avi", "flv", "wmv", "mpg", "mpeg", "ts", "m3u8", "3gp"],
        [FileCategory.apps] = ["dmg", "pkg", "app", "exe", "msi", "msix", "appx", "apk", "ipa", "deb", "rpm", "appimage", "jar", "xpi", "crx"],
        [FileCategory.document] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "epub", "mobi", "csv", "md", "pages", "numbers", "key", "udf"],
        [FileCategory.compressed] = ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "img", "cab"],
    });

    private static Dictionary<string, FileCategory> Build(Dictionary<FileCategory, string[]> src)
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cat, exts) in src) foreach (var e in exts) d[e] = cat;
        return d;
    }

    public static FileCategory Detect(string fileName, DownloadKind kind)
    {
        if (kind == DownloadKind.video) return FileCategory.video;
        var ext = FileNames.Extension(fileName);
        return ext.Length > 0 && table.TryGetValue(ext, out var c) ? c : FileCategory.other;
    }
}

/// File types the app captures (browser downloads, clipboard links). Grouped by category for the UI.
public static class CaptureTypes
{
    public sealed record Group(string Id, FileCategory Category, string[] Extensions);

    public static readonly Group[] Groups =
    [
        new("compressed", FileCategory.compressed, ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "img", "cab"]),
        new("apps", FileCategory.apps, ["exe", "msi", "msix", "apk", "dmg", "pkg", "ipa", "deb", "rpm", "appimage", "jar", "bin"]),
        new("video", FileCategory.video, ["mp4", "m4v", "mkv", "webm", "mov", "avi", "flv", "wmv", "mpg", "mpeg", "m3u8", "3gp"]),
        new("music", FileCategory.music, ["mp3", "m4a", "aac", "flac", "wav", "ogg", "opus", "wma", "aiff"]),
        new("document", FileCategory.document, ["pdf", "epub", "mobi", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "csv"]),
        new("image", FileCategory.image, ["jpg", "jpeg", "png", "gif", "webp", "heic", "bmp", "tiff", "psd", "svg", "raw"]),
    ];

    /// Categories enabled by default on first launch.
    public static readonly string[] DefaultGroupIds = ["compressed", "apps", "video", "music", "document"];

    public static List<string> Extensions(IEnumerable<string> groupIds, string custom)
    {
        var ids = new HashSet<string>(groupIds, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var g in Groups) if (ids.Contains(g.Id)) result.AddRange(g.Extensions);
        foreach (var token in custom.ToLowerInvariant().Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var e = token.Trim('.', ' ');
            if (e.Length > 0 && !result.Contains(e)) result.Add(e);
        }
        return result;
    }

    public static bool Matches(Uri url, IReadOnlyCollection<string> extensions)
    {
        var ext = FileNames.Extension(url.AbsolutePath);
        return ext.Length > 0 && extensions.Contains(ext);
    }
}

public static class FileNames
{
    /// Lower-case extension without the dot ("" when there is none). Culture-independent on purpose:
    /// under a Turkish locale "ZIP".ToLower() would not be "zip".
    public static string Extension(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : "";
    }

    public static string FromUrl(Uri url)
    {
        var last = url.Segments.Length > 0 ? url.Segments[^1].TrimEnd('/') : "";
        var name = Uri.UnescapeDataString(last);
        if (name.Length == 0) name = url.Host.Length > 0 ? url.Host : "download";
        return name;
    }

    private static readonly HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// Makes a server- or browser-supplied name safe to create on NTFS: no path separators, no reserved
    /// characters or device names, no trailing dots/spaces, bounded length.
    public static string Sanitize(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') chars[i] = '_';
        }
        var n = new string(chars).Trim().TrimEnd('.', ' ');
        if (n.StartsWith('.')) n = "_" + n;
        if (n.Length > 200)
        {
            var ext = Path.GetExtension(n);
            var stem = Path.GetFileNameWithoutExtension(n);
            n = stem[..Math.Min(stem.Length, 180)].TrimEnd('.', ' ') + (ext.Length <= 20 ? ext : "");
        }
        var stemOnly = n.Split('.')[0];
        if (reserved.Contains(stemOnly)) n = "_" + n;
        return n.Length == 0 ? "download" : n;
    }

    /// A file or a folder (a finished playlist is a folder).
    public static bool Exists(string? path) => !string.IsNullOrEmpty(path) && (File.Exists(path) || System.IO.Directory.Exists(path));

    public static string UniqueDestination(string path)
    {
        if (!File.Exists(path) && !System.IO.Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var ext = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !System.IO.Directory.Exists(candidate)) return candidate;
        }
    }

    private static readonly Dictionary<string, string> mimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = "pdf", ["application/zip"] = "zip", ["application/x-zip-compressed"] = "zip",
        ["application/x-rar-compressed"] = "rar", ["application/vnd.rar"] = "rar", ["application/x-7z-compressed"] = "7z",
        ["application/gzip"] = "gz", ["application/x-tar"] = "tar", ["application/x-bzip2"] = "bz2", ["application/x-xz"] = "xz",
        ["application/x-msdownload"] = "exe", ["application/vnd.microsoft.portable-executable"] = "exe",
        ["application/x-msi"] = "msi", ["application/vnd.android.package-archive"] = "apk",
        ["application/x-apple-diskimage"] = "dmg", ["application/x-iso9660-image"] = "iso", ["application/java-archive"] = "jar",
        ["application/epub+zip"] = "epub", ["application/msword"] = "doc", ["application/rtf"] = "rtf",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.ms-excel"] = "xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.ms-powerpoint"] = "ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/json"] = "json", ["application/xml"] = "xml", ["text/xml"] = "xml",
        ["text/plain"] = "txt", ["text/csv"] = "csv", ["text/html"] = "html",
        ["image/jpeg"] = "jpg", ["image/png"] = "png", ["image/gif"] = "gif", ["image/webp"] = "webp",
        ["image/svg+xml"] = "svg", ["image/bmp"] = "bmp", ["image/tiff"] = "tiff", ["image/heic"] = "heic",
        ["video/mp4"] = "mp4", ["video/webm"] = "webm", ["video/x-matroska"] = "mkv", ["video/quicktime"] = "mov",
        ["video/x-msvideo"] = "avi", ["video/x-flv"] = "flv", ["video/mpeg"] = "mpg", ["video/mp2t"] = "ts",
        ["audio/mpeg"] = "mp3", ["audio/mp4"] = "m4a", ["audio/aac"] = "aac", ["audio/flac"] = "flac",
        ["audio/wav"] = "wav", ["audio/x-wav"] = "wav", ["audio/ogg"] = "ogg", ["audio/opus"] = "opus", ["audio/webm"] = "weba",
    };

    public static string? ExtensionForMime(string? contentType) =>
        contentType != null && mimeExtensions.TryGetValue(contentType.Trim(), out var e) ? e : null;
}
