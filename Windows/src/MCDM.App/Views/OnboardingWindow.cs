using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MCDM.App.Views;

/// First-launch flow: 1) file types  2) browser extensions (opens the guided wizard) and Windows startup.
public sealed class OnboardingWindow : Window
{
    private readonly App app;
    private int step;
    private bool finished;
    private readonly TextBlock subtitle = new() { Classes = { "secondary" } };
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
    private readonly CheckBox startup = new() { Content = Loc.T("Start MC Download Manager when I sign in to Windows (recommended)"), IsChecked = true };

    public OnboardingWindow(App app)
    {
        this.app = app;
        Title = AppInfo.Name;
        Icon = Icons.AppIcon;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = Loc.T("Welcome to MC Download Manager"), Classes = { "title" } });
        heading.Children.Add(subtitle);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var logo = Ui.Logo(56);
        logo.Margin = new Thickness(0, 0, 14, 0);
        header.Children.Add(logo);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);

        var top = new StackPanel { Spacing = 16, Margin = new Thickness(22, 22, 22, 18) };
        top.Children.Add(header);
        top.Children.Add(body);

        var root = new DockPanel();
        var bottom = Ui.Footer(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(top);
        Content = root;

        startup.IsVisible = Shell.StartupSupported;
        // Closing the dialog with the title bar button counts as "Not Now".
        Closed += (_, _) => Finish(openWizard: false);
        Render();
    }

    private void Render()
    {
        body.Children.Clear();
        footer.Children.Clear();
        if (step == 0)
        {
            subtitle.Text = Loc.T("Which file types should be captured?");
            body.Children.Add(Ui.Text(Loc.T("Browser downloads and copied links with these extensions are handed to the app. Everything else stays in the browser. You can change this later in Settings ▸ File Types."), "caption"));
            body.Children.Add(new CaptureTypesEditor(app.Settings));
            var clip = new CheckBox { Content = Loc.T("Watch the clipboard for links and ask to download them"), IsChecked = app.Settings.WatchClipboard };
            clip.IsCheckedChanged += (_, _) => app.Settings.WatchClipboard = clip.IsChecked == true;
            body.Children.Add(clip);
            var cont = Ui.Button(Loc.T("Continue"), () => { step = 1; Render(); }, accent: true);
            cont.IsDefault = true;
            Grid.SetColumn(cont, 3);
            footer.Children.Add(cont);
        }
        else
        {
            subtitle.Text = Loc.T("Install the browser extensions?");
            body.Children.Add(Ui.Text(Loc.T("The extensions let your browsers hand their downloads to MC Download Manager and capture videos on pages. A guided setup walks you through each browser, one at a time. You can run it later from Settings ▸ Browser Extensions."), "caption"));
            var list = new StackPanel { Spacing = 8 };
            var installed = BrowserIntegration.InstalledBrowsers;
            foreach (var b in installed)
                list.Children.Add(Ui.Row(new PathIcon { Data = Icons.Get("IconGlobe") }, new TextBlock { Text = b.Name }));
            if (installed.Count == 0)
                list.Children.Add(Ui.Text(Loc.T("No supported browser was found (Google Chrome, Microsoft Edge, Brave, Opera, Firefox)."), "caption"));
            body.Children.Add(list);
            body.Children.Add(startup);
            body.Children.Add(Ui.Text(Loc.T("The app must be running for the browser extension to capture downloads. It keeps running in the notification area even when the window is closed."), "caption"));

            footer.Children.Add(Ui.Button(Loc.T("Back"), () => { step = 0; Render(); }));
            var later = Ui.Button(Loc.T("Not Now"), () => { Finish(openWizard: false); Close(); });
            later.IsCancel = true;
            Grid.SetColumn(later, 2);
            footer.Children.Add(later);
            var start = Ui.Button(Loc.T("Start Setup"), () => { Finish(openWizard: true); Close(); }, accent: true);
            start.IsDefault = true;
            start.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(start, 3);
            footer.Children.Add(start);
        }
    }

    private void Finish(bool openWizard)
    {
        if (finished) return;
        finished = true;
        app.Settings.OnboardingDone = true;
        app.Store.PrepareDownloadFolder();
        Shell.EnsureStartMenuShortcut();
        // Reaching the second page shows the checkbox; quitting on the first page leaves startup alone.
        if (step == 1 && Shell.StartupSupported)
        {
            try { Shell.SetStartup(startup.IsChecked == true); } catch { }
        }
        if (openWizard) Avalonia.Threading.Dispatcher.UIThread.Post(app.ShowWizard, Avalonia.Threading.DispatcherPriority.Background);
    }
}
