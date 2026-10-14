using System.Runtime.InteropServices;

namespace MCDM;

public static class AppInfo
{
    public const string Name = "MC Download Manager";
    /// The Windows app's own version (its own line since 2.0; up to 1.3.0 it followed the macOS app).
    /// The extension popup shows it.
    public const string Version = "2.0.0";
    public const int DefaultPort = 48213;
}

/// Where the app keeps its files. `MCDM_DATA_DIR` redirects everything (used by the tests).
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();
    public static string BinDir => Path.Combine(DataDir, "bin");
    public static string CookiesDir => Path.Combine(DataDir, "cookies");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DownloadsFile => Path.Combine(DataDir, "downloads.json");

    /// Folder of the running executable; `tools\` (yt-dlp, ffmpeg) and `ChromeExtension\` sit next to it.
    public static string AppDir { get; } = AppContext.BaseDirectory;
    public static string BundledToolsDir => Path.Combine(AppDir, "tools");

    public static string DefaultDownloadDir { get; } = Path.Combine(DownloadsFolder(), AppInfo.Name);

    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string ResolveDataDir()
    {
        var custom = Environment.GetEnvironmentVariable("MCDM_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    private static string DownloadsFolder()
    {
        var custom = Environment.GetEnvironmentVariable("MCDM_DOWNLOADS_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        if (OperatingSystem.IsWindows())
        {
            // The Downloads folder can be relocated, so ask the shell instead of assuming %USERPROFILE%\Downloads.
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var path = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}

/// The store, the bridge and the UI all live on one thread. The app points this at its UI dispatcher.
public static class MainThread
{
    private static Action<Action> post = DefaultPost;
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> queue = new();
    private static Thread? worker;

    public static void Use(Action<Action> dispatcher) => post = dispatcher;
    public static void Post(Action action) => post(action);

    public static Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }

    public static Task Run(Action f) => Run(() => { f(); return true; });

    /// Without a UI (tests, tools) a single background thread plays the main thread.
    private static void DefaultPost(Action action)
    {
        if (worker == null)
        {
            lock (queue)
            {
                worker ??= StartWorker();
            }
        }
        queue.Add(action);
    }

    private static Thread StartWorker()
    {
        var t = new Thread(() =>
        {
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); } catch (Exception e) { Console.Error.WriteLine(e); }
            }
        }) { IsBackground = true, Name = "mcdm-main" };
        t.Start();
        return t;
    }
}
