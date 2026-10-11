using System.Text.Json;
using Xunit;

namespace MCDM.Tests;

/// The fixtures are real `yt-dlp -J` listings (October 2026) reduced to the fields the parser reads.
public class VideoFormatTests
{
    private static VideoInfo Load(string name, string container = "mp4")
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"formats-{name}.json")));
        return VideoFormats.Parse(doc.RootElement, container);
    }

    [Fact]
    public void X_offers_its_four_plain_mp4_files_with_exact_sizes()
    {
        var info = Load("x");
        Assert.Equal([1080, 720, 360, 270], info.Choices.Select(c => c.Nominal));
        // The plain https files (codecs unknown to yt-dlp, sound included) beat the HLS playlists.
        Assert.Equal(["http-10368", "http-2176", "http-832", "http-256"], info.Choices.Select(c => c.Selector));
        Assert.Equal([239486544L, 50262607L, 19218055L, 5913248L], info.Choices.Select(c => c.ApproxBytes!.Value));
        Assert.Equal(1078, info.Choices[0].Height);         // 1900×1078 on the wire, "1080p" to people
        Assert.Equal("1080p", info.Choices[0].Label);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public void An_instagram_reel_is_named_by_its_short_side_and_gets_the_separate_sound_track()
    {
        var info = Load("igreel");
        Assert.Equal([1080, 720, 540], info.Choices.Select(c => c.Nominal));
        Assert.Equal(1920, info.Choices[0].Height);          // portrait: the cap must be the real height
        Assert.All(info.Choices, c => Assert.Matches(@"^dash-\d+v\+dash-\d+a$", c.Selector));
        // Of the five 720p encodings the highest bitrate is taken.
        Assert.Equal("dash-2270704607002777v+dash-2270693820337189a", info.Choices[1].Selector);
        Assert.All(info.Choices, c => Assert.Null(c.ApproxBytes));    // no duration, no sizes: nothing to estimate from
    }

    [Fact]
    public void Youtube_takes_h264_with_aac_for_mp4_and_skips_the_compressed_dynamics_track()
    {
        var info = Load("yt");
        Assert.Equal([240, 144], info.Choices.Select(c => c.Nominal));
        Assert.Equal("133+140", info.Choices[0].Selector);
        Assert.Equal("160+140", info.Choices[1].Selector);
        Assert.Equal(433081 + 309288, info.Choices[0].ApproxBytes);
        Assert.Equal(309288, info.AudioBytes);

        var webm = Load("yt", "webm");
        Assert.Equal("242+251", webm.Choices[0].Selector);
    }

    [Fact]
    public void A_page_without_video_formats_has_nothing_to_ask_about()
    {
        using var doc = JsonDocument.Parse("""{"title":"podcast","formats":[{"format_id":"a","vcodec":"none","acodec":"mp4a.40.2","ext":"m4a","tbr":128},{"format_id":"sb0","vcodec":"none","acodec":"none","format_note":"storyboard","height":90}]}""");
        var info = VideoFormats.Parse(doc.RootElement, "mp4");
        Assert.Empty(info.Choices);
        Assert.True(info.HasAudio);
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(VideoFormats.Parse(empty.RootElement, "mp4").Choices);
    }

    [Fact]
    public void The_preselected_quality_never_exceeds_the_setting_unless_nothing_smaller_exists()
    {
        var choices = Load("x").Choices;
        Assert.Equal(1080, VideoFormatAnswer.Pick(choices, 1080)!.Nominal);
        Assert.Equal(720, VideoFormatAnswer.Pick(choices, 1000)!.Nominal);
        Assert.Equal(360, VideoFormatAnswer.Pick(choices, 480)!.Nominal);
        Assert.Equal(270, VideoFormatAnswer.Pick(choices, 144)!.Nominal);      // smallest there is
        Assert.Equal(1080, VideoFormatAnswer.Pick(choices, 0)!.Nominal);       // 0 = best
        Assert.Null(VideoFormatAnswer.Pick([], 720));
    }

    [Fact]
    public void A_picked_quality_names_its_formats_first_and_keeps_the_general_rule_as_fallback()
    {
        var args = AppSettings.BuildFormatArguments("", "mp4", 1078, "http-10368");
        Assert.Equal("http-10368/bv*[height<=1078][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=1078][ext=mp4]+ba[ext=m4a]/bv*[height<=1078]+ba/b[height<=1078]/b", args[1]);
        Assert.Equal(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0"], AppSettings.BuildFormatArguments("", "mp4", -2, "ignored"));
        Assert.Equal("137+140/bv*[height<=1080]+ba/b[height<=1080]/b", AppSettings.BuildFormatArguments("", "mkv", 1080, "137+140")[1]);
    }

    // ---- The whole flow through the store, against a stand-in for yt-dlp ----

    private sealed class Chooser(Func<VideoInfo, VideoFormatAnswer?> choose) : IStoreUi
    {
        public VideoInfo? Asked;
        public void ShowDetail(Guid id) { }
        public void DetailCompleted(Guid id, bool autoClose) { }
        public void NotifyCompleted(DownloadItem item) { }
        public Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync() => Task.FromResult((false, false));
        public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info)
        {
            Asked = info;
            return Task.FromResult(choose(info));
        }
    }

    /// A shell script that answers `-J` with a format listing and otherwise "downloads": it records its
    /// arguments and writes the output file the way yt-dlp reports it.
    private static (AppSettings, DownloadStore, Chooser, string ArgsLog) Rig(Func<VideoInfo, VideoFormatAnswer?> choose, bool probeFails = false)
    {
        var dir = Setup.TempDir();
        var log = Path.Combine(dir, "args.log");
        var fake = Path.Combine(dir, "yt-dlp");
        var listing = Path.Combine(AppContext.BaseDirectory, "Fixtures", "formats-x.json");
        File.WriteAllText(fake, $$"""
            #!/bin/sh
            for a in "$@"; do if [ "$a" = "-J" ]; then
              {{(probeFails ? "echo 'ERROR: [generic] no formats' >&2; exit 1" : $"cat '{listing}'; exit 0")}}
            fi; done
            printf '%s\n' "$@" > '{{log}}'
            out='{{dir}}/dl/clip.mp4'
            mkdir -p '{{dir}}/dl'
            echo "[download] Destination: $out"
            echo "DMP|500|1000|NA|2048.5"
            printf 'video' > "$out"
            echo "$out"
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = AppSettings.Load(Path.Combine(dir, "settings.json"));
        settings.DownloadDirectory = Path.Combine(dir, "dl");
        settings.OrganizeByCategory = false;
        settings.ShowDetailOnStart = false;
        settings.YtdlpPath = fake;
        settings.DenoPromptSuppressed = true;
        var ui = new Chooser(choose);
        var store = MainThread.Run(() => new DownloadStore(settings, Path.Combine(dir, "downloads.json")) { Ui = ui }).Result;
        return (settings, store, ui, log);
    }

    private static DownloadItem AddVideo(DownloadStore store) =>
        MainThread.Run(() => store.Add(new NewDownloadRequest(new Uri("https://video.example/watch/1")) { Kind = DownloadKind.video })).Result;

    [Fact]
    public async Task The_store_asks_for_the_quality_and_downloads_what_was_picked()
    {
        if (OperatingSystem.IsWindows()) return;        // the stand-in is a shell script
        var (settings, store, ui, log) = Rig(info => new VideoFormatAnswer(info.Choices[2].Height, info.Choices[2].Nominal, false, info.Choices[2].Selector));
        var item = AddVideo(store);
        await Setup.Until(() => item.State == DownloadState.completed, what: $"completed (is {item.State}: {item.ErrorMessage})");
        Assert.Equal(4, ui.Asked!.Choices.Count);
        var args = File.ReadAllLines(log);
        Assert.StartsWith("http-832/bv*[height<=360]", args[Array.IndexOf(args, "-f") + 1]);
        Assert.StartsWith("http-832/", item.FormatArgs![1]);
        // The picked quality is part of the file name, so another quality of the same video is another file.
        Assert.Equal("360p", item.QualityTag);
        Assert.EndsWith("%(title).150B [%(id)s] 360p.%(ext)s", args[Array.IndexOf(args, "-o") + 1]);
        Assert.Equal("clip.mp4", item.FileName);
        Assert.True(settings.AskVideoQuality);
        Assert.Equal(1080, settings.VideoMaxHeight);
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task Always_use_this_stores_the_choice_and_stops_asking()
    {
        if (OperatingSystem.IsWindows()) return;
        var (settings, store, ui, log) = Rig(_ => new VideoFormatAnswer(-2, -2, true));
        var item = AddVideo(store);
        await Setup.Until(() => item.State == DownloadState.completed, what: "completed");
        Assert.Contains("mp3", File.ReadAllLines(log));
        Assert.Null(item.QualityTag);       // audio only: the extension already tells it apart
        Assert.EndsWith("%(title).150B [%(id)s].%(ext)s", File.ReadAllLines(log)[Array.IndexOf(File.ReadAllLines(log), "-o") + 1]);
        Assert.False(settings.AskVideoQuality);
        Assert.Equal(-2, settings.VideoMaxHeight);

        ui.Asked = null;
        var second = AddVideo(store);
        await Setup.Until(() => second.State == DownloadState.completed, what: "second completed");
        Assert.Null(ui.Asked);
        Assert.Null(second.FormatArgs);
        Assert.Contains("mp3", File.ReadAllLines(log));     // the stored setting applies
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task Closing_the_question_cancels_the_download()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, store, ui, log) = Rig(_ => null);
        var item = AddVideo(store);
        await Setup.Until(() => item.State == DownloadState.cancelled, what: $"cancelled (is {item.State})");
        Assert.NotNull(ui.Asked);
        Assert.False(File.Exists(log));
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task When_the_formats_cannot_be_listed_the_download_goes_ahead_with_the_defaults()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, store, ui, log) = Rig(_ => throw new InvalidOperationException("must not be asked"), probeFails: true);
        var item = AddVideo(store);
        await Setup.Until(() => item.State == DownloadState.completed, what: $"completed (is {item.State}: {item.ErrorMessage})");
        Assert.Null(ui.Asked);
        Assert.Null(item.FormatArgs);
        var args = File.ReadAllLines(log);
        Assert.StartsWith("bv*[height<=1080][vcodec^=avc1]", args[Array.IndexOf(args, "-f") + 1]);
        Assert.Contains("no formats", item.Log);
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task With_the_question_turned_off_nothing_is_asked()
    {
        if (OperatingSystem.IsWindows()) return;
        var (settings, store, ui, log) = Rig(_ => throw new InvalidOperationException("must not be asked"));
        settings.AskVideoQuality = false;
        settings.VideoMaxHeight = 480;
        var item = AddVideo(store);
        await Setup.Until(() => item.State == DownloadState.completed, what: "completed");
        var args = File.ReadAllLines(log);
        Assert.StartsWith("bv*[height<=480]", args[Array.IndexOf(args, "-f") + 1]);
        await MainThread.Run(store.Shutdown);
    }
}

/// Against the real sites with the real yt-dlp; run with MCDM_LIVE=1 (needs yt-dlp, ffmpeg and deno on the machine).
/// The X and Instagram checks take the post to try from MCDM_LIVE_X / MCDM_LIVE_IG and are skipped without them.
public class LiveVideoTests
{
    private sealed class Chooser(Func<VideoInfo, VideoFormatAnswer?> choose) : IStoreUi
    {
        public VideoInfo? Asked;
        public void ShowDetail(Guid id) { }
        public void DetailCompleted(Guid id, bool autoClose) { }
        public void NotifyCompleted(DownloadItem item) { }
        public Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync() => Task.FromResult((false, false));
        public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) { Asked = info; return Task.FromResult(choose(info)); }
    }

    private static async Task<(DownloadItem, VideoInfo)> Download(string url, Func<VideoInfo, VideoFormatAnswer?> choose)
    {
        var dir = Setup.TempDir();
        var settings = AppSettings.Load(Path.Combine(dir, "settings.json"));
        settings.DownloadDirectory = Path.Combine(dir, "dl");
        settings.ShowDetailOnStart = false;
        settings.DenoPromptSuppressed = true;
        var ui = new Chooser(choose);
        var store = await MainThread.Run(() => new DownloadStore(settings, Path.Combine(dir, "downloads.json")) { Ui = ui });
        var item = await MainThread.Run(() => store.Add(new NewDownloadRequest(new Uri(url)) { Kind = DownloadKind.video }));
        await Setup.Until(() => item.State is DownloadState.completed or DownloadState.failed, 180, "the download to end");
        await MainThread.Run(store.Shutdown);
        Assert.True(item.State == DownloadState.completed, item.ErrorMessage + "\n" + item.Log);
        Console.WriteLine($"LIVE {url}\n  offered: {string.Join(", ", ui.Asked!.Choices.Select(c => $"{c.Label} {(c.ApproxBytes is { } b ? Fmt.Size(b) : "?")} [{c.Selector}]"))}\n  got: {item.FinalPath} ({new FileInfo(item.FinalPath!).Length} bytes)");
        return (item, ui.Asked!);
    }

    private static VideoFormatAnswer Smallest(VideoInfo info) { var c = info.Choices[^1]; return new(c.Height, c.Nominal, false, c.Selector); }

    [Fact]
    public async Task Youtube_smallest_quality()
    {
        if (Environment.GetEnvironmentVariable("MCDM_LIVE") != "1") return;
        var (item, info) = await Download("https://www.youtube.com/watch?v=jNQXAC9IVRw", Smallest);
        Assert.EndsWith($"] {info.Choices[^1].Nominal}p.mp4", item.FinalPath);
        Assert.InRange(new FileInfo(item.FinalPath!).Length, info.Choices[^1].ApproxBytes!.Value * 0.9, info.Choices[^1].ApproxBytes!.Value * 1.15);
    }

    [Fact]
    public async Task Youtube_as_mp3()
    {
        if (Environment.GetEnvironmentVariable("MCDM_LIVE") != "1") return;
        var (item, _) = await Download("https://www.youtube.com/watch?v=jNQXAC9IVRw", _ => new VideoFormatAnswer(-2, -2, false));
        Assert.EndsWith(".mp3", item.FinalPath);
        Assert.Equal(FileCategory.music, FileCategories.Detect(item.FileName, DownloadKind.file));
    }

    [Fact]
    public async Task X_smallest_quality_has_the_size_that_was_shown()
    {
        if (Environment.GetEnvironmentVariable("MCDM_LIVE") != "1" || Environment.GetEnvironmentVariable("MCDM_LIVE_X") is not { Length: > 0 } url) return;
        var (item, info) = await Download(url, Smallest);
        Assert.True(info.Choices.Count >= 3);
        Assert.InRange(new FileInfo(item.FinalPath!).Length, info.Choices[^1].ApproxBytes!.Value * 0.97, info.Choices[^1].ApproxBytes!.Value * 1.03);
    }

    [Fact]
    public async Task Instagram_reel_smallest_quality()
    {
        if (Environment.GetEnvironmentVariable("MCDM_LIVE") != "1" || Environment.GetEnvironmentVariable("MCDM_LIVE_IG") is not { Length: > 0 } url) return;
        var (item, info) = await Download(url, Smallest);
        Assert.EndsWith(".mp4", item.FinalPath);
        Assert.True(info.Choices.Count >= 2);
    }
}
