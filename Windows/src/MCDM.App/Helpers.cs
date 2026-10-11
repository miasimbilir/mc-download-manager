using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;

namespace MCDM.App;

/// `{l:T 'English text'}` in XAML: the Turkish translation when the UI language is Turkish.
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}

public static class Icons
{
    public static Geometry? Get(string key) =>
        Application.Current != null && Application.Current.TryFindResource(key, out var value) ? value as Geometry : null;

    public static string KeyFor(FileCategory c) => c switch
    {
        FileCategory.image => "IconImage",
        FileCategory.music => "IconMusicNote2",
        FileCategory.video => "IconVideoClip",
        FileCategory.apps => "IconApps",
        FileCategory.document => "IconDocumentText",
        FileCategory.compressed => "IconFolderZip",
        _ => "IconDocument",
    };

    public static WindowIcon AppIcon { get; } = Load();

    private static WindowIcon Load()
    {
        using var s = AssetLoader.Open(new Uri("avares://MCDownloadManager/Assets/app.ico"));
        return new WindowIcon(s);
    }
}

public sealed class CategoryIconConverter : IValueConverter
{
    public static readonly CategoryIconConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FileCategory c ? Icons.Get(Icons.KeyFor(c)) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public static class WindowExtensions
{
    /// Shows a window and brings it in front of other apps (a download handed over by the browser
    /// arrives while the browser has the focus).
    public static void Present(this Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        if (!w.Topmost)
        {
            w.Topmost = true;
            w.Topmost = false;
        }
    }
}
