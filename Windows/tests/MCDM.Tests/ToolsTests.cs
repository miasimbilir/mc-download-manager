using System.IO.Compression;
using Xunit;

namespace MCDM.Tests;

/// yt-dlp and ffmpeg no longer come with the app: the first video download asks for them.
public class ToolsTests
{
    private sealed class ToolsUi(bool install) : IStoreUi
    {
        public readonly List<string[]> Asked = [];
        public void ShowDetail(Guid id) { }
        public void DetailCompleted(Guid id, bool autoClose) { }
        public void NotifyCompleted(DownloadItem item) { }
        public async Task<(bool Install, bool DontAskAgain)> AskInstallToolsAsync(IReadOnlyList<string> tools)
        {
            Asked.Add([.. tools]);
            await Task.Delay(300);      // a person reads the question meanwhile: other videos come in and wait too
            return (install, false);
        }
        public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) => Task.FromResult<VideoFormatAnswer?>(VideoFormatAnswer.Default);
    }

    [Fact]
    public async Task Asks_once_for_the_tools_of_the_waiting_videos_and_asks_again_for_the_next_one()
    {
        VideoDownloader.SearchSystem = false;       // as on a PC where nothing is installed yet
        try
        {
            var ui = new ToolsUi(install: false);
            using var rig = new StoreTests.Rig(s => { s.MaxConcurrent = 3; s.YtdlpPath = ""; }, ui);
            var first = rig.Add("https://video.example/watch/1", DownloadKind.video);
            var second = rig.Add("https://video.example/watch/2", DownloadKind.video);
            await rig.Wait(first, DownloadState.failed);
            await rig.Wait(second, DownloadState.failed);
            Assert.Equal([["yt-dlp", "ffmpeg"]], ui.Asked);      // not YouTube: no deno
            Assert.Contains("yt-dlp not found", first.ErrorMessage);

            // Saying no once does not stop the question for good: the next video asks again, and YouTube adds deno.
            var third = rig.Add("https://www.youtube.com/watch?v=dQw4w9WgXcQ", DownloadKind.video);
            await rig.Wait(third, DownloadState.failed);
            Assert.Equal(2, ui.Asked.Count);
            Assert.Equal(["yt-dlp", "ffmpeg", "deno"], ui.Asked[1]);
        }
        finally
        {
            VideoDownloader.SearchSystem = true;
        }
    }

    [Fact]
    public async Task Not_now_on_the_full_question_still_offers_deno_with_the_next_video()
    {
        VideoDownloader.SearchSystem = false;
        try
        {
            var ui = new ToolsUi(install: false);
            using var rig = new StoreTests.Rig(s => { s.YtdlpPath = ""; s.DenoPromptSuppressed = false; }, ui);
            var first = rig.Add("https://www.youtube.com/watch?v=jNQXAC9IVRw", DownloadKind.video);
            await rig.Wait(first, DownloadState.failed);
            var second = rig.Add("https://www.youtube.com/watch?v=aqz-KE-bpKQ", DownloadKind.video);
            await rig.Wait(second, DownloadState.failed);
            Assert.Equal([["yt-dlp", "ffmpeg", "deno"], ["yt-dlp", "ffmpeg", "deno"]], ui.Asked);
        }
        finally
        {
            VideoDownloader.SearchSystem = true;
        }
    }

    [Fact]
    public async Task A_refused_ffmpeg_is_not_asked_again_after_the_quality_question()
    {
        if (OperatingSystem.IsWindows()) return;        // the stand-in yt-dlp is a shell script
        VideoDownloader.SearchSystem = false;           // no ffmpeg anywhere
        try
        {
            var dir = Setup.TempDir();
            var fake = Path.Combine(dir, "yt-dlp");
            var listing = Path.Combine(AppContext.BaseDirectory, "Fixtures", "formats-x.json");
            File.WriteAllText(fake, $$"""
                #!/bin/sh
                for a in "$@"; do if [ "$a" = "-J" ]; then cat '{{listing}}'; exit 0; fi; done
                out='{{dir}}/dl/clip.mp4'
                mkdir -p '{{dir}}/dl'
                echo "[download] Destination: $out"
                printf 'video' > "$out"
                echo "$out"
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var ui = new ToolsUi(install: false);
            using var rig = new StoreTests.Rig(s => { s.YtdlpPath = fake; s.AskVideoQuality = true; s.VideoFormat = ""; }, ui);
            var item = rig.Add("https://video.example/watch/1", DownloadKind.video);
            await rig.Wait(item, DownloadState.completed);
            Assert.Equal([["ffmpeg"]], ui.Asked);
        }
        finally
        {
            VideoDownloader.SearchSystem = true;
        }
    }

    [Fact]
    public void Unpacks_ffmpeg_ffprobe_their_dlls_and_the_licence_from_the_ffmpeg_build()
    {
        var dir = Setup.TempDir();
        var zip = Path.Combine(dir, "ffmpeg.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[]
            {
                "ffmpeg-master-latest-win64-gpl-shared/bin/ffmpeg.exe", "ffmpeg-master-latest-win64-gpl-shared/bin/ffprobe.exe",
                "ffmpeg-master-latest-win64-gpl-shared/bin/ffplay.exe", "ffmpeg-master-latest-win64-gpl-shared/bin/avcodec-62.dll",
                "ffmpeg-master-latest-win64-gpl-shared/LICENSE.txt", "ffmpeg-master-latest-win64-gpl-shared/doc/ffmpeg.html",
                "ffmpeg-master-latest-win64-gpl-shared/lib/avcodec.lib", "ffmpeg-master-latest-win64-gpl-shared/include/x/bin/y.dll",
            })
                using (var w = new StreamWriter(archive.CreateEntry(name).Open())) w.Write(name);
        }
        var folder = Path.Combine(dir, "out");
        var exe = ToolInstaller.UnpackFfmpeg(zip, folder);
        Assert.Equal(Path.Combine(folder, "ffmpeg.exe"), exe);
        Assert.Equal(["LICENSE.txt", "avcodec-62.dll", "ffmpeg.exe", "ffprobe.exe"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_archive_without_ffmpeg_is_refused()
    {
        var dir = Setup.TempDir();
        var zip = Path.Combine(dir, "other.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("build/bin/other.exe");
        Assert.Throws<DownloadFailure>(() => ToolInstaller.UnpackFfmpeg(zip, Path.Combine(dir, "out")));
    }

    [Theory]
    [InlineData("ffmpeg version N-128713-g0f89d936b3-20261108 Copyright (c) 2000-2026 the FFmpeg developers", "ffmpeg N-128713-g0f89d936b3-20261108")]
    [InlineData("ffmpeg version 7.1", "ffmpeg 7.1")]
    [InlineData("something else", "something else")]
    public void The_ffmpeg_version_line_loses_its_copyright(string line, string shown) =>
        Assert.Equal(shown, ToolInstaller.ShortFfmpegVersion(line));
}
