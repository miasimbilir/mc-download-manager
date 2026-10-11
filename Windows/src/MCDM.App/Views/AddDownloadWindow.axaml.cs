using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Input;

namespace MCDM.App.Views;

public partial class AddDownloadWindow : Window
{
    private readonly App app = null!;

    public AddDownloadWindow() => InitializeComponent();      // designer

    public AddDownloadWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        Icon = Icons.AppIcon;

        void Update()
        {
            var n = (int)Segments.Value;
            SegmentsLabel.Text = n == 0 ? Loc.T("Connections: default ({0})", app.Settings.SegmentCount) : Loc.T("Connections: {0}", n);
            SegmentsRow.IsVisible = AsVideo.IsChecked != true;
            DownloadButton.IsEnabled = !string.IsNullOrWhiteSpace(Urls.Text);
        }
        Segments.ValueChanged += (_, _) => Update();
        AsVideo.IsCheckedChanged += (_, _) => Update();
        Urls.TextChanged += (_, _) => Update();
        Paste.Click += async (_, _) =>
        {
            if (await ClipboardText() is { Length: > 0 } s) Urls.Text = s.Trim();
        };
        CancelButton.Click += (_, _) => Close();
        DownloadButton.Click += (_, _) => Submit();
        // Enter adds a line in the URL box; Ctrl+Enter starts the downloads.
        Urls.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; Submit(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            Urls.Focus();
            // A single link on the clipboard is most likely what the user is about to add.
            if (await ClipboardText() is { } s && Links.UrlOnlyContent(s) is { Count: 1 } one && string.IsNullOrEmpty(Urls.Text))
            {
                Urls.Text = one[0].AbsoluteUri;
                Urls.SelectAll();
            }
        };
        Update();
    }

    private async Task<string?> ClipboardText()
    {
        try { return Clipboard is { } c ? await c.TryGetTextAsync() : null; }
        catch { return null; }
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(Urls.Text)) return;
        var n = (int)Segments.Value;
        var video = AsVideo.IsChecked == true;
        app.Store.AddUrls(Urls.Text, video ? DownloadKind.video : DownloadKind.file, n == 0 || video ? null : n);
        Close();
    }
}
