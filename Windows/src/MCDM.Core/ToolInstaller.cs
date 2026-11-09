using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MCDM;

/// Downloads the latest yt-dlp, ffmpeg and deno from GitHub into the app data folder: none of them comes with the app
/// (since 2.2.2), the first video download asks for them, and yt-dlp is kept current (YouTube changes break old yt-dlp
/// builds often).
public sealed class ToolInstaller : INotifyPropertyChanged
{
    public static ToolInstaller Shared { get; } = new();

    public bool Busy { get; private set; }
    public double Progress { get; private set; }
    public string? Message { get; private set; }
    public string? YtDlpVersion { get; private set; }
    public string? DenoVersion { get; private set; }
    public string? FfmpegVersion { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private static string YtDlpAsset => OperatingSystem.IsWindows() ? "yt-dlp.exe" : OperatingSystem.IsMacOS() ? "yt-dlp_macos" : "yt-dlp_linux";

    private static string DenoTriple
    {
        get
        {
            var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            // deno publishes no Windows ARM64 build; the x64 one runs under emulation.
            if (OperatingSystem.IsWindows()) return "x86_64-pc-windows-msvc";
            if (OperatingSystem.IsMacOS()) return arm ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
            return arm ? "aarch64-unknown-linux-gnu" : "x86_64-unknown-linux-gnu";
        }
    }

    private static string DenoZipUrl => $"https://github.com/denoland/deno/releases/latest/download/deno-{DenoTriple}.zip";

    /// yt-dlp's own Windows build of FFmpeg, as DLLs beside a small ffmpeg.exe: 86 MB to download where the single-file
    /// build is 192 MB. Unpacked into a folder of its own, so its DLLs are found next to it.
    private const string FfmpegBase = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
    private const string FfmpegZip = "ffmpeg-master-latest-win64-gpl-shared.zip";

    /// Approximate download sizes, shown in the install prompt.
    public const string DenoDownloadSize = "~45 MB";
    public const string YtDlpDownloadSize = "~18 MB";
    public const string FfmpegDownloadSize = "~86 MB";

    public static string UserYtDlpPath => Path.Combine(AppPaths.BinDir, AppPaths.Exe("yt-dlp"));
    public static string UserDenoPath => Path.Combine(AppPaths.BinDir, AppPaths.Exe("deno"));
    public static string FfmpegDir => Path.Combine(AppPaths.BinDir, "ffmpeg");

    public void RefreshVersions(string customYtDlp)
    {
        Task.Run(() =>
        {
            var y = VideoDownloader.LocateYtDlp(customYtDlp) is { } yp ? VideoDownloader.Version(yp) : null;
            var d = VideoDownloader.LocateDeno() is { } dp ? VideoDownloader.Version(dp) : null;
            var f = VideoDownloader.LocateFfmpeg() is { } fp ? FfmpegVersionOf(fp) : null;
            MainThread.Post(() => { YtDlpVersion = y; DenoVersion = d; FfmpegVersion = f; Changed(); });
        });
    }

    private void Begin(string message)
    {
        Busy = true;
        Progress = 0;
        Message = message;
        Changed();
    }

    private (string?, Exception?) End(string? version, Exception? error, string okMessage)
    {
        Busy = false;
        Progress = 0;
        Message = error == null ? okMessage : Loc.T("Install failed: {0}", error.Message);
        Changed();
        return (version, error);
    }

    private Task<Exception?> Fetch(string url, string destination)
    {
        var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long total = -1;
        var dl = new SegmentedDownloader(new SegmentedDownloader.Configuration
        {
            Url = new Uri(url),
            Headers = new(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = DownloadStore.DefaultUserAgent },
            PartPath = destination,
            SegmentCount = 4,
        });
        dl.OnProbe = p => total = p.TotalBytes;
        dl.OnProgress = (received, _) => MainThread.Post(() =>
        {
            if (total <= 0 || !Busy) return;
            Progress = Math.Min(1, (double)received / total);
            Changed();
        });
        dl.OnFinish = e => tcs.TrySetResult(e);
        dl.Start();
        return tcs.Task;
    }

    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<string> GetText(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", DownloadStore.DefaultUserAgent);
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// Puts `staged` at `destination`. Windows refuses to overwrite or delete an executable that is running
    /// (a video download in progress), but allows renaming it; the old copy is removed on a later start.
    private static void ReplaceExecutable(string staged, string destination)
    {
        try
        {
            File.Move(staged, destination, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(destination, destination + ".old-" + DateTime.UtcNow.Ticks);
            File.Move(staged, destination);
        }
    }

    /// Removes leftovers of earlier installs (renamed running copies, interrupted downloads).
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(AppPaths.BinDir)) return;
            foreach (var f in Directory.EnumerateFiles(AppPaths.BinDir))
            {
                var name = Path.GetFileName(f);
                if (name.Contains(".old-", StringComparison.Ordinal) || name.StartsWith("yt-dlp-new", StringComparison.Ordinal) || name.StartsWith('.'))
                    try { File.Delete(f); } catch { }
            }
            foreach (var d in Directory.EnumerateDirectories(AppPaths.BinDir))
            {
                var name = Path.GetFileName(d);
                if (name.Contains(".old-", StringComparison.Ordinal) || name.StartsWith('.'))
                    try { Directory.Delete(d, true); } catch { }
            }
            if (Directory.Exists(FfmpegDir))
                foreach (var f in Directory.EnumerateFiles(FfmpegDir, "*.old-*"))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private static void MakeExecutable(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute); } catch { }
    }

    /// Downloads yt-dlp, checks it against the release's SHA2-256SUMS, verifies it runs, and installs it
    /// to the app data folder. Call on the main thread. Returns (version, error).
    public async Task<(string? Version, Exception? Error)> InstallOrUpdateYtDlpAsync()
    {
        if (Busy) return (null, new DownloadFailure(Loc.T("Another tool install is in progress.")));
        Begin(Loc.T("Downloading yt-dlp…"));
        var tmp = Path.Combine(AppPaths.BinDir, ".yt-dlp.download");
        string? version = null;
        Exception? error;
        try
        {
            Directory.CreateDirectory(AppPaths.BinDir);
            error = await Fetch(ReleaseBase + YtDlpAsset, tmp);
            if (error == null)
            {
                Message = Loc.T("Verifying…");
                Changed();
                (version, error) = await Task.Run(async () =>
                {
                    try
                    {
                        var sums = await GetText(ReleaseBase + "SHA2-256SUMS").ConfigureAwait(false);
                        var expected = sums.Split('\n')
                            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                            .FirstOrDefault(p => p.Length >= 2 && p[1].TrimStart('*') == YtDlpAsset)?[0].ToLowerInvariant();
                        if (expected is not { Length: 64 }) throw new DownloadFailure(Loc.T("Could not fetch the checksum for verification."));
                        if (Sha256(tmp) != expected) throw new DownloadFailure(Loc.T("Checksum mismatch; the download was discarded."));
                        // Run it under its real name: on Windows only *.exe can be started.
                        var staged = Path.Combine(AppPaths.BinDir, AppPaths.Exe("yt-dlp-new"));
                        File.Move(tmp, staged, true);
                        MakeExecutable(staged);
                        var v = VideoDownloader.Version(staged, 60000);
                        if (string.IsNullOrEmpty(v))
                        {
                            try { File.Delete(staged); } catch { }
                            throw new DownloadFailure(Loc.T("The downloaded yt-dlp binary could not be executed."));
                        }
                        ReplaceExecutable(staged, UserYtDlpPath);
                        return (v, (Exception?)null);
                    }
                    catch (Exception e)
                    {
                        return ((string?)null, e);
                    }
                });
            }
        }
        catch (Exception e)
        {
            error = e;
        }
        try { File.Delete(tmp); } catch { }
        if (error == null) YtDlpVersion = version;
        return End(version, error, Loc.T("yt-dlp {0} installed.", version ?? ""));
    }

    /// Downloads the latest deno release zip for this platform, checks it against the published SHA-256,
    /// unpacks it and installs it to the app data folder. Call on the main thread.
    public async Task<(string? Version, Exception? Error)> InstallDenoAsync()
    {
        if (Busy) return (null, new DownloadFailure(Loc.T("Another tool install is in progress.")));
        Begin(Loc.T("Downloading deno…"));
        var zip = Path.Combine(AppPaths.BinDir, ".deno.zip");
        var staging = Path.Combine(AppPaths.BinDir, ".deno-unpack");
        string? version = null;
        Exception? error;
        try
        {
            Directory.CreateDirectory(AppPaths.BinDir);
            error = await Fetch(DenoZipUrl, zip);
            if (error == null)
            {
                Message = Loc.T("Verifying…");
                Changed();
                (version, error) = await Task.Run(async () =>
                {
                    try
                    {
                        // The checksum file is `<hash>  <name>` for macOS/Linux and PowerShell's
                        // `Hash : <HASH>` listing for Windows; take the 64-digit hex run from either.
                        var sum = await GetText(DenoZipUrl + ".sha256sum").ConfigureAwait(false);
                        var m = Regex.Match(sum, @"\b[0-9a-fA-F]{64}\b");
                        if (!m.Success) throw new DownloadFailure(Loc.T("Could not fetch the checksum for verification."));
                        if (Sha256(zip) != m.Value.ToLowerInvariant()) throw new DownloadFailure(Loc.T("Checksum mismatch; the download was discarded."));
                        if (Directory.Exists(staging)) Directory.Delete(staging, true);
                        ZipFile.ExtractToDirectory(zip, staging);
                        var bin = Path.Combine(staging, AppPaths.Exe("deno"));
                        if (!File.Exists(bin)) throw new DownloadFailure(Loc.T("The deno archive could not be unpacked."));
                        MakeExecutable(bin);
                        var v = VideoDownloader.Version(bin, 60000);
                        if (v == null || !v.StartsWith("deno ", StringComparison.Ordinal))
                            throw new DownloadFailure(Loc.T("The downloaded deno binary could not be executed."));
                        ReplaceExecutable(bin, UserDenoPath);
                        return (v, (Exception?)null);
                    }
                    catch (Exception e)
                    {
                        return ((string?)null, e);
                    }
                });
            }
        }
        catch (Exception e)
        {
            error = e;
        }
        try { File.Delete(zip); } catch { }
        try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        if (error == null) DenoVersion = version;
        return End(version, error, Loc.T("{0} installed.", version ?? "deno"));
    }
    /// "ffmpeg version N-128713-g0f89d936b3-20261108 Copyright (c) 2000-2026 the FFmpeg developers" →
    /// "ffmpeg N-128713-g0f89d936b3-20261108" (the copyright made the settings row and "… installed." read badly);
    /// ffmpeg answers `-version`, not `--version`.
    private static string? FfmpegVersionOf(string path) => ShortFfmpegVersion(VideoDownloader.Version(path, 20000, "-version"));

    internal static string? ShortFfmpegVersion(string? line)
    {
        if (line == null) return null;
        var copyright = line.IndexOf(" Copyright", StringComparison.Ordinal);
        if (copyright > 0) line = line[..copyright];
        return line.StartsWith("ffmpeg version ", StringComparison.Ordinal) ? "ffmpeg " + line["ffmpeg version ".Length..] : line;
    }

    /// Downloads yt-dlp's FFmpeg build for Windows, checks it against the release's checksums.sha256, unpacks ffmpeg,
    /// ffprobe and their DLLs into their own folder in the app data folder and checks that ffmpeg runs. Call on the
    /// main thread.
    public async Task<(string? Version, Exception? Error)> InstallFfmpegAsync()
    {
        if (Busy) return (null, new DownloadFailure(Loc.T("Another tool install is in progress.")));
        if (!OperatingSystem.IsWindows()) return (null, new DownloadFailure(Loc.T("ffmpeg can be installed here only on Windows.")));
        Begin(Loc.T("Downloading ffmpeg…"));
        var zip = Path.Combine(AppPaths.BinDir, ".ffmpeg.zip");
        var staging = Path.Combine(AppPaths.BinDir, ".ffmpeg-unpack");
        string? version = null;
        Exception? error;
        try
        {
            Directory.CreateDirectory(AppPaths.BinDir);
            error = await Fetch(FfmpegBase + FfmpegZip, zip);
            if (error == null)
            {
                Message = Loc.T("Verifying…");
                Changed();
                (version, error) = await Task.Run(async () =>
                {
                    try
                    {
                        var sums = await GetText(FfmpegBase + "checksums.sha256").ConfigureAwait(false);
                        var expected = sums.Split('\n')
                            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                            .FirstOrDefault(p => p.Length >= 2 && p[1].TrimStart('*') == FfmpegZip)?[0].ToLowerInvariant();
                        if (expected is not { Length: 64 }) throw new DownloadFailure(Loc.T("Could not fetch the checksum for verification."));
                        if (Sha256(zip) != expected) throw new DownloadFailure(Loc.T("Checksum mismatch; the download was discarded."));
                        if (Directory.Exists(staging)) Directory.Delete(staging, true);
                        var bin = UnpackFfmpeg(zip, staging);
                        var v = FfmpegVersionOf(bin);
                        if (v == null || !v.StartsWith("ffmpeg ", StringComparison.Ordinal))
                            throw new DownloadFailure(Loc.T("The downloaded ffmpeg could not be executed."));
                        ReplaceFolder(staging, FfmpegDir);
                        return (v, (Exception?)null);
                    }
                    catch (Exception e)
                    {
                        return ((string?)null, e);
                    }
                });
            }
        }
        catch (Exception e)
        {
            error = e;
        }
        try { File.Delete(zip); } catch { }
        try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        if (error == null) FfmpegVersion = version;
        return End(version, error, Loc.T("{0} installed.", version ?? "ffmpeg"));
    }

    /// From FFmpeg-Builds' zip (`<build>/bin/…`, `<build>/LICENSE.txt`): ffmpeg.exe, ffprobe.exe, the DLLs they load
    /// and the licence, flat into `folder`. ffplay (a player) is left out. Returns the path of ffmpeg.exe.
    internal static string UnpackFfmpeg(string zip, string folder)
    {
        Directory.CreateDirectory(folder);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var name = entry.Name;
            var wanted = parts.Length == 3 && parts[1] == "bin"
                    && (name is "ffmpeg.exe" or "ffprobe.exe" || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                || parts.Length == 2 && name == "LICENSE.txt";
            // Entry names come from the archive: only a plain file name is written, never a path.
            if (!wanted || name != Path.GetFileName(name) || name.Contains('\\')) continue;
            entry.ExtractToFile(Path.Combine(folder, name), overwrite: true);
        }
        var exe = Path.Combine(folder, "ffmpeg.exe");
        if (!File.Exists(exe)) throw new DownloadFailure(Loc.T("The ffmpeg archive could not be unpacked."));
        return exe;
    }

    /// Puts the folder `staged` at `destination`. The old folder is first moved aside in one step, so a failure never
    /// leaves half of it. A video being merged keeps ffmpeg and its DLLs open and the folder cannot move: then each file
    /// is replaced on its own (a running file can be renamed); the renamed ones go on a later start (CleanUp).
    private static void ReplaceFolder(string staged, string destination)
    {
        if (Directory.Exists(destination))
        {
            var aside = destination + ".old-" + DateTime.UtcNow.Ticks;
            try
            {
                Directory.Move(destination, aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                foreach (var file in Directory.GetFiles(staged))
                    ReplaceExecutable(file, Path.Combine(destination, Path.GetFileName(file)));
                try { Directory.Delete(staged, true); } catch { }
                return;
            }
            try { Directory.Delete(aside, true); } catch { }
        }
        Directory.Move(staged, destination);
    }
}
