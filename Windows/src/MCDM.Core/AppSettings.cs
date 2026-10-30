using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCDM;

/// User preferences, stored as JSON in the app data folder. Every change is written straight away.
public sealed class AppSettings : INotifyPropertyChanged
{
    private int segmentCount = 8;
    private int maxConcurrent = 3;
    private int serverPort = AppInfo.DefaultPort;
    private string downloadDirectory = AppPaths.DefaultDownloadDir;
    private string ytdlpPath = "";
    private string videoFormat = "";            // custom -f (overrides the pickers when non-empty)
    private string videoContainer = "mp4";      // mp4 | mkv | webm | original
    private int videoMaxHeight = 1080;          // 0 = best, -1 = audio m4a, -2 = audio mp3
    private bool autoStart = true;
    private bool notifyOnComplete = true;
    private bool showDetailOnStart = true;
    private bool autoCloseDetail;
    private bool watchClipboard = true;
    private List<string> captureGroupIds = [.. CaptureTypes.DefaultGroupIds];
    private string customExtensions = "";
    private bool onboardingDone;
    private bool organizeByCategory = true;
    private bool startInTray = true;
    private bool denoPromptSuppressed;
    private bool askVideoQuality = true;
    private List<string> chromiumExtensionIds = [];
    private List<string> browserOnlySites = [];
    private Dictionary<string, HostLimit> hostLimits = new(StringComparer.Ordinal);
    private Dictionary<string, string> extensionsSeen = new(StringComparer.Ordinal);
    private bool askBeforeDownload = true;
    private bool speedLimitEnabled;
    private int speedLimitKb = 1024;
    private bool videoSubtitles;
    private string subtitleLanguages = DefaultSubtitleLanguages;
    private bool checkForUpdates = true;
    private string skippedVersion = "";
    private DateTime lastUpdateCheck;
    private bool? startWithWindows;
    private string theme = "system";
    private string accentColor = "";

    public int SegmentCount { get => segmentCount; set => Set(ref segmentCount, Math.Clamp(value, 1, 16)); }
    public int MaxConcurrent { get => maxConcurrent; set => Set(ref maxConcurrent, Math.Clamp(value, 1, 10)); }
    public int ServerPort { get => serverPort; set => Set(ref serverPort, value); }
    public string DownloadDirectory { get => downloadDirectory; set => Set(ref downloadDirectory, string.IsNullOrWhiteSpace(value) ? AppPaths.DefaultDownloadDir : value); }
    public string YtdlpPath { get => ytdlpPath; set => Set(ref ytdlpPath, value ?? ""); }
    public string VideoFormat { get => videoFormat; set { if (Set(ref videoFormat, value ?? "")) Raise(nameof(EffectiveArguments)); } }
    public string VideoContainer { get => videoContainer; set { if (Set(ref videoContainer, value ?? "mp4")) Raise(nameof(EffectiveArguments)); } }
    public int VideoMaxHeight { get => videoMaxHeight; set { if (Set(ref videoMaxHeight, value)) Raise(nameof(EffectiveArguments)); } }
    public bool AutoStart { get => autoStart; set => Set(ref autoStart, value); }
    public bool NotifyOnComplete { get => notifyOnComplete; set => Set(ref notifyOnComplete, value); }
    public bool ShowDetailOnStart { get => showDetailOnStart; set => Set(ref showDetailOnStart, value); }
    public bool AutoCloseDetail { get => autoCloseDetail; set => Set(ref autoCloseDetail, value); }
    public bool WatchClipboard { get => watchClipboard; set => Set(ref watchClipboard, value); }
    public List<string> CaptureGroupIds { get => captureGroupIds; set => Set(ref captureGroupIds, value ?? []); }
    public string CustomExtensions { get => customExtensions; set => Set(ref customExtensions, value ?? ""); }
    public bool OnboardingDone { get => onboardingDone; set => Set(ref onboardingDone, value); }
    public bool OrganizeByCategory { get => organizeByCategory; set => Set(ref organizeByCategory, value); }
    /// When Windows starts the app at sign-in, stay in the notification area without opening the window.
    public bool StartInTray { get => startInTray; set => Set(ref startInTray, value); }
    /// The user chose “Don't ask again” on the deno install prompt for YouTube downloads.
    public bool DenoPromptSuppressed { get => denoPromptSuppressed; set => Set(ref denoPromptSuppressed, value); }

    /// Before a video download starts, list the resolutions the video offers and let the user pick one.
    public bool AskVideoQuality { get => askVideoQuality; set => Set(ref askVideoQuality, value); }

    /// Ids of the Chromium extensions that have talked to the bridge; they may call the native host that starts the app.
    public List<string> ChromiumExtensionIds { get => chromiumExtensionIds; set => Set(ref chromiumExtensionIds, value ?? []); }

    /// Sites whose downloads stay in the browser (keys from Sites.Key; subdomains match too).
    public List<string> BrowserOnlySites { get => browserOnlySites; set => Set(ref browserOnlySites, Sites.With(value, null, false)); }

    public void SetBrowserOnly(string? hostOrUrl, bool on) => BrowserOnlySites = Sites.With(browserOnlySites, hostOrUrl, on);

    /// The extension version each browser (bridge id: chrome, edge, opera…) last reported: Settings can tell an extension
    /// that is installed but quiet right now (its browser closed, no page open yet) from one that was never loaded.
    public Dictionary<string, string> ExtensionsSeen { get => extensionsSeen; set => Set(ref extensionsSeen, value ?? new(StringComparer.Ordinal)); }

    /// Written only when the version differs from the one stored (every bridge request calls this).
    public void NoteExtensionSeen(string browser, string version)
    {
        if (extensionsSeen.TryGetValue(browser, out var known) && known == version) return;
        ExtensionsSeen = new Dictionary<string, string>(extensionsSeen, StringComparer.Ordinal) { [browser] = version };
    }

    /// Servers that accepted fewer connections than asked for, by site key (Sites.Key): the next download from there starts with that many.
    public Dictionary<string, HostLimit> HostLimits { get => hostLimits; set => Set(ref hostLimits, value ?? new(StringComparer.Ordinal)); }

    public const int MaxHostLimits = 200;
    public static readonly TimeSpan HostLimitLifetime = TimeSpan.FromDays(7);

    /// The remembered connection count for this host (or URL), or null when there is none or it has expired.
    public int? ConnectionLimit(string? hostOrUrl, DateTime nowUtc)
    {
        var key = Sites.Key(hostOrUrl);
        if (key != null && hostLimits.TryGetValue(key, out var limit) && limit != null && limit.Until > nowUtc)
            return limit.Connections;
        return null;
    }

    /// Remembers that this host accepted `connections` (clamped to 1..16) for HostLimitLifetime from nowUtc.
    /// A lower count already remembered and not yet expired is kept (its Until is extended); a higher one is replaced.
    /// Expired entries are dropped. When more than MaxHostLimits entries remain, the ones with the earliest Until go.
    /// An invalid host does nothing. Always assigns a NEW dictionary to HostLimits (Set compares references, so the file is saved).
    public void RememberConnections(string? hostOrUrl, int connections, DateTime nowUtc)
    {
        var key = Sites.Key(hostOrUrl);
        if (key == null) return;

        var clamped = Math.Clamp(connections, 1, 16);
        var until = nowUtc + HostLimitLifetime;

        var next = new Dictionary<string, HostLimit>(StringComparer.Ordinal);
        foreach (var (k, v) in hostLimits)
        {
            if (v != null && v.Until > nowUtc && !string.Equals(k, key, StringComparison.Ordinal))
                next[k] = v;
        }

        if (hostLimits.TryGetValue(key, out var existing) && existing != null && existing.Until > nowUtc)
            clamped = Math.Min(existing.Connections, clamped);

        next[key] = new HostLimit(clamped, until);

        if (next.Count > MaxHostLimits)
        {
            var toRemove = next.OrderBy(kv => kv.Value.Until)
                               .Take(next.Count - MaxHostLimits)
                               .Select(kv => kv.Key)
                               .ToList();
            foreach (var k in toRemove)
                next.Remove(k);
        }

        HostLimits = next;
    }

    /// A file handed over by the browser is shown first (name, size, where it goes) and starts when the user says so.
    public bool AskBeforeDownload { get => askBeforeDownload; set => Set(ref askBeforeDownload, value); }

    /// One budget shared by every connection of every download, in KB/s.
    public bool SpeedLimitEnabled { get => speedLimitEnabled; set => Set(ref speedLimitEnabled, value); }
    public int SpeedLimitKb { get => speedLimitKb; set => Set(ref speedLimitKb, Math.Clamp(value, MinSpeedLimitKb, MaxSpeedLimitKb)); }
    public const int MinSpeedLimitKb = 16, MaxSpeedLimitKb = 1_000_000;
    /// 0 = no limit.
    [JsonIgnore] public long SpeedLimitBytes => speedLimitEnabled ? speedLimitKb * 1024L : 0;

    /// Subtitles are saved next to a video as .srt files. The quality question can switch them for one video only.
    public bool VideoSubtitles { get => videoSubtitles; set => Set(ref videoSubtitles, value); }
    /// yt-dlp language list ("tr,en").
    public string SubtitleLanguages { get => subtitleLanguages; set => Set(ref subtitleLanguages, CleanLanguages(value)); }
    public const string DefaultSubtitleLanguages = "tr,en";

    /// Letters, digits and the few signs yt-dlp's language patterns use; anything else is dropped.
    internal static string CleanLanguages(string? value)
    {
        var parts = (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new string([.. p.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '*')]).TrimStart('-'))
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        return parts.Count == 0 ? DefaultSubtitleLanguages : string.Join(",", parts);
    }

    public bool CheckForUpdates { get => checkForUpdates; set => Set(ref checkForUpdates, value); }
    /// "Skip This Version": this release is not offered again (a later one is).
    public string SkippedVersion { get => skippedVersion; set => Set(ref skippedVersion, value ?? ""); }
    public DateTime LastUpdateCheck { get => lastUpdateCheck; set => Set(ref lastUpdateCheck, value); }

    /// "system" (follow Windows), "light" or "dark".
    public string Theme { get => theme; set => Set(ref theme, value is "light" or "dark" ? value : "system"); }

    /// "" = Windows' accent colour, otherwise one of AccentColors.
    public string AccentColor { get => accentColor; set => Set(ref accentColor, AccentColors.Any(a => a.Hex == value) ? value! : ""); }

    /// Windows' own accent swatches, so a picked colour looks the way it would if it were picked in Windows.
    public static readonly (string Hex, string Name)[] AccentColors =
    [
        ("#0078D4", "Blue"), ("#038387", "Teal"), ("#107C10", "Green"), ("#8764B8", "Purple"), ("#CA5010", "Orange"), ("#E3008C", "Pink"),
    ];

    /// "Start with Windows" as the user last chose it (the Run entry itself is in the registry); null = not known yet.
    public bool? StartWithWindows { get => startWithWindows; set => Set(ref startWithWindows, value); }

    /// All file extensions (lowercase, no dot) the app should capture.
    [JsonIgnore] public List<string> CaptureExtensions => CaptureTypes.Extensions(captureGroupIds, customExtensions);

    public void SetCaptureGroup(string id, bool on)
    {
        var list = new List<string>(captureGroupIds);
        if (on && !list.Contains(id)) list.Add(id);
        if (!on) list.Remove(id);
        list.Sort(StringComparer.Ordinal);
        CaptureGroupIds = list;
    }

    [JsonIgnore] public string EffectiveArguments => string.Join(" ", YtdlpFormatArguments);

    /// yt-dlp arguments for the format/quality chosen in Settings.
    [JsonIgnore] public List<string> YtdlpFormatArguments => BuildFormatArguments(videoFormat, videoContainer, videoMaxHeight);

    /// `maxHeight`: 0 = best, a positive number = resolution cap, -1 = audio only (M4A), -2 = audio only (MP3).
    /// `preferred` (e.g. "137+140") is tried first; the general expression stays behind it as the fallback.
    public static List<string> BuildFormatArguments(string customFormat, string container, int maxHeight, string? preferred = null)
    {
        var custom = customFormat.Trim();
        if (custom.Length > 0) return ["-f", custom, "--merge-output-format", container == "original" ? "mkv" : container];
        // The site's own M4A track when there is one: taking the best track of any kind would mean re-encoding it.
        if (maxHeight == -1) return ["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a", "--audio-quality", "0"];
        if (maxHeight == -2) return ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0"];
        var h = maxHeight > 0 ? $"[height<={maxHeight}]" : "";
        var first = string.IsNullOrWhiteSpace(preferred) ? "" : preferred.Trim() + "/";
        return container switch
        {
            // Prefer H.264 + AAC so the file plays everywhere; fall back to anything and merge into mp4.
            "mp4" => ["-f", $"{first}bv*{h}[vcodec^=avc1]+ba[acodec^=mp4a]/bv*{h}[ext=mp4]+ba[ext=m4a]/bv*{h}+ba/b{h}/b", "--merge-output-format", "mp4", "--remux-video", "mp4"],
            "webm" => ["-f", $"{first}bv*{h}[ext=webm]+ba[ext=webm]/bv*{h}+ba/b{h}/b", "--merge-output-format", "webm"],
            "mkv" => ["-f", $"{first}bv*{h}+ba/b{h}/b", "--merge-output-format", "mkv"],
            _ => ["-f", $"{first}bv*{h}+ba/b{h}/b"],
        };
    }

    // ---- Persistence ----

    private string? path;
    private bool loading;
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public static AppSettings Load(string? file = null)
    {
        file ??= AppPaths.SettingsFile;
        AppSettings? s = null;
        try
        {
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                var fresh = new AppSettings { loading = true };
                using var doc = JsonDocument.Parse(text);
                fresh.Apply(doc.RootElement);
                fresh.loading = false;
                s = fresh;
            }
        }
        catch { }
        s ??= new AppSettings();
        s.path = file;
        return s;
    }

    /// Reads known keys one by one, so an unknown or malformed value never throws away the rest.
    private void Apply(JsonElement root)
    {
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (!p.CanWrite || p.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Length > 0) continue;
            if (!root.TryGetProperty(p.Name, out var el)) continue;
            try { p.SetValue(this, el.Deserialize(p.PropertyType)); } catch { }
        }
    }

    public void Save()
    {
        if (path == null || loading) return;
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, json));
            File.Move(tmp, path, true);
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Save();
        Raise(name);
        return true;
    }
}

/// How many parallel connections a server accepted, and until when that is trusted.
public sealed record HostLimit(int Connections, DateTime Until);
