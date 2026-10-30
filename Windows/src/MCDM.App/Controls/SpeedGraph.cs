using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MCDM.App.Controls;

/// A download's speed from its start as a filled curve, on top of the progress bar and as wide as it: across the
/// width goes how far the download has got (or the time, when the size is unknown). Gridlines carry the scale.
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<IBrush?> GridProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Grid));
    public static readonly StyledProperty<IBrush?> LabelProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(Label));
    public static readonly StyledProperty<bool> PausedProperty = AvaloniaProperty.Register<SpeedGraph, bool>(nameof(Paused));

    static SpeedGraph()
    {
        AffectsRender<SpeedGraph>(StrokeProperty, GridProperty, LabelProperty, PausedProperty);
    }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Grid { get => GetValue(GridProperty); set => SetValue(GridProperty, value); }
    public IBrush? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool Paused { get => GetValue(PausedProperty); set => SetValue(PausedProperty, value); }

    private List<(double X, double Speed)> values = [];

    /// x from 0 to 1, y in bytes per second (SpeedHistory.Points).
    public void SetPoints(List<(double X, double Speed)> points)
    {
        values = points;
        InvalidateVisual();
    }

    private static readonly IBrush pausedBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0x8A, 0x1F));

    /// 1, 2 or 5 times a power of ten (in MB/s when that is at least 1), so the gridlines read as round numbers.
    internal static double NiceTop(double peak)
    {
        if (peak <= 0) return 1024 * 1024;
        var unit = peak >= 1024 * 1024 ? 1024.0 * 1024 : 1024.0;
        var v = peak * 1.15 / unit;
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var top = v <= p ? p : v <= 2 * p ? 2 * p : v <= 5 * p ? 5 * p : 10 * p;
        return top * unit;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var accent = Paused ? pausedBrush : Stroke ?? Brushes.DodgerBlue;
        var grid = new Pen(Grid ?? Brushes.Gray, 1);
        var top = NiceTop(values.Count == 0 ? 0 : values.Max(v => v.Speed));
        const double labelHeight = 14;
        var plotTop = labelHeight;
        var plotHeight = size.Height - plotTop;

        // Gridlines at the top, the middle and the bottom; the top one is labelled with the scale.
        foreach (var f in new[] { 0.0, 0.5 })
        {
            var y = Math.Round(plotTop + plotHeight * f) + 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(size.Width, y));
        }
        var label = new FormattedText(Fmt.Speed(top), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Label ?? Brushes.Gray);
        context.DrawText(label, new Point(0, 0));

        if (values.Count < 2) return;
        var points = values.Select(v => new Point(v.X * size.Width, plotTop + plotHeight * (1 - Math.Min(1, v.Speed / top)))).ToArray();

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(points[0], false);
            a.BeginFigure(new Point(points[0].X, size.Height), true);
            a.LineTo(points[0]);
            for (int i = 1; i < points.Length; i++)
            {
                // Catmull–Rom through the samples, as cubic Béziers: a smooth curve that still passes every point.
                var p0 = points[Math.Max(0, i - 2)];
                var p1 = points[i - 1];
                var p2 = points[i];
                var p3 = points[Math.Min(points.Length - 1, i + 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Clamp(p1.Y + (p2.Y - p0.Y) / 6));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Clamp(p2.Y - (p3.Y - p1.Y) / 6));
                l.CubicBezierTo(c1, c2, p2);
                a.CubicBezierTo(c1, c2, p2);
            }
            a.LineTo(new Point(points[^1].X, size.Height));
            l.EndFigure(false);
            a.EndFigure(true);
        }
        double Clamp(double y) => Math.Clamp(y, plotTop, size.Height);

        var fill = accent is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.25) : accent;
        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(accent, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        context.DrawEllipse(accent, null, points[^1], 3.5, 3.5);
    }
}
