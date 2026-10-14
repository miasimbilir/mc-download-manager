using System.Diagnostics;
using Xunit;

namespace MCDM.Tests;

/// What 2.0 added around a download handed over by the browser: the question before it starts, the
/// "you already have this" check, the speed limit and the action after the last download.
public class AskTests
{
    /// Stands in for the windows: answers are scripted, and what was asked is recorded.
    private sealed class Asker : IStoreUi
    {
        public Func<DownloadItem, Task<DownloadAnswer>> Download = _ => Task.FromResult(DownloadAnswer.Start);
        public Func<DownloadItem, DuplicateDecision> Duplicate = _ => DuplicateDecision.Cancel;
        public readonly List<DownloadItem> AskedDownload = [], AskedDuplicate = [];
        public readonly List<Guid> Shown = [];
        public readonly List<QueueEndAction> Finished = [];

        public void ShowDetail(Guid id) => Shown.Add(id);
        public void DetailCompleted(Guid id, bool autoClose) { }
        public void NotifyCompleted(DownloadItem item) { }
        public Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync() => Task.FromResult((false, false));
        public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) => Task.FromResult<VideoFormatAnswer?>(VideoFormatAnswer.Default);
        public Task<DownloadAnswer> AskDownloadAsync(DownloadItem item) { AskedDownload.Add(item); return Download(item); }
        public Task<DuplicateDecision> AskDuplicateAsync(DownloadItem existing) { AskedDuplicate.Add(existing); return Task.FromResult(Duplicate(existing)); }
        public void QueueFinished(QueueEndAction action) => Finished.Add(action);
    }

    private static StoreTests.Rig Rig(Asker ui, Action<AppSettings>? configure = null) =>
        new(s => { s.AskBeforeDownload = true; configure?.Invoke(s); }, ui);

    private static DownloadItem FromBrowser(StoreTests.Rig rig, string url, string? name = null) =>
        rig.On(s => s.Add(new NewDownloadRequest(new Uri(url)) { Confirm = true, FileName = name }));

    // ---- The question before a download ----

    [Fact]
    public async Task A_file_from_the_browser_waits_for_the_answer_and_shows_what_the_server_says_about_it()
    {
        using var server = new TestServer(700_000);
        var ui = new Asker();
        var answer = new TaskCompletionSource<DownloadAnswer>();
        ui.Download = _ => answer.Task;
        using var rig = Rig(ui);

        var item = FromBrowser(rig, server.Url("/named"));
        Assert.Same(item, Assert.Single(ui.AskedDownload));
        Assert.Equal(DownloadState.queued, item.State);
        Assert.True(item.AwaitingConfirmation);

        // The size and the server's name for the file arrive while the question is open…
        await Setup.Until(() => item.Probed, what: "the probe");
        Assert.Equal(700_000, item.TotalBytes);
        Assert.Equal("report 2026.pdf", item.FileName);
        // …with a single one-byte request; nothing is downloaded and nothing starts behind the question.
        Assert.Equal("bytes=0-0", Assert.Single(server.Requests).Headers["Range"]);
        rig.On(s => s.PumpQueue());
        await Task.Delay(200);
        Assert.Equal(DownloadState.queued, item.State);
        Assert.Single(server.Requests);
        Assert.Empty(ui.Shown);

        // "Download", into a folder of the user's choice and under a name of the user's choice.
        var elsewhere = Path.Combine(rig.Dir, "elsewhere");
        answer.SetResult(new DownloadAnswer(DownloadDecision.Start, elsewhere, "benim adım.pdf"));
        await rig.Wait(item, DownloadState.completed);
        Assert.False(item.AwaitingConfirmation);
        Assert.Equal(Path.Combine(elsewhere, "benim adım.pdf"), item.FinalPath);     // no category folder, not the server's name
        Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath!));
        Assert.True(rig.Settings.AskBeforeDownload);
    }

    [Fact]
    public async Task Download_starts_at_once_even_when_new_downloads_are_set_to_wait()
    {
        using var server = new TestServer(200_000);
        var ui = new Asker();
        using var rig = Rig(ui, s => { s.AutoStart = false; s.ShowDetailOnStart = true; });
        var item = FromBrowser(rig, server.Url("/file.zip"));
        await rig.Wait(item, DownloadState.completed);
        Assert.Equal(Path.Combine(rig.Settings.DownloadDirectory, "Archives", "file.zip"), item.FinalPath);
        Assert.Equal([item.Id], ui.Shown);      // the download window opens with the download, not with the question
    }

    [Fact]
    public async Task Later_keeps_it_in_the_list_paused_and_cancel_drops_it()
    {
        using var server = new TestServer(200_000);
        var ui = new Asker { Download = _ => Task.FromResult(new DownloadAnswer(DownloadDecision.Later)) };
        using var rig = Rig(ui);
        var later = FromBrowser(rig, server.Url("/a.zip"));
        await Setup.Until(() => later.State == DownloadState.paused, what: "paused");
        Assert.False(later.AwaitingConfirmation);
        rig.On(s => s.Resume(later.Id));
        await rig.Wait(later, DownloadState.completed);

        ui.Download = _ => Task.FromResult(new DownloadAnswer(DownloadDecision.Cancel));
        var dropped = FromBrowser(rig, server.Url("/b.zip"));
        await Setup.Until(() => rig.On(s => s.Find(dropped.Id)) == null, what: "the cancelled download to leave the list");
        Assert.Single(rig.On(s => s.Items.ToList()));
    }

    [Fact]
    public async Task Dont_ask_again_switches_the_question_off_and_typed_addresses_are_never_asked_about()
    {
        using var server = new TestServer(100_000);
        var ui = new Asker { Download = _ => Task.FromResult(new DownloadAnswer(DownloadDecision.Start, DontAskAgain: true)) };
        using var rig = Rig(ui);
        var first = FromBrowser(rig, server.Url("/1.zip"));
        await rig.Wait(first, DownloadState.completed);
        Assert.False(rig.Settings.AskBeforeDownload);

        var second = FromBrowser(rig, server.Url("/2.zip"));
        await rig.Wait(second, DownloadState.completed);
        Assert.Single(ui.AskedDownload);

        // "Add URL", a pasted link, the clipboard prompt: the user has already said yes there.
        rig.Settings.AskBeforeDownload = true;
        var typed = rig.Add(server.Url("/3.zip"));
        await rig.Wait(typed, DownloadState.completed);
        Assert.Single(ui.AskedDownload);
    }

    [Fact]
    public async Task Pausing_or_starting_it_from_the_list_answers_the_open_question()
    {
        using var server = new TestServer(200_000);
        var ui = new Asker { Download = _ => new TaskCompletionSource<DownloadAnswer>().Task };     // never answered
        using var rig = Rig(ui);
        var paused = FromBrowser(rig, server.Url("/a.zip"));
        rig.On(s => s.Pause(paused.Id));
        Assert.Equal(DownloadState.paused, paused.State);
        Assert.False(paused.AwaitingConfirmation);

        var started = FromBrowser(rig, server.Url("/b.zip"));
        Assert.True(started.AwaitingConfirmation);
        rig.On(s => s.StartNow(started.Id));
        Assert.False(started.AwaitingConfirmation);
        await rig.Wait(started, DownloadState.completed);

        var removed = FromBrowser(rig, server.Url("/c.zip"));
        rig.On(s => s.Remove(removed.Id));
        Assert.Equal(2, rig.On(s => s.Items.Count));
    }

    [Fact]
    public async Task Start_now_runs_a_waiting_download_ahead_of_the_queue()
    {
        using var server = new TestServer(300_000);
        using var rig = new StoreTests.Rig(s => s.AutoStart = false);
        var item = rig.Add(server.Url("/x.zip"));
        Assert.Equal(DownloadState.queued, item.State);
        rig.On(s => s.StartNow(item.Id));
        await rig.Wait(item, DownloadState.completed);
    }

    // ---- The same thing again ----

    [Fact]
    public async Task Something_already_downloaded_is_asked_about_instead_of_fetched_twice()
    {
        using var server = new TestServer(150_000);
        var ui = new Asker();
        using var rig = Rig(ui, s => s.AskBeforeDownload = false);
        var first = FromBrowser(rig, server.Url("/named"));
        await rig.Wait(first, DownloadState.completed);

        // "Cancel": nothing is added, the bridge is told about the file that is there.
        var again = FromBrowser(rig, server.Url("/named"));
        Assert.Same(first, again);
        Assert.Same(first, Assert.Single(ui.AskedDuplicate));
        await Task.Delay(150);
        Assert.Single(rig.On(s => s.Items.ToList()));

        // "Download again": a second copy under a numbered name.
        ui.Duplicate = _ => DuplicateDecision.DownloadAgain;
        FromBrowser(rig, server.Url("/named"));
        await Setup.Until(() => rig.On(s => s.Items.Count) == 2, what: "the second copy to be added");
        var copy = rig.On(s => s.Items[0]);
        await rig.Wait(copy, DownloadState.completed);
        Assert.Equal("report 2026 (2).pdf", copy.FileName);

        // The file was deleted meanwhile: there is nothing to ask about, it is simply downloaded.
        File.Delete(first.FinalPath!);
        File.Delete(copy.FinalPath!);
        var asked = ui.AskedDuplicate.Count;
        var fresh = FromBrowser(rig, server.Url("/named"));
        Assert.NotSame(first, fresh);
        Assert.Equal(asked, ui.AskedDuplicate.Count);
        await rig.Wait(fresh, DownloadState.completed);
    }

    [Fact]
    public async Task A_download_that_is_under_way_is_shown_instead_of_added_twice_and_a_failed_one_is_retried()
    {
        using var server = new TestServer(3 * 1024 * 1024) { ChunkDelayMs = 60 };
        var ui = new Asker();
        using var rig = Rig(ui, s => { s.AskBeforeDownload = false; s.SegmentCount = 2; });
        var running = FromBrowser(rig, server.Url("/slow"));
        await Setup.Until(() => running.ReceivedBytes > 0, what: "progress");
        Assert.Same(running, FromBrowser(rig, server.Url("/slow")));
        Assert.Equal([running.Id], ui.Shown);
        Assert.Single(rig.On(s => s.Items.ToList()));
        rig.On(s => s.Remove(running.Id));

        // Failed for lack of a sign-in; the browser hands it over again, now with the cookie.
        var failed = FromBrowser(rig, server.Url("/missing"));
        await rig.Wait(failed, DownloadState.failed);
        var retried = rig.On(s => s.Add(new NewDownloadRequest(new Uri(server.Url("/missing"))) { Confirm = true, CookieHeader = "session=1" }));
        Assert.Same(failed, retried);
        Assert.Equal("session=1", failed.Headers["Cookie"]);
        await Setup.Until(() => server.Requests.Count(r => r.Path == "/missing") >= 2, what: "the retry");
        Assert.Single(rig.On(s => s.Items.ToList()));

        // Typed into the app twice: that is what the user asked for, so it is added twice.
        rig.Add(server.Url("/missing"));
        Assert.Equal(2, rig.On(s => s.Items.Count));
    }

    // ---- Speed limit ----

    [Fact]
    public void The_limiter_lines_reads_up_so_that_together_they_keep_to_the_budget()
    {
        try
        {
            RateLimiter.BytesPerSecond = 0;
            Assert.Equal(TimeSpan.Zero, RateLimiter.Reserve(1_000_000));
            Assert.Equal(128 * 1024, RateLimiter.ChunkSize(128 * 1024));

            RateLimiter.BytesPerSecond = 100_000;
            Assert.Equal(10_000, RateLimiter.ChunkSize(128 * 1024));        // a tenth of a second's worth
            // The first tenth of a second's worth is free (it makes up for waits that end late); from
            // then on every read pushes the next one back by its own share.
            var first = RateLimiter.Reserve(10_000).TotalSeconds;
            var second = RateLimiter.Reserve(10_000).TotalSeconds;
            var third = RateLimiter.Reserve(20_000).TotalSeconds;
            Assert.InRange(first, 0, 0.005);
            Assert.InRange(second, 0.09, 0.105);
            Assert.InRange(third, 0.28, 0.305);

            RateLimiter.BytesPerSecond = 1_000_000;                         // a new limit does not inherit the old queue
            Assert.InRange(RateLimiter.Reserve(100_000).TotalSeconds, 0, 0.005);
            Assert.InRange(RateLimiter.Reserve(100_000).TotalSeconds, 0.09, 0.105);
            RateLimiter.BytesPerSecond = 1000;
            Assert.Equal(1024, RateLimiter.ChunkSize(128 * 1024));          // never less than 1 KB at a time
        }
        finally { RateLimiter.BytesPerSecond = 0; }
    }

    [Fact]
    public async Task A_speed_limit_slows_a_download_down_and_lifting_it_lets_it_run()
    {
        using var server = new TestServer(600_000);
        using var rig = new StoreTests.Rig(s => { s.SegmentCount = 4; s.SpeedLimitKb = 200; s.SpeedLimitEnabled = true; });
        try
        {
            Assert.Equal(200 * 1024, RateLimiter.BytesPerSecond);
            var clock = Stopwatch.StartNew();
            var item = rig.Add(server.Url("/file.bin"));
            await rig.Wait(item, DownloadState.completed);
            // 600 KB at 200 KB/s cannot take less than about three seconds; unlimited it takes a few milliseconds.
            Assert.InRange(clock.Elapsed.TotalSeconds, 2.3, 12);
            Assert.Equal(server.Data, File.ReadAllBytes(item.FinalPath!));

            rig.Settings.SpeedLimitEnabled = false;
            Assert.Equal(0, RateLimiter.BytesPerSecond);
            clock.Restart();
            var free = rig.Add(server.Url("/file2.bin"));
            await rig.Wait(free, DownloadState.completed);
            Assert.InRange(clock.Elapsed.TotalSeconds, 0, 2);
        }
        finally { RateLimiter.BytesPerSecond = 0; }
    }

    [Fact]
    public void Video_downloads_get_the_limit_as_a_ytdlp_option()
    {
        List<string> Args(VideoOptions o) => VideoDownloader.BuildArguments(new Uri("https://v.example/1"), "out", [], new Dictionary<string, string>(), null, null, null, o);
        var limited = Args(new VideoOptions { LimitKb = 512 });
        Assert.Equal("512K", limited[limited.IndexOf("--limit-rate") + 1]);
        Assert.Equal("1", limited[limited.IndexOf("-N") + 1]);          // the limit counts per connection
        var free = Args(VideoOptions.None);
        Assert.DoesNotContain("--limit-rate", free);
        Assert.Equal("4", free[free.IndexOf("-N") + 1]);
    }

    // ---- After the last download ----

    [Fact]
    public async Task The_computer_is_sent_to_sleep_once_when_the_last_download_ends_by_itself()
    {
        using var server = new TestServer(2 * 1024 * 1024) { ChunkDelayMs = 40 };
        var ui = new Asker();
        using var rig = new StoreTests.Rig(s => { s.MaxConcurrent = 1; s.SegmentCount = 1; }, ui);
        rig.On(s => s.WhenDone = QueueEndAction.Sleep);

        // Pausing is the user's doing, not the end of the queue.
        var paused = rig.Add(server.Url("/slow?1"));
        await Setup.Until(() => paused.ReceivedBytes > 0, what: "progress");
        rig.On(s => s.Pause(paused.Id));
        await Task.Delay(200);
        Assert.Empty(ui.Finished);
        Assert.Equal(QueueEndAction.Sleep, rig.On(s => s.WhenDone));

        // Two in a row: nothing after the first (one is still waiting), sleep after the second.
        server.ChunkDelayMs = 0;
        var a = rig.Add(server.Url("/file?a"));
        var b = rig.Add(server.Url("/missing"));
        await rig.Wait(a, DownloadState.completed);
        await rig.Wait(b, DownloadState.failed);                    // a failure ends the queue just as well
        await Setup.Until(() => ui.Finished.Count > 0, what: "the queue-end action");
        Assert.Equal([QueueEndAction.Sleep], ui.Finished);
        Assert.Equal(QueueEndAction.None, rig.On(s => s.WhenDone));

        // It was a one-off: the next download ends without it.
        var c = rig.Add(server.Url("/file?c"));
        await rig.Wait(c, DownloadState.completed);
        await Task.Delay(150);
        Assert.Single(ui.Finished);
    }
}
