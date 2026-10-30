using Xunit;

namespace MCDM.Tests;

public class SpeedHistoryTests
{
    [Fact]
    public void With_a_known_size_the_curve_follows_how_far_the_download_got()
    {
        var h = new SpeedHistory();
        h.Add(10, 0.0);
        h.Add(20, 0.10);
        h.Add(30, 0.10);        // same spot: averaged
        h.Add(40, 0.42);
        var points = h.Points(0.43);
        Assert.Equal(3, points.Count);
        Assert.Equal(25, points[1].Speed);
        Assert.Equal(0.43, points[^1].X, 3);    // the curve ends where the bar does
        Assert.All(points, p => Assert.InRange(p.X, 0, 1));
        Assert.Equal(40, h.Now);
        Assert.Equal(40, h.Peak);
        Assert.Equal(25, h.Average);
    }

    [Fact]
    public void Without_a_size_the_whole_download_is_squeezed_into_the_width()
    {
        var h = new SpeedHistory();
        for (int i = 0; i < 1000; i++) h.Add(i, -1);
        var points = h.Points(-1);
        Assert.InRange(points.Count, 2, SpeedHistory.Buckets);
        Assert.Equal(0, points[0].X);
        Assert.Equal(1, points[^1].X);
        Assert.True(points[0].Speed < points[^1].Speed);
        Assert.Equal(999, h.Peak);
        Assert.Equal(499.5, h.Average);
    }

    [Fact]
    public void Clear_starts_over()
    {
        var h = new SpeedHistory();
        h.Add(5, 0.5);
        var v = h.Version;
        h.Clear();
        Assert.NotEqual(v, h.Version);
        Assert.Equal(0, h.Count);
        Assert.Empty(h.Points(0.5));
        Assert.Equal(0, h.Peak);
    }
}
