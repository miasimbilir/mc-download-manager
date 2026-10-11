using System.ComponentModel;
using Avalonia.Controls;

namespace MCDM.App.Views;

public sealed record ConnectionRow(int Index, string Received, string Start, string Info);

/// Per-download window (IDM style): status, segment bar and the connection table.
/// Turns into a "completed" view when the download finishes.
public partial class DetailWindow : Window
{
    private readonly App app = null!;
    private readonly DownloadItem item = null!;
    private bool showDetails = true;
    private string lastLog = "";

    public DetailWindow() => InitializeComponent();      // designer

    public DetailWindow(App app, DownloadItem item)
    {
        this.app = app;
        this.item = item;
        InitializeComponent();
        Icon = Icons.AppIcon;

        CloseWhenDone.IsChecked = app.Settings.AutoCloseDetail;
        CloseWhenDone.IsCheckedChanged += (_, _) => app.Settings.AutoCloseDetail = CloseWhenDone.IsChecked == true;
        ToggleDetails.Click += (_, _) => { showDetails = !showDetails; Refresh(); };
        PrimaryButton.Click += (_, _) =>
        {
            if (item.State is DownloadState.downloading or DownloadState.probing or DownloadState.queued) app.Store.Pause(item.Id);
            else app.Store.Resume(item.Id);
        };
        SecondaryButton.Click += (_, _) =>
        {
            if (item.State is DownloadState.downloading or DownloadState.probing or DownloadState.queued) app.Store.Cancel(item.Id);
            else { app.Store.Remove(item.Id); Close(); }
        };
        DoneClose.Click += (_, _) => Close();
        DoneReveal.Click += (_, _) => app.Store.Reveal(item);
        DoneOpen.Click += (_, _) => { app.Store.Open(item); Close(); };

        item.PropertyChanged += OnItemChanged;
        app.Store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            item.PropertyChanged -= OnItemChanged;
            app.Store.Changed -= OnStoreChanged;
        };
        Refresh();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnStoreChanged()
    {
        if (app.Store.Find(item.Id) != null) return;
        ActiveView.IsVisible = false;
        CompletedView.IsVisible = false;
        RemovedText.IsVisible = true;
        Title = item.FileName;
    }

    private void Refresh()
    {
        var done = item.State == DownloadState.completed;
        // "28% · file.zip" also shows in the taskbar.
        Title = item.TotalBytes > 0 && !done ? $"{item.Percent}% · {item.FileName}" : item.FileName;
        ActiveView.IsVisible = !done;
        CompletedView.IsVisible = done;
        if (done) { RefreshCompleted(); return; }

        UrlText.Text = item.Url;
        ToolTip.SetTip(UrlText, item.Url);
        StatusText.Text = item.State switch
        {
            DownloadState.downloading when item.Kind == DownloadKind.video => item.Stage.Length > 0 ? item.Stage : Loc.T("Downloading with yt-dlp…"),
            DownloadState.downloading => Loc.T("Receiving data…"),
            DownloadState.failed => item.ErrorMessage ?? Loc.T("Error"),
            _ => item.State.Label(),
        };
        StatusText.Classes.Set("error", item.State == DownloadState.failed);
        ToolTip.SetTip(StatusText, item.State == DownloadState.failed ? item.ErrorMessage : null);
        SizeText.Text = item.TotalBytes > 0 ? Fmt.Size(item.TotalBytes) : Loc.T("Unknown");
        DownloadedText.Text = item.TotalBytes > 0 ? $"{Fmt.Size(item.ReceivedBytes)}  ({item.Percent}%)" : Fmt.Size(item.ReceivedBytes);
        RateText.Text = item.State == DownloadState.downloading ? Fmt.Speed(item.Speed) : "—";
        EtaText.Text = item.State == DownloadState.downloading && item.Eta is { } eta ? Fmt.Duration(eta) : "—";
        ResumeText.Text = item.Kind == DownloadKind.video ? "yt-dlp"
            : item.SupportsRanges ? Loc.T("Yes")
            : item.State is DownloadState.queued or DownloadState.probing ? "?" : Loc.T("No");
        var save = item.FinalPath ?? app.Store.DestinationPath(item);
        SaveText.Text = save;
        ToolTip.SetTip(SaveText, save);

        Bar.Fallback = item.Progress;
        Bar.Paused = item.State == DownloadState.paused;
        Bar.Failed = item.State == DownloadState.failed;

        ToggleDetails.Content = showDetails ? Loc.T("<< Hide Details") : Loc.T("Show Details >>");
        var running = item.State is DownloadState.downloading or DownloadState.probing or DownloadState.queued;
        PrimaryButton.Content = running ? Loc.T("Pause") : Loc.T("Resume");
        PrimaryButton.Classes.Set("accent", !running);
        SecondaryButton.Content = running ? Loc.T("Cancel") : Loc.T("Remove from List");

        DetailsPanel.IsVisible = showDetails;
        if (!showDetails) return;
        var video = item.Kind == DownloadKind.video;
        ConnView.IsVisible = !video;
        LogView.IsVisible = video;
        if (video)
        {
            var log = item.Log.Length == 0 ? Loc.T("Waiting…") : item.Log;
            if (log != lastLog)
            {
                lastLog = log;
                LogText.Text = log.TrimEnd('\n');
                LogScroll.ScrollToEnd();
            }
        }
        else
        {
            ConnBar.Segments = item.Segments;
            ConnBar.Total = item.TotalBytes;
            ConnBar.Fallback = item.Progress;
            ConnBar.Paused = item.State == DownloadState.paused;
            ConnBar.Failed = item.State == DownloadState.failed;
            Connections.ItemsSource = ConnectionRows();
        }
    }

    private List<ConnectionRow> ConnectionRows()
    {
        var rows = new List<ConnectionRow>();
        if (item.Segments.Count == 0)
        {
            if (item.State == DownloadState.queued) return rows;
            var info = item.State switch
            {
                DownloadState.probing => Loc.T("Sending GET…"),
                DownloadState.downloading => Loc.T("Receiving data… (single stream)"),
                DownloadState.paused => Loc.T("Paused"),
                _ => item.State.Label(),
            };
            rows.Add(new ConnectionRow(1, Fmt.Size(item.ReceivedBytes), Fmt.Size(0), info));
            return rows;
        }
        for (int i = 0; i < item.Segments.Count; i++)
        {
            var s = item.Segments[i];
            var info = s.IsDone ? Loc.T("Completed")
                : item.State == DownloadState.downloading ? (s.Received == 0 ? Loc.T("Sending GET…") : Loc.T("Receiving data…"))
                : item.State == DownloadState.paused ? Loc.T("Paused")
                : item.State.Label();
            rows.Add(new ConnectionRow(i + 1, Fmt.Size(s.Received), Fmt.Size(s.Start), info));
        }
        return rows;
    }

    private void RefreshCompleted()
    {
        DoneName.Text = item.FileName;
        DoneSize.Text = Fmt.Size(item.TotalBytes);
        var location = item.FinalPath != null ? Path.GetDirectoryName(item.FinalPath) ?? "" : app.Store.DestinationDirectory(item);
        DoneLocation.Text = location;
        ToolTip.SetTip(DoneLocation, location);
        DoneUrl.Text = item.Url;
        ToolTip.SetTip(DoneUrl, item.Url);
        DoneAt.Text = item.CompletedAt is { } d ? Fmt.DateTimeShort(d) : "";
    }
}
