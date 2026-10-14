using System.Text.Json;
using Xunit;

namespace MCDM.Tests;

/// Playlists and subtitles: recognising the address, the yt-dlp options, and the whole flow through the
/// store against a stand-in for yt-dlp.
public class PlaylistTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", PlaylistKind.Only)]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abc", PlaylistKind.Only)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG&index=2", PlaylistKind.WithVideo)]
    [InlineData("https://youtu.be/jNQXAC9IVRw?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", PlaylistKind.WithVideo)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", PlaylistKind.None)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=RDjNQXAC9IVRw", PlaylistKind.None)]     // a "mix": endless
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=", PlaylistKind.None)]
    [InlineData("https://vimeo.com/showcase/123?list=abc", PlaylistKind.None)]                           // only YouTube's lists are known
    public void A_youtube_address_tells_whether_it_carries_a_playlist(string url, PlaylistKind expected) =>
        Assert.Equal(expected, Links.Playlist(new Uri(url)));

    [Fact]
    public void A_flat_listing_gives_the_playlists_name_and_length()
    {
        using var listing = JsonDocument.Parse("""{"_type":"playlist","id":"PLx","title":"Dersler: 2026 güz","playlist_count":3,"entries":[{"id":"a"},{"id":"b"},{"id":"c"}]}""");
        Assert.Equal(new PlaylistInfo("Dersler: 2026 güz", 3, true), VideoFormats.ParsePlaylist(listing.RootElement, only: true));
        using var uncounted = JsonDocument.Parse("""{"_type":"playlist","title":"x","entries":[{"id":"a"},{"id":"b"}]}""");
        Assert.Equal(2, VideoFormats.ParsePlaylist(uncounted.RootElement, only: false)!.Count);
        using var video = JsonDocument.Parse("""{"_type":"video","title":"x","formats":[]}""");
        Assert.Null(VideoFormats.ParsePlaylist(video.RootElement, only: false));
        using var empty = JsonDocument.Parse("""{"_type":"playlist","title":"x","entries":[]}""");
        Assert.Null(VideoFormats.ParsePlaylist(empty.RootElement, only: true));
    }

    [Fact]
    public void Playlist_and_subtitle_choices_become_ytdlp_options()
    {
        List<string> Args(VideoOptions o) => VideoDownloader.BuildArguments(new Uri("https://www.youtube.com/playlist?list=PLx"), "out", [], new Dictionary<string, string>(), null, null, null, o);

        var list = Args(new VideoOptions { Playlist = true, QualityTag = "720p" });
        Assert.Contains("--yes-playlist", list);
        Assert.DoesNotContain("--no-playlist", list);
        // Its own folder, the order of the list in the names, and no quality tag (it differs per video).
        Assert.Equal(Path.Combine("out", "%(playlist_title|Playlist).100B", "%(playlist_index)03d - %(title).150B [%(id)s].%(ext)s"), list[list.IndexOf("-o") + 1]);
        Assert.DoesNotContain("--write-subs", list);

        var subs = Args(new VideoOptions { SubtitleLanguages = "tr,en" });
        Assert.Contains("--no-playlist", subs);
        Assert.Equal("tr,en", subs[subs.IndexOf("--sub-langs") + 1]);
        Assert.Equal("srt", subs[subs.IndexOf("--convert-subs") + 1]);
        Assert.Contains("--write-auto-subs", subs);
        Assert.Contains("--ignore-errors", subs);       // a subtitle the site refuses must not cost the video
        Assert.Equal("--", subs[^2]);

        Assert.Equal((3, 12), VideoDownloader.PlaylistPosition("[download] Downloading item 3 of 12"));
        Assert.Null(VideoDownloader.PlaylistPosition("[download] Destination: x.mp4"));
    }

    [Theory]
    [InlineData("tr,en", "tr,en")]
    [InlineData(" tr ; en  de ", "tr,en,de")]
    [InlineData("en.*,tr-TR", "en.*,tr-TR")]
    [InlineData("--exec,rm;tr", "exec,rm,tr")]      // signs that could be read as an option are dropped
    [InlineData("", "tr,en")]
    [InlineData(null, "tr,en")]
    public void Subtitle_languages_are_reduced_to_a_plain_list(string? typed, string expected) =>
        Assert.Equal(expected, AppSettings.CleanLanguages(typed));

    // ---- The whole flow ----

    private sealed class Chooser(Func<VideoInfo, VideoFormatAnswer?> choose) : IStoreUi
    {
        public VideoInfo? Asked;
        public void ShowDetail(Guid id) { }
        public void DetailCompleted(Guid id, bool autoClose) { }
        public void NotifyCompleted(DownloadItem item) { }
        public Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync() => Task.FromResult((false, false));
        public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) { Asked = info; return Task.FromResult(choose(info)); }
    }

    /// A shell script in yt-dlp's place. `--flat-playlist` gets a three-video listing, `-J` the formats of
    /// one video; a download run records its arguments and "downloads" the first and the third video of
    /// the list (the second is "unavailable"), ending with exit code 1 the way yt-dlp does then.
    private static (AppSettings, DownloadStore, Chooser, string Dir) Rig(Func<VideoInfo, VideoFormatAnswer?> choose)
    {
        var dir = Setup.TempDir();
        var fake = Path.Combine(dir, "yt-dlp");
        var formats = Path.Combine(AppContext.BaseDirectory, "Fixtures", "formats-x.json");
        File.WriteAllText(fake, $$"""
            #!/bin/sh
            for a in "$@"; do if [ "$a" = "--flat-playlist" ]; then
              echo '{"_type":"playlist","title":"Yaz okulu","playlist_count":3,"entries":[{"id":"a"},{"id":"b"},{"id":"c"}]}'; exit 0
            fi; done
            for a in "$@"; do if [ "$a" = "-J" ]; then cat '{{formats}}'; exit 0; fi; done
            printf '%s\n' "$@" > '{{dir}}/args.log'
            list=no; for a in "$@"; do if [ "$a" = "--yes-playlist" ]; then list=yes; fi; done
            if [ "$list" = no ]; then
              out='{{dir}}/dl/tek video [a].mp4'; mkdir -p '{{dir}}/dl'
              echo "[download] Destination: $out"; printf 'video' > "$out"; printf 'altyazi' > '{{dir}}/dl/tek video [a].tr.srt'; echo "$out"; exit 0
            fi
            folder='{{dir}}/dl/Yaz okulu'; mkdir -p "$folder"
            echo "[download] Downloading item 1 of 3"
            echo "[download] Destination: $folder/001 - Giriş [a].f137.mp4"
            echo "DMP|500|1000|NA|2048.5"
            printf 'birinci' > "$folder/001 - Giriş [a].mp4"; echo "$folder/001 - Giriş [a].mp4"
            echo "[download] Downloading item 2 of 3"
            echo "ERROR: [youtube] b: Video unavailable" >&2
            echo "[download] Downloading item 3 of 3"
            echo "[download] Destination: $folder/003 - Kapanış [c].f137.mp4"
            printf 'ucuncu!' > "$folder/003 - Kapanış [c].mp4"; echo "$folder/003 - Kapanış [c].mp4"
            exit 1
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
        return (settings, store, ui, dir);
    }

    private static DownloadItem Add(DownloadStore store, string url) =>
        MainThread.Run(() => store.Add(new NewDownloadRequest(new Uri(url)) { Kind = DownloadKind.video })).Result;

    private static string[] Args(string dir) => File.ReadAllLines(Path.Combine(dir, "args.log"));

    [Fact]
    public async Task A_playlist_goes_into_its_own_folder_and_counts_as_done_with_what_could_be_fetched()
    {
        if (OperatingSystem.IsWindows()) return;        // the stand-in is a shell script
        var (_, store, ui, dir) = Rig(info => new VideoFormatAnswer(info.Choices[1].Height, info.Choices[1].Nominal, false, info.Choices[1].Selector) { Playlist = true });
        var item = Add(store, "https://www.youtube.com/playlist?list=PLabc");
        await Setup.Until(() => item.State is DownloadState.completed or DownloadState.failed, what: "the playlist to end");
        Assert.True(item.State == DownloadState.completed, item.ErrorMessage + "\n" + item.Log);

        // The question named the playlist.
        Assert.Equal(new PlaylistInfo("Yaz okulu", 3, true), ui.Asked!.Playlist);
        var args = Args(dir);
        Assert.Contains("--yes-playlist", args);
        // The 720p that was picked becomes a general cap: format ids differ from video to video.
        Assert.StartsWith("bv*[height<=720]", args[Array.IndexOf(args, "-f") + 1]);
        Assert.Contains("%(playlist_title|Playlist).100B", args[Array.IndexOf(args, "-o") + 1]);

        // The row is the folder; the video that was not there shows as a remark, not as a failure.
        var folder = Path.Combine(dir, "dl", "Yaz okulu");
        Assert.Equal(folder, item.FinalPath);
        Assert.Equal("Yaz okulu", item.FileName);
        Assert.Equal("birinci".Length + "ucuncu!".Length, item.TotalBytes);
        Assert.Equal("2 of 3 videos saved", item.Note);
        Assert.Contains("2 of 3 videos saved", item.StatusText);
        Assert.Contains("Video unavailable", item.Log);
        Assert.True(item.Playlist);
        Assert.Null(item.QualityTag);

        // Removing it "with its file" takes the folder.
        await MainThread.Run(() => store.Remove(item.Id, deleteFile: true));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(store.Items);
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task One_video_out_of_a_playlist_stays_one_video_unless_the_whole_list_is_asked_for()
    {
        if (OperatingSystem.IsWindows()) return;
        var elsewhere = Path.Combine(Setup.TempDir(), "videolar");
        var (settings, store, ui, dir) = Rig(info => new VideoFormatAnswer(info.Choices[0].Height, info.Choices[0].Nominal, false, info.Choices[0].Selector) { Subtitles = true, Directory = elsewhere });
        var item = Add(store, "https://www.youtube.com/watch?v=aaaaaaaaaaa&list=PLabc");
        await Setup.Until(() => item.State is DownloadState.completed or DownloadState.failed, what: "the video to end");
        Assert.True(item.State == DownloadState.completed, item.ErrorMessage + "\n" + item.Log);

        Assert.Equal(new PlaylistInfo("Yaz okulu", 3, false), ui.Asked!.Playlist);   // offered, not taken
        var args = Args(dir);
        Assert.Contains("--no-playlist", args);
        Assert.StartsWith("http-10368/", args[Array.IndexOf(args, "-f") + 1]);        // the exact format that was shown
        Assert.Equal("tr,en", args[Array.IndexOf(args, "--sub-langs") + 1]);
        // The folder picked in the question is used as it is.
        Assert.StartsWith(elsewhere + Path.DirectorySeparatorChar, args[Array.IndexOf(args, "-o") + 1]);
        Assert.False(item.Playlist);
        Assert.Null(item.Note);
        Assert.True(item.Subtitles);
        Assert.True(settings.VideoSubtitles);           // the next question starts from the last choice
        await MainThread.Run(store.Shutdown);
    }

    [Fact]
    public async Task With_the_question_off_an_address_that_is_only_a_playlist_is_still_downloaded_as_one()
    {
        if (OperatingSystem.IsWindows()) return;
        var (settings, store, _, dir) = Rig(_ => throw new InvalidOperationException("must not be asked"));
        settings.AskVideoQuality = false;
        settings.VideoSubtitles = true;
        settings.SubtitleLanguages = "de";
        var item = Add(store, "https://www.youtube.com/playlist?list=PLabc");
        await Setup.Until(() => item.State is DownloadState.completed or DownloadState.failed, what: "the playlist to end");
        Assert.True(item.State == DownloadState.completed, item.ErrorMessage + "\n" + item.Log);
        var args = Args(dir);
        Assert.Contains("--yes-playlist", args);
        Assert.Equal("de", args[Array.IndexOf(args, "--sub-langs") + 1]);
        Assert.Equal(Path.Combine(dir, "dl", "Yaz okulu"), item.FinalPath);
        await MainThread.Run(store.Shutdown);
    }
}
