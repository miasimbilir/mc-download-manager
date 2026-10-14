using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MCDM;

public sealed class NewDownloadRequest(Uri url)
{
    public Uri Url { get; } = url;
    public string? FileName { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? CookieHeader { get; set; }
    public List<BrowserCookie>? Cookies { get; set; }
    public DownloadKind Kind { get; set; } = DownloadKind.file;
    public Dictionary<string, string> ExtraHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int? Segments { get; set; }
    /// Handed over by the browser rather than typed or pasted into the app: the user has not seen it yet,
    /// so a file is shown first ("download this?") and an address that is already in the list is not added twice.
    public bool Confirm { get; set; }
    /// The user asked for another copy of something that was already downloaded.
    public bool AllowDuplicate { get; set; }
}

public enum DownloadDecision { Start, Later, Cancel }

/// The answer to "download this file?". `Directory` and `FileName` are null unless the user changed them.
public sealed record DownloadAnswer(DownloadDecision Decision, string? Directory = null, string? FileName = null, bool DontAskAgain = false)
{
    public static DownloadAnswer Start { get; } = new(DownloadDecision.Start);
}

public enum DuplicateDecision { Open, Reveal, DownloadAgain, Cancel }

/// What to do with the computer once nothing is left to download. Chosen for one run of the queue.
public enum QueueEndAction { None, Sleep, Shutdown }

public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, bool Secure, double ExpirationDate);

/// What the store needs from the UI. The app implements it; headless use gets `NoUi`.
public interface IStoreUi
{
    void ShowDetail(Guid id);
    void DetailCompleted(Guid id, bool autoClose);
    void NotifyCompleted(DownloadItem item);
    /// Asks once whether to install deno. Returns (install, don't ask again).
    Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync();
    /// Lets the user pick a resolution (or audio only) for a video. null = the user cancelled the download.
    Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info);
    /// Shows a file the browser handed over (its size and the server's name for it arrive on the item a
    /// moment later) and asks whether and where to download it. Without a UI: download it as it is.
    Task<DownloadAnswer> AskDownloadAsync(DownloadItem item) => Task.FromResult(DownloadAnswer.Start);
    /// The browser handed over something that was downloaded before and is still on disk.
    /// Without a UI: download it again (it gets a numbered name).
    Task<DuplicateDecision> AskDuplicateAsync(DownloadItem existing) => Task.FromResult(DuplicateDecision.DownloadAgain);
    /// The last download ended and the user asked for the computer to sleep or shut down after that.
    void QueueFinished(QueueEndAction action) { }
}

public sealed class NoUi : IStoreUi
{
    public void ShowDetail(Guid id) { }
    public void DetailCompleted(Guid id, bool autoClose) { }
    public void NotifyCompleted(DownloadItem item) { }
    public Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync() => Task.FromResult((false, false));
    public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) => Task.FromResult<VideoFormatAnswer?>(VideoFormatAnswer.Default);
}

/// State of all downloads, the queue and persistence. Used on the main thread only.
public sealed class DownloadStore
{
    public ObservableCollection<DownloadItem> Items { get; } = [];
    public AppSettings Settings { get; }
    public IStoreUi Ui { get; set; } = new NoUi();

    /// Raised after anything changed (twice a second at most while downloads run): counts, speeds, states.
    public event Action? Changed;

    private readonly Dictionary<Guid, SegmentedDownloader> downloaders = [];
    private readonly Dictionary<Guid, Process> videoProcesses = [];
    /// yt-dlp downloads several files (video, audio): bytes completed in earlier phases + approx total.
    private readonly Dictionary<Guid, (long Base, long LastDownloaded, long ApproxTotal)> videoPhase = [];
    /// YouTube downloads waiting for the deno install prompt / install to finish.
    private readonly List<Guid> denoWaiters = [];
    private bool denoPrompting;
    private bool denoDeclinedThisLaunch;
    /// Video downloads that are retried without cookies after YouTube rejected the signed-in client.
    private readonly HashSet<Guid> cookielessVideos = [];
    /// Videos whose formats are being listed, or whose quality question is on screen.
    private readonly Dictionary<Guid, CancellationTokenSource> formatQuestions = [];
    /// Videos that have been through the quality question in this run (answered, or nothing to choose from).
    private readonly HashSet<Guid> formatDecided = [];
    /// Files whose "download this?" question is on screen.
    private readonly Dictionary<Guid, CancellationTokenSource> confirmations = [];
    /// Finished downloads whose "you already have this" question is on screen.
    private readonly HashSet<Guid> duplicateQuestions = [];
    /// Playlists: which video yt-dlp is at.
    private readonly Dictionary<Guid, (int Index, int Count)> playlistPosition = [];
    private QueueEndAction whenDone;
    private readonly Dictionary<Guid, (long Bytes, long Ticks)> samples = [];
    private readonly Timer timer;
    private readonly string storePath;
    private int tickCount;
    private bool saveScheduled;
    private bool dirty;

    public DownloadStore(AppSettings settings, string? storePath = null)
    {
        Settings = settings;
        this.storePath = storePath ?? AppPaths.DownloadsFile;
        Load();
        RateLimiter.BytesPerSecond = settings.SpeedLimitBytes;
        settings.PropertyChanged += OnSettingsChanged;
        timer = new Timer(_ => MainThread.Post(Tick), null, 500, 500);
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.SpeedLimitEnabled) or nameof(AppSettings.SpeedLimitKb))) return;
        RateLimiter.BytesPerSecond = Settings.SpeedLimitBytes;
        RaiseChanged();
    }

    /// One-shot: what happens to the computer when the last download has ended. Back to None once it fired.
    public QueueEndAction WhenDone
    {
        get => whenDone;
        set
        {
            if (whenDone == value) return;
            whenDone = value;
            RaiseChanged();
        }
    }

    // ---- Queries ----

    /// Final folder for an item: base folder, plus a category subfolder when enabled; a folder the user
    /// picked for this download is taken as it is.
    public string DestinationDirectory(DownloadItem item) =>
        Settings.OrganizeByCategory && !item.ExactDirectory ? Path.Combine(item.Directory, item.Category.Label()) : item.Directory;

    /// Where `item` would go under another name (the category follows the extension).
    public string DestinationDirectory(DownloadItem item, string fileName) =>
        Settings.OrganizeByCategory && !item.ExactDirectory ? Path.Combine(item.Directory, FileCategories.Detect(fileName, item.Kind).Label()) : item.Directory;

    public string DestinationPath(DownloadItem item) => Path.Combine(DestinationDirectory(item), item.FileName);

    public int ActiveCount => Items.Count(i => i.State.IsActive());
    public int UnfinishedCount => Items.Count(i => !i.IsFinished);
    public double TotalSpeed => Items.Where(i => i.State == DownloadState.downloading).Sum(i => i.Speed);

    /// Byte-weighted progress of active downloads with a known size (null when nothing measurable is running).
    public double? OverallProgress
    {
        get
        {
            long total = 0, received = 0;
            foreach (var i in Items)
            {
                if (!i.State.IsActive() || i.TotalBytes <= 0) continue;
                total += i.TotalBytes;
                received += Math.Min(i.ReceivedBytes, i.TotalBytes);
            }
            return total > 0 ? (double)received / total : null;
        }
    }

    public DownloadItem? Find(Guid id) => Items.FirstOrDefault(i => i.Id == id);

    private void Update(Guid id, Action<DownloadItem> mutate)
    {
        var item = Find(id);
        if (item == null) return;
        mutate(item);
        item.Touch();
        dirty = true;
    }

    private void RaiseChanged()
    {
        dirty = false;
        Changed?.Invoke();
    }

    // ---- Adding ----

    public DownloadItem Add(NewDownloadRequest req)
    {
        var headers = new Dictionary<string, string>(req.ExtraHeaders, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(req.Referer)) headers["Referer"] = req.Referer;
        if (!string.IsNullOrEmpty(req.UserAgent)) headers["User-Agent"] = req.UserAgent;
        if (!string.IsNullOrEmpty(req.CookieHeader)) headers["Cookie"] = req.CookieHeader;
        if (!headers.ContainsKey("User-Agent")) headers["User-Agent"] = DefaultUserAgent;

        var fromBrowser = req.Confirm && req.Kind == DownloadKind.file;
        if (fromBrowser && !req.AllowDuplicate && AlreadyKnown(req, headers) is { } known) return known;

        var name = string.IsNullOrEmpty(req.FileName) ? FileNames.FromUrl(req.Url) : req.FileName;
        if (req.Kind == DownloadKind.video) name = req.Url.Host.Length > 0 ? req.Url.Host : "video";
        name = FileNames.Sanitize(name);

        var item = new DownloadItem
        {
            Url = req.Url.AbsoluteUri,
            FileName = name,
            Directory = Settings.DownloadDirectory,
            Kind = req.Kind,
            Headers = headers,
            RequestedSegments = req.Segments ?? 0,
        };
        if (req.Kind == DownloadKind.video && req.Cookies is { Count: > 0 })
            item.CookieFile = WriteCookieFile(req.Cookies, item.Id);
        try { Directory.CreateDirectory(item.Directory); } catch { }
        Items.Insert(0, item);
        ScheduleSave();
        if (fromBrowser && Settings.AskBeforeDownload) AskBeforeStart(item);
        else
        {
            if (Settings.AutoStart) PumpQueue();
            if (Settings.ShowDetailOnStart) Ui.ShowDetail(item.Id);
        }
        RaiseChanged();
        return item;
    }

    /// The browser handed over an address that is already in the list. An unfinished download is shown
    /// instead of being added a second time; for a finished one whose file is still there the user is asked.
    /// Returns the existing item, or null when the request should be added as usual.
    private DownloadItem? AlreadyKnown(NewDownloadRequest req, Dictionary<string, string> headers)
    {
        var url = req.Url.AbsoluteUri;
        foreach (var old in Items.Where(i => i.Kind == DownloadKind.file && i.Url == url).ToList())
        {
            switch (old.State)
            {
                case DownloadState.cancelled:
                    Remove(old.Id);                     // the new request takes its place
                    break;
                case DownloadState.completed:
                    if (!FileNames.Exists(old.FinalPath)) break;
                    if (duplicateQuestions.Add(old.Id)) AskAboutDuplicate(old, req);
                    return old;
                case DownloadState.failed:
                    // Try again with what the browser sends now (an expired sign-in is a common reason for the failure).
                    old.Headers = headers;
                    Resume(old.Id);
                    if (Settings.ShowDetailOnStart) Ui.ShowDetail(old.Id);
                    return old;
                default:
                    if (old.State == DownloadState.paused) old.Headers = headers;
                    if (!old.AwaitingConfirmation) Ui.ShowDetail(old.Id);
                    return old;
            }
        }
        return null;
    }

    private void AskAboutDuplicate(DownloadItem old, NewDownloadRequest req)
    {
        var id = old.Id;
        Ui.AskDuplicateAsync(old).ContinueWith(t => MainThread.Post(() =>
        {
            duplicateQuestions.Remove(id);
            switch (t.IsCompletedSuccessfully ? t.Result : DuplicateDecision.Cancel)
            {
                case DuplicateDecision.Open: Open(old); break;
                case DuplicateDecision.Reveal: Reveal(old); break;
                case DuplicateDecision.DownloadAgain:
                    // The user has just asked for it: no second question, and it starts now.
                    req.AllowDuplicate = true;
                    req.Confirm = false;
                    var again = Add(req);
                    if (again.State == DownloadState.queued) StartNow(again.Id);
                    break;
            }
        }));
    }

    /// A file from the browser waits in the list until the user has seen what it is and where it goes.
    private void AskBeforeStart(DownloadItem item)
    {
        var id = item.Id;
        var cts = new CancellationTokenSource();
        confirmations[id] = cts;
        item.AwaitingConfirmation = true;
        bool Waiting() => confirmations.TryGetValue(id, out var c) && ReferenceEquals(c, cts);

        // Size and the server's name for the file, for the question. The download asks again when it starts.
        var url = item.Uri;
        var headers = new Dictionary<string, string>(item.Headers, StringComparer.OrdinalIgnoreCase);
        Task.Run(async () =>
        {
            ProbeResult? probe = null;
            try { probe = await SegmentedDownloader.ProbeOnceAsync(url, headers, cts.Token).ConfigureAwait(false); }
            catch { }               // the question works without it; a real problem shows when the download starts
            MainThread.Post(() =>
            {
                if (!Waiting() || Find(id) is not { } current) return;
                if (probe != null) ApplyProbe(current, probe);
                current.Probed = true;
                current.Touch();
                RaiseChanged();
            });
        });

        Ui.AskDownloadAsync(item).ContinueWith(t => MainThread.Post(() =>
        {
            if (!Waiting()) return;         // removed or paused meanwhile, or the app is closing
            StopConfirmation(id);
            var answer = t.IsCompletedSuccessfully ? t.Result : DownloadAnswer.Start;
            if (answer.DontAskAgain) Settings.AskBeforeDownload = false;
            if (answer.Decision == DownloadDecision.Cancel) { Remove(id); return; }
            Update(id, i =>
            {
                if (!string.IsNullOrWhiteSpace(answer.FileName) && FileNames.Sanitize(answer.FileName) is var name && name != i.FileName)
                {
                    i.FileName = name;
                    i.NameLocked = true;
                }
                if (!string.IsNullOrWhiteSpace(answer.Directory))
                {
                    i.Directory = answer.Directory;
                    i.ExactDirectory = true;
                }
                if (answer.Decision == DownloadDecision.Later) i.State = DownloadState.paused;
            });
            ScheduleSave();
            if (answer.Decision == DownloadDecision.Start && Find(id) is { } go)
            {
                // The user has just said "download": it starts now, whatever the queue settings are.
                StartItem(go);
                if (Settings.ShowDetailOnStart) Ui.ShowDetail(id);
            }
            RaiseChanged();
        }));
    }

    private void StopConfirmation(Guid id)
    {
        if (!confirmations.Remove(id, out var cts)) return;
        try { cts.Cancel(); } catch { }
        if (Find(id) is { } item) item.AwaitingConfirmation = false;
    }

    public int AddUrls(string text, DownloadKind kind, int? segments = null)
    {
        var count = 0;
        foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Links.ParseHttp(line.Trim()) is not { } url) continue;
            Add(new NewDownloadRequest(url) { Kind = kind, Segments = segments });
            count++;
        }
        return count;
    }

    // ---- Queue ----

    public void PumpQueue()
    {
        var slots = Math.Max(0, Settings.MaxConcurrent - ActiveCount);
        foreach (var item in Items.Reverse().ToList())
        {
            if (slots <= 0) break;
            if (item.State != DownloadState.queued || item.AwaitingConfirmation) continue;
            StartItem(item);
            slots--;
        }
    }

    /// "Download" on something that has not started yet: starts it now, ahead of the queue and its limit.
    public void StartNow(Guid id)
    {
        var item = Find(id);
        if (item == null) return;
        StopConfirmation(id);       // "Download" in the list answers the open question as well
        if (item.State is DownloadState.paused or DownloadState.failed or DownloadState.cancelled)
        {
            Update(id, i => { i.State = DownloadState.queued; i.ErrorMessage = null; });
            cookielessVideos.Remove(id);
            formatDecided.Remove(id);
        }
        if (item.State != DownloadState.queued) return;
        StartItem(item);
        RaiseChanged();
    }

    private void StartItem(DownloadItem item)
    {
        if (item.Kind == DownloadKind.video) StartVideo(item); else StartFile(item);
    }

    private void StartFile(DownloadItem item)
    {
        var id = item.Id;
        var resumable = item.SupportsRanges && item.TotalBytes > 0 && item.Segments.Count > 0 && File.Exists(item.PartPath);
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = item.Uri,
            Headers = item.Headers,
            PartPath = item.PartPath,
            SegmentCount = item.RequestedSegments > 0 ? item.RequestedSegments : Settings.SegmentCount,
            ResumeSegments = resumable ? [.. item.Segments] : [],
            ResumeTotal = resumable ? item.TotalBytes : -1,
        });
        bool Current() => downloaders.TryGetValue(id, out var d) && ReferenceEquals(d, dl);
        dl.OnProbe = probe => MainThread.Post(() => { if (Current()) HandleProbe(id, probe); });
        dl.OnProgress = (received, segs) => MainThread.Post(() =>
        {
            if (!Current()) return;
            Update(id, i =>
            {
                i.ReceivedBytes = received;
                i.Segments = [.. segs];
                if (i.State == DownloadState.probing) i.State = DownloadState.downloading;
            });
        });
        dl.OnFinish = error => MainThread.Post(() => { if (Current()) HandleFinish(id, error); });
        downloaders[id] = dl;
        Update(id, i =>
        {
            i.State = resumable ? DownloadState.downloading : DownloadState.probing;
            i.ErrorMessage = null;
            i.Speed = 0;
            if (!resumable) { i.ReceivedBytes = 0; i.Segments = []; }
        });
        dl.Start();
    }

    private void HandleProbe(Guid id, ProbeResult p)
    {
        // Paused or cancelled just before the answer arrived: the row must not come back to life.
        if (Find(id) is not { State: DownloadState.probing }) return;
        Update(id, item =>
        {
            ApplyProbe(item, p);
            item.State = DownloadState.downloading;
        });
    }

    /// What the server says about the file: its size and, unless the user named it, its name.
    private static void ApplyProbe(DownloadItem item, ProbeResult p)
    {
        item.Probed = true;
        item.TotalBytes = p.TotalBytes;
        item.SupportsRanges = p.SupportsRanges;
        if (item.NameLocked) return;
        if (!string.IsNullOrEmpty(p.SuggestedFileName))
        {
            item.FileName = FileNames.Sanitize(p.SuggestedFileName);
        }
        else if (p.FinalUrl != null && p.FinalUrl != item.Uri && item.FileName == FileNames.Sanitize(FileNames.FromUrl(item.Uri)))
        {
            var candidate = FileNames.FromUrl(p.FinalUrl);
            if (FileNames.Extension(candidate).Length > 0) item.FileName = FileNames.Sanitize(candidate);
        }
        if (FileNames.Extension(item.FileName).Length == 0 && FileNames.ExtensionForMime(p.ContentType) is { } ext)
            item.FileName += "." + ext;
    }

    private void HandleFinish(Guid id, Exception? error)
    {
        downloaders.Remove(id);
        var item = Find(id);
        if (item == null) return;
        if (error == null)
        {
            try
            {
                var destDir = DestinationDirectory(item);
                Directory.CreateDirectory(destDir);
                var dest = FileNames.UniqueDestination(Path.Combine(destDir, item.FileName));
                File.Move(item.PartPath, dest);
                Shell.MarkAsDownloaded(dest, item.Url, item.Headers.GetValueOrDefault("Referer"));
                item.FileName = Path.GetFileName(dest);
                item.FinalPath = dest;
                item.State = DownloadState.completed;
                item.CompletedAt = DateTime.UtcNow;
                item.Speed = 0;
                if (item.TotalBytes <= 0) item.TotalBytes = item.ReceivedBytes;
                item.Touch();
                if (Settings.NotifyOnComplete) Ui.NotifyCompleted(item);
                Ui.DetailCompleted(id, Settings.AutoCloseDetail);
            }
            catch (Exception e)
            {
                item.State = DownloadState.failed;
                item.ErrorMessage = e.Message;
                item.Speed = 0;
                item.Touch();
            }
        }
        else if (item.State is not (DownloadState.paused or DownloadState.cancelled))
        {
            item.State = DownloadState.failed;
            item.ErrorMessage = Describe(error);
            item.Speed = 0;
            item.Touch();
        }
        ScheduleSave();
        PumpQueue();
        if (item.State is DownloadState.completed or DownloadState.failed) QueueMayHaveEnded();
        RaiseChanged();
    }

    /// Called when a download ended by itself (finished or failed, not paused by the user): if that was
    /// the last one and the user asked for it, the computer goes to sleep or shuts down.
    private void QueueMayHaveEnded()
    {
        if (whenDone == QueueEndAction.None) return;
        if (Items.Any(i => i.State.IsActive() || i.State == DownloadState.queued)) return;
        var action = whenDone;
        whenDone = QueueEndAction.None;
        Save();
        Ui.QueueFinished(action);
    }

    private static string Describe(Exception e)
    {
        // HttpRequestException usually wraps the socket error that actually says what went wrong.
        var inner = e;
        while (inner.InnerException != null && inner is HttpRequestException or AggregateException) inner = inner.InnerException;
        return inner.Message;
    }

    // ---- Video (yt-dlp) ----

    private void StartVideo(DownloadItem item)
    {
        var id = item.Id;
        var exe = VideoDownloader.LocateYtDlp(Settings.YtdlpPath);
        if (exe == null)
        {
            Update(id, i =>
            {
                i.State = DownloadState.failed;
                i.ErrorMessage = Loc.T("yt-dlp not found. Use “Install yt-dlp” in Settings ▸ Video.");
            });
            MainThread.Post(() => { PumpQueue(); QueueMayHaveEnded(); });
            return;
        }
        if (VideoDownloader.IsYouTube(item.Uri) && VideoDownloader.LocateDeno() == null
            && !Settings.DenoPromptSuppressed && !denoDeclinedThisLaunch)
        {
            Update(id, i =>
            {
                i.State = DownloadState.downloading; i.ErrorMessage = null; i.Speed = 0;
                i.Stage = Loc.T("Waiting for the JavaScript runtime (deno)…");
            });
            if (!denoWaiters.Contains(id)) denoWaiters.Add(id);
            if (!denoPrompting)
            {
                denoPrompting = true;
                MainThread.Post(PromptForDeno);
            }
            return;
        }
        if (item.FormatArgs == null && Settings.AskVideoQuality && Settings.VideoFormat.Trim().Length == 0 && !formatDecided.Contains(id))
        {
            AskFormat(item, exe);
            return;
        }
        var useCookies = !cookielessVideos.Contains(id);
        // An address that names nothing but a playlist is one, asked or not.
        var playlist = item.Playlist || Links.Playlist(item.Uri) == PlaylistKind.Only;
        Update(id, i =>
        {
            i.State = DownloadState.downloading; i.ErrorMessage = null; i.ReceivedBytes = 0; i.Speed = 0; i.TotalBytes = -1;
            i.Stage = Loc.T("Fetching video information…");
            i.Playlist = playlist;
            i.Note = null;
            i.WorkingPath = null;
            if (useCookies) i.Log = "";     // keep the failed attempt's output visible above the retry
        });
        var outDir = DestinationDirectory(item);
        try { Directory.CreateDirectory(outDir); } catch { }
        var options = new VideoOptions
        {
            QualityTag = item.QualityTag,
            Playlist = playlist,
            SubtitleLanguages = item.Subtitles ?? Settings.VideoSubtitles ? Settings.SubtitleLanguages : null,
            LimitKb = Settings.SpeedLimitEnabled ? Settings.SpeedLimitKb : 0,
        };
        Process? started = null;
        bool Current() => started != null && videoProcesses.TryGetValue(id, out var p) && ReferenceEquals(p, started);
        videoPhase[id] = (0, 0, -1);
        playlistPosition.Remove(id);
        string Staged(string stage) => playlistPosition.TryGetValue(id, out var at) ? $"{at.Index}/{at.Count} · {stage}" : stage;
        started = VideoDownloader.Run(exe, item.Uri, outDir, item.FormatArgs ?? Settings.YtdlpFormatArguments, item.Headers, item.CookieFile, useCookies,
            onProgress: p => MainThread.Post(() =>
            {
                if (!Current()) return;
                var ph = videoPhase.GetValueOrDefault(id, (0, 0, -1));
                if (p.Downloaded < ph.LastDownloaded) ph.Base += ph.LastDownloaded;     // a new file started
                ph.LastDownloaded = p.Downloaded;
                videoPhase[id] = ph;
                var received = ph.Base + p.Downloaded;
                Update(id, i =>
                {
                    i.ReceivedBytes = received;
                    var phaseTotal = p.Total > 0 ? ph.Base + p.Total : -1;
                    i.TotalBytes = Math.Max(ph.ApproxTotal, Math.Max(phaseTotal, received));
                    i.Speed = p.Speed;
                });
            }),
            onLine: line => MainThread.Post(() =>
            {
                if (!Current()) return;
                Update(id, i =>
                {
                    var log = i.Log + line + "\n";
                    i.Log = log.Length > 6000 ? log[^6000..] : log;
                    if (VideoDownloader.PlaylistPosition(line) is { } at)
                    {
                        // The next video of the list: its sizes start from nothing again.
                        playlistPosition[id] = at;
                        videoPhase[id] = (0, 0, -1);
                        i.ReceivedBytes = 0; i.TotalBytes = -1; i.Speed = 0;
                        i.Stage = Staged(Loc.T("Fetching video information…"));
                    }
                    if (VideoDownloader.Stage(line) is { } st) i.Stage = Staged(st);
                    if (line.StartsWith("MCFMT|", StringComparison.Ordinal))
                    {
                        var parts = line.Split('|');
                        if (parts.Length > 5 && VideoDownloader.ParseNumber(parts[5]) is > 0 and var n)
                        {
                            var ph = videoPhase.GetValueOrDefault(id, (0, 0, -1));
                            ph.ApproxTotal = (long)n;
                            videoPhase[id] = ph;
                            if (i.TotalBytes < (long)n) i.TotalBytes = (long)n;
                        }
                    }
                    var d = line.IndexOf("Destination: ", StringComparison.Ordinal);
                    if (d >= 0)
                    {
                        // Show the final title; strip yt-dlp's intermediate ".f137" format suffix.
                        var path = line[(d + "Destination: ".Length)..].Trim();
                        var name = Path.GetFileName(path);
                        if (name.Length > 0)
                        {
                            i.WorkingPath = Path.Combine(Path.GetDirectoryName(path) ?? "", VideoDownloader.StripFormatSuffix(name));
                            // A playlist keeps the list's name in the row; its folder says so as soon as it exists.
                            if (!playlist) i.FileName = VideoDownloader.StripFormatSuffix(name);
                            else if (Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } folder && Path.IsPathFullyQualified(path)) i.FileName = folder;
                        }
                    }
                    if (!playlist && line.StartsWith("[Merger] Merging formats into \"", StringComparison.Ordinal))
                    {
                        int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                        if (q1 >= 0 && q2 > q1) i.FileName = Path.GetFileName(line[(q1 + 1)..q2]);
                    }
                });
            }),
            onFinish: result => MainThread.Post(() => { if (Current() || started == null) HandleVideoFinish(id, result); }),
            options);
        if (started != null) videoProcesses[id] = started;
    }

    /// Lists the formats the video offers and asks which one to download. The download itself starts
    /// (through StartVideo again) once the question is answered.
    private void AskFormat(DownloadItem item, string exe)
    {
        var id = item.Id;
        if (formatQuestions.ContainsKey(id)) return;
        var cts = new CancellationTokenSource();
        formatQuestions[id] = cts;
        Update(id, i =>
        {
            i.State = DownloadState.downloading; i.ErrorMessage = null; i.Speed = 0;
            i.Stage = Loc.T("Fetching quality options…");
        });
        var useCookies = !cookielessVideos.Contains(id);
        var url = item.Uri;
        var headers = new Dictionary<string, string>(item.Headers, StringComparer.OrdinalIgnoreCase);
        var cookieFile = item.CookieFile;
        var container = Settings.VideoContainer;

        // The question is still this download's own: not paused, cancelled, removed or restarted meanwhile.
        bool Current() => formatQuestions.TryGetValue(id, out var c) && ReferenceEquals(c, cts) && Find(id) is { State: DownloadState.downloading };
        void Close() { if (formatQuestions.TryGetValue(id, out var c) && ReferenceEquals(c, cts)) formatQuestions.Remove(id); }

        Task.Run(async () =>
        {
            var (info, error) = await VideoFormats.ProbeAsync(exe, url, headers, cookieFile, useCookies, container, cts.Token).ConfigureAwait(false);
            MainThread.Post(() =>
            {
                if (!Current()) { Close(); return; }
                var current = Find(id)!;
                if (info == null)
                {
                    Close();
                    if (useCookies && VideoDownloader.IsYouTube(url) && VideoDownloader.IsSignedInClientFailure(error ?? "")
                        && VideoDownloader.HasCookies(cookieFile, url.Host))
                    {
                        cookielessVideos.Add(id);
                        Update(id, i => i.Log += Loc.T("→ YouTube rejected the signed-in request; retrying without cookies…") + "\n");
                        AskFormat(current, exe);
                        return;
                    }
                    // Could not list the formats: download with the defaults; if the page really is broken, that run says why.
                    formatDecided.Add(id);
                    StartVideo(current);
                    if (!string.IsNullOrEmpty(error)) Update(id, i => i.Log = error + "\n" + i.Log);
                    return;
                }
                if (info.Choices.Count == 0)
                {
                    Close();
                    formatDecided.Add(id);
                    StartVideo(current);
                    return;
                }
                Update(id, i =>
                {
                    var title = info.Playlist is { Only: true, Title.Length: > 0 } list ? list.Title : info.Title;
                    if (title.Length > 0) i.FileName = FileNames.Sanitize(title);
                    i.Stage = Loc.T("Waiting for the quality choice…");
                });
                Ui.AskVideoFormatAsync(current, info).ContinueWith(t => MainThread.Post(() =>
                {
                    var mine = Current();
                    Close();
                    if (!mine) return;
                    var answer = t.IsCompletedSuccessfully ? t.Result : VideoFormatAnswer.Default;
                    if (answer == null) { Cancel(id); return; }
                    if (answer.Subtitles is { } subtitles) Settings.VideoSubtitles = subtitles;     // the next question starts from this
                    Update(id, i =>
                    {
                        i.Playlist = answer.Playlist;
                        if (answer.Subtitles != null) i.Subtitles = answer.Subtitles;
                        if (!string.IsNullOrWhiteSpace(answer.Directory))
                        {
                            i.Directory = answer.Directory;
                            i.ExactDirectory = true;
                        }
                    });
                    if (answer.MaxHeight != VideoFormatAnswer.UseDefault)
                    {
                        if (answer.Remember)
                        {
                            Settings.VideoMaxHeight = answer.Nominal;
                            Settings.AskVideoQuality = false;
                        }
                        Update(id, i =>
                        {
                            if (answer.Playlist)
                            {
                                // Format ids change from video to video: the list gets the picked height as a general cap.
                                var cap = answer.MaxHeight > 0 ? Math.Max(answer.MaxHeight, answer.Nominal) : answer.MaxHeight;
                                i.FormatArgs = AppSettings.BuildFormatArguments("", Settings.VideoContainer, cap);
                                i.QualityTag = null;
                            }
                            else
                            {
                                i.FormatArgs = AppSettings.BuildFormatArguments("", Settings.VideoContainer, answer.MaxHeight, answer.Selector);
                                i.QualityTag = answer.MaxHeight > 0 ? answer.Nominal + "p" : null;
                            }
                        });
                    }
                    ScheduleSave();
                    formatDecided.Add(id);
                    if (Find(id) is { } again) StartVideo(again);
                }));
            });
        });
    }

    private void StopFormatQuestion(Guid id)
    {
        if (formatQuestions.Remove(id, out var cts)) try { cts.Cancel(); } catch { }
    }

    private void HandleVideoFinish(Guid id, VideoResult result)
    {
        videoProcesses.Remove(id);
        videoPhase.Remove(id);
        var position = playlistPosition.GetValueOrDefault(id);
        playlistPosition.Remove(id);
        var item = Find(id);
        if (item == null || item.State != DownloadState.downloading) return;    // paused or cancelled by the user
        var error = result.Error;
        var files = result.Files.Where(File.Exists).ToList();
        // yt-dlp can end with an error and still have done its job: a playlist with one video that is no
        // longer there, a subtitle the site would not give. What was saved counts.
        if (error == null || files.Count > 0)
        {
            item.State = DownloadState.completed;
            item.CompletedAt = DateTime.UtcNow;
            item.Speed = 0;
            item.Stage = "";
            item.WorkingPath = null;
            if (error != null) item.Log += "→ " + error.Message + "\n";
            foreach (var f in files) Shell.MarkAsDownloaded(f, item.Url, null);
            if (item.Playlist && files.Count > 0)
            {
                // The row stands for the folder the videos went into.
                var folder = Path.GetDirectoryName(files[^1])!;
                item.FinalPath = folder;
                item.FileName = Path.GetFileName(folder);
                long size = 0;
                foreach (var f in files) try { size += new FileInfo(f).Length; } catch { }
                item.TotalBytes = item.ReceivedBytes = size;
                if (position.Count > files.Count) item.Note = Loc.T("{0} of {1} videos saved", files.Count, position.Count);
            }
            else if (files.Count > 0)
            {
                var path = files[^1];
                item.FinalPath = path;
                item.FileName = Path.GetFileName(path);
                try
                {
                    var size = new FileInfo(path).Length;
                    item.TotalBytes = size;
                    item.ReceivedBytes = size;
                }
                catch { }
            }
            else if (item.TotalBytes > 0)
            {
                item.ReceivedBytes = item.TotalBytes;
            }
            item.Touch();
            if (Settings.NotifyOnComplete) Ui.NotifyCompleted(item);
            Ui.DetailCompleted(id, Settings.AutoCloseDetail);
        }
        else
        {
            if (!cookielessVideos.Contains(id) && VideoDownloader.IsYouTube(item.Uri)
                && VideoDownloader.IsSignedInClientFailure(error.Message)
                && VideoDownloader.HasCookies(item.CookieFile, item.Host))
            {
                cookielessVideos.Add(id);
                item.Log += Loc.T("→ YouTube rejected the signed-in request; retrying without cookies…") + "\n";
                item.Touch();
                StartVideo(item);
                return;
            }
            item.State = DownloadState.failed;
            item.ErrorMessage = error.Message;
            item.Speed = 0;
            item.Touch();
        }
        ScheduleSave();
        PumpQueue();
        QueueMayHaveEnded();
        RaiseChanged();
    }

    /// Asks once whether to install deno, which yt-dlp needs for YouTube, then starts the waiting downloads.
    private async void PromptForDeno()
    {
        try
        {
            var (install, dontAsk) = await Ui.AskInstallDenoAsync();
            if (dontAsk) Settings.DenoPromptSuppressed = true;
            if (!install)
            {
                denoDeclinedThisLaunch = true;
                ResumeDenoWaiters();
                return;
            }
            foreach (var id in denoWaiters) Update(id, i => i.Stage = Loc.T("Installing deno…"));
            var (_, error) = await ToolInstaller.Shared.InstallDenoAsync();
            if (error != null)
            {
                denoDeclinedThisLaunch = true;   // don't loop on a failing install; Settings ▸ Video can retry
                foreach (var id in denoWaiters)
                    Update(id, i => i.Log += Loc.T("deno could not be installed: {0}", error.Message) + "\n");
            }
        }
        catch
        {
            denoDeclinedThisLaunch = true;
        }
        ResumeDenoWaiters();
    }

    private void ResumeDenoWaiters()
    {
        denoPrompting = false;
        var waiting = denoWaiters.ToList();
        denoWaiters.Clear();
        foreach (var id in waiting)
        {
            var item = Find(id);
            if (item is { State: DownloadState.downloading }) StartVideo(item);
        }
    }

    // ---- User actions ----

    public void Pause(Guid id)
    {
        var item = Find(id);
        if (item == null || !(item.State.IsActive() || item.State == DownloadState.queued)) return;
        Update(id, i => { i.State = DownloadState.paused; i.Speed = 0; });
        if (downloaders.TryGetValue(id, out var dl)) dl.Stop();
        if (videoProcesses.Remove(id, out var p)) VideoDownloader.Kill(p);
        denoWaiters.Remove(id);
        StopFormatQuestion(id);
        StopConfirmation(id);
        ScheduleSave();
        PumpQueue();
        RaiseChanged();
    }

    public void Resume(Guid id)
    {
        var item = Find(id);
        if (item == null || item.State is not (DownloadState.paused or DownloadState.failed or DownloadState.cancelled)) return;
        Update(id, i => { i.State = DownloadState.queued; i.ErrorMessage = null; });
        cookielessVideos.Remove(id);
        formatDecided.Remove(id);       // a video without a stored choice is asked about again
        PumpQueue();
        RaiseChanged();
    }

    public void Cancel(Guid id)
    {
        var item = Find(id);
        if (item == null || item.State == DownloadState.completed) return;
        if (downloaders.Remove(id, out var dl)) dl.Stop();
        if (videoProcesses.Remove(id, out var p)) VideoDownloader.Kill(p);
        denoWaiters.Remove(id);
        StopFormatQuestion(id);
        StopConfirmation(id);
        TryDelete(item.PartPath);
        DeleteVideoPartials(item, p);
        Update(id, i =>
        {
            i.State = DownloadState.cancelled; i.Speed = 0; i.ReceivedBytes = 0; i.Segments = [];
            i.FormatArgs = null; i.QualityTag = null; i.Stage = "";
        });
        ScheduleSave();
        PumpQueue();
        RaiseChanged();
    }

    public void Remove(Guid id, bool deleteFile = false)
    {
        var item = Find(id);
        if (item == null) return;
        // Always goes to the Recycle Bin so the user can recover the file. If it could not be moved
        // (open in a player, the user declined a permanent delete), the row stays so that is visible.
        // Two rows can stand for the same playlist folder (the list downloaded twice): it goes with the last of them.
        if (deleteFile && FileNames.Exists(item.FinalPath) && !Items.Any(o => !ReferenceEquals(o, item) && o.FinalPath == item.FinalPath))
        {
            Shell.MoveToRecycleBin(item.FinalPath!);
            if (FileNames.Exists(item.FinalPath)) return;
        }
        if (downloaders.Remove(id, out var dl)) dl.Stop();
        if (videoProcesses.Remove(id, out var p)) VideoDownloader.Kill(p);
        denoWaiters.Remove(id);
        StopFormatQuestion(id);
        StopConfirmation(id);
        playlistPosition.Remove(id);
        formatDecided.Remove(id);
        TryDelete(item.PartPath);
        DeleteVideoPartials(item, p);
        if (item.CookieFile != null) TryDelete(item.CookieFile);
        Items.Remove(item);
        samples.Remove(id);
        cookielessVideos.Remove(id);
        ScheduleSave();
        PumpQueue();
        RaiseChanged();
    }

    public void PauseAll()
    {
        foreach (var i in Items.Where(i => i.State.IsActive() || i.State == DownloadState.queued).ToList()) Pause(i.Id);
    }

    public void ResumeAll()
    {
        foreach (var i in Items.Where(i => i.State is DownloadState.paused or DownloadState.failed).ToList()) Resume(i.Id);
    }

    public void ClearFinished()
    {
        foreach (var i in Items.Where(i => i.State is DownloadState.completed or DownloadState.cancelled).ToList()) Remove(i.Id);
    }

    public void Reveal(DownloadItem item)
    {
        if (FileNames.Exists(item.FinalPath)) Shell.RevealFile(item.FinalPath!);
        else Shell.OpenFolder(Directory.Exists(DestinationDirectory(item)) ? DestinationDirectory(item) : item.Directory);
    }

    public void Open(DownloadItem item)
    {
        if (File.Exists(item.FinalPath)) Shell.OpenFile(item.FinalPath);
        else if (Directory.Exists(item.FinalPath)) Shell.OpenFolder(item.FinalPath);       // a playlist
    }

    /// Creates the download folder up front so the first download does not fail on a missing folder.
    public void PrepareDownloadFolder()
    {
        try { Directory.CreateDirectory(Settings.DownloadDirectory); } catch { }
    }

    public void Shutdown()
    {
        timer.Dispose();
        Settings.PropertyChanged -= OnSettingsChanged;
        PauseAll();
        Save();
    }

    // ---- Speed measurement ----

    private void Tick()
    {
        var now = Environment.TickCount64;
        var activeIds = new HashSet<Guid>();
        foreach (var item in Items)
        {
            if (item.State != DownloadState.downloading || item.Kind == DownloadKind.video) continue;
            activeIds.Add(item.Id);
            if (samples.TryGetValue(item.Id, out var s) && now > s.Ticks)
            {
                var inst = Math.Max(0, (item.ReceivedBytes - s.Bytes) / ((now - s.Ticks) / 1000.0));
                item.Speed = item.Speed == 0 ? inst : item.Speed * 0.6 + inst * 0.4;
                item.Touch();
                dirty = true;
            }
            samples[item.Id] = (item.ReceivedBytes, now);
        }
        foreach (var gone in samples.Keys.Where(k => !activeIds.Contains(k)).ToList()) samples.Remove(gone);
        tickCount++;
        if (activeIds.Count > 0 && tickCount % 10 == 0) Save();     // segment state goes to disk every 5 s
        if (dirty) RaiseChanged();
    }

    // ---- Persistence ----

    private static readonly JsonSerializerOptions json = new() { WriteIndented = false };

    private void Load()
    {
        try
        {
            if (!File.Exists(storePath)) return;
            var list = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(storePath), json);
            if (list == null) return;
            foreach (var item in list)
            {
                if (item.State.IsActive() || item.State == DownloadState.queued) item.State = DownloadState.paused;
                item.Headers = new Dictionary<string, string>(item.Headers ?? [], StringComparer.OrdinalIgnoreCase);
                item.Segments ??= [];
                if (Links.ParseHttp(item.Url) == null) continue;
                Items.Add(item);
            }
            CleanupOrphanParts();
        }
        catch { }
    }

    /// Deletes `.dm-<id>.part` files left behind by downloads that are no longer in the list.
    private void CleanupOrphanParts()
    {
        try
        {
            var dir = Settings.DownloadDirectory;
            if (!Directory.Exists(dir)) return;
            var known = new HashSet<string>(Items.Select(i => Path.GetFileName(i.PartPath)), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir, ".dm-*.part"))
                if (!known.Contains(Path.GetFileName(f))) TryDelete(f);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var tmp = storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items.ToList(), json));
            File.Move(tmp, storePath, true);
        }
        catch { }
    }

    private void ScheduleSave()
    {
        if (saveScheduled) return;
        saveScheduled = true;
        Task.Delay(500).ContinueWith(_ => MainThread.Post(() => { saveScheduled = false; Save(); }));
    }

    private static string? WriteCookieFile(List<BrowserCookie> cookies, Guid id)
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies)
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Domain)) continue;
            var value = c.Value ?? "";
            if (c.Name.IndexOfAny(['\t', '\n', '\r']) >= 0 || value.IndexOfAny(['\t', '\n', '\r']) >= 0
                || c.Domain.IndexOfAny(['\t', '\n', '\r']) >= 0) continue;
            var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
            var flag = c.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            sb.Append(string.Join('\t', c.Domain, flag, path, c.Secure ? "TRUE" : "FALSE", ((long)c.ExpirationDate).ToString(System.Globalization.CultureInfo.InvariantCulture), c.Name, value)).Append('\n');
        }
        try
        {
            Directory.CreateDirectory(AppPaths.CookiesDir);
            var file = Path.Combine(AppPaths.CookiesDir, id + ".txt");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// A cancelled or removed video leaves yt-dlp's part files in the download folder (hundreds of MB for
    /// a long video); nothing else would ever clean them up. Waits for the killed process to let go of them.
    private void DeleteVideoPartials(DownloadItem item, Process? killed)
    {
        if (item.Kind != DownloadKind.video || item.State == DownloadState.completed) return;
        // Another row may be fetching the same video into the same files.
        if (Items.Any(i => !ReferenceEquals(i, item) && i.Kind == DownloadKind.video && i.State.IsActive() && i.Url == item.Url)) return;
        var id = item.Id;
        // A playlist writes into its own folder, one video after the other; the row itself carries the list's name.
        var dir = item.WorkingPath != null && Path.GetDirectoryName(item.WorkingPath) is { Length: > 0 } working ? working : DestinationDirectory(item);
        var name = item.WorkingPath != null ? Path.GetFileName(item.WorkingPath) : item.FileName;
        Task.Run(async () =>
        {
            try { if (killed != null) await killed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            // Windows keeps a file locked for a moment after its process is gone.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                // Retried in the meantime: the files belong to the new run.
                if (!await MainThread.Run(() => Find(id) is not { } i || i.State == DownloadState.cancelled).ConfigureAwait(false)) return;
                if (VideoDownloader.DeletePartials(dir, name)) return;
                await Task.Delay(500).ConfigureAwait(false);
            }
        });
    }

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
}

public enum PlaylistKind { None, WithVideo, Only }

/// Parsing of links typed, pasted or copied by the user.
public static class Links
{
    public static Uri? ParseHttp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!Uri.TryCreate(s.Trim(), UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return null;
        return url.Host.Length > 0 ? url : null;
    }

    /// Returns the links if every non-empty line of `text` is a single http(s) URL; null otherwise.
    public static List<Uri>? UrlOnlyContent(string text)
    {
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines.Count > 50) return null;
        var urls = new List<Uri>();
        foreach (var line in lines)
        {
            if (line.Contains(' ') || ParseHttp(line) is not { } url) return null;
            if (!urls.Contains(url)) urls.Add(url);
        }
        return urls;
    }

    private static readonly string[] videoHosts =
        ["youtube.com", "youtu.be", "vimeo.com", "dailymotion.com", "twitter.com", "x.com",
         "instagram.com", "tiktok.com", "twitch.tv", "facebook.com", "reddit.com", "bilibili.com"];

    /// Whether a YouTube address carries a playlist: on its own (/playlist?list=…) or along with the video
    /// being watched (watch?v=…&list=…). "Mixes" (list=RD…) are endless and made up per viewer; they do not count.
    public static PlaylistKind Playlist(Uri url)
    {
        if (!VideoDownloader.IsYouTube(url)) return PlaylistKind.None;
        string? list = null, video = null;
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2 || kv[1].Length == 0) continue;
            if (kv[0] == "list") list = kv[1];
            else if (kv[0] == "v") video = kv[1];
        }
        if (list == null || list.StartsWith("RD", StringComparison.Ordinal)) return PlaylistKind.None;
        var path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/playlist", StringComparison.Ordinal)) return PlaylistKind.Only;
        // youtu.be/<id>?list=… and /watch?v=<id>&list=…
        return video != null || VideoDownloader.HostMatches(url.Host, "youtu.be") ? PlaylistKind.WithVideo : PlaylistKind.None;
    }

    public static bool LooksLikeVideoPage(Uri url)
    {
        if (FileCategories.Detect(FileNames.FromUrl(url), DownloadKind.file) != FileCategory.other) return false;
        return VideoDownloader.HostMatches(url.Host, videoHosts);
    }
}
