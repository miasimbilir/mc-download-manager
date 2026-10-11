using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using MCDM;
using MCDM.App;
using MCDM.App.Views;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "shots");
var lang = args.Length > 1 ? args[1] : "tr";
Directory.CreateDirectory(outDir);
var data = Path.Combine(Path.GetTempPath(), "mcdm-shots-" + Guid.NewGuid().ToString("N")[..6]);
Environment.SetEnvironmentVariable("MCDM_DATA_DIR", data);
// Not the default port: with the app itself running, the status bar would show "address already in use".
Directory.CreateDirectory(data);
File.WriteAllText(Path.Combine(data, "settings.json"), "{\"ServerPort\":48557}");
Environment.SetEnvironmentVariable("MCDM_DOWNLOADS_DIR", @"C:\Users\Kullanıcı\Downloads");
Environment.SetEnvironmentVariable("MCDM_LANG", lang);
var culture = Environment.GetEnvironmentVariable("SHOTS_CULTURE") ?? (lang == "tr" ? "tr-TR" : "en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = new(culture);
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.CurrentUICulture = new(culture);
var only = Environment.GetEnvironmentVariable("SHOTS_ONLY");

App.Headless = true;
var builder = AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
// The headless default on macOS (Helvetica) draws "İ" without its dot; Arial is closer to what Windows shows.
var font = Environment.GetEnvironmentVariable("SHOTS_FONT") is { Length: > 0 } f ? f : OperatingSystem.IsWindows() ? "Segoe UI" : "Arial";
builder = builder.With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = font });
builder.SetupWithoutStarting();
Console.WriteLine($"culture {culture}, default font {Avalonia.Media.FontManager.Current.DefaultFontFamily.Name}");
var app = App.Instance;
app.Settings.AutoStart = false;
app.Settings.ShowDetailOnStart = false;
app.Settings.OnboardingDone = true;
app.Settings.DownloadDirectory = @"C:\Users\Kullanıcı\Downloads\MC Download Manager";

DownloadItem Add(string url, DownloadKind kind, Action<DownloadItem> setup)
{
    var item = app.Store.Add(new NewDownloadRequest(new Uri(url)) { Kind = kind });
    setup(item);
    item.Touch();
    return item;
}

const long MB = 1024 * 1024;
Add("https://example.com/tatil-fotograflari.jpg", DownloadKind.file, i => { i.State = DownloadState.completed; i.TotalBytes = i.ReceivedBytes = 4 * MB; i.CreatedAt = DateTime.UtcNow.AddDays(-3); i.CompletedAt = i.CreatedAt; });
Add("https://example.com/muzik-arsivi.zip", DownloadKind.file, i => { i.CreatedAt = DateTime.UtcNow.AddHours(-5); });
Add("https://download.example.com/setup-x64.exe", DownloadKind.file, i => { i.State = DownloadState.failed; i.ErrorMessage = Loc.T("Server returned HTTP {0}", 403); i.CreatedAt = DateTime.UtcNow.AddHours(-3); });
var paused = Add("https://static.media.example.com/TV_FOOTAGE_AMG_GLC_53_4MATIC_high_tech_silver_HD_PAL.mp4", DownloadKind.file, i =>
{
    i.State = DownloadState.paused; i.TotalBytes = 2355 * MB; i.SupportsRanges = true;
    i.Segments = [.. SegmentedDownloader.MakeSegments(i.TotalBytes, 8).Select((s, n) => new SegmentInfo(s.Start, s.End, (long)(s.Length * (0.35 + 0.07 * n))))];
    i.ReceivedBytes = i.Segments.Sum(s => s.Received); i.CreatedAt = DateTime.UtcNow.AddMinutes(-50);
});
var done = Add("https://uyap.example.gov.tr/dilekce-ornegi.pdf", DownloadKind.file, i =>
{
    i.State = DownloadState.completed; i.TotalBytes = i.ReceivedBytes = 1_300_000; i.CompletedAt = DateTime.UtcNow.AddMinutes(-12);
    i.FinalPath = @"C:\Users\Kullanıcı\Downloads\MC Download Manager\Belge\dilekce-ornegi.pdf"; i.CreatedAt = DateTime.UtcNow.AddMinutes(-13);
});
var video = Add("https://www.youtube.com/watch?v=1H-0x4xk2Xk", DownloadKind.video, i =>
{
    i.State = DownloadState.downloading; i.FileName = "Squadron 42 CitizenCon 2954 Live Gameplay Reveal [1H-0x4xk2Xk].mp4";
    i.TotalBytes = 1824 * MB; i.ReceivedBytes = 186 * MB; i.Speed = 18.3 * MB; i.Stage = Loc.T("Downloading video…"); i.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
    i.Log = "[youtube] Extracting URL: https://www.youtube.com/watch?v=1H-0x4xk2Xk\n[youtube] 1H-0x4xk2Xk: Downloading webpage\n[youtube] 1H-0x4xk2Xk: Downloading tv client config\n[youtube] 1H-0x4xk2Xk: Downloading player 5203c085-main\n[youtube] [jsc:deno] Solving JS challenges using deno\n[info] 1H-0x4xk2Xk: Downloading 1 format(s): 299+140\nMCFMT|1920x818|mp4|avc1.64002A|mp4a.40.2|1912913608|299+140|4572\n[download] Destination: C:\\Users\\Kullanıcı\\Downloads\\MC Download Manager\\Video\\Squadron 42 CitizenCon 2954 Live Gameplay Reveal [1H-0x4xk2Xk].f299.mp4\n";
});
var active = Add("https://releases.ubuntu.com/26.04/ubuntu-26.04-desktop-amd64.iso", DownloadKind.file, i =>
{
    i.State = DownloadState.downloading; i.TotalBytes = 5912 * MB; i.SupportsRanges = true; i.Speed = 106.1 * MB;
    double[] f = [0.52, 0.47, 0.61, 0.44, 0.39, 0.5, 0.33, 0.28];
    i.Segments = [.. SegmentedDownloader.MakeSegments(i.TotalBytes, 8).Select((s, n) => new SegmentInfo(s.Start, s.End, (long)(s.Length * f[n])))];
    i.ReceivedBytes = i.Segments.Sum(s => s.Received);
});

void Pump()
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}

void Shot(Window w, string name, Action? before = null)
{
    if (only != null && !name.StartsWith(only)) return;
    w.Show();
    Pump();
    before?.Invoke();
    Pump();
    Pump();
    using var frame = w.CaptureRenderedFrame();
    var file = Path.Combine(outDir, name + ".png");
    frame?.Save(file);
    Console.WriteLine($"{name}: {(frame == null ? "NO FRAME" : $"{frame.PixelSize.Width}x{frame.PixelSize.Height}")}");
    w.Hide();
}

foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
{
    app.RequestedThemeVariant = theme;
    var t = theme == ThemeVariant.Dark ? "dark" : "light";
    Shot(new MainWindow(app), $"main-{t}");
    Shot(new DetailWindow(app, active), $"detail-file-{t}");
    if (theme == ThemeVariant.Dark) continue;
    Shot(new DetailWindow(app, video), "detail-video");
    Shot(new DetailWindow(app, paused), "detail-paused");
    Shot(new DetailWindow(app, done), "detail-done");
    Shot(new AddDownloadWindow(app), "add");
    for (int tab = 0; tab < 4; tab++)
    {
        var s = new SettingsWindow(app) { Height = tab is 0 or 3 ? 940 : 640 };
        s.SelectTab(tab);
        Shot(s, $"settings-{tab}");
    }
    var wizard = new WizardWindow(app);
    Shot(wizard, "wizard-0");
    for (int step = 1; step < 4; step++)
    {
        wizard.Show();
        wizard.GetType().GetMethod("Next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(wizard, null);
        Shot(wizard, $"wizard-{step}");
    }
    app.Settings.OnboardingDone = false;
    var onboarding = new OnboardingWindow(app);
    Shot(onboarding, "onboarding-0");
    onboarding.GetType().GetField("step", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(onboarding, 1);
    onboarding.GetType().GetMethod("Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(onboarding, null);
    Shot(onboarding, "onboarding-1");
    app.Settings.OnboardingDone = true;
    Shot(PromptWindow.Create(Loc.T("A link was copied. Download it?"), "https://releases.ubuntu.com/26.04/ubuntu-26.04-desktop-amd64.iso",
        [Loc.T("Download"), Loc.T("Download Video (yt-dlp)"), Loc.T("Ignore")], Loc.T("Stop watching the clipboard"), 2), "prompt");
    Shot(ToastWindow.Create(app, Loc.T("Download completed"), done.FileName, done), "toast");
    // The quality question, fed with a recorded yt-dlp listing (run from the Windows folder).
    var fixture = Path.Combine("tests", "MCDM.Tests", "Fixtures", "formats-x.json");
    if (File.Exists(fixture))
    {
        using var listing = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture));
        var parsed = VideoFormats.Parse(listing.RootElement, "mp4") with { Title = "Belgesel: Boğaz'ın iki yakası — 4. bölüm" };
        Shot(new VideoFormatWindow(app.Settings, video, parsed), "quality");
    }
}

// An empty list
foreach (var i in app.Store.Items.ToList()) app.Store.Remove(i.Id);
app.RequestedThemeVariant = ThemeVariant.Light;
Shot(new MainWindow(app), "main-empty");

try { Directory.Delete(data, true); } catch { }
if (!OperatingSystem.IsWindows())
    foreach (var stray in Directory.GetDirectories(Environment.CurrentDirectory, @"C:\*"))
        try { Directory.Delete(stray, false); } catch { }
Environment.Exit(0);
