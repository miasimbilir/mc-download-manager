using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace MCDM.Tests;

/// The update check (which release counts as newer, what can be installed unattended) and the upkeep
/// around the browser extensions that 2.0 added.
public class UpdaterTests
{
    private static string Release(string tag, bool draft = false, bool prerelease = false, string? digest = "sha256:6c2e61c0c07f460af0d0dd7b27511fcdc5a23ba66bb7870c5dfa87290d43f735", bool installer = true)
    {
        var version = tag.Replace("windows-v", "");
        var assets = new List<object>
        {
            new { name = $"MC-Download-Manager-{version}-win-x64.zip", size = 126043677, digest = "sha256:13435a9985d45d3b25aaa5008b71d21887b80b9b4abcabb08ec0f211c6f4aaac",
                  browser_download_url = $"https://github.com/miasimbilir/mc-download-manager/releases/download/{tag}/MC-Download-Manager-{version}-win-x64.zip" },
        };
        if (installer)
            assets.Add(new { name = $"MC-Download-Manager-{version}-Setup.exe", size = 98005199, digest,
                  browser_download_url = $"https://github.com/miasimbilir/mc-download-manager/releases/download/{tag}/MC-Download-Manager-{version}-Setup.exe" });
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease, body = "## Yenilikler\r\n- bir\r\n- iki",
            html_url = $"https://github.com/miasimbilir/mc-download-manager/releases/tag/{tag}", assets,
        });
    }

    private static string Feed(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void The_newest_windows_release_above_the_running_version_is_offered()
    {
        var feed = Feed(Release("windows-v2.0.0"), Release("windows-v2.1.0"), Release("windows-v2.0.5"), Release("v9.0.0"), Release("windows-v3.0.0", draft: true), Release("windows-v2.2.0", prerelease: true));
        var update = Updater.Parse(feed, "2.0.0")!;
        Assert.Equal("2.1.0", update.Version);
        Assert.Equal("https://github.com/miasimbilir/mc-download-manager/releases/tag/windows-v2.1.0", update.PageUrl);
        Assert.Equal("https://github.com/miasimbilir/mc-download-manager/releases/download/windows-v2.1.0/MC-Download-Manager-2.1.0-Setup.exe", update.InstallerUrl);
        Assert.Equal("6c2e61c0c07f460af0d0dd7b27511fcdc5a23ba66bb7870c5dfa87290d43f735", update.InstallerSha256);
        Assert.Equal(98005199, update.InstallerSize);
        Assert.Equal("## Yenilikler\n- bir\n- iki", update.Notes);
        Assert.True(update.CanInstall);

        Assert.Null(Updater.Parse(feed, "2.1.0"));                              // nothing above what is running
        Assert.Null(Updater.Parse(feed, "2.1"));                                // 2.1 is 2.1.0, not older than it
        Assert.Null(Updater.Parse(feed, "10.0.0"));                             // numbers, not text: 10 > 2
        Assert.Equal("2.1.0", Updater.Parse(feed, "1.3.0")!.Version);
        Assert.Null(Updater.Parse("[]", "2.0.0"));
        Assert.Equal("2.1.0", Updater.Parse(Release("windows-v2.1.0"), "2.0.0")!.Version);     // a single release object works too
    }

    [Fact]
    public void A_release_is_installed_unattended_only_when_its_installer_can_be_checked()
    {
        Assert.False(Updater.Parse(Feed(Release("windows-v2.1.0", digest: null)), "2.0.0")!.CanInstall);
        Assert.False(Updater.Parse(Feed(Release("windows-v2.1.0", digest: "md5:abc")), "2.0.0")!.CanInstall);
        Assert.False(Updater.Parse(Feed(Release("windows-v2.1.0", digest: "sha256:zzzz")), "2.0.0")!.CanInstall);
        var zipOnly = Updater.Parse(Feed(Release("windows-v2.1.0", installer: false)), "2.0.0")!;
        Assert.False(zipOnly.CanInstall);
        Assert.Null(zipOnly.InstallerUrl);
        Assert.StartsWith("https://github.com/", zipOnly.PageUrl);              // still offered: the page opens instead
    }

    [Fact]
    public async Task The_installer_is_kept_only_when_its_checksum_matches()
    {
        using var server = new TestServer(300_000);
        var sha = Convert.ToHexString(SHA256.HashData(server.Data)).ToLowerInvariant();
        var good = new UpdateInfo("9.9.1", "https://example.com/r", server.Url("/Setup.exe"), sha, server.Data.Length, "");
        double last = 0;
        var file = await Updater.DownloadInstallerAsync(good, p => last = p, CancellationToken.None);
        Assert.Equal(Path.Combine(Updater.UpdateDir, "MC-Download-Manager-9.9.1-Setup.exe"), file);
        Assert.Equal(server.Data, File.ReadAllBytes(file));
        Assert.Equal(1, last);

        var tampered = good with { Version = "9.9.2", InstallerSha256 = new string('0', 64) };
        await Assert.ThrowsAsync<DownloadFailure>(() => Updater.DownloadInstallerAsync(tampered, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Updater.UpdateDir, "*9.9.2*"));

        // A test feed may point at a file on disk.
        var local = Path.Combine(Setup.TempDir(), "Setup.exe");
        File.WriteAllBytes(local, server.Data);
        var copied = await Updater.DownloadInstallerAsync(good with { Version = "9.9.3", InstallerUrl = local }, null, CancellationToken.None);
        Assert.Equal(server.Data, File.ReadAllBytes(copied));

        Updater.CleanUp();
        Assert.False(Directory.Exists(Updater.UpdateDir));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\MC Download Manager\", @"C:\Users\x\AppData\Local\Programs\MC Download Manager", true)]
    [InlineData(@"c:\users\x\appdata\local\programs\mc download manager", @"C:\Users\x\AppData\Local\Programs\MC Download Manager\", true)]
    [InlineData(@"C:\Users\x\Desktop\MC Download Manager", @"C:\Users\x\AppData\Local\Programs\MC Download Manager", false)]
    [InlineData("", "", false)]
    public void The_installed_copy_is_told_from_an_unpacked_one_by_its_folder(string registered, string running, bool same) =>
        Assert.Equal(same, Updater.SameFolder(registered, running));

    // ---- Browser extension upkeep ----

    [Fact]
    public void An_extension_that_does_not_send_its_id_is_an_old_one()
    {
        using var rig = new StoreTests.Rig(s => s.AutoStart = false);
        var server = new ServerController(rig.Store, rig.Settings);
        try
        {
            var changes = 0;
            server.PropertyChanged += (_, _) => changes++;
            HttpRequest Ping(string browser, string? id)
            {
                var r = StoreTests.Req("GET", "/ping", origin: null, ext: browser);
                if (id != null) r.Headers["x-mcdm-extension-id"] = id;
                return r;
            }
            MainThread.Run(() => server.Handle(Ping("edge", null))).Wait();
            Assert.True(server.IsExtensionConnected("edge"));
            Assert.True(server.IsExtensionOutdated("edge"));
            Assert.False(server.IsExtensionOutdated("firefox"));
            Assert.Equal(1, changes);

            // "Reload" on the extensions page: the new code introduces itself with its id.
            MainThread.Run(() => server.Handle(Ping("edge", "abcdefghijklmnopabcdefghijklmnop"))).Wait();
            Assert.False(server.IsExtensionOutdated("edge"));
            Assert.Equal(2, changes);
            MainThread.Run(() => server.Handle(Ping("edge", "abcdefghijklmnopabcdefghijklmnop"))).Wait();
            Assert.Equal(2, changes);                       // nothing new, nothing announced
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void The_list_of_remembered_extension_ids_stays_short()
    {
        var settings = AppSettings.Load(Path.Combine(Setup.TempDir(), "s.json"));
        string Id(int n) => new([.. Enumerable.Range(0, 32).Select(i => (char)('a' + (n >> (i % 8) & 1) + (i % 3)))]);
        var ids = Enumerable.Range(0, 60).Select(Id).Distinct().ToList();
        Assert.True(ids.Count > NativeHostRegistry.MaxRememberedIds + 5);
        foreach (var id in ids) Assert.True(NativeHostRegistry.NoteExtensionId(settings, id));
        Assert.Equal(NativeHostRegistry.MaxRememberedIds, settings.ChromiumExtensionIds.Count);
        Assert.Equal(ids[^NativeHostRegistry.MaxRememberedIds..], settings.ChromiumExtensionIds);     // the oldest went
    }

    [Fact]
    public void After_an_app_update_the_prepared_extension_folders_get_the_new_files_in_place()
    {
        var bundled = Path.Combine(Setup.TempDir(), "ChromeExtension");
        Directory.CreateDirectory(Path.Combine(bundled, "icons"));
        File.WriteAllText(Path.Combine(bundled, "manifest.json"), """{"name":"x","version":"9.9.9"}""");
        File.WriteAllText(Path.Combine(bundled, "background.js"), "// new");
        File.WriteAllText(Path.Combine(bundled, "icons", "icon.txt"), "new");
        var before = Environment.GetEnvironmentVariable("MCDM_EXTENSION_DIR");
        Environment.SetEnvironmentVariable("MCDM_EXTENSION_DIR", bundled);
        var edge = BrowserKind.All.First(b => b.Id == "edge");
        var brave = BrowserKind.All.First(b => b.Id == "brave");
        try
        {
            // Edge was set up with an earlier version; Brave never was.
            var folder = BrowserIntegration.PluginFolder(edge);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), """{"name":"x","version":"1.2.0"}""");
            File.WriteAllText(Path.Combine(folder, "background.js"), "// old");
            if (Directory.Exists(BrowserIntegration.PluginFolder(brave))) Directory.Delete(BrowserIntegration.PluginFolder(brave), true);

            Assert.Equal([edge], BrowserIntegration.RefreshPreparedFiles());
            Assert.Equal("9.9.9", BrowserIntegration.PreparedVersion(edge));
            Assert.Equal("// new", File.ReadAllText(Path.Combine(folder, "background.js")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "icons", "icon.txt")));
            Assert.False(BrowserIntegration.FilesPrepared(brave));              // not prepared, not touched
            Assert.Empty(BrowserIntegration.RefreshPreparedFiles());            // up to date now
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCDM_EXTENSION_DIR", before);
            try { Directory.Delete(BrowserIntegration.PluginFolder(edge), true); } catch { }
        }
    }
}
