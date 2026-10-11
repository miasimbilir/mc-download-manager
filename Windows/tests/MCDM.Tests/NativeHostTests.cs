using System.Text;
using System.Text.Json;
using Xunit;

namespace MCDM.Tests;

public class NativeHostTests
{
    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. BitConverter.GetBytes((uint)body.Length), .. body];
    }

    private static List<JsonElement> Replies(byte[] output)
    {
        var list = new List<JsonElement>();
        for (int i = 0; i + 4 <= output.Length;)
        {
            var n = (int)BitConverter.ToUInt32(output, i);
            list.Add(JsonDocument.Parse(output.AsMemory(i + 4, n)).RootElement);
            i += 4 + n;
        }
        return list;
    }

    [Fact]
    public void The_host_answers_each_framed_message_until_the_browser_closes_the_pipe()
    {
        var launches = 0;
        var input = new MemoryStream([.. Frame("""{"action":"ping"}"""), .. Frame("""{"action":"launchApp"}"""), .. Frame("""{"action":"format-disk"}"""), .. Frame("""{"other":1}""")]);
        var output = new MemoryStream();
        NativeHost.Serve(input, output, () => { launches++; return true; });
        var replies = Replies(output.ToArray());
        Assert.Equal(4, replies.Count);
        Assert.True(replies[0].GetProperty("ok").GetBoolean());
        Assert.True(replies[1].GetProperty("ok").GetBoolean());
        Assert.False(replies[2].GetProperty("ok").GetBoolean());
        Assert.False(replies[3].GetProperty("ok").GetBoolean());
        Assert.Equal(1, launches);                  // only launchApp starts anything
    }

    [Fact]
    public void A_failed_launch_is_reported_and_broken_input_ends_the_host_quietly()
    {
        var output = new MemoryStream();
        NativeHost.Serve(new MemoryStream(Frame("""{"action":"launchApp"}""")), output, () => false);
        Assert.False(Replies(output.ToArray()).Single().GetProperty("ok").GetBoolean());

        foreach (var bad in new[] { Array.Empty<byte>(), new byte[] { 1, 2 }, [.. BitConverter.GetBytes(50u), 1, 2, 3], [.. BitConverter.GetBytes(0u)],
                                    [.. BitConverter.GetBytes(uint.MaxValue), 1], Frame("not json") })
        {
            var o = new MemoryStream();
            NativeHost.Serve(new MemoryStream(bad), o, () => throw new InvalidOperationException("must not launch"));
            Assert.Equal(0, o.Length);
        }
    }

    [Theory]
    [InlineData(new[] { "chrome-extension://knldjmfmopnpolahpmmgbagdohdnhkik/", "--parent-window=0" }, true)]
    [InlineData(new[] { @"C:\Users\x\AppData\Roaming\MC Download Manager\native-host\firefox.json", "mc-download-manager@mstcvk.github.io" }, true)]
    [InlineData(new[] { "--background" }, false)]
    [InlineData(new[] { "--selftest", "report.txt" }, false)]
    [InlineData(new string[0], false)]
    public void Only_a_browser_call_is_taken_for_a_host_invocation(string[] args, bool expected) =>
        Assert.Equal(expected, NativeHost.IsHostInvocation(args));

    [Fact]
    public void Extension_ids_are_derived_from_the_folder_the_way_chromium_does_it()
    {
        // SHA-256 of the path (UTF-8 here, UTF-16 with an upper-case drive letter on Windows), first 16 bytes, 0-f → a-p
        Assert.Equal("bekpfpnobkpfhgfeifgmfkngamgcgbom", NativeHostRegistry.UnpackedExtensionId("/Users/x/Downloads/MC Download Manager/Browser Plugins/Google Chrome", windows: false));
        const string edge = @"C:\Users\x\Downloads\MC Download Manager\Browser Plugins\Microsoft Edge";
        Assert.Equal("flopcoekjmklnfojpgkgiielliabpjmn", NativeHostRegistry.UnpackedExtensionId(edge, windows: true));
        Assert.Equal("flopcoekjmklnfojpgkgiielliabpjmn", NativeHostRegistry.UnpackedExtensionId("c" + edge[1..], windows: true));
        Assert.True(NativeHostRegistry.IsValidChromiumId("bekpfpnobkpfhgfeifgmfkngamgcgbom"));
        Assert.False(NativeHostRegistry.IsValidChromiumId("bekpfpnobkpfhgfeifgmfkngamgcgboz"));    // q-z never occur
        Assert.False(NativeHostRegistry.IsValidChromiumId("abc"));
    }

    [Fact]
    public void Only_extensions_that_reached_the_bridge_or_sit_in_a_wizard_folder_may_start_the_app()
    {
        var settings = AppSettings.Load(Path.Combine(Setup.TempDir(), "s.json"));
        Assert.False(NativeHostRegistry.NoteExtensionId(settings, null));
        Assert.False(NativeHostRegistry.NoteExtensionId(settings, "https://evil.example"));
        Assert.True(NativeHostRegistry.NoteExtensionId(settings, "KNLDJMFMOPNPOLAHPMMGBAGDOHDNHKIK"));
        Assert.False(NativeHostRegistry.NoteExtensionId(settings, "knldjmfmopnpolahpmmgbagdohdnhkik"));      // already known
        var ids = NativeHostRegistry.AllowedChromiumIds(settings);
        Assert.Contains("knldjmfmopnpolahpmmgbagdohdnhkik", ids);
        Assert.Contains(NativeHostRegistry.UnpackedExtensionId(BrowserIntegration.PluginFolder(BrowserKind.All[0])), ids);
        Assert.All(ids, id => Assert.True(NativeHostRegistry.IsValidChromiumId(id)));

        using var chromium = JsonDocument.Parse(NativeHostRegistry.BuildManifest(@"C:\Apps\MC Download Manager\MCDownloadManager.exe", firefox: false, ids));
        Assert.Equal("com.mesutcevik.mcdownloadmanager", chromium.RootElement.GetProperty("name").GetString());
        Assert.Equal("stdio", chromium.RootElement.GetProperty("type").GetString());
        Assert.Equal(@"C:\Apps\MC Download Manager\MCDownloadManager.exe", chromium.RootElement.GetProperty("path").GetString());
        Assert.Contains("chrome-extension://knldjmfmopnpolahpmmgbagdohdnhkik/", chromium.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()));
        Assert.False(chromium.RootElement.TryGetProperty("allowed_extensions", out _));

        using var firefox = JsonDocument.Parse(NativeHostRegistry.BuildManifest("x.exe", firefox: true, ids));
        Assert.Equal(["mc-download-manager@mstcvk.github.io"], firefox.RootElement.GetProperty("allowed_extensions").EnumerateArray().Select(e => e.GetString()!));
        Assert.False(firefox.RootElement.TryGetProperty("allowed_origins", out _));
    }

    [Fact]
    public void The_bridge_remembers_the_id_of_a_chromium_extension_that_talks_to_it()
    {
        var dir = Setup.TempDir();
        var settings = AppSettings.Load(Path.Combine(dir, "settings.json"));
        settings.ServerPort = 48931;
        var store = MainThread.Run(() => new DownloadStore(settings, Path.Combine(dir, "downloads.json"))).Result;
        var server = new ServerController(store, settings);
        try
        {
            HttpRequest Ping(Dictionary<string, string> headers)
            {
                headers["host"] = "127.0.0.1:48931";
                return new HttpRequest("GET", "/ping", headers, []);
            }
            // Chrome omits Origin on GETs; the extension sends its id in a header instead.
            MainThread.Run(() => server.Handle(Ping(new() { ["x-mcdm-extension"] = "edge", ["x-mcdm-extension-id"] = "abcdefghijklmnopabcdefghijklmnop" }))).Wait();
            MainThread.Run(() => server.Handle(Ping(new() { ["origin"] = "chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba", ["x-mcdm-extension-id"] = "ignored-when-origin-is-there" }))).Wait();
            // A web page is turned away before anything is recorded.
            MainThread.Run(() => server.Handle(Ping(new() { ["origin"] = "https://evil.example", ["x-mcdm-extension-id"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }))).Wait();
            Assert.Equal(["abcdefghijklmnopabcdefghijklmnop", "ponmlkjihgfedcbaponmlkjihgfedcba"], settings.ChromiumExtensionIds);
        }
        finally
        {
            server.Stop();
            MainThread.Run(store.Shutdown).Wait();
        }
    }
}
