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
    private NativeMenuItem? trayStatus, trayBridge, trayLimit, trayWhenDone;
    private readonly List<(QueueEndAction Action, NativeMenuItem Item)> trayWhenDoneItems = [];
    private readonly Dictionary<Guid, DetailWindow> details = [];
    private DispatcherTimer? updateTimer;

    /// What the last update check found (null = nothing newer, or not checked yet). Settings shows it.
    public UpdateInfo? AvailableUpdate { get; private set; }
    public event Action? UpdateStateChanged;

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
            Server.ExtensionOutdated += browser => _ = TellOutdatedExtension(browser);
            Settings.PropertyChanged += (_, _) => UpdateTray();
            Shell.RefreshStartupPath();
            ToolInstaller.CleanUp();
            Updater.CleanUp();
            // Lets the browser extensions start the app when a download arrives while it is closed.
            Task.Run(() => NativeHostRegistry.Register(Settings));
            // After an app update: bring the folders the browsers load the extension from up to date.
            Task.Run(BrowserIntegration.RefreshPreparedFiles);
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
                else if (message == "quit") Dispatcher.UIThread.Post(Quit);
            });
            // A copy without its tools folder cannot download or merge video; say so instead of failing later.
            if (OperatingSystem.IsWindows() && !Shell.CompleteCopy
                && (VideoDownloader.LocateYtDlp(Settings.YtdlpPath) == null || VideoDownloader.LocateFfmpeg() == null))
                Dispatcher.UIThread.Post(() => _ = PromptWindow.Ask(null,
                    Loc.T("This copy of the app is incomplete"),
                    Loc.T("The copy in {0} has no “tools” folder next to it, so video downloads will not work. Start the app from the folder it was unpacked into, or from MC Download Manager in the Start menu.", AppPaths.AppDir.TrimEnd(Path.DirectorySeparatorChar)),
                    [Loc.T("Close")]), DispatcherPriority.Background);
            // A little after start, then a few times a day; CheckForUpdatesAsync itself keeps it to once a day.
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            updateTimer.Tick += (_, _) =>
            {
                updateTimer.Interval = TimeSpan.FromHours(6);
                if (Settings.CheckForUpdates && (DateTime.UtcNow - Settings.LastUpdateCheck).TotalHours >= 20) _ = CheckForUpdatesAsync(manual: false);
            };
            updateTimer.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    // ---- Updates ----

    private bool checkingUpdates;

    /// Looks for a newer release. Found one: the update window opens (an automatic check stays quiet about
    /// a version the user chose to skip). Returns a line for the Settings window.
    public async Task<string> CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdates) return Loc.T("Checking…");
        checkingUpdates = true;
        try
        {
            var found = await Updater.CheckAsync();
            Settings.LastUpdateCheck = DateTime.UtcNow;
            AvailableUpdate = found;
            UpdateStateChanged?.Invoke();
            if (found == null) return Loc.T("You're up to date");
            if (manual || found.Version != Settings.SkippedVersion) UpdateWindow.Show(this, found);
            return Loc.T("Version {0} is available", found.Version);
        }
        catch (Exception e)
        {
            return Loc.T("Could not check for updates") + ": " + e.Message;
        }
        finally
        {
            checkingUpdates = false;
        }
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
        Headless ? Task.FromResult<VideoFormatAnswer?>(VideoFormatAnswer.Default) : VideoFormatWindow.Ask(Store, item, info);

    public Task<DownloadAnswer> AskDownloadAsync(DownloadItem item) =>
        Headless ? Task.FromResult(DownloadAnswer.Start) : DownloadAskWindow.Ask(this, item);

    public async Task<DuplicateDecision> AskDuplicateAsync(DownloadItem existing)
    {
        if (Headless) return DuplicateDecision.DownloadAgain;
        var when = existing.CompletedAt is { } d ? Fmt.DateTimeShort(d) : "";
        var (button, _) = await PromptWindow.Ask(null,
            Loc.T("This file was already downloaded"),
            existing.FileName + "\n" + (existing.FinalPath ?? "") + (when.Length > 0 ? "\n" + Loc.T("Downloaded: {0}", when) : ""),
            [Loc.T("Open"), Loc.T("Show in Folder"), Loc.T("Download Again"), Loc.T("Cancel")], cancelIndex: 3);
        return button switch
        {
            0 => DuplicateDecision.Open,
            1 => DuplicateDecision.Reveal,
            2 => DuplicateDecision.DownloadAgain,
            _ => DuplicateDecision.Cancel,
        };
    }

    public async Task<HandOffAnswer> AskHandOffAsync(DownloadItem item, string site)
    {
        if (Headless) return HandOffAnswer.Close;
        var (button, always) = await PromptWindow.Ask(null,
            Loc.T("Download in the browser instead?"),
            Loc.T("“{0}” could not be downloaded: {1}\n\nSome sites (for example WeTransfer) only allow their downloads in a browser. MC Download Manager can hand this download back to the browser.", item.FileName, item.ErrorMessage ?? ""),
            [Loc.T("Download in Browser"), Loc.T("Retry Here"), Loc.T("Close")],
            checkbox: Loc.T("Always leave downloads from {0} to the browser", site), cancelIndex: 2);
        return new(button switch { 0 => HandOffDecision.Browser, 1 => HandOffDecision.Retry, _ => HandOffDecision.Close }, always);
    }

    private bool outdatedAsking;
    private readonly Queue<string> outdatedWaiting = [];

    /// A browser still runs an extension older than the one this app carries: say so once, and offer the fix
    /// (Chromium: the folder is current, the extension has to be reloaded; Firefox: install the new add-on).
    /// One window at a time; a second browser waits for the first.
    private async Task TellOutdatedExtension(string browserId)
    {
        if (Headless) return;
        if (outdatedAsking) { if (!outdatedWaiting.Contains(browserId)) outdatedWaiting.Enqueue(browserId); return; }
        if (BrowserKind.ForId(browserId) is not { } kind) { TellNextOutdated(); return; }
        outdatedAsking = true;
        try
        {
            var old = Server.ExtensionVersion(browserId) is { Length: > 0 } v ? v : Loc.T("an older version");
            var bundled = BrowserIntegration.BundledVersion ?? AppInfo.Version;
            if (!kind.Firefox && BrowserIntegration.FilesPrepared(kind) && BrowserIntegration.PreparedVersion(kind) != bundled)
                await Task.Run(BrowserIntegration.RefreshPreparedFiles);
            var text = kind.Firefox
                ? Loc.T("{0} runs extension {1}, this app comes with {2}. Install the new add-on: Firefox asks you to confirm.", kind.Name, old, bundled)
                : Loc.T("{0} runs extension {1}, this app comes with {2}. The extension folder has been updated; click the reload icon (↻) of MC Download Manager on the extensions page, or restart {3}.", kind.Name, old, bundled, kind.Name);
            var (button, _) = await PromptWindow.Ask(null, Loc.T("The {0} extension is out of date", kind.Name), text,
                [kind.Firefox ? Loc.T("Add to Firefox") : Loc.T("Open Extensions Page"), Loc.T("Later")], cancelIndex: 1);
            if (button != 0) return;
            if (kind.Firefox) BrowserIntegration.InstallFirefoxAddon(kind); else BrowserIntegration.OpenExtensionsPage(kind);
        }
        catch (Exception e) { CrashLog.Write(e); }
        finally
        {
            outdatedAsking = false;
            TellNextOutdated();
        }
    }

    private void TellNextOutdated()
    {
        // Still outdated? It may have been reloaded while the other window was open.
        while (outdatedWaiting.TryDequeue(out var next))
            if (Server.IsExtensionOutdated(next)) { _ = TellOutdatedExtension(next); return; }
    }

    public async void QueueFinished(QueueEndAction action)
    {
        if (Headless || action == QueueEndAction.None) return;
        try
        {
            if (!await CountdownWindow.Ask(action)) return;
            // Something was started during the countdown: wait for that to end as well.
            if (Store.Items.Any(i => i.State.IsActive() || i.State == DownloadState.queued)) { Store.WhenDone = action; return; }
            // Sleep returns only when the computer is awake again: keep it off the UI thread.
            var done = await Task.Run(() => action == QueueEndAction.Shutdown ? Shell.Shutdown() : Shell.Sleep());
            if (!done) ToastWindow.Show(this, AppInfo.Name, action == QueueEndAction.Shutdown
                ? Loc.T("Windows did not let the app shut the computer down.") : Loc.T("Windows did not let the app put the computer to sleep."));
        }
        catch (Exception e) { CrashLog.Write(e); }
    }

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
        // Both say their state in their text ("Speed limit: off", "✓ Shut down the computer"): how a
        // check mark is drawn in this menu differs from one Windows version to the next.
        trayLimit = Item("", () => Settings.SpeedLimitEnabled = !Settings.SpeedLimitEnabled);
        menu.Add(trayLimit);
        trayWhenDone = new NativeMenuItem { Menu = [] };
        foreach (var action in WhenDoneOptions)
        {
            var choice = Item(WhenDoneLabel(action), () => Store.WhenDone = action);
            trayWhenDoneItems.Add((action, choice));
            trayWhenDone.Menu.Add(choice);
        }
        menu.Add(trayWhenDone);
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

    public static readonly QueueEndAction[] WhenDoneOptions = [QueueEndAction.None, QueueEndAction.Sleep, QueueEndAction.Shutdown];

    public static string WhenDoneLabel(QueueEndAction action) => action switch
    {
        QueueEndAction.Sleep => Loc.T("Put the computer to sleep"),
        QueueEndAction.Shutdown => Loc.T("Shut down the computer"),
        _ => Loc.T("Do nothing"),
    };

    /// "Speed limit: 1,00 MB/s" / "Speed limit: off" — for the tray menu and the status bar.
    public string SpeedLimitLabel => Settings.SpeedLimitEnabled
        ? Loc.T("Speed limit: {0}", Fmt.Speed(Settings.SpeedLimitBytes))
        : Loc.T("Speed limit: off");

    public string WhenDoneSummary => Loc.T("When done: {0}", WhenDoneLabel(Store.WhenDone));

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
        // Each header only when it changes: the menu is rebuilt by the system on every assignment.
        static void Set(NativeMenuItem? item, string header)
        {
            if (item != null && item.Header != header) item.Header = header;
        }
        Set(trayLimit, SpeedLimitLabel);
        Set(trayWhenDone, WhenDoneSummary);
        foreach (var (action, item) in trayWhenDoneItems) Set(item, (Store.WhenDone == action ? "✓ " : "") + WhenDoneLabel(action));
        Set(trayBridge, Server.Running ? Loc.T("Bridge is running") : Loc.T("Bridge is not running"));
        if (tip == lastTooltip) return;
        lastTooltip = tip;
        tray.ToolTipText = tip;
        Set(trayStatus, status);
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
        try { updateTimer?.Stop(); } catch { }
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
