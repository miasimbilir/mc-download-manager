using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MCDM;

/// Windows shell integration: Explorer, Recycle Bin, Mark of the Web, sign-in startup.
/// Off Windows (development builds) each call degrades to the nearest equivalent or a no-op.
public static class Shell
{
    /// Tags a downloaded file the way browsers do (Zone.Identifier stream, Internet zone), so SmartScreen
    /// and Office Protected View assess it on first open.
    public static void MarkAsDownloaded(string file, string sourceUrl, string? referrer)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sb = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
            if (IsSingleLine(referrer)) sb.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
            if (IsSingleLine(sourceUrl)) sb.Append("HostUrl=").Append(sourceUrl).Append("\r\n");
            File.WriteAllText(file + ":Zone.Identifier", sb.ToString(), Encoding.ASCII);
        }
        catch { }   // FAT/exFAT volumes have no alternate data streams
    }

    private static bool IsSingleLine(string? s) => !string.IsNullOrEmpty(s) && s.IndexOfAny(['\r', '\n']) < 0;

    public static bool MoveToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
                return true;
            }
            catch { return false; }
        }
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                // WANTNUKEWARNING: ask before a file too big for the bin (or on a volume without one) is deleted for good.
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI | FOF_SILENT,
            };
            return SHFileOperation(ref op) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RevealFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else OpenFolder(Path.GetDirectoryName(path) ?? path);
        }
        catch { }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]);
        }
        catch { }
    }

    public static void OpenFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]);
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        if (Links.ParseHttp(url) == null) return;
        OpenFile(url);
    }

    // ---- Power (after the queue has finished) ----

    /// `MCDM_POWER_DRYRUN=1` writes what would have happened to power.log in the app data folder instead
    /// of doing it (for testing the flow without losing the session).
    private static bool PowerDryRun(string what)
    {
        if (Environment.GetEnvironmentVariable("MCDM_POWER_DRYRUN") != "1") return false;
        try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "power.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}\n"); } catch { }
        return true;
    }

    /// Puts the computer to sleep (not hibernation; wake timers stay allowed).
    public static bool Sleep()
    {
        if (PowerDryRun("sleep")) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try { return SetSuspendState(false, false, false); }
        catch { return false; }
    }

    /// Shuts Windows down the ordinary way: a program with unsaved work can still hold it up.
    public static bool Shutdown()
    {
        if (PowerDryRun("shutdown")) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            using var p = Process.Start(new ProcessStartInfo(exe, "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
            return p != null;
        }
        catch { return false; }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    // ---- Start with Windows (HKCU\...\Run) ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string BackgroundArgument = "--background";

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    public static bool StartupSupported => OperatingSystem.IsWindows();

    public static bool StartupEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(AppInfo.Name) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void SetStartup(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(AppInfo.Name, StartupCommand);
        else key.DeleteValue(AppInfo.Name, false);
    }

    /// The app is portable: if its folder was moved since startup was enabled, point the entry at the new
    /// place. An entry whose program still exists is left alone — a second copy started once (a build
    /// folder found by Windows Search, say) must not take over from the installed one.
    public static void RefreshStartupPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(AppInfo.Name) is not string s || s.Length == 0) return;
            if (string.Equals(s, StartupCommand, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(CommandProgram(s))) return;
            key.SetValue(AppInfo.Name, StartupCommand);
        }
        catch { }
    }

    /// `"C:\dir\app.exe" --flag` → `C:\dir\app.exe`
    internal static string CommandProgram(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    /// True when this executable has its `tools` folder (yt-dlp, ffmpeg) next to it, i.e. it is a complete
    /// copy of the app rather than a bare build output.
    public static bool CompleteCopy => Directory.Exists(AppPaths.BundledToolsDir);

    /// The app comes as a zip, so nothing puts it in the Start menu; without an entry it cannot be found
    /// again after "Quit". Creates Start menu ▸ MC Download Manager for a complete copy, and repairs the
    /// entry when the program it points at no longer exists.
    public static void EnsureStartMenuShortcut()
    {
        if (!OperatingSystem.IsWindows() || !CompleteCopy || Environment.ProcessPath is not { } exe) return;
        try
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk");
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(link);      // opens the existing one when there is one
            string target = shortcut.TargetPath ?? "";
            if (File.Exists(link) && target.Length > 0 && File.Exists(target)) return;
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Description = AppInfo.Name;
            shortcut.Save();
        }
        catch { }
    }

    public static void RemoveStartMenuShortcut()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk")); } catch { }
    }

    /// Clipboard change counter (cheap to poll; avoids reading the clipboard when nothing changed).
    public static uint ClipboardSequence => OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}

/// Browsers the guided extension setup knows about: the Chromium ones load the unpacked extension from
/// a folder, Firefox installs the signed add-on file.
public sealed class BrowserKind
{
    /// The name the bridge files this browser's extension under (see ServerController.NoteConnection).
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ExtensionsPage { get; init; }
    public required string ExeName { get; init; }
    public required string[] WindowsFolders { get; init; }  // under Program Files / Program Files (x86) / LocalAppData
    public required string MacApp { get; init; }
    public bool Firefox { get; init; }
    /// Opera and Opera GX both install an opera.exe and share one App Paths entry; they are told apart by folder.
    public bool AppPathsLookup { get; init; } = true;

    public static readonly BrowserKind[] All =
    [
        new() { Id = "chrome", Name = "Google Chrome", ExtensionsPage = "chrome://extensions", ExeName = "chrome.exe",
                WindowsFolders = [@"Google\Chrome\Application"], MacApp = "Google Chrome" },
        new() { Id = "edge", Name = "Microsoft Edge", ExtensionsPage = "edge://extensions", ExeName = "msedge.exe",
                WindowsFolders = [@"Microsoft\Edge\Application"], MacApp = "Microsoft Edge" },
        new() { Id = "brave", Name = "Brave", ExtensionsPage = "brave://extensions", ExeName = "brave.exe",
                WindowsFolders = [@"BraveSoftware\Brave-Browser\Application"], MacApp = "Brave Browser" },
        new() { Id = "opera", Name = "Opera", ExtensionsPage = "opera://extensions", ExeName = "opera.exe",
                WindowsFolders = ["Opera", @"Programs\Opera"], MacApp = "Opera", AppPathsLookup = false },
        new() { Id = "opera", Name = "Opera GX", ExtensionsPage = "opera://extensions", ExeName = "opera.exe",
                WindowsFolders = ["Opera GX", @"Programs\Opera GX"], MacApp = "Opera GX", AppPathsLookup = false },
        new() { Id = "firefox", Name = "Firefox", ExtensionsPage = "about:addons", ExeName = "firefox.exe",
                WindowsFolders = ["Mozilla Firefox"], MacApp = "Firefox", Firefox = true },
    ];

    public string? ExecutablePath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var hive in AppPathsLookup ? new[] { Registry.CurrentUser, Registry.LocalMachine } : [])
                {
                    try
                    {
                        using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + ExeName);
                        if (key?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                    }
                    catch { }
                }
                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                };
                foreach (var root in roots)
                    foreach (var folder in WindowsFolders)
                    {
                        if (string.IsNullOrEmpty(root)) continue;
                        var p = Path.Combine(root, folder, ExeName);
                        if (File.Exists(p)) return p;
                    }
                return null;
            }
            if (OperatingSystem.IsMacOS())
            {
                var app = $"/Applications/{MacApp}.app";
                return Directory.Exists(app) ? app : null;
            }
            return null;
        }
    }

    public bool IsInstalled => ExecutablePath != null;
}

/// Browser extension helpers: the unpacked extension shipped next to the executable is copied to
/// Downloads\MC Download Manager\Browser Plugins\<browser> and loaded from there.
public static class BrowserIntegration
{
    /// Set this once the extension is published on the Chrome Web Store; the wizard then links to it.
    public static readonly string? ChromeWebStoreUrl = null;

    public static List<BrowserKind> InstalledBrowsers => [.. BrowserKind.All.Where(b => b.IsInstalled)];

    public static string BundledExtensionFolder =>
        Environment.GetEnvironmentVariable("MCDM_EXTENSION_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppPaths.AppDir, "ChromeExtension");

    public static string PluginsRoot => Path.Combine(AppPaths.DefaultDownloadDir, "Browser Plugins");

    public static string PluginFolder(BrowserKind kind) => Path.Combine(PluginsRoot, kind.Name);

    /// Copies the unpacked extension into the browser's plugin folder. Returns the folder.
    public static string PrepareFiles(BrowserKind kind)
    {
        var source = BundledExtensionFolder;
        if (!File.Exists(Path.Combine(source, "manifest.json")))
            throw new DownloadFailure(Loc.T("The extension files are missing from the app folder: {0}", source));
        var dest = PluginFolder(kind);
        Directory.CreateDirectory(PluginsRoot);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        CopyDirectory(source, dest);
        var readme = $"""
            MC Download Manager — {kind.Name} extension

            1. Open {kind.ExtensionsPage} in {kind.Name}.
            2. Turn on "Developer mode".
            3. Click "Load unpacked" and select this folder.

            Do not move or delete this folder while the extension is installed.
            """;
        try { File.WriteAllText(Path.Combine(dest, "README.txt"), readme); } catch { }
        return dest;
    }

    public static bool FilesPrepared(BrowserKind kind) => File.Exists(Path.Combine(PluginFolder(kind), "manifest.json"));

    /// After an app update the folders the browsers load the extension from still hold the previous
    /// version. Copies the bundled files over them, in place (the browser has the folder loaded, so it
    /// is not removed). Returns the browsers whose folder was brought up to date; each of them still has
    /// to reload the extension to run the new code.
    public static List<BrowserKind> RefreshPreparedFiles()
    {
        var refreshed = new List<BrowserKind>();
        var source = BundledExtensionFolder;
        var bundled = BundledVersion;
        if (bundled == null) return refreshed;
        foreach (var kind in BrowserKind.All)
        {
            if (kind.Firefox || !FilesPrepared(kind) || PreparedVersion(kind) == bundled) continue;
            try
            {
                CopyDirectory(source, PluginFolder(kind));
                refreshed.Add(kind);
            }
            catch { }
        }
        return refreshed;
    }

    /// Version of the extension copy the browser is loading (null when not prepared).
    public static string? PreparedVersion(BrowserKind kind) => ManifestVersion(PluginFolder(kind));
    /// Read once per folder: the bridge asks for it on every request.
    public static string? BundledVersion
    {
        get
        {
            var folder = BundledExtensionFolder;
            var cached = bundledVersionCache;
            if (cached is { } c && c.Folder == folder) return c.Version;
            var v = ManifestVersion(folder);
            bundledVersionCache = (folder, v);
            return v;
        }
    }
    private static (string Folder, string? Version)? bundledVersionCache;

    private static string? ManifestVersion(string folder)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch { return null; }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(source)) CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// The Firefox add-on signed by Mozilla (the one the macOS release carries), shipped next to the
    /// executable. null when this build has none.
    public static string? FirefoxAddon
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("MCDM_FIREFOX_XPI") is { Length: > 0 } custom
                ? custom
                : Path.Combine(AppPaths.AppDir, "FirefoxExtension.xpi");
            return File.Exists(path) ? path : null;
        }
    }

    /// Hands the signed add-on to Firefox, which shows its own “Add MC Download Manager?” prompt and
    /// keeps the add-on installed.
    public static bool InstallFirefoxAddon(BrowserKind kind)
    {
        var exe = kind.ExecutablePath;
        var xpi = FirefoxAddon;
        if (exe == null || xpi == null) return false;
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", ["-a", exe, xpi]);
            else
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add(xpi);
                Process.Start(psi);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// Tests see what would have been opened instead of a browser window appearing.
    internal static Action<Uri, string?>? OpenInBrowserHook;

    /// Opens an http(s) address in the given browser (an id such as "chrome" or "firefox", as the bridge
    /// names them), or in the default browser when that one is unknown or not installed.
    public static void OpenInBrowser(Uri url, string? browserId)
    {
        if (Links.ParseHttp(url.AbsoluteUri) is not { } safe) return;
        if (OpenInBrowserHook is { } hook) { hook(safe, browserId); return; }
        var exe = browserId == null ? null : BrowserKind.All.Where(k => k.Id == browserId).Select(k => k.ExecutablePath).FirstOrDefault(p => p != null);
        if (exe != null)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) Process.Start("open", ["-a", exe, safe.AbsoluteUri]);
                else
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                    psi.ArgumentList.Add(safe.AbsoluteUri);
                    Process.Start(psi);
                }
                return;
            }
            catch { }
        }
        Shell.OpenUrl(safe.AbsoluteUri);
    }

    /// Opens the browser's extensions page (the same as running `chrome.exe chrome://extensions`).
    public static bool OpenExtensionsPage(BrowserKind kind)
    {
        var exe = kind.ExecutablePath;
        if (exe == null) return false;
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", ["-a", exe, kind.ExtensionsPage]);
            else
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add(kind.ExtensionsPage);
                Process.Start(psi);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
