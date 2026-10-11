using System.Globalization;
using Xunit;

namespace MCDM.Tests;

public class MiscTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j.txt", "a_b_c_d_e_f_g_h_i_j.txt")]
    [InlineData("  spaced name.zip  ", "spaced name.zip")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData(".htaccess", "_.htaccess")]
    [InlineData("", "download")]
    [InlineData("...", "download")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("Dilekçe Örneği (İstanbul).docx", "Dilekçe Örneği (İstanbul).docx")]
    public void Sanitizes_names_for_ntfs(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Long_names_are_cut_but_keep_their_extension()
    {
        var n = FileNames.Sanitize(new string('a', 400) + ".mkv");
        Assert.True(n.Length <= 200);
        Assert.EndsWith(".mkv", n);
    }

    [Fact]
    public void Category_and_capture_matching_ignore_turkish_casing_rules()
    {
        // Under tr-TR, "ZIP".ToLower() is "zıp" and "iso".ToUpper() is "İSO".
        Assert.Equal("tr-TR", CultureInfo.CurrentCulture.Name);
        Assert.Equal(FileCategory.compressed, FileCategories.Detect("ARCHIVE.ZIP", DownloadKind.file));
        Assert.Equal(FileCategory.compressed, FileCategories.Detect("DISK.ISO", DownloadKind.file));
        Assert.Equal(FileCategory.image, FileCategories.Detect("FOTO.TIFF", DownloadKind.file));
        Assert.Equal(FileCategory.other, FileCategories.Detect("noextension", DownloadKind.file));
        Assert.Equal(FileCategory.video, FileCategories.Detect("whatever.bin", DownloadKind.video));
        var exts = CaptureTypes.Extensions(["compressed"], " .TORRENT, srt;ISO ");
        Assert.Contains("torrent", exts);
        Assert.Contains("srt", exts);
        Assert.Equal(1, exts.Count(e => e == "iso"));
        Assert.True(CaptureTypes.Matches(new Uri("https://x.example/a/B.ISO?token=1"), exts));
        Assert.False(CaptureTypes.Matches(new Uri("https://x.example/a/page.html"), exts));
        Assert.True(VideoDownloader.IsYouTube(new Uri("https://WWW.YOUTUBE.COM/watch?v=1")));
        Assert.True(VideoDownloader.IsYouTube(new Uri("https://youtu.be/1")));
        Assert.False(VideoDownloader.IsYouTube(new Uri("https://notyoutube.com/1")));
    }

    [Fact]
    public void File_names_come_from_the_last_url_segment()
    {
        Assert.Equal("özet dosyası.pdf", FileNames.FromUrl(new Uri("https://x.example/a/%C3%B6zet%20dosyas%C4%B1.pdf?dl=1")));
        Assert.Equal("x.example", FileNames.FromUrl(new Uri("https://x.example/")));
        Assert.Equal("dir", FileNames.FromUrl(new Uri("https://x.example/dir/")));
    }

    [Fact]
    public void Clipboard_text_counts_only_when_it_is_nothing_but_links()
    {
        Assert.Equal(2, Links.UrlOnlyContent("https://a.example/x.zip\r\n\r\n  http://b.example/y.rar ")!.Count);
        Assert.Single(Links.UrlOnlyContent("https://a.example/x.zip\nhttps://a.example/x.zip")!);
        Assert.Null(Links.UrlOnlyContent("see https://a.example/x.zip"));
        Assert.Null(Links.UrlOnlyContent("https://a.example/x.zip and more"));
        Assert.Null(Links.UrlOnlyContent("ftp://a.example/x.zip"));
        Assert.Null(Links.UrlOnlyContent(""));
        Assert.Null(Links.UrlOnlyContent("C:\\Users\\x\\file.zip"));
        Assert.True(Links.LooksLikeVideoPage(new Uri("https://www.youtube.com/watch?v=1")));
        Assert.True(Links.LooksLikeVideoPage(new Uri("https://x.com/user/status/1")));
        Assert.False(Links.LooksLikeVideoPage(new Uri("https://www.youtube.com/intro.mp4")));
        Assert.False(Links.LooksLikeVideoPage(new Uri("https://example.com/watch")));
    }

    [Fact]
    public void Ytdlp_lines_become_stages_and_numbers_parse_without_the_locale()
    {
        Assert.Equal("Downloading video…", VideoDownloader.Stage(@"[download] Destination: C:\Users\x\Video\Title [abc].f299.mp4"));
        Assert.Equal("Downloading audio…", VideoDownloader.Stage(@"[download] Destination: C:\Users\x\Video\Title [abc].f140.m4a"));
        Assert.Equal("Downloading audio…", VideoDownloader.Stage("[download] Destination: /tmp/Title [abc].f251-drc.webm"));
        Assert.Equal("Downloading…", VideoDownloader.Stage("[download] Destination: /tmp/Title [abc].mp4"));
        Assert.Equal("Merging video and audio (ffmpeg)…", VideoDownloader.Stage("[Merger] Merging formats into \"x.mp4\""));
        Assert.Null(VideoDownloader.Stage("[youtube] abc: Downloading webpage"));
        // "1912913608.5" must not turn into 19129136085 under a comma-decimal culture
        Assert.Equal(1912913608.5, VideoDownloader.ParseNumber("1912913608.5"));
        Assert.Null(VideoDownloader.ParseNumber("NA"));
        var stage = VideoDownloader.Stage("MCFMT|1920x818|mp4|avc1.64002A|mp4a.40.2|1912913608|299+140|4572");
        Assert.StartsWith("Selected: 1920x818 · MP4 · avc1 · ~1", stage);
        Assert.Equal("Title [abc].mp4", VideoDownloader.StripFormatSuffix("Title [abc].f299.mp4"));
        Assert.Equal("Title [abc].mp4", VideoDownloader.StripFormatSuffix("Title [abc].mp4"));
    }

    [Fact]
    public void Ytdlp_arguments_follow_the_format_settings()
    {
        var s = AppSettings.Load(Path.Combine(Setup.TempDir(), "s.json"));
        Assert.Equal("-f bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=1080][ext=mp4]+ba[ext=m4a]/bv*[height<=1080]+ba/b[height<=1080]/b --merge-output-format mp4 --remux-video mp4", s.EffectiveArguments);
        s.VideoMaxHeight = -2;
        Assert.Equal("-f ba/b -x --audio-format mp3 --audio-quality 0", s.EffectiveArguments);
        s.VideoMaxHeight = 0; s.VideoContainer = "mkv";
        Assert.Equal("-f bv*+ba/b/b --merge-output-format mkv", s.EffectiveArguments);
        s.VideoFormat = "best[height<=480]"; s.VideoContainer = "original";
        Assert.Equal("-f best[height<=480] --merge-output-format mkv", s.EffectiveArguments);

        var args = VideoDownloader.BuildArguments(new Uri("https://www.youtube.com/watch?v=abc"), @"C:\dl\Video", ["-f", "b"],
            new Dictionary<string, string> { ["User-Agent"] = "UA", ["Referer"] = "https://r.example/" }, @"C:\c.txt", @"C:\t\ffmpeg.exe", @"C:\t\deno.exe");
        Assert.Equal("https://www.youtube.com/watch?v=abc", args[^1]);
        Assert.Equal("--", args[^2]);
        Assert.Equal(@"C:\c.txt", args[args.IndexOf("--cookies") + 1]);
        Assert.Equal(@"deno:C:\t\deno.exe", args[args.IndexOf("--js-runtimes") + 1]);
        Assert.Equal("UA", args[args.IndexOf("--user-agent") + 1]);
        Assert.Contains("--no-playlist", args);
        Assert.DoesNotContain("--cookies", VideoDownloader.BuildArguments(new Uri("https://v.example/1"), "/dl", [], new Dictionary<string, string>(), null, null, null));
    }

    [Fact]
    public void Settings_round_trip_and_ignore_damaged_values()
    {
        var file = Path.Combine(Setup.TempDir(), "settings.json");
        var s = AppSettings.Load(file);
        Assert.Equal(8, s.SegmentCount);
        Assert.True(s.WatchClipboard);
        s.SegmentCount = 99;            // clamped
        s.SetCaptureGroup("image", true);
        s.SetCaptureGroup("video", false);
        s.CustomExtensions = "torrent";
        s.OnboardingDone = true;
        var again = AppSettings.Load(file);
        Assert.Equal(16, again.SegmentCount);
        Assert.Contains("image", again.CaptureGroupIds);
        Assert.DoesNotContain("video", again.CaptureGroupIds);
        Assert.Contains("torrent", again.CaptureExtensions);
        Assert.True(again.OnboardingDone);

        File.WriteAllText(file, "{\"SegmentCount\":\"many\",\"MaxConcurrent\":5,\"Unknown\":1}");
        var partial = AppSettings.Load(file);
        Assert.Equal(8, partial.SegmentCount);
        Assert.Equal(5, partial.MaxConcurrent);
        File.WriteAllText(file, "not json");
        Assert.Equal(3, AppSettings.Load(file).MaxConcurrent);
    }

    [Fact]
    public void Site_cookie_files_are_validated_and_scoped_to_their_domains()
    {
        var dir = Setup.TempDir();
        var good = Path.Combine(dir, "cookies.txt");
        File.WriteAllText(good, "# Netscape HTTP Cookie File\r\n# comment\r\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tSID\tx\r\n#HttpOnly_.google.com\tTRUE\t/\tTRUE\t1893456000\tHSID\ty\r\n");
        var bad = Path.Combine(dir, "notes.txt");
        File.WriteAllText(bad, "just some text\n");
        var jar = SiteCookies.Shared;
        jar.Clear();
        Assert.Throws<DownloadFailure>(() => jar.Import(bad));
        Assert.Equal(0, jar.CookieCount);
        jar.Import(good);
        Assert.Equal(2, jar.CookieCount);
        Assert.Equal(["google.com", "youtube.com"], jar.Domains);
        Assert.True(jar.AppliesTo("www.youtube.com"));
        Assert.True(jar.AppliesTo("accounts.google.com"));
        Assert.False(jar.AppliesTo("vimeo.com"));
        Assert.False(jar.AppliesTo("notyoutube.com"));
        Assert.Equal(SiteCookies.FilePath, VideoDownloader.MergedCookieFile(null, "m.youtube.com"));
        Assert.Null(VideoDownloader.MergedCookieFile(null, "vimeo.com"));
        jar.Clear();
        Assert.Equal(0, jar.CookieCount);
        Assert.False(jar.AppliesTo("www.youtube.com"));
    }

    [Fact]
    public void Sizes_and_durations_read_like_explorer()
    {
        Assert.Equal("512 bytes", Fmt.Size(512));
        Assert.Equal("1,00 KB", Fmt.Size(1024));
        Assert.Equal("37,1 MB", Fmt.Size((long)(37.1 * 1024 * 1024)));
        Assert.Equal("2,30 GB", Fmt.Size((long)(2.3 * 1024 * 1024 * 1024)));
        Assert.Equal("120 MB", Fmt.Size(120L * 1024 * 1024));
        Assert.Equal("?", Fmt.Size(-1));
        Assert.Equal("15 s", Fmt.Duration(15));
        Assert.Equal("1 min 35 s", Fmt.Duration(95));
        Assert.Equal("2 h 5 min", Fmt.Duration(7500));
    }

    [Fact]
    public void Http_requests_are_parsed_only_when_complete()
    {
        byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        Assert.Null(LocalServer.Parse(B("POST /download HTTP/1.1\r\nHost: 127.0.0.1:48213\r\n")));
        Assert.Null(LocalServer.Parse(B("POST /download HTTP/1.1\r\nContent-Length: 10\r\n\r\n12345")));
        var r = LocalServer.Parse(B("post /download?x=1 HTTP/1.1\r\nHost: 127.0.0.1:48213\r\nX-MCDM-Extension:  chrome \r\nContent-Length: 5\r\n\r\n12345extra"))!;
        Assert.Equal("POST", r.Method);
        Assert.Equal("/download?x=1", r.Path);
        Assert.Equal("chrome", r.Headers["x-mcdm-extension"]);
        Assert.Equal("12345", System.Text.Encoding.UTF8.GetString(r.Body));
        Assert.Empty(LocalServer.Parse(B("GET /ping HTTP/1.1\r\nContent-Length: -5\r\n\r\n"))!.Body);
    }

    [Fact]
    public void A_cancelled_video_takes_its_part_files_with_it_and_nothing_else()
    {
        var dir = Setup.TempDir();
        const string stem = "Bizim Dizinin Yeni Bölümü ｜ Işık [l4zTyNw1DjI]";
        string[] leftovers =
        [
            stem + ".f299.mp4.part", stem + ".f140.m4a", stem + ".f251-drc.webm.part", stem + ".mp4.part", stem + ".mp4.ytdl",
            stem + ".f299.mp4.part-Frag12", stem + ".f299.mp4.part-Frag12.part", stem + ".temp.mp4",
        ];
        string[] kept =
        [
            stem + ".mp4", stem + ".mp3", stem + ".f4v", stem + " (2).mp4", "Başka Video [abcdefghijk].f299.mp4.part",
            stem + "x.mp4.part", ".dm-0f0f.part",
        ];
        foreach (var f in leftovers.Concat(kept)) File.WriteAllText(Path.Combine(dir, f), "x");

        // The list shows the name without yt-dlp's format suffix; either form finds the same files.
        Assert.True(VideoDownloader.DeletePartials(dir, stem + ".f299.mp4"));
        foreach (var f in leftovers) Assert.False(File.Exists(Path.Combine(dir, f)), f);
        foreach (var f in kept) Assert.True(File.Exists(Path.Combine(dir, f)), f);

        // Before yt-dlp names a file the row carries the host or the bare title: nothing to look for.
        File.WriteAllText(Path.Combine(dir, "www.youtube.com.part"), "x");
        File.WriteAllText(Path.Combine(dir, "Me at the zoo.mp4.part"), "x");
        Assert.True(VideoDownloader.DeletePartials(dir, "www.youtube.com"));
        Assert.True(VideoDownloader.DeletePartials(dir, "Me at the zoo"));
        Assert.True(File.Exists(Path.Combine(dir, "www.youtube.com.part")));
        Assert.True(File.Exists(Path.Combine(dir, "Me at the zoo.mp4.part")));
        Assert.True(VideoDownloader.DeletePartials(Path.Combine(dir, "missing"), stem + ".mp4"));

        if (OperatingSystem.IsWindows())
        {
            // A file the dying process still holds open cannot be deleted yet; the caller retries.
            var held = Path.Combine(dir, stem + ".f18.mp4.part");
            using (new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.None))
                Assert.False(VideoDownloader.DeletePartials(dir, stem + ".mp4"));
            Assert.True(VideoDownloader.DeletePartials(dir, stem + ".mp4"));
            Assert.False(File.Exists(held));
        }
    }

    [Fact]
    public void Leftovers_of_a_video_with_a_quality_in_its_name_are_found_too()
    {
        var dir = Setup.TempDir();
        const string stem = "Ders 4 [abc123XYZ_-] 720p";
        string[] leftovers = [stem + ".f136.mp4.part", stem + ".f140.m4a", stem + ".mp4.ytdl"];
        // The 1080p copy of the same video and its own part file belong to another download.
        string[] kept = ["Ders 4 [abc123XYZ_-] 1080p.mp4", "Ders 4 [abc123XYZ_-] 1080p.f137.mp4.part", "Ders 4 [abc123XYZ_-].mp4", stem + ".mp4"];
        foreach (var f in leftovers.Concat(kept)) File.WriteAllText(Path.Combine(dir, f), "x");
        Assert.True(VideoDownloader.DeletePartials(dir, stem + ".f136.mp4"));
        foreach (var f in leftovers) Assert.False(File.Exists(Path.Combine(dir, f)), f);
        foreach (var f in kept) Assert.True(File.Exists(Path.Combine(dir, f)), f);
    }

    [Fact]
    public void The_quality_tag_reaches_the_output_name_only_in_its_expected_form()
    {
        string Template(string? tag) => VideoDownloader.BuildArguments(new Uri("https://v.example/1"), "out", [], new Dictionary<string, string>(), null, null, null, tag)
            is var a ? Path.GetFileName(a[a.IndexOf("-o") + 1]) : "";
        Assert.Equal("%(title).150B [%(id)s] 1080p.%(ext)s", Template("1080p"));
        Assert.Equal("%(title).150B [%(id)s].%(ext)s", Template(null));
        Assert.Equal("%(title).150B [%(id)s].%(ext)s", Template("%(uploader)s"));      // never a template of someone's making
        Assert.Equal("%(title).150B [%(id)s].%(ext)s", Template("../x"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\MC Download Manager\\MCDownloadManager.exe\" --background", "C:\\Program Files\\MC Download Manager\\MCDownloadManager.exe")]
    [InlineData("C:\\apps\\MCDownloadManager.exe --background", "C:\\apps\\MCDownloadManager.exe")]
    [InlineData("C:\\apps\\MCDownloadManager.exe", "C:\\apps\\MCDownloadManager.exe")]
    public void The_program_is_read_out_of_a_startup_command(string command, string expected) =>
        Assert.Equal(expected, Shell.CommandProgram(command));
}
