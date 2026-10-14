using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MCDM.App.Views;

public partial class SettingsWindow : Window
{
    private readonly App app = null!;
    private readonly AppSettings settings = null!;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private static readonly (string Tag, string Label)[] containers =
    [
        ("mp4", "MP4 (H.264/AAC, plays everywhere)"), ("mkv", "MKV"), ("webm", "WebM"), ("original", "Original (no conversion)"),
    ];

    private static readonly (int Tag, string Label)[] standardHeights =
    [
        (0, "Best available"), (2160, "2160p (4K)"), (1440, "1440p"), (1080, "1080p"), (720, "720p"), (480, "480p"), (360, "360p"),
        (-1, "Audio only (M4A)"), (-2, "Audio only (MP3)"),
    ];

    /// What the resolution box lists: the standard entries plus the current setting when it is none of them.
    private List<(int Tag, string Label)> heights = [];
    private bool fillingHeights;

    /// "Always use this quality" in the quality question can store a resolution the standard list lacks
    /// (144p, 240p, 540p…). It gets its own entry; without one the box would fall back to "Best available"
    /// and write that over the user's choice.
    private void FillHeights()
    {
        var list = standardHeights.Select(h => (h.Tag, Label: Loc.T(h.Label))).ToList();
        var current = settings.VideoMaxHeight;
        if (current > 0 && !list.Any(h => h.Tag == current))
        {
            var at = list.FindIndex(h => h.Tag < current && h.Tag != 0);    // keeps the resolutions in descending order
            list.Insert(at < 0 ? list.Count : at, (current, current + "p"));
        }
        fillingHeights = true;
        try
        {
            heights = list;
            HeightBox.ItemsSource = list.Select(h => h.Label).ToList();
            HeightBox.SelectedIndex = Math.Max(0, list.FindIndex(h => h.Tag == current));
        }
        finally { fillingHeights = false; }
    }

    public SettingsWindow() => InitializeComponent();      // designer

    public SettingsWindow(App app)
    {
        this.app = app;
        settings = app.Settings;
        InitializeComponent();
        Icon = Icons.AppIcon;
        DataContext = settings;

        SetupGeneral();
        CaptureHost.Content = new CaptureTypesEditor(settings);
        SetupBrowsers();
        SetupVideo();

        settings.PropertyChanged += OnSettingsChanged;
        app.Server.PropertyChanged += OnServerChanged;
        ToolInstaller.Shared.PropertyChanged += OnInstallerChanged;
        timer.Tick += (_, _) => RefreshBrowsers();
        timer.Start();
        Activated += (_, _) => RefreshBrowsers();
        Closed += (_, _) =>
        {
            timer.Stop();
            settings.PropertyChanged -= OnSettingsChanged;
            app.Server.PropertyChanged -= OnServerChanged;
            ToolInstaller.Shared.PropertyChanged -= OnInstallerChanged;
        };
        ToolInstaller.Shared.RefreshVersions(settings.YtdlpPath);
    }

    public void SelectTab(int index) => Tabs.SelectedIndex = Math.Clamp(index, 0, 3);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.EffectiveArguments)) RefreshEffective();
        // "Always use this" in the quality question changes the resolution behind this window's back.
        if (e.PropertyName is nameof(AppSettings.VideoMaxHeight))
        {
            var index = heights.FindIndex(h => h.Tag == settings.VideoMaxHeight);
            if (index < 0) FillHeights();
            else if (HeightBox.SelectedIndex != index) HeightBox.SelectedIndex = index;
        }
        if (e.PropertyName is nameof(AppSettings.YtdlpPath)) { RefreshVideo(); ToolInstaller.Shared.RefreshVersions(settings.YtdlpPath); }
    }

    private void OnServerChanged(object? sender, PropertyChangedEventArgs e) => RefreshBrowsers();
    private void OnInstallerChanged(object? sender, PropertyChangedEventArgs e) => RefreshVideo();

    // ---- General ----

    private void SetupGeneral()
    {
        ShowFolder.Click += (_, _) => Shell.OpenFolder(settings.DownloadDirectory);
        ChooseFolder.Click += async (_, _) =>
        {
            IStorageFolder? start = null;
            try { start = await StorageProvider.TryGetFolderFromPathAsync(settings.DownloadDirectory); } catch { }
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("Download folder"), AllowMultiple = false, SuggestedStartLocation = start });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { Length: > 0 } path) settings.DownloadDirectory = path;
        };

        SegmentSlider.Value = settings.SegmentCount;
        ConcurrentSlider.Value = settings.MaxConcurrent;
        void Labels()
        {
            SegmentLabel.Text = Loc.T("Connections per download: {0}", settings.SegmentCount);
            ConcurrentLabel.Text = Loc.T("Concurrent downloads: {0}", settings.MaxConcurrent);
        }
        SegmentSlider.ValueChanged += (_, e) => { settings.SegmentCount = (int)Math.Round(e.NewValue); Labels(); };
        ConcurrentSlider.ValueChanged += (_, e) => { settings.MaxConcurrent = (int)Math.Round(e.NewValue); Labels(); app.Store.PumpQueue(); };
        Labels();

        SpeedLimitBox.Value = settings.SpeedLimitKb;
        SpeedLimitBox.ValueChanged += (_, e) => { if (e.NewValue is { } kb) settings.SpeedLimitKb = (int)kb; };
        SpeedLimitUnit.Text = Loc.T("{0}/s", "KB");

        Startup.IsEnabled = Shell.StartupSupported;
        Startup.IsChecked = Shell.StartupEnabled;
        Startup.IsCheckedChanged += (_, _) =>
        {
            try
            {
                Shell.SetStartup(Startup.IsChecked == true);
                StartupError.IsVisible = false;
            }
            catch (Exception e)
            {
                StartupError.Text = e.Message;
                StartupError.IsVisible = true;
            }
        };

        VersionText.Text = $"{AppInfo.Name} {AppInfo.Version}";
        ProjectPage.Click += (_, _) => Shell.OpenUrl(Updater.ProjectUrl);
        CheckUpdates.Click += async (_, _) =>
        {
            CheckUpdates.IsEnabled = false;
            UpdateStatus.Text = Loc.T("Checking…");
            UpdateStatus.IsVisible = true;
            UpdateStatus.Text = await app.CheckForUpdatesAsync(manual: true);
            CheckUpdates.IsEnabled = true;
        };
        if (app.AvailableUpdate is { } found)
        {
            UpdateStatus.Text = Loc.T("Version {0} is available", found.Version);
            UpdateStatus.IsVisible = true;
        }
    }

    // ---- Browser extensions ----

    private void SetupBrowsers()
    {
        OpenWizard.Click += (_, _) => app.ShowWizard();
        RebuildBrowsers();
        RefreshButton.Click += (_, _) => RebuildBrowsers();
        PluginPath.Text = BrowserIntegration.PluginsRoot;
        ToolTip.SetTip(PluginPath, BrowserIntegration.PluginsRoot);
        ShowPlugins.Click += (_, _) => Shell.OpenFolder(BrowserIntegration.PluginsRoot);
        Port.Text = settings.ServerPort.ToString();
        Port.TextChanged += (_, _) =>
        {
            if (int.TryParse(Port.Text, out var p) && p is >= 1024 and <= 65535) settings.ServerPort = p;
        };
    }

    private List<BrowserKind> browsers = [];
    // Keyed by name: Opera and Opera GX share one bridge id.
    private readonly Dictionary<string, (PathIcon Icon, TextBlock Text)> browserRows = [];

    private void RebuildBrowsers()
    {
        browsers = BrowserIntegration.InstalledBrowsers;
        BrowserList.Children.Clear();
        browserRows.Clear();
        if (browsers.Count == 0)
            BrowserList.Children.Add(new TextBlock { Text = Loc.T("No supported browser was found (Google Chrome, Microsoft Edge, Brave, Opera, Firefox)."), Classes = { "caption" } });
        foreach (var b in browsers)
        {
            var icon = new PathIcon { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 30 };
            row.Children.Add(new PathIcon { Data = Icons.Get("IconGlobe"), Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0) });
            var name = new TextBlock { Text = b.Name, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            Grid.SetColumn(icon, 2);
            row.Children.Add(icon);
            Grid.SetColumn(status, 3);
            row.Children.Add(status);
            BrowserList.Children.Add(row);
            browserRows[b.Name] = (icon, status);
        }
        RefreshBrowsers();
    }

    private void RefreshBrowsers()
    {
        foreach (var b in browsers)
        {
            if (!browserRows.TryGetValue(b.Name, out var row)) continue;
            var connected = app.Server.IsExtensionConnected(b.Id, 3600);
            // Connected, but with the code of an earlier version: the browser has to load the new files.
            var outdated = connected && app.Server.IsExtensionOutdated(b.Id);
            var prepared = !b.Firefox && BrowserIntegration.FilesPrepared(b);
            row.Text.Text = outdated ? (b.Firefox ? Loc.T("Old version — add it again with the setup wizard") : Loc.T("Old version — click “Reload” on the extensions page"))
                : connected ? Loc.T("Connected") : prepared ? Loc.T("Files prepared, not loaded yet") : Loc.T("Not installed");
            row.Icon.Data = Icons.Get(outdated ? "IconWarningFilled" : connected ? "IconCheckmarkCircleFilled" : "IconCircle");
            var brush = new DynamicResourceExtension(outdated ? "WarnBrush" : connected ? "OkBrush" : "SecondaryTextBrush");
            row.Icon[!ForegroundProperty] = brush;
            row.Text[!TextBlock.ForegroundProperty] = brush;
        }
        BridgeDot.Fill = app.Server.Running ? new SolidColorBrush(Color.FromRgb(0x2E, 0xBD, 0x59)) : new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
        BridgeStatus.Text = app.Server.StatusText;
    }

    // ---- Video ----

    private void SetupVideo()
    {
        ImportCookies.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T("Import cookies.txt…"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("cookies.txt") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            try { SiteCookies.Shared.Import(path); cookieError = null; }
            catch (Exception e) { cookieError = e.Message; }
            RefreshVideo();
        };
        ClearCookies.Click += (_, _) => { SiteCookies.Shared.Clear(); cookieError = null; RefreshVideo(); };

        InstallYtDlp.Click += async (_, _) =>
        {
            await ToolInstaller.Shared.InstallOrUpdateYtDlpAsync();
            ToolInstaller.Shared.RefreshVersions(settings.YtdlpPath);
        };
        InstallDeno.Click += async (_, _) =>
        {
            await ToolInstaller.Shared.InstallDenoAsync();
            ToolInstaller.Shared.RefreshVersions(settings.YtdlpPath);
        };
        ChooseYtDlp.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "yt-dlp", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) settings.YtdlpPath = path;
        };

        ContainerBox.ItemsSource = containers.Select(c => Loc.T(c.Label)).ToList();
        ContainerBox.SelectedIndex = Math.Max(0, Array.FindIndex(containers, c => c.Tag == settings.VideoContainer));
        ContainerBox.SelectionChanged += (_, _) => { if (ContainerBox.SelectedIndex >= 0) settings.VideoContainer = containers[ContainerBox.SelectedIndex].Tag; };
        FillHeights();
        HeightBox.SelectionChanged += (_, _) =>
        {
            if (!fillingHeights && HeightBox.SelectedIndex >= 0 && HeightBox.SelectedIndex < heights.Count)
                settings.VideoMaxHeight = heights[HeightBox.SelectedIndex].Tag;
        };

        RefreshVideo();
        RefreshEffective();
    }

    private string? cookieError;

    private void RefreshEffective() => Effective.Text = Loc.T("Effective arguments: {0}", settings.EffectiveArguments);

    private void RefreshVideo()
    {
        var jar = SiteCookies.Shared;
        if (jar.CookieCount > 0)
        {
            CookieStatus.Text = Loc.T("Signed in: {0}", string.Join(", ", jar.Domains.Take(4)));
            CookieDetail.Text = jar.SavedAt is { } d ? Loc.T("{0} cookies saved {1}", jar.CookieCount, Fmt.DateTimeShort(d)) : "";
        }
        else
        {
            CookieStatus.Text = Loc.T("Not signed in");
            CookieDetail.Text = "";
        }
        if (cookieError != null) CookieDetail.Text = cookieError;
        CookieDetail.Classes.Set("error", cookieError != null);
        CookieDetail.IsVisible = CookieDetail.Text.Length > 0;
        ClearCookies.IsEnabled = jar.CookieCount > 0;

        var installer = ToolInstaller.Shared;
        var ytdlp = VideoDownloader.LocateYtDlp(settings.YtdlpPath);
        YtDlpTitle.Text = ytdlp == null ? Loc.T("yt-dlp not found") : installer.YtDlpVersion is { } v ? Loc.T("yt-dlp {0}", v) : "yt-dlp";
        YtDlpTitle.Classes.Set("error", ytdlp == null);
        YtDlpPathText.Text = ytdlp ?? "";
        YtDlpPathText.IsVisible = ytdlp != null;
        ToolTip.SetTip(YtDlpPathText, ytdlp);
        InstallYtDlp.Content = installer.Busy ? Loc.T("Installing…") : ytdlp == null ? Loc.T("Install yt-dlp") : Loc.T("Update yt-dlp");
        InstallYtDlp.IsEnabled = !installer.Busy;
        InstallProgress.IsVisible = installer.Busy;
        InstallProgress.Value = installer.Progress;
        InstallMessage.Text = installer.Message ?? "";
        InstallMessage.IsVisible = !string.IsNullOrEmpty(installer.Message);

        var deno = VideoDownloader.LocateDeno();
        DenoTitle.Text = deno == null ? Loc.T("deno not installed (needed for YouTube)") : installer.DenoVersion ?? "deno";
        if (deno == null) DenoTitle[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("WarnBrush");
        else DenoTitle.ClearValue(TextBlock.ForegroundProperty);
        DenoPathText.Text = deno ?? "";
        DenoPathText.IsVisible = deno != null;
        InstallDeno.Content = deno == null ? Loc.T("Install deno") : Loc.T("Update deno");
        InstallDeno.IsEnabled = !installer.Busy;

        var ffmpeg = VideoDownloader.LocateFfmpeg();
        FfmpegText.Text = ffmpeg != null ? Loc.T("ffmpeg: {0}", ffmpeg) : Loc.T("ffmpeg not found (merging video and audio will not work)");
        FfmpegText.Classes.Set("error", ffmpeg == null);
    }
}
