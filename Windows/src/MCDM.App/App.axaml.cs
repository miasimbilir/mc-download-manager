using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MCDM.App.Views;

namespace MCDM.App;

public partial class App : Application, IStoreUi
{
    /// Set by the screenshot tool: no tray icon, no clipboard polling, no window shown at start.
    public static bool Headless { get; set; }

    public AppSettings Settings { get; private set; } = null!;
    public DownloadStore Store { get; private set; } = null!;
    public ServerController Server { get; private set; } = null!;
    public ClipboardWatcher Clipboard { get; private set; } = null!;
    public bool Quitting { get; private set; }

    public static App Instance => (App)Current!;

    private MainWindow? main;
    private SettingsWindow? settingsWindow;
    private WizardWindow? wizard;
    private TrayIcon? tray;
    private NativeMenuItem? trayStatus, trayBridge;
    private readonly Dictionary<Guid, DetailWindow> details = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (!Resources.ContainsKey("AccentBrush"))
            Resources["AccentBrush"] = new SolidColorBrush(this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color color ? color : Color.FromRgb(0x2F, 0x6F, 0xEB));

        // A failing callback is logged instead of taking the whole app down with it.
        MainThread.Use(a => Dispatcher.UIThread.Post(() =>
        {
            try { a(); } catch (Exception e) { CrashLog.Write(e); }
        }));
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.Handled = true;
        };
        Settings = AppSettings.Load();
        Store = new DownloadStore(Settings) { Ui = this };
        Server = new ServerController(Store, Settings);
        Clipboard = new ClipboardWatcher(this);

        if (!Headless && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) => Shutdown();
            SetupTray();
            Store.Changed += UpdateTray;
            Server.PropertyChanged += (_, _) => UpdateTray();
            Shell.RefreshStartupPath();
            ToolInstaller.CleanUp();
            if (Settings.OnboardingDone)
            {
                Store.PrepareDownloadFolder();
                Shell.EnsureStartMenuShortcut();
            }
            main = new MainWindow(this);
            Clipboard.Start(main);
            // Started by Windows at sign-in: stay in the notification area. Started by the user: show the window.
            var hidden = Program.StartedInBackground && Settings.StartInTray && Settings.OnboardingDone;
            if (!hidden) ShowMainWindow();
            ToolInstaller.Shared.RefreshVersions(Settings.YtdlpPath);
            // Only now is it safe to post to the UI thread from the pipe thread.
            SingleInstance.Listen(message =>
            {
                if (message == "show") Dispatcher.UIThread.Post(ShowMainWindow);
            });
            // A copy without its tools folder cannot download or merge video; say so instead of failing later.
            if (OperatingSystem.IsWindows() && !Shell.CompleteCopy
                && (VideoDownloader.LocateYtDlp(Settings.YtdlpPath) == null || VideoDownloader.LocateFfmpeg() == null))
                Dispatcher.UIThread.Post(() => _ = PromptWindow.Ask(null,
                    Loc.T("This copy of the app is incomplete"),
                    Loc.T("The copy in {0} has no “tools” folder next to it, so video downloads will not work. Start the app from the folder it was unpacked into, or from MC Download Manager in the Start menu.", AppPaths.AppDir.TrimEnd(Path.DirectorySeparatorChar)),
                    [Loc.T("Close")]), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // ---- Windows ----

    public MainWindow MainWindow => main ??= new MainWindow(this);

    public void ShowMainWindow()
    {
        MainWindow.Present();
        if (!Settings.OnboardingDone) MainWindow.ShowOnboarding();
    }

    public void ShowAddDialog()
    {
        ShowMainWindow();
        MainWindow.ShowAdd();
    }

    public void ShowSettings(int tab = -1)
    {
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        if (tab >= 0) settingsWindow.SelectTab(tab);
        settingsWindow.Present();
    }

    public void ShowWizard()
    {
        if (wizard == null)
        {
            wizard = new WizardWindow(this);
            wizard.Closed += (_, _) => wizard = null;
        }
        wizard.Present();
    }

    public void ShowDetail(Guid id)
    {
        if (Headless) return;
        if (details.TryGetValue(id, out var existing)) { existing.Present(); return; }
        var item = Store.Find(id);
        if (item == null) return;
        var w = new DetailWindow(this, item);
        // Cascade from the last download window so several downloads do not stack exactly.
        if (details.Values.LastOrDefault() is { } last)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = new PixelPoint(last.Position.X + 28, last.Position.Y + 28);
        }
        details[id] = w;
        w.Closed += (_, _) => details.Remove(id);
        w.Present();
    }

    public void CloseDetail(Guid id)
    {
        if (details.TryGetValue(id, out var w)) w.Close();
    }

    public void DetailCompleted(Guid id, bool autoClose)
    {
        if (autoClose) CloseDetail(id);
    }

    public void NotifyCompleted(DownloadItem item)
    {
        if (Headless) return;
        // The download window already shows "completed"; a toast on top of it would say the same thing twice.
        if (details.TryGetValue(item.Id, out var w) && w.IsVisible && !Settings.AutoCloseDetail) return;
        ToastWindow.Show(this, Loc.T("Download completed"), item.FileName, item);
    }

    public async Task<(bool Install, bool DontAskAgain)> AskInstallDenoAsync()
    {
        if (Headless) return (false, false);
        var (button, check) = await PromptWindow.Ask(null,
            Loc.T("Install the JavaScript runtime for YouTube?"),
            Loc.T("YouTube now requires yt-dlp to run JavaScript (deno) to unlock video streams. Without it, YouTube downloads may fail with errors such as “The page needs to be reloaded” or “n challenge solving failed”.\n\nMC Download Manager can download deno ({0}) from its official GitHub releases and keep it in its own app data folder.", ToolInstaller.DenoDownloadSize),
            [Loc.T("Install deno"), Loc.T("Not Now")],
            checkbox: Loc.T("Don’t ask again"), cancelIndex: 1);
        return (button == 0, check);
    }

    public Task<VideoFormatAnswer?> AskVideoFormatAsync(DownloadItem item, VideoInfo info) =>
        Headless ? Task.FromResult<VideoFormatAnswer?>(VideoFormatAnswer.Default) : VideoFormatWindow.Ask(Settings, item, info);

    // ---- Notification area ----

    private void SetupTray()
    {
        var menu = new NativeMenu();
        trayStatus = new NativeMenuItem(Loc.T("No active downloads")) { IsEnabled = false };
        trayBridge = new NativeMenuItem(Loc.T("Bridge is running")) { IsEnabled = false };
        menu.Add(trayStatus);
        menu.Add(trayBridge);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("Show Window"), ShowMainWindow));
        menu.Add(Item(Loc.T("New Download…"), ShowAddDialog));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("Pause All"), () => Store.PauseAll()));
        menu.Add(Item(Loc.T("Resume All"), () => Store.ResumeAll()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("Open Download Folder"), () => Shell.OpenFolder(Settings.DownloadDirectory)));
        menu.Add(Item(Loc.T("Settings…"), () => ShowSettings()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("Quit MC Download Manager"), Quit));

        tray = new TrayIcon { Icon = Icons.AppIcon, ToolTipText = AppInfo.Name, Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, [tray]);
        UpdateTray();

        static NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }
    }

    private string? lastTooltip;

    private void UpdateTray()
    {
        if (tray == null) return;
        var active = Store.ActiveCount;
        var status = active > 0 ? Loc.T("{0} downloading · {1}", active, Fmt.Speed(Store.TotalSpeed)) : Loc.T("No active downloads");
        var tip = AppInfo.Name;
        if (active > 0)
        {
            tip += "\n" + status;
            if (Store.OverallProgress is { } p) tip += $" · {(int)(p * 100)}%";
        }
        if (tip == lastTooltip) return;
        lastTooltip = tip;
        tray.ToolTipText = tip;
        if (trayStatus != null) trayStatus.Header = status;
        if (trayBridge != null) trayBridge.Header = Server.Running ? Loc.T("Bridge is running") : Loc.T("Bridge is not running");
    }

    // ---- Exit ----

    public void Quit()
    {
        Shutdown();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void Shutdown()
    {
        if (Quitting) return;
        Quitting = true;
        try { Clipboard.Stop(); } catch { }
        try { Store.Shutdown(); } catch { }
        try { Server.Stop(); } catch { }
        try
        {
            // Remove the icon now; otherwise Windows leaves a ghost until the pointer passes over it.
            if (tray != null) { tray.IsVisible = false; tray.Dispose(); tray = null; }
        }
        catch { }
    }
}
