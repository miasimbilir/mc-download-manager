using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MCDM.App.Views;

/// A small question dialog: title, message, a row of buttons and an optional checkbox.
public sealed class PromptWindow : Window
{
    private int result = -1;
    private readonly CheckBox? check;

    private PromptWindow(string title, string message, string[] buttons, string? checkbox, int cancelIndex)
    {
        Title = AppInfo.Name;
        Icon = Icons.AppIcon;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var logo = new Image { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
        try { logo.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MCDownloadManager/Assets/app-64.png"))); } catch { }

        var text = new StackPanel { Spacing = 8 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 }.Resource(TextBlock.ForegroundProperty, "SecondaryTextBrush"));
        if (checkbox != null)
        {
            check = new CheckBox { Content = checkbox, Margin = new Thickness(0, 4, 0, 0) };
            text.Children.Add(check);
        }

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(20, 20, 20, 16) };
        top.Children.Add(logo);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) { b.Classes.Add("accent"); b.IsDefault = true; }
            if (i == cancelIndex) b.IsCancel = true;
            b.Click += (_, _) => { result = index; Close(); };
            row.Children.Add(b);
        }
        var bottom = new Border
        {
            Padding = new Thickness(20, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row,
        };
        bottom[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush");
        bottom[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("PaneBrush");

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(top);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = cancelIndex; Close(); } };
    }

    internal static PromptWindow Create(string title, string message, string[] buttons, string? checkbox, int cancelIndex) =>
        new(title, message, buttons, checkbox, cancelIndex);

    /// Returns the index of the chosen button (or `cancelIndex` / -1 when the dialog was dismissed) and the checkbox state.
    public static async Task<(int Button, bool Checked)> Ask(Window? owner, string title, string message, string[] buttons,
        string? checkbox = null, int cancelIndex = -1)
    {
        var w = new PromptWindow(title, message, buttons, checkbox, cancelIndex);
        if (owner is { IsVisible: true })
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
            await w.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            w.Closed += (_, _) => closed.TrySetResult();
            w.Topmost = true;
            w.Show();
            w.Activate();
            await closed.Task;
        }
        return (w.result < 0 ? cancelIndex : w.result, w.check?.IsChecked == true);
    }
}
