using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace MCDM.App.Views;

/// "Download completed" card in the corner of the screen. It never takes the focus and closes by itself.
public sealed class ToastWindow : Window
{
    private static ToastWindow? current;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(7) };

    private ToastWindow(App app, string title, string body, DownloadItem? item)
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var logo = new Image { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
        try { logo.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MCDownloadManager/Assets/app-64.png"))); } catch { }

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        // A file name is cut to one line; a sentence (the "still running in the notification area" hint) has to be readable to its end.
        text.Children.Add(item != null
            ? new TextBlock { Text = body, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "secondary" } }
            : new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        if (item != null)
        {
            var open = new Button { Content = Loc.T("Open") };
            open.Click += (_, _) => { app.Store.Open(item); Close(); };
            var reveal = new Button { Content = Loc.T("Show in Folder") };
            reveal.Click += (_, _) => { app.Store.Reveal(item); Close(); };
            buttons.Children.Add(open);
            buttons.Children.Add(reveal);
            text.Children.Add(buttons);
        }

        var close = new Button { Classes = { "tool" }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top, Content = new PathIcon { Data = Icons.Get("IconDismiss"), Width = 10, Height = 10 } };
        close.Click += (_, _) => Close();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(logo);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 8, 12), Child = grid };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("PaneBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("LineBrush");
        Content = card;

        timer.Tick += (_, _) => Close();
        PointerEntered += (_, _) => timer.Stop();
        PointerExited += (_, _) => timer.Start();
        Opened += (_, _) => { Place(); timer.Start(); };
        // On Windows the height the content asked for (SizeToContent) is not in yet when Opened fires;
        // placed with the earlier height, the card ends up halfway up the screen.
        PropertyChanged += (_, e) => { if (e.Property == ClientSizeProperty && IsVisible) Place(); };
        Closed += (_, _) => { timer.Stop(); if (ReferenceEquals(current, this)) current = null; };
    }

    private void Place()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var size = ClientSize;
        var w = (int)Math.Ceiling(size.Width * scale);
        var h = (int)Math.Ceiling(size.Height * scale);
        var margin = (int)(16 * scale);
        Position = new PixelPoint(area.Right - w - margin, area.Bottom - h - margin);
    }

    internal static ToastWindow Create(App app, string title, string body, DownloadItem? item) => new(app, title, body, item);

    public static void Show(App app, string title, string body, DownloadItem? item = null)
    {
        try
        {
            current?.Close();
            current = new ToastWindow(app, title, body, item);
            current.Show();
        }
        catch { }
    }
}
