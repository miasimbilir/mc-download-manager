using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MCDM;

/// Native messaging host for Chrome, Edge, Brave, Opera and Firefox: the Windows counterpart of the macOS
/// app's `mcdm-native-host`. The browser starts it when the extension calls runtime.sendNativeMessage; it
/// understands one request, {"action": "launchApp"}, which starts MC Download Manager in the background so
/// the extension can hand over a download while the app was not running. Nothing else (URLs, cookies)
/// passes through here.
///
/// There is no separate host binary on Windows: the browsers start MCDownloadManager.exe itself, which
/// recognises the call by its arguments and serves it before any window or bridge exists.
///
/// Protocol: each message is a 32-bit length in native byte order followed by UTF-8 JSON, on stdin/stdout.
public static class NativeHost
{
    public const string HostName = "com.mesutcevik.mcdownloadmanager";
    public const string FirefoxAddonId = "mc-download-manager@mstcvk.github.io";

    /// Chromium passes the calling extension's origin ("chrome-extension://<id>/", plus --parent-window=…);
    /// Firefox passes the manifest path and the add-on id.
    public static bool IsHostInvocation(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal) || a == FirefoxAddonId);

    public static int Run()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            Serve(input, output, LaunchApp);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    /// sendNativeMessage sends one message and closes stdin; connectNative keeps it open. Serves until EOF.
    public static void Serve(Stream input, Stream output, Func<bool> launchApp)
    {
        while (ReadMessage(input) is { } message)
        {
            var action = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            switch (action)
            {
                case "launchApp": Send(output, new JsonObject { ["ok"] = launchApp() }); break;
                case "ping": Send(output, new JsonObject { ["ok"] = true }); break;
                default: Send(output, new JsonObject { ["ok"] = false, ["error"] = "unknown action" }); break;
            }
        }
    }

    private static JsonElement? ReadMessage(Stream input)
    {
        var header = new byte[4];
        if (!ReadExactly(input, header)) return null;
        var length = BitConverter.ToUInt32(header);
        if (length == 0 || length > 1 << 20) return null;
        var body = new byte[length];
        if (!ReadExactly(input, body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void Send(Stream output, JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        output.Write(BitConverter.GetBytes((uint)body.Length));
        output.Write(body);
        output.Flush();
    }

    /// Starts the app without a window (`--background`); a running app is left as it is (the second copy
    /// sees the first and exits).
    public static bool LaunchApp()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            if (OperatingSystem.IsWindows()) return StartDetached(exe, Shell.BackgroundArgument);
            using var p = Process.Start(new ProcessStartInfo(exe, Shell.BackgroundArgument) { UseShellExecute = false });
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    /// The app has to outlive this host and must not hold on to the browser's pipes:
    /// - no handle inheritance, or the browser would never see the host's stdout close;
    /// - CREATE_BREAKAWAY_FROM_JOB, because Firefox puts the host into a job object and kills the whole job
    ///   when the message is answered. Where breaking away is not allowed, the plain start is tried.
    private static bool StartDetached(string exe, string argument)
    {
        const uint DETACHED_PROCESS = 0x00000008, CREATE_NEW_PROCESS_GROUP = 0x00000200, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        foreach (var flags in new[] { DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP })
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var command = new StringBuilder($"\"{exe}\" {argument}");
            if (!CreateProcessW(exe, command, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref startup, out var info))
                continue;
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// Registers the native messaging host with the installed browsers, so their extension can start the app.
/// On Windows each browser looks under HKCU\Software\…\NativeMessagingHosts\<name>; the key's default value
/// is the path of a JSON manifest that names the host program and the extensions allowed to call it.
public static class NativeHostRegistry
{
    /// Chrome's key is also what Opera and other Chromium browsers read, so it is always written;
    /// the others only when that browser has ever been set up for this user.
    private static readonly (string Vendor, string Key, bool Firefox)[] browsers =
    [
        (@"Software\Google\Chrome", @"Software\Google\Chrome\NativeMessagingHosts", false),
        (@"Software\Microsoft\Edge", @"Software\Microsoft\Edge\NativeMessagingHosts", false),
        (@"Software\BraveSoftware\Brave-Browser", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", false),
        (@"Software\Chromium", @"Software\Chromium\NativeMessagingHosts", false),
        (@"Software\Mozilla", @"Software\Mozilla\NativeMessagingHosts", true),
    ];

    public static string ManifestDirectory => Path.Combine(AppPaths.DataDir, "native-host");
    private static string ManifestPath(bool firefox) => Path.Combine(ManifestDirectory, firefox ? "firefox.json" : "chromium.json");

    public static bool IsValidChromiumId(string id) => id.Length == 32 && id.All(c => c is >= 'a' and <= 'p');

    /// An unpacked extension without a "key" gets its id from its folder path: the first 128 bits of
    /// SHA-256(path), hex digits mapped 0-f → a-p. On Windows Chromium hashes the path as UTF-16 with an
    /// upper-case drive letter; elsewhere as UTF-8. Known in advance for the folders the setup wizard prepares.
    public static string UnpackedExtensionId(string path) => UnpackedExtensionId(path, OperatingSystem.IsWindows());

    internal static string UnpackedExtensionId(string path, bool windows)
    {
        byte[] bytes;
        if (windows)
        {
            if (path.Length >= 2 && path[1] == ':' && path[0] is >= 'a' and <= 'z') path = char.ToUpperInvariant(path[0]) + path[1..];
            bytes = Encoding.Unicode.GetBytes(path);
        }
        else bytes = Encoding.UTF8.GetBytes(path);
        var hash = SHA256.HashData(bytes);
        var id = new StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            id.Append((char)('a' + (hash[i] >> 4)));
            id.Append((char)('a' + (hash[i] & 0xF)));
        }
        return id.ToString();
    }

    /// Ids the extensions reported themselves (see `NoteExtensionId`) plus those of the wizard's folders.
    public static List<string> AllowedChromiumIds(AppSettings settings)
    {
        var ids = new SortedSet<string>(settings.ChromiumExtensionIds.Where(IsValidChromiumId), StringComparer.Ordinal);
        foreach (var kind in BrowserKind.All)
            if (!kind.Firefox) ids.Add(UnpackedExtensionId(BrowserIntegration.PluginFolder(kind)));
        return [.. ids];
    }

    /// Called by the bridge with the id of a Chromium extension that talked to it. Returns true when the id
    /// is new (a folder in another place, a store install); the caller then registers again.
    public static bool NoteExtensionId(AppSettings settings, string? id)
    {
        id = id?.ToLowerInvariant();
        if (id == null || !IsValidChromiumId(id) || settings.ChromiumExtensionIds.Contains(id)) return false;
        // Newest last. The list is fed by a header any local program can send, so it is kept short:
        // beyond the limit the oldest entries go (the wizard's own folders are always allowed anyway).
        var ids = settings.ChromiumExtensionIds.Append(id).ToList();
        if (ids.Count > MaxRememberedIds) ids.RemoveRange(0, ids.Count - MaxRememberedIds);
        settings.ChromiumExtensionIds = ids;
        return true;
    }

    public const int MaxRememberedIds = 32;

    public static string BuildManifest(string hostProgram, bool firefox, IEnumerable<string> chromiumIds)
    {
        var manifest = new JsonObject
        {
            ["name"] = NativeHost.HostName,
            ["description"] = "MC Download Manager launcher",
            ["path"] = hostProgram,
            ["type"] = "stdio",
        };
        if (firefox) manifest["allowed_extensions"] = new JsonArray(NativeHost.FirefoxAddonId);
        else manifest["allowed_origins"] = new JsonArray([.. chromiumIds.Select(id => (JsonNode)$"chrome-extension://{id}/")]);
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// The program a manifest file names, or null.
    internal static string? ManifestProgram(string manifestFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
            return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// Writes the manifests and the registry entries. Only a complete copy of the app registers itself, and
    /// an entry whose program still exists is left to that copy — the same rule as for the sign-in entry.
    public static void Register(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows() || !Shell.CompleteCopy || Environment.ProcessPath is not { } exe) return;
        try
        {
            Directory.CreateDirectory(ManifestDirectory);
            var ids = AllowedChromiumIds(settings);
            foreach (var firefox in new[] { false, true })
            {
                var file = ManifestPath(firefox);
                var owner = File.Exists(file) ? ManifestProgram(file) : null;
                if (owner != null && !string.Equals(owner, exe, StringComparison.OrdinalIgnoreCase) && File.Exists(owner)) continue;
                var text = BuildManifest(exe, firefox, ids);
                if (!File.Exists(file) || File.ReadAllText(file) != text) File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            foreach (var (vendor, key, firefox) in browsers)
            {
                try
                {
                    if (!vendor.EndsWith("Chrome", StringComparison.Ordinal))
                    {
                        using var installed = Registry.CurrentUser.OpenSubKey(vendor);
                        if (installed == null) continue;
                    }
                    using var k = Registry.CurrentUser.CreateSubKey(key + "\\" + NativeHost.HostName);
                    var file = ManifestPath(firefox);
                    if (k.GetValue(null) as string != file) k.SetValue(null, file);
                }
                catch { }
            }
        }
        catch { }
    }

    /// One line per browser key, for the self-test report.
    public static string Describe()
    {
        if (!OperatingSystem.IsWindows()) return "not Windows — skipped";
        var parts = new List<string>();
        foreach (var (_, key, firefox) in browsers)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key + "\\" + NativeHost.HostName);
                if (k?.GetValue(null) is not string file) continue;
                var name = key.Split('\\')[^2];
                parts.Add($"{name} → {ManifestProgram(file) ?? "(unreadable manifest)"}");
            }
            catch { }
        }
        return parts.Count == 0 ? "not registered" : string.Join("; ", parts);
    }
}
