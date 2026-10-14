using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MCDM.App.Views;

/// "Version X is available": what is new, and one button that brings it — the installer is downloaded,
/// checked and run for an installed copy; a copy unpacked from the zip is taken to the download page.
public sealed class UpdateWindow : Window
{
    private readonly App app = null!;
    private readonly UpdateInfo update = null!;
    private readonly bool canInstall;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Height = 4, IsVisible = false };
    private readonly TextBlock status = new() { Classes = { "caption" }, IsVisible = false };
    private readonly Button install, later, skip;
    private readonly CancellationTokenSource cts = new();
    private bool busy;

    internal UpdateWindow(App app, UpdateInfo update, bool canInstall)
    {
        this.app = app;
        this.update = update;
        this.canInstall = canInstall;
        Title = AppInfo.Name;
        Icon = Icons.AppIcon;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock { Text = Loc.T("{0} {1} is available", AppInfo.Name, update.Version), FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Text(Loc.T("You have version {0}.", AppInfo.Version), "secondary"));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var logo = Ui.Logo(44);
        logo.VerticalAlignment = VerticalAlignment.Top;
        logo.Margin = new Thickness(0, 2, 14, 0);
        top.Children.Add(logo);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20, 20, 20, 14) };
        body.Children.Add(top);
        if (update.Notes.Length > 0)
        {
            var notes = new SelectableTextBlock { Text = Plain(update.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            body.Children.Add(new Border
            {
                Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Child = new ScrollViewer { MaxHeight = 220, Content = notes },
            }.Resource(Border.BackgroundProperty, "CardBrush").Resource(Border.BorderBrushProperty, "LineBrush"));
        }
        body.Children.Add(progress);
        body.Children.Add(status);
        if (!canInstall)
            body.Children.Add(Ui.Text(Loc.T("The download page opens in your browser. Get the Setup file there and run it; your settings and list are kept."), "caption"));

        install = Ui.Button(canInstall ? Loc.T("Update Now") : Loc.T("Open Download Page"), Install, accent: true);
        install.IsDefault = true;
        later = Ui.Button(Loc.T("Later"), Close);
        later.IsCancel = true;
        skip = Ui.Button(Loc.T("Skip This Version"), () => { app.Settings.SkippedVersion = update.Version; Close(); });
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        footer.Children.Add(skip);
        Grid.SetColumn(later, 2);
        footer.Children.Add(later);
        Grid.SetColumn(install, 3);
        install.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(install);

        var root = new DockPanel();
        var bottom = Ui.Footer(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) Close(); };
        Closed += (_, _) => { try { cts.Cancel(); } catch { } };
    }

    /// Release notes are written in Markdown; shown as text, they read better without its marks.
    internal static string Plain(string notes)
    {
        var lines = notes.Split('\n').Select(l => l.TrimEnd().TrimStart('#').Replace("**", "").Replace("`", ""))
            .Select(l => l.StartsWith(' ') && !l.StartsWith("  ") ? l[1..] : l)
            .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "• " + l[2..] : l);
        var text = string.Join("\n", lines).Trim();
        return text.Length > 4000 ? text[..4000] + "…" : text;
    }

    private void Say(string text, bool error = false)
    {
        status.Text = text;
        status.IsVisible = true;
        status.Classes.Set("error", error);
    }

    private async void Install()
    {
        if (busy) return;
        if (!canInstall)
        {
            Shell.OpenUrl(update.PageUrl);
            Close();
            return;
        }
        busy = true;
        install.IsEnabled = skip.IsEnabled = false;
        later.Content = Loc.T("Cancel");
        progress.IsVisible = true;
        Say(Loc.T("Downloading the update…"));
        try
        {
            var file = await Updater.DownloadInstallerAsync(update, p => Dispatcher.UIThread.Post(() => progress.Value = p), cts.Token);
            Say(Loc.T("Installing… The app closes and comes back by itself."));
            progress.IsIndeterminate = true;
            later.IsEnabled = false;
            // The installer asks this app to quit, replaces the files and starts the new version.
            if (!Updater.RunInstaller(file)) throw new DownloadFailure(Loc.T("The installer could not be started."));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            busy = false;
            progress.IsVisible = false;
            progress.IsIndeterminate = false;
            install.IsEnabled = skip.IsEnabled = later.IsEnabled = true;
            later.Content = Loc.T("Later");
            Say(Loc.T("The update could not be installed: {0}", e.Message), error: true);
        }
    }

    internal static UpdateWindow Create(App app, UpdateInfo update, bool canInstall) => new(app, update, canInstall);

    private static UpdateWindow? current;

    public static void Show(App app, UpdateInfo update)
    {
        if (current != null) { current.Present(); return; }
        current = new UpdateWindow(app, update, update.CanInstall && Updater.InstalledByInstaller);
        current.Closed += (_, _) => current = null;
        current.Present();
    }
}
