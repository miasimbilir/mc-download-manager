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
}

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
        timer = new Timer(_ => MainThread.Post(Tick), null, 500, 500);
    }

    // ---- Queries ----

    /// Final folder for an item: base folder, plus a category subfolder when enabled.
    public string DestinationDirectory(DownloadItem item) =>
        Settings.OrganizeByCategory ? Path.Combine(item.Directory, item.Category.Label()) : item.Directory;

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
        if (Settings.AutoStart) PumpQueue();
        if (Settings.ShowDetailOnStart) Ui.ShowDetail(item.Id);
        RaiseChanged();
        return item;
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
            if (item.State != DownloadState.queued) continue;
            StartItem(item);
            slots--;
        }
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
        Update(id, item =>
        {
            item.TotalBytes = p.TotalBytes;
            item.SupportsRanges = p.SupportsRanges;
            item.State = DownloadState.downloading;
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
        });
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
        RaiseChanged();
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
            MainThread.Post(PumpQueue);
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
        Update(id, i =>
        {
            i.State = DownloadState.downloading; i.ErrorMessage = null; i.ReceivedBytes = 0; i.Speed = 0; i.TotalBytes = -1;
            i.Stage = Loc.T("Fetching video information…");
            if (useCookies) i.Log = "";     // keep the failed attempt's output visible above the retry
        });
        var outDir = DestinationDirectory(item);
        try { Directory.CreateDirectory(outDir); } catch { }
        Process? started = null;
        bool Current() => started != null && videoProcesses.TryGetValue(id, out var p) && ReferenceEquals(p, started);
        videoPhase[id] = (0, 0, -1);
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
                    if (VideoDownloader.Stage(line) is { } st) i.Stage = st;
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
                        var name = Path.GetFileName(line[(d + "Destination: ".Length)..].Trim());
                        if (name.Length > 0) i.FileName = VideoDownloader.StripFormatSuffix(name);
                    }
                    if (line.StartsWith("[Merger] Merging formats into \"", StringComparison.Ordinal))
                    {
                        int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                        if (q1 >= 0 && q2 > q1) i.FileName = Path.GetFileName(line[(q1 + 1)..q2]);
                    }
                });
            }),
            onFinish: (path, error) => MainThread.Post(() => { if (Current() || started == null) HandleVideoFinish(id, path, error); }),
            qualityTag: item.QualityTag);
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
                    if (info.Title.Length > 0) i.FileName = FileNames.Sanitize(info.Title);
                    i.Stage = Loc.T("Waiting for the quality choice…");
                });
                Ui.AskVideoFormatAsync(current, info).ContinueWith(t => MainThread.Post(() =>
                {
                    var mine = Current();
                    Close();
                    if (!mine) return;
                    var answer = t.IsCompletedSuccessfully ? t.Result : VideoFormatAnswer.Default;
                    if (answer == null) { Cancel(id); return; }
                    if (answer.MaxHeight != VideoFormatAnswer.UseDefault)
                    {
                        if (answer.Remember)
                        {
                            Settings.VideoMaxHeight = answer.Nominal;
                            Settings.AskVideoQuality = false;
                        }
                        Update(id, i =>
                        {
                            i.FormatArgs = AppSettings.BuildFormatArguments("", Settings.VideoContainer, answer.MaxHeight, answer.Selector);
                            i.QualityTag = answer.MaxHeight > 0 ? answer.Nominal + "p" : null;
                        });
                        ScheduleSave();
                    }
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

    private void HandleVideoFinish(Guid id, string? path, Exception? error)
    {
        videoProcesses.Remove(id);
        videoPhase.Remove(id);
        var item = Find(id);
        if (item == null || item.State != DownloadState.downloading) return;    // paused or cancelled by the user
        if (error == null)
        {
            item.State = DownloadState.completed;
            item.CompletedAt = DateTime.UtcNow;
            item.Speed = 0;
            item.Stage = "";
            if (path != null)
            {
                item.FinalPath = path;
                item.FileName = Path.GetFileName(path);
                Shell.MarkAsDownloaded(path, item.Url, null);
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
        if (deleteFile && item.FinalPath != null && File.Exists(item.FinalPath))
        {
            Shell.MoveToRecycleBin(item.FinalPath);
            if (File.Exists(item.FinalPath)) return;
        }
        if (downloaders.Remove(id, out var dl)) dl.Stop();
        if (videoProcesses.Remove(id, out var p)) VideoDownloader.Kill(p);
        denoWaiters.Remove(id);
        StopFormatQuestion(id);
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
        if (item.FinalPath != null && File.Exists(item.FinalPath)) Shell.RevealFile(item.FinalPath);
        else Shell.OpenFolder(Directory.Exists(DestinationDirectory(item)) ? DestinationDirectory(item) : item.Directory);
    }

    public void Open(DownloadItem item)
    {
        if (item.FinalPath != null && File.Exists(item.FinalPath)) Shell.OpenFile(item.FinalPath);
    }

    /// Creates the download folder up front so the first download does not fail on a missing folder.
    public void PrepareDownloadFolder()
    {
        try { Directory.CreateDirectory(Settings.DownloadDirectory); } catch { }
    }

    public void Shutdown()
    {
        timer.Dispose();
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
        var dir = DestinationDirectory(item);
        var name = item.FileName;
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

    public static bool LooksLikeVideoPage(Uri url)
    {
        if (FileCategories.Detect(FileNames.FromUrl(url), DownloadKind.file) != FileCategory.other) return false;
        return VideoDownloader.HostMatches(url.Host, videoHosts);
    }
}
