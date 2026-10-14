using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MCDM.App.Views;

/// "All downloads have finished — the computer shuts down in 30 seconds": the last chance to say no
/// before the action chosen under "When done" is carried out.
public sealed class CountdownWindow : Window
{
    private readonly QueueEndAction action;
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int left;
    private bool go;

    internal CountdownWindow(QueueEndAction action, int seconds)
    {
        this.action = action;
        left = seconds;
        Title = AppInfo.Name;
        Icon = Icons.AppIcon;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var text = new StackPanel { Spacing = 8 };
        text.Children.Add(new TextBlock { Text = Loc.T("All downloads have finished"), FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(message.Resource(TextBlock.ForegroundProperty, "SecondaryTextBrush"));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(20, 20, 20, 16) };
        var logo = Ui.Logo(44);
        logo.VerticalAlignment = VerticalAlignment.Top;
        logo.Margin = new Thickness(0, 2, 14, 0);
        top.Children.Add(logo);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        // "Cancel" is the default: a stray Enter must not switch the computer off.
        var cancel = Ui.Button(Loc.T("Cancel"), Close, accent: true);
        cancel.IsDefault = true;
        cancel.IsCancel = true;
        var now = Ui.Button(action == QueueEndAction.Shutdown ? Loc.T("Shut Down Now") : Loc.T("Sleep Now"), () => { go = true; Close(); });
        var buttons = Ui.Row(now, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        var root = new DockPanel();
        var footer = Ui.Footer(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(top);
        Content = root;

        timer.Tick += (_, _) =>
        {
            left--;
            if (left <= 0) { go = true; Close(); }
            else Render();
        };
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Render();
    }

    private void Render() => message.Text = action == QueueEndAction.Shutdown
        ? Loc.T("The computer will shut down in {0} seconds.", left)
        : Loc.T("The computer will go to sleep in {0} seconds.", left);

    /// True when the countdown ran out (or "now" was clicked); false when the user called it off.
    public static async Task<bool> Ask(QueueEndAction action, int seconds = 30)
    {
        var w = new CountdownWindow(action, seconds);
        var closed = new TaskCompletionSource();
        w.Closed += (_, _) => closed.TrySetResult();
        w.Topmost = true;
        w.Show();
        w.Activate();
        await closed.Task;
        return w.go;
    }
}
