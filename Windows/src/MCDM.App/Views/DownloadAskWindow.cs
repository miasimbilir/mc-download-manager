using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MCDM.App.Views;

/// "Download this file?" — shown when the browser hands over a file: its name, its size and where it
/// will be saved, each of which can be changed before the download starts (or put off, or dropped).
public sealed class DownloadAskWindow : Window
{
    private static int open;

    private readonly App app = null!;
    private readonly DownloadItem item = null!;
    private readonly TextBox name = new();
    private readonly TextBlock size = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock clash = new() { Classes = { "caption" }, IsVisible = false };
    private readonly CheckBox dontAsk = new() { Content = Loc.T("Don’t ask again; start downloads right away") };
    private readonly PathIcon icon = new() { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
    private readonly Grid folderRow;
    private string? folder;             // set once the user has picked one
    private string filled = "";         // the name last put into the box by the window itself
    private bool nameEdited;
    private DownloadAnswer? result;

    internal DownloadAskWindow(App app, DownloadItem item)
    {
        this.app = app;
        this.item = item;
        Title = AppInfo.Name;
        Icon = Icons.AppIcon;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        icon.Resource(TemplatedControl.ForegroundProperty, "AccentBrush");
        var head = new StackPanel { Spacing = 3 };
        head.Children.Add(new TextBlock { Text = Loc.T("Download this file?"), FontSize = 16, FontWeight = FontWeight.SemiBold });
        var from = new TextBlock { Text = item.Url, Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(from, item.Url);
        head.Children.Add(from);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        top.Children.Add(icon);
        Grid.SetColumn(head, 1);
        top.Children.Add(head);

        folderRow = Ui.FolderRow(this, () => Folder, picked => { folder = picked; RefreshClash(); });
        clash.Resource(TextBlock.ForegroundProperty, "WarnBrush");

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 10 };
        void Row(int row, string label, Control value)
        {
            var key = new TextBlock { Text = label, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            Grid.SetRow(key, row);
            form.Children.Add(key);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            form.Children.Add(value);
        }
        Row(0, Loc.T("File name"), name);
        Row(1, Loc.T("Size"), size);
        Row(2, Loc.T("Save to"), folderRow);
        Grid.SetRow(clash, 3);
        Grid.SetColumn(clash, 1);
        form.Children.Add(clash);

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(20, 18, 20, 14) };
        body.Children.Add(top);
        body.Children.Add(form);
        var ask = new StackPanel { Spacing = 0 };
        ask.Children.Add(dontAsk);
        ask.Children.Add(Ui.Text(Loc.T("The question can be turned back on in Settings ▸ General."), "caption"));
        body.Children.Add(ask);

        var download = Ui.Button(Loc.T("Download"), () => Finish(DownloadDecision.Start), accent: true);
        download.IsDefault = true;
        var later = Ui.Button(Loc.T("Download Later"), () => Finish(DownloadDecision.Later));
        var cancel = Ui.Button(Loc.T("Cancel"), () => Finish(DownloadDecision.Cancel));
        cancel.IsCancel = true;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        footer.Children.Add(cancel);
        Grid.SetColumn(later, 2);
        footer.Children.Add(later);
        Grid.SetColumn(download, 3);
        download.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(download);

        var root = new DockPanel();
        var bottom = Ui.Footer(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        // TextChanged is raised a moment after the text was set, also for text the window put there
        // itself: only a text that differs from that counts as the user's own.
        name.TextChanged += (_, _) =>
        {
            if (name.Text != filled) nameEdited = true;
            RefreshFolder();
        };
        item.PropertyChanged += OnItemChanged;
        app.Store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            item.PropertyChanged -= OnItemChanged;
            app.Store.Changed -= OnStoreChanged;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(DownloadDecision.Cancel); };
        Refresh();
    }

    private string CurrentName => string.IsNullOrWhiteSpace(name.Text) ? item.FileName : FileNames.Sanitize(name.Text);

    /// The folder the user picked, or the usual one for a file of this name.
    private string Folder => folder ?? app.Store.DestinationDirectory(item, CurrentName);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// Paused, started or removed from the list while the question was open: it has been answered elsewhere.
    private void OnStoreChanged()
    {
        if (!item.AwaitingConfirmation || app.Store.Find(item.Id) == null) Close();
    }

    private void Refresh()
    {
        if (!nameEdited && name.Text != item.FileName)
        {
            filled = item.FileName;
            name.Text = item.FileName;
        }
        size.Text = item.TotalBytes > 0 ? Fmt.Size(item.TotalBytes) : item.Probed ? Loc.T("Unknown") : Loc.T("Asking the server…");
        RefreshFolder();
    }

    private void RefreshFolder()
    {
        icon.Data = Icons.Get(Icons.KeyFor(FileCategories.Detect(CurrentName, item.Kind)));
        Ui.Refresh(folderRow);
        RefreshClash();
    }

    /// Nothing is ever overwritten; say under which name the file will end up when the name is taken.
    private void RefreshClash()
    {
        var target = Path.Combine(Folder, CurrentName);
        var taken = false;
        try { taken = FileNames.Exists(target); } catch { }
        clash.IsVisible = taken;
        if (taken) clash.Text = Loc.T("A file with this name is already there. This one will be saved as “{0}”.", Path.GetFileName(FileNames.UniqueDestination(target)));
    }

    private void Finish(DownloadDecision decision)
    {
        result = new DownloadAnswer(decision, folder, nameEdited && !string.IsNullOrWhiteSpace(name.Text) ? name.Text.Trim() : null, dontAsk.IsChecked == true && decision != DownloadDecision.Cancel);
        Close();
    }

    /// Closing the window without a button counts as "Cancel".
    public static async Task<DownloadAnswer> Ask(App app, DownloadItem item)
    {
        var w = new DownloadAskWindow(app, item);
        var closed = new TaskCompletionSource();
        w.Closed += (_, _) => { open = Math.Max(0, open - 1); closed.TrySetResult(); };
        // Several files handed over at once: fan the questions out instead of stacking them exactly.
        if (open > 0)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            var area = (app.MainWindow.Screens.Primary ?? app.MainWindow.Screens.All.FirstOrDefault())?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);
            var step = 30 * (open % 8);
            w.Position = new PixelPoint(area.X + area.Width / 2 - 320 + step, area.Y + area.Height / 3 - 120 + step);
        }
        open++;
        // The request comes from the browser, so the question has to come to the front on its own.
        w.Topmost = true;
        w.Show();
        w.Activate();
        w.name.Focus();
        await closed.Task;
        return w.result ?? new DownloadAnswer(DownloadDecision.Cancel);
    }
}
