using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MCDM.App.Views;

/// Guided, one-browser-at-a-time setup of the browser extension (Chrome, Edge, Brave, Opera, Firefox).
public sealed class WizardWindow : Window
{
    /// Chromium: prepare the folder, open the extensions page, load the folder, done. Firefox: add the signed file, done.
    private int StepCount => Browser.Firefox ? 2 : 4;
    private readonly App app;
    private readonly List<BrowserKind> browsers;
    private int browserIndex;
    private int step;
    private string? error;
    private bool copied;
    private string signature = "";
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock subtitle = new() { Classes = { "caption" } };
    private readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel body = new() { Spacing = 14, Margin = new Thickness(22) };
    private readonly Button back, skip, next;

    public WizardWindow(App app)
    {
        this.app = app;
        browsers = BrowserIntegration.InstalledBrowsers;
        if (browsers.Count == 0) browsers = [BrowserKind.All[0]];

        Title = Loc.T("Browser Extension Setup");
        Icon = Icons.AppIcon;
        Width = 640;
        Height = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(22, 14) };
        header.Children.Add(new PathIcon { Data = Icons.Get("IconPuzzlePiece"), Width = 34, Height = 34, Margin = new Thickness(0, 0, 14, 0) }
            .Resource(TemplatedControl.ForegroundProperty, "AccentBrush"));
        var names = new StackPanel { Spacing = 2 };
        names.Children.Add(title);
        names.Children.Add(subtitle);
        Grid.SetColumn(names, 1);
        header.Children.Add(names);
        Grid.SetColumn(dots, 2);
        header.Children.Add(dots);

        back = Ui.Button(Loc.T("Back"), Back);
        skip = Ui.Button(Loc.T("Skip This Browser"), SkipBrowser);
        next = Ui.Button(Loc.T("Next"), Next, accent: true);
        next.IsDefault = true;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        footer.Children.Add(back);
        Grid.SetColumn(skip, 2);
        footer.Children.Add(skip);
        Grid.SetColumn(next, 3);
        next.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(next);

        var root = new DockPanel();
        var top = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = header }.Resource(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(top, Dock.Top);
        var bottom = Ui.Footer(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;

        // The status rows (files ready, extension connected) change while the user works in the browser.
        timer.Tick += (_, _) => { if (Signature() != signature) Render(); };
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Render();
    }

    private BrowserKind Browser => browsers[browserIndex];
    private bool IsLastStep => step >= StepCount - 1;
    private bool IsLastBrowser => browserIndex >= browsers.Count - 1;
    private bool Connected => app.Server.IsExtensionConnected(Browser.Id);
    /// Connected, but still with the code of an earlier version of the extension.
    private bool Outdated => Connected && app.Server.IsExtensionOutdated(Browser.Id);

    private string Signature() => $"{browserIndex}/{step}/{BrowserIntegration.FilesPrepared(Browser)}/{Connected}/{Outdated}/{copied}/{error}";

    private void Next()
    {
        error = null;
        copied = false;
        if (IsLastStep)
        {
            if (IsLastBrowser) { Close(); return; }
            browserIndex++;
            step = 0;
        }
        else step++;
        Render();
    }

    private void Back()
    {
        error = null;
        if (step > 0) step--;
        else if (browserIndex > 0) { browserIndex--; step = 0; }
        Render();
    }

    private void SkipBrowser()
    {
        error = null;
        if (IsLastBrowser) { Close(); return; }
        browserIndex++;
        step = 0;
        Render();
    }

    private void Render()
    {
        signature = Signature();
        var b = Browser;
        title.Text = b.Name;
        subtitle.Text = Loc.T("Browser {0} of {1} · Step {2} of {3}", browserIndex + 1, browsers.Count, step + 1, StepCount);
        dots.Children.Clear();
        for (int i = 0; i < StepCount; i++)
            dots.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8 }
                .Resource(Avalonia.Controls.Shapes.Shape.FillProperty, i <= step ? "AccentBrush" : "TrackBrush"));
        back.IsEnabled = !(step == 0 && browserIndex == 0);
        skip.IsVisible = !IsLastBrowser || !IsLastStep;
        next.Content = IsLastStep ? (IsLastBrowser ? Loc.T("Finish") : Loc.T("Next Browser")) : Loc.T("Next");

        body.Children.Clear();
        if (b.Firefox)
        {
            RenderFirefox(b);
            if (error != null) body.Children.Add(Ui.Text(error, "caption", "error"));
            return;
        }
        var folder = BrowserIntegration.PluginFolder(b);
        switch (step)
        {
            case 0:
                body.Children.Add(Ui.Text(Loc.T("Prepare the extension files"), "section"));
                body.Children.Add(Ui.Text(Loc.T("{0} does not allow apps to install extensions automatically, so the extension is loaded from a folder. The files are placed in:", b.Name)));
                body.Children.Add(Ui.PathBox(folder));
                var reveal = Ui.Button(Loc.T("Show in Folder"), () => Shell.OpenFolder(folder));
                reveal.IsEnabled = BrowserIntegration.FilesPrepared(b);
                body.Children.Add(Ui.Row(
                    Ui.Button(Loc.T("Prepare Files"), () =>
                    {
                        try { BrowserIntegration.PrepareFiles(b); error = null; }
                        catch (Exception e) { error = e.Message; }
                        Render();
                    }, "IconFolderAdd", accent: !BrowserIntegration.FilesPrepared(b)),
                    reveal));
                body.Children.Add(Ui.StatusRow(BrowserIntegration.FilesPrepared(b), Loc.T("Files are ready"), Loc.T("Click “Prepare Files”")));
                body.Children.Add(Ui.Text(Loc.T("Keep this folder in place; the browser loads the extension from it."), "caption"));
                break;

            case 1:
                body.Children.Add(Ui.Text(Loc.T("Open the extensions page and turn on Developer mode"), "section"));
                body.Children.Add(Ui.Text(Loc.T("Click the button below: {0} opens and the address of its extensions page is copied. If that page does not appear by itself, click the address bar, paste (Ctrl+V) and press Enter.", b.Name)));
                body.Children.Add(Ui.Row(Ui.Button(copied ? Loc.T("Address Copied — Paste It in {0}", b.Name) : Loc.T("Open {0} and Copy the Address", b.Name), async () =>
                {
                    try { if (Clipboard is { } c) await c.SetTextAsync(b.ExtensionsPage); copied = true; } catch { }
                    error = BrowserIntegration.OpenExtensionsPage(b) ? null : Loc.T("{0} could not be started. Type the address below into its address bar.", b.Name);
                    Render();
                }, "IconGlobe", accent: !copied)));
                body.Children.Add(Ui.PathBox(b.ExtensionsPage));
                body.Children.Add(Ui.Text(Loc.T("On the extensions page, switch on “Developer mode” (top right in Chrome, Brave and Opera, in the left-hand menu in Edge).")));
                if (BrowserIntegration.ChromeWebStoreUrl is { } store)
                    body.Children.Add(Ui.Row(Ui.Button(Loc.T("Or install from the Chrome Web Store"), () => Shell.OpenUrl(store))));
                break;

            case 2:
                body.Children.Add(Ui.Text(Loc.T("Load the extension folder"), "section"));
                body.Children.Add(Ui.Text(Loc.T("Click “Load unpacked” on the extensions page. In the folder dialog, paste the path below into the “Folder” box at the bottom and click “Select Folder”.")));
                body.Children.Add(Ui.PathBox(folder));
                body.Children.Add(Ui.Row(
                    Ui.Button(copied ? Loc.T("Copied") : Loc.T("Copy Folder Path"), async () =>
                    {
                        app.Clipboard.Ignore(folder);
                        try { if (Clipboard is { } c) await c.SetTextAsync(folder); copied = true; } catch { }
                        Render();
                    }, "IconCopy", accent: !copied),
                    Ui.Button(Loc.T("Show in Folder"), () => Shell.OpenFolder(folder))));
                if (!BrowserIntegration.FilesPrepared(b))
                    body.Children.Add(Ui.Text(Loc.T("The files are not prepared yet. Go back one step and click “Prepare Files”."), "caption", "error"));
                break;

            default:
                body.Children.Add(Ui.Text(Loc.T("{0} is ready", b.Name), "section"));
                body.Children.Add(Ui.Text(Loc.T("Pin MC Download Manager to the toolbar (puzzle icon ▸ pin). Its popup should show “Connected”. Downloads of the captured file types are now handed to the app.")));
                body.Children.Add(Ui.StatusRow(Connected, Loc.T("Extension has connected to the app"), Loc.T("Waiting for the extension to connect (it connects right after loading)")));
                if (Outdated)
                    body.Children.Add(Ui.Text(Loc.T("{0} is still running an older version of the extension. Open its extensions page and click “Reload” (the round arrow) on the MC Download Manager card.", b.Name), "caption").Resource(TextBlock.ForegroundProperty, "WarnBrush"));
                break;
        }
        if (error != null) body.Children.Add(Ui.Text(error, "caption", "error"));
    }

    private void RenderFirefox(BrowserKind b)
    {
        if (step > 0)
        {
            body.Children.Add(Ui.Text(Loc.T("{0} is ready", b.Name), "section"));
            body.Children.Add(Ui.Text(Loc.T("Pin MC Download Manager to the toolbar (puzzle icon ▸ pin). Its popup should show “Connected”. Downloads of the captured file types are now handed to the app.")));
            body.Children.Add(Ui.StatusRow(Connected, Loc.T("Extension has connected to the app"), Loc.T("Waiting for the extension to connect (it connects right after loading)")));
            if (Outdated)
                body.Children.Add(Ui.Text(Loc.T("Firefox is still running an older version of the add-on. Go back one step and add it again."), "caption").Resource(TextBlock.ForegroundProperty, "WarnBrush"));
            return;
        }
        if (BrowserIntegration.FirefoxAddon is not { } xpi)
        {
            body.Children.Add(Ui.Text(Loc.T("Firefox add-on not included"), "section"));
            body.Children.Add(Ui.Text(Loc.T("This copy of the app has no FirefoxExtension.xpi next to MCDownloadManager.exe. Download the app again and keep the files of its folder together.")));
            return;
        }
        body.Children.Add(Ui.Text(Loc.T("Add the extension to Firefox"), "section"));
        body.Children.Add(Ui.Text(Loc.T("The Firefox add-on is included in the app and signed by Mozilla. Click the button below; Firefox opens and asks “Add MC Download Manager?”. Click “Add”.")));
        body.Children.Add(Ui.Row(Ui.Button(Loc.T("Add to Firefox"), () =>
        {
            error = BrowserIntegration.InstallFirefoxAddon(b) ? null : Loc.T("Firefox could not be started. Drag the file below into a Firefox window instead.");
            Render();
        }, "IconPuzzlePiece", accent: !Connected)));
        body.Children.Add(Ui.Text(Loc.T("If Firefox opens without asking, drag this file into its window:"), "caption"));
        body.Children.Add(Ui.PathBox(xpi));
        body.Children.Add(Ui.StatusRow(Connected, Loc.T("Extension has connected to the app"), Loc.T("Waiting for the extension to connect (it connects right after loading)")));
    }
}
