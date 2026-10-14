using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace MCDM;

/// A newer release of the Windows app. `InstallerUrl`/`InstallerSha256` are set when the release carries a
/// Setup.exe whose checksum GitHub publishes; only then can the app install it by itself.
public sealed record UpdateInfo(string Version, string PageUrl, string? InstallerUrl, string? InstallerSha256, long InstallerSize, string Notes)
{
    public bool CanInstall => InstallerUrl != null && InstallerSha256 is { Length: 64 };
}

/// Looks for newer Windows releases on GitHub (tags "windows-vX.Y.Z" of `Repo`) and, for a copy that was
/// installed with the installer, downloads the new Setup.exe, checks it and runs it.
public static class Updater
{
    public const string Repo = "miasimbilir/mc-download-manager";
    public const string ProjectUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = ProjectUrl + "/releases/latest";
    private const string TagPrefix = "windows-v";
    /// The installer's AppId (packaging\installer.iss) as Windows files it under "Uninstall".
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B6F3A2C4-6E0B-4B0E-9C59-5D1B3C7A2E41}_is1";

    /// `MCDM_UPDATE_FEED` (a file or an http address with the same JSON as the GitHub API) replaces the
    /// real release list; used to try the update path before a newer release exists.
    private static string Feed => Environment.GetEnvironmentVariable("MCDM_UPDATE_FEED") is { Length: > 0 } custom
        ? custom
        : $"https://api.github.com/repos/{Repo}/releases?per_page=20";

    public static string UpdateDir => Path.Combine(AppPaths.DataDir, "update");

    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// The newest release above the running version, or null when there is none. Throws when the list
    /// cannot be fetched.
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var feed = Feed;
        string json;
        if (Links.ParseHttp(feed) is { } url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", $"MCDownloadManager/{AppInfo.Version}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else json = await File.ReadAllTextAsync(feed, ct).ConfigureAwait(false);
        return Parse(json, AppInfo.Version);
    }

    /// Picks the highest "windows-vX.Y.Z" release (no drafts, no pre-releases) above `current`.
    public static UpdateInfo? Parse(string json, string current)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        if (ParseVersion(current) is not { } running) return null;
        UpdateInfo? best = null;
        Version? bestVersion = null;
        foreach (var r in releases)
        {
            if (r.ValueKind != JsonValueKind.Object || Flag(r, "draft") || Flag(r, "prerelease")) continue;
            var tag = Str(r, "tag_name") ?? "";
            if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) || ParseVersion(tag[TagPrefix.Length..]) is not { } version) continue;
            if (version <= running || (bestVersion != null && version <= bestVersion)) continue;
            string? installer = null, sha = null;
            long size = 0;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name") ?? "";
                    if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    installer = Str(a, "browser_download_url");
                    var digest = Str(a, "digest") ?? "";
                    sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].Trim().ToLowerInvariant() : null;
                    size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    break;
                }
            }
            if (sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit)) sha = null;
            var page = Links.ParseHttp(Str(r, "html_url"))?.AbsoluteUri ?? ReleasesUrl;
            best = new UpdateInfo(version.ToString(3), page, installer, sha, size, (Str(r, "body") ?? "").Replace("\r\n", "\n").Trim());
            bestVersion = version;
        }
        return best;
    }

    /// "2.1" and "2.1.0" are the same version (System.Version alone would rank the first below the second).
    private static Version? ParseVersion(string text) =>
        Version.TryParse(text.Trim(), out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)) : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// True when this copy sits where the installer put it: only then can a Setup.exe replace it in place.
    /// A copy unpacked from the zip is pointed at the download page instead.
    public static bool InstalledByInstaller
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") is string location && SameFolder(location, AppPaths.AppDir);
            }
            catch { return false; }
        }
    }

    internal static bool SameFolder(string a, string b)
    {
        static string Norm(string p) => p.Trim().Trim('"').TrimEnd('\\', '/');
        return Norm(a).Length > 0 && string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    /// Downloads the release's installer into the app data folder and checks it against the published
    /// SHA-256. Returns the file; throws when the download fails or the checksum differs.
    public static async Task<string> DownloadInstallerAsync(UpdateInfo update, Action<double>? onProgress, CancellationToken ct)
    {
        if (!update.CanInstall) throw new DownloadFailure(Loc.T("This release has no installer that can be verified."));
        Directory.CreateDirectory(UpdateDir);
        var file = Path.Combine(UpdateDir, $"MC-Download-Manager-{update.Version}-Setup.exe");
        var part = file + ".part";
        try { File.Delete(part); } catch { }
        if (Links.ParseHttp(update.InstallerUrl) is { } url)
        {
            var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            long total = update.InstallerSize;
            var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
            {
                Url = url, PartPath = part, SegmentCount = 4,
                Headers = new(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = DownloadStore.DefaultUserAgent },
            })
            {
                OnProbe = p => { if (p.TotalBytes > 0) total = p.TotalBytes; },
                OnProgress = (received, _) => { if (total > 0) onProgress?.Invoke(Math.Min(1, (double)received / total)); },
                OnFinish = e => done.TrySetResult(e),
            };
            using var stop = ct.Register(() => { dl.Stop(); done.TrySetCanceled(ct); });
            dl.Start();
            if (await done.Task.ConfigureAwait(false) is { } error) throw error;
        }
        else File.Copy(update.InstallerUrl!, part, true);     // a test feed may name a local file

        var actual = await Task.Run(() =>
        {
            using var s = File.OpenRead(part);
            return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }, ct).ConfigureAwait(false);
        if (actual != update.InstallerSha256)
        {
            try { File.Delete(part); } catch { }
            throw new DownloadFailure(Loc.T("Checksum mismatch; the download was discarded."));
        }
        File.Move(part, file, true);
        return file;
    }

    /// Starts the installer without its wizard pages. It closes the running app (`--quit`), replaces the
    /// files and starts the new version with its window (`/SHOWAPP=1`, see installer.iss).
    public static bool RunInstaller(string file)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) ?? "" };
            foreach (var a in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/SHOWAPP=1" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    /// Installers of earlier updates are of no use once the app runs again.
    public static void CleanUp()
    {
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); } catch { }
    }
}
