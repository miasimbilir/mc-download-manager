using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MCDM.App.Views;

/// "Which quality?" — lists the resolutions this video really offers (with the size each would take)
/// plus audio-only, before a video download starts.
public sealed class VideoFormatWindow : Window
{
    private VideoFormatAnswer? result;
    private readonly List<(RadioButton Button, VideoFormatAnswer Answer)> options = [];
    private readonly CheckBox remember = new() { Content = Loc.T("Don’t ask again; always use this quality") };

    internal VideoFormatWindow(AppSettings settings, DownloadItem item, VideoInfo info)
    {
        Title = AppInfo.Name;
        Icon = Icons.AppIcon;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(new TextBlock { Text = Loc.T("Which quality should be downloaded?"), FontSize = 16, FontWeight = FontWeight.SemiBold });
        var name = info.Title.Length > 0 ? info.Title : item.Url;
        var title = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap };
        ToolTip.SetTip(title, name);
        head.Children.Add(title);
        var sub = item.Host ?? "";
        if (info.Duration is > 0) sub += (sub.Length > 0 ? " · " : "") + Fmt.Duration(info.Duration.Value);
        head.Children.Add(new TextBlock { Text = sub, Classes = { "caption" } });

        var list = new StackPanel { Spacing = 0 };
        // Preselect the largest one that does not exceed the usual setting (audio only if that is the setting).
        var target = settings.VideoMaxHeight;
        var preselected = target < 0 ? null : VideoFormatAnswer.Pick(info.Choices, target);
        foreach (var c in info.Choices)
        {
            var size = c.ApproxBytes is > 0 ? "~" + Fmt.Size(c.ApproxBytes.Value) : "";
            Add(list, c.Label, size, new VideoFormatAnswer(c.Height, c.Nominal, false, c.Selector), ReferenceEquals(c, preselected));
        }
        if (info.HasAudio)
        {
            list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6) }.Resource(Border.BackgroundProperty, "LineBrush"));
            var audioSize = info.AudioBytes is > 0 ? "~" + Fmt.Size(info.AudioBytes.Value) : "";
            Add(list, Loc.T("Audio only (MP3)"), audioSize, new VideoFormatAnswer(-2, -2, false), target == -2);
            Add(list, Loc.T("Audio only (M4A)"), audioSize, new VideoFormatAnswer(-1, -1, false), target == -1);
        }
        if (options.Count > 0 && !options.Any(o => o.Button.IsChecked == true)) options[0].Button.IsChecked = true;

        var container = settings.VideoContainer == "original" ? Loc.T("Original (no conversion)") : settings.VideoContainer.ToUpperInvariant();
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20, 18, 20, 14) };
        body.Children.Add(head);
        body.Children.Add(new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = list }
            .Resource(Border.BackgroundProperty, "CardBrush").Resource(Border.BorderBrushProperty, "LineBrush"));
        body.Children.Add(remember);
        body.Children.Add(Ui.Text(Loc.T("Video is saved as {0}. The format and this question can be changed in Settings ▸ Video.", container), "caption"));

        var download = Ui.Button(Loc.T("Download"), Accept, accent: true);
        download.IsDefault = true;
        var cancel = Ui.Button(Loc.T("Cancel"), Close);
        cancel.IsCancel = true;
        var buttons = Ui.Row(cancel, download);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        var root = new DockPanel();
        var footer = Ui.Footer(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Add(StackPanel list, string label, string size, VideoFormatAnswer answer, bool selected)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 340 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var right = new TextBlock { Text = size, Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        var button = new RadioButton { Content = row, GroupName = "quality", IsChecked = selected, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        button.DoubleTapped += (_, _) => Accept();
        options.Add((button, answer));
        list.Children.Add(button);
    }

    private void Accept()
    {
        var chosen = options.FirstOrDefault(o => o.Button.IsChecked == true).Answer;
        if (chosen == null) return;
        result = chosen with { Remember = remember.IsChecked == true };
        Close();
    }

    /// Returns null when the window was closed without a choice.
    public static async Task<VideoFormatAnswer?> Ask(AppSettings settings, DownloadItem item, VideoInfo info)
    {
        var w = new VideoFormatWindow(settings, item, info);
        var closed = new TaskCompletionSource();
        w.Closed += (_, _) => closed.TrySetResult();
        // The request usually comes from the browser, so the question has to come to the front on its own.
        w.Topmost = true;
        w.Show();
        w.Activate();
        await closed.Task;
        return w.result;
    }
}
