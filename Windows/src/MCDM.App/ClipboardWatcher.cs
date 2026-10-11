using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using MCDM.App.Views;

namespace MCDM.App;

/// Watches the clipboard for http(s) links and asks the user whether to download them.
/// Also used by Ctrl+V in the main window.
public sealed class ClipboardWatcher(App app)
{
    private DispatcherTimer? timer;
    private TopLevel? host;
    private uint lastSequence;
    private string? lastText;
    private readonly List<string> asked = [];
    private bool prompting;
    private bool busy;

    public void Start(TopLevel clipboardHost)
    {
        host = clipboardHost;
        lastSequence = Shell.ClipboardSequence;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Poll();
        // Whatever is on the clipboard when the app starts is old news.
        _ = Prime();
    }

    private async Task Prime()
    {
        lastText = await ReadText();
        timer?.Start();
    }

    public void Stop() => timer?.Stop();

    /// Links the app itself puts on the clipboard ("Copy URL") must not come back as a prompt.
    public void Ignore(string text) => Remember(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task<string?> ReadText()
    {
        try { return host?.Clipboard is { } c ? await c.TryGetTextAsync() : null; }
        catch { return null; }
    }

    private async void Poll()
    {
        if (!app.Settings.WatchClipboard || prompting || busy) return;
        busy = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var seq = Shell.ClipboardSequence;
                if (seq == lastSequence) return;
                lastSequence = seq;
            }
            var text = await ReadText();
            if (text == lastText) return;
            lastText = text;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) return;
            // Only react when the clipboard holds nothing but links, and the links point at files of a captured type.
            var only = Links.UrlOnlyContent(text);
            if (only == null) return;
            var exts = app.Settings.CaptureExtensions;
            var urls = only.Where(u => CaptureTypes.Matches(u, exts) && !asked.Contains(u.AbsoluteUri)).ToList();
            if (urls.Count == 0) return;
            Remember(urls.Select(u => u.AbsoluteUri));
            await Ask(urls, fromClipboard: true);
        }
        finally
        {
            busy = false;
        }
    }

    /// Called for Ctrl+V in the main window (an explicit user action: any http(s) link is accepted).
    public async Task HandlePaste(string text)
    {
        var urls = Links.UrlOnlyContent(text);
        if (urls == null || urls.Count == 0 || prompting) return;
        Remember(urls.Select(u => u.AbsoluteUri));
        await Ask(urls, fromClipboard: false);
    }

    private void Remember(IEnumerable<string> urls)
    {
        asked.AddRange(urls);
        if (asked.Count > 50) asked.RemoveRange(0, asked.Count - 50);
    }

    private async Task Ask(List<Uri> urls, bool fromClipboard)
    {
        prompting = true;
        try
        {
            string title, message;
            if (urls.Count == 1)
            {
                title = fromClipboard ? Loc.T("A link was copied. Download it?") : Loc.T("Download the pasted link?");
                message = urls[0].AbsoluteUri;
            }
            else
            {
                title = fromClipboard ? Loc.T("{0} links were copied. Download them?", urls.Count) : Loc.T("Download the {0} pasted links?", urls.Count);
                message = string.Join("\n", urls.Take(6).Select(u => u.AbsoluteUri)) + (urls.Count > 6 ? "\n…" : "");
            }
            var preferVideo = urls.All(Links.LooksLikeVideoPage);
            string[] buttons = preferVideo
                ? [Loc.T("Download Video (yt-dlp)"), Loc.T("Download as File"), Loc.T("Ignore")]
                : [Loc.T("Download"), Loc.T("Download Video (yt-dlp)"), Loc.T("Ignore")];
            var owner = !fromClipboard && app.MainWindow.IsVisible ? app.MainWindow : null;
            var (button, check) = await PromptWindow.Ask(owner, title, message, buttons,
                checkbox: fromClipboard ? Loc.T("Stop watching the clipboard") : null, cancelIndex: 2);
            if (check) app.Settings.WatchClipboard = false;
            DownloadKind? kind = button switch
            {
                0 => preferVideo ? DownloadKind.video : DownloadKind.file,
                1 => preferVideo ? DownloadKind.file : DownloadKind.video,
                _ => null,
            };
            if (kind == null) return;
            foreach (var url in urls) app.Store.Add(new NewDownloadRequest(url) { Kind = kind.Value });
        }
        finally
        {
            prompting = false;
        }
    }
}
