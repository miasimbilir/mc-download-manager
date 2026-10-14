using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MCDM.App.Views;

/// Small builders for the dialogs that are laid out in code.
internal static class Ui
{
    public static T Resource<T>(this T control, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        control[!property] = new DynamicResourceExtension(key);
        return control;
    }

    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    public static Button Button(string text, Action onClick, string? icon = null, bool accent = false)
    {
        var b = new Button { MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (icon == null) b.Content = text;
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(new PathIcon { Data = Icons.Get(icon) });
            row.Children.Add(new TextBlock { Text = text });
            b.Content = row;
        }
        if (accent) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.AddRange(children);
        return p;
    }

    /// Selectable monospace path in a bordered box.
    public static Border PathBox(string path)
    {
        var text = new SelectableTextBlock
        {
            Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
        };
        return new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = text }
            .Resource(Border.BackgroundProperty, "PaneBrush")
            .Resource(Border.BorderBrushProperty, "LineBrush");
    }

    /// Green check + `okText` when `ok`, otherwise a hollow circle + `pendingText`.
    public static StackPanel StatusRow(bool ok, string okText, string pendingText)
    {
        var brush = ok ? "OkBrush" : "SecondaryTextBrush";
        var icon = new PathIcon { Data = Icons.Get(ok ? "IconCheckmarkCircleFilled" : "IconCircle"), Width = 16, Height = 16 }
            .Resource(TemplatedControl.ForegroundProperty, brush);
        var text = new TextBlock { Text = ok ? okText : pendingText, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (!ok) text.Resource(TextBlock.ForegroundProperty, brush);
        return Row(icon, text);
    }

    /// "C:\…\Downloads  [Choose…]": shows `current()` and lets the user pick another folder.
    public static Grid FolderRow(Window owner, Func<string> current, Action<string> picked)
    {
        var path = new TextBlock { TextTrimming = TextTrimming.PathSegmentEllipsis, VerticalAlignment = VerticalAlignment.Center };
        void Show()
        {
            path.Text = current();
            ToolTip.SetTip(path, path.Text);
        }
        var choose = new Button { Content = Loc.T("Choose…"), Margin = new Thickness(10, 0, 0, 0) };
        choose.Click += async (_, _) =>
        {
            try
            {
                IStorageFolder? start = null;
                // The category subfolder is often not there yet; without a folder that exists the picker
                // opens in Documents. Start from the nearest one that does.
                var dir = current();
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                try { if (!string.IsNullOrEmpty(dir)) start = await owner.StorageProvider.TryGetFolderFromPathAsync(dir); } catch { }
                var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = Loc.T("Save to"), AllowMultiple = false, SuggestedStartLocation = start,
                });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } chosen)
                {
                    picked(chosen);
                    Show();
                }
            }
            catch { }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(path);
        Grid.SetColumn(choose, 1);
        row.Children.Add(choose);
        row.Tag = (Action)Show;         // callers refresh the text through Refresh(row)
        Show();
        return row;
    }

    public static void Refresh(Grid folderRow) => (folderRow.Tag as Action)?.Invoke();

    public static Image Logo(double size)
    {
        var img = new Image { Width = size, Height = size };
        try { img.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MCDownloadManager/Assets/app.png"))); } catch { }
        return img;
    }

    public static Border Footer(Control content) =>
        new Border { Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = content }
            .Resource(Border.BorderBrushProperty, "LineBrush")
            .Resource(Border.BackgroundProperty, "PaneBrush");
}
