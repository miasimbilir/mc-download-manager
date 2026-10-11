using System.IO.Pipes;
using System.Text;
using Avalonia;
using Avalonia.Threading;

namespace MCDM.App;

internal static class Program
{
    public static bool StartedInBackground { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Started by a browser as its native messaging host: answer and leave, no window, no bridge.
        if (NativeHost.IsHostInvocation(args)) return NativeHost.Run();

        if (args.Contains("--selftest")) return SelfTest.Run(args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault());

        // The installer and the uninstaller run this before they touch the files.
        if (args.Contains("--quit")) return SingleInstance.Quit() ? 0 : 1;

        StartedInBackground = args.Contains(Shell.BackgroundArgument);
        // One instance per user: a second launch asks the running one to show its window.
        using var mutex = new Mutex(true, SingleInstance.MutexName, out var first);
        if (!first)
        {
            SingleInstance.Signal(StartedInBackground ? "noop" : "show");
            return 0;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception); e.SetObserved(); };
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            throw;
        }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

internal static class CrashLog
{
    public static string File => Path.Combine(AppPaths.DataDir, "crash.log");

    public static void Write(Exception? e)
    {
        if (e == null) return;
        try
        {
            System.IO.File.AppendAllText(File, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {AppInfo.Name} {AppInfo.Version} on {Environment.OSVersion}\n{e}\n\n", Encoding.UTF8);
        }
        catch { }
    }
}

internal static class SingleInstance
{
    public static string MutexName => "MCDownloadManager.SingleInstance." + Environment.UserName;
    private static string PipeName => "MCDownloadManager." + Environment.UserName;

    /// `--quit`: asks the running instance to exit the way "Quit" in the tray menu does (downloads paused,
    /// list saved, tray icon removed) and waits until it has. True when no instance is left.
    public static bool Quit(int timeoutMs = 15000)
    {
        if (!Mutex.TryOpenExisting(MutexName, out var running)) return true;
        running.Dispose();
        // Wait for the process, not for the mutex: the mutex goes when the main thread ends, a moment
        // before the process does, and until then the executable cannot be replaced or deleted.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var others = System.Diagnostics.Process.GetProcessesByName(self.ProcessName)
            .Where(p => p.Id != self.Id && p.SessionId == self.SessionId).ToList();
        Signal("quit");
        var deadline = Environment.TickCount64 + timeoutMs;
        var gone = true;
        foreach (var p in others)
        {
            try { if (!p.WaitForExit((int)Math.Max(1, deadline - Environment.TickCount64))) gone = false; }
            catch { }
            finally { p.Dispose(); }
        }
        return gone;
    }

    public static void Signal(string message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // The first instance starts listening once its UI is up; give a launch that is still starting time to get there.
            pipe.Connect(8000);
            var bytes = Encoding.UTF8.GetBytes(message);
            pipe.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }

    public static void Listen(Action<string> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var message = reader.ReadToEnd().Trim();
                    if (message.Length > 0) onMessage(message);
                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true, Name = "mcdm-single-instance" };
        thread.Start();
    }
}
