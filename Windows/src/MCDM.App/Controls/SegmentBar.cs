using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MCDM.App.Controls;

/// Bar showing how much of each segment has been received.
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<SegmentInfo>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentBar, IReadOnlyList<SegmentInfo>?>(nameof(Segments));
    public static readonly StyledProperty<long> TotalProperty = AvaloniaProperty.Register<SegmentBar, long>(nameof(Total));
    public static readonly StyledProperty<double> FallbackProperty = AvaloniaProperty.Register<SegmentBar, double>(nameof(Fallback));
    public static readonly StyledProperty<bool> PausedProperty = AvaloniaProperty.Register<SegmentBar, bool>(nameof(Paused));
    public static readonly StyledProperty<bool> FailedProperty = AvaloniaProperty.Register<SegmentBar, bool>(nameof(Failed));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Track));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Fill));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(SegmentsProperty, TotalProperty, FallbackProperty, PausedProperty, FailedProperty, TrackProperty, FillProperty);
    }

    public IReadOnlyList<SegmentInfo>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public long Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    /// Overall progress (0–1), used when there is no per-segment state.
    public double Fallback { get => GetValue(FallbackProperty); set => SetValue(FallbackProperty, value); }
    public bool Paused { get => GetValue(PausedProperty); set => SetValue(PausedProperty, value); }
    public bool Failed { get => GetValue(FailedProperty); set => SetValue(FailedProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static readonly IBrush pausedBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0x8A, 0x1F));
    private static readonly IBrush failedBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly IBrush dividerBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var rect = new Rect(size);
        var radius = size.Height / 2;
        var track = Track ?? Brushes.Gray;
        var fill = Failed ? failedBrush : Paused ? pausedBrush : Fill ?? Brushes.DodgerBlue;
        context.DrawRectangle(track, null, rect, radius, radius);
        using var clip = context.PushClip(new RoundedRect(rect, radius));
        var segments = Segments;
        var total = Total;
        if (segments == null || segments.Count == 0 || total <= 0)
        {
            var w = size.Width * Math.Clamp(Fallback, 0, 1);
            if (w > 0) context.DrawRectangle(fill, null, new Rect(0, 0, w, size.Height));
            return;
        }
        foreach (var s in segments)
        {
            if (s.Received <= 0) continue;
            var x = (double)s.Start / total * size.Width;
            var w = (double)Math.Min(s.Received, s.Length) / total * size.Width;
            context.DrawRectangle(fill, null, new Rect(x, 0, Math.Max(w, 1), size.Height));
        }
        for (int i = 1; i < segments.Count; i++)
        {
            var x = Math.Round((double)segments[i].Start / total * size.Width);
            context.DrawRectangle(dividerBrush, null, new Rect(x, 0, 1, size.Height));
        }
    }
}
