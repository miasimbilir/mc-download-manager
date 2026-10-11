using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MCDM.App.Views;

/// Category checkboxes + custom extensions. Used by the first-launch dialog and the Settings tab.
public sealed class CaptureTypesEditor : StackPanel
{
    public CaptureTypesEditor(AppSettings settings)
    {
        Spacing = 4;
        foreach (var g in CaptureTypes.Groups)
        {
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = g.Category.Label() });
            text.Children.Add(new TextBlock { Text = string.Join(", ", g.Extensions), Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.NoWrap });
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new PathIcon { Data = Icons.Get(Icons.KeyFor(g.Category)), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(text);
            var box = new CheckBox { Content = content, IsChecked = settings.CaptureGroupIds.Contains(g.Id), MinHeight = 42 };
            box.IsCheckedChanged += (_, _) => settings.SetCaptureGroup(g.Id, box.IsChecked == true);
            Children.Add(box);
        }
        var custom = new TextBox { Text = settings.CustomExtensions, Watermark = Loc.T("e.g. torrent, srt, ttf") };
        custom.TextChanged += (_, _) => settings.CustomExtensions = custom.Text ?? "";
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = Loc.T("Other extensions"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        Grid.SetColumn(custom, 1);
        row.Children.Add(custom);
        Children.Add(row);
    }
}
