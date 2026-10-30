using Xunit;

namespace MCDM.Tests;

public class HostLimitTests
{
    private static readonly DateTime FixedNow = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unknown_host_returns_null_and_remembered_host_is_found_by_url()
    {
        var settings = new AppSettings();

        // unknown host -> null
        Assert.Null(settings.ConnectionLimit("example.com", FixedNow));

        // remember 4 -> 4
        settings.RememberConnections("example.com", 4, FixedNow);
        Assert.Equal(4, settings.ConnectionLimit("example.com", FixedNow));

        // same host via "https://www.EXAMPLE.com/file.zip" -> 4
        Assert.Equal(4, settings.ConnectionLimit("https://www.EXAMPLE.com/file.zip", FixedNow));
    }

    [Fact]
    public void Remembering_keeps_lower_count_and_replaces_higher()
    {
        var settings = new AppSettings();

        settings.RememberConnections("example.com", 4, FixedNow);
        Assert.Equal(4, settings.ConnectionLimit("example.com", FixedNow));

        // remembering 6 after 4 keeps 4
        settings.RememberConnections("example.com", 6, FixedNow);
        Assert.Equal(4, settings.ConnectionLimit("example.com", FixedNow));

        // remembering 2 after 4 gives 2
        settings.RememberConnections("example.com", 2, FixedNow);
        Assert.Equal(2, settings.ConnectionLimit("example.com", FixedNow));
    }

    [Fact]
    public void Expiry_drops_old_limits_and_higher_number_can_be_stored_after_expiry()
    {
        var settings = new AppSettings();

        settings.RememberConnections("example.com", 4, FixedNow);
        Assert.Equal(4, settings.ConnectionLimit("example.com", FixedNow));

        // expiry: at now + 7 days + 1 second -> null
        var expiredTime = FixedNow + TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        Assert.Null(settings.ConnectionLimit("example.com", expiredTime));

        // remembering again after expiry with a higher number gives the new number
        settings.RememberConnections("example.com", 6, expiredTime);
        Assert.Equal(6, settings.ConnectionLimit("example.com", expiredTime));
    }

    [Fact]
    public void Clamping_constrains_connections_to_range_1_to_16()
    {
        var settings = new AppSettings();

        // 0 -> 1
        settings.RememberConnections("zero.com", 0, FixedNow);
        Assert.Equal(1, settings.ConnectionLimit("zero.com", FixedNow));

        // 99 -> 16
        settings.RememberConnections("max.com", 99, FixedNow);
        Assert.Equal(16, settings.ConnectionLimit("max.com", FixedNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://x.com")]
    [InlineData("a b")]
    public void Invalid_host_stores_nothing(string? invalidHost)
    {
        var settings = new AppSettings();

        settings.RememberConnections("valid.com", 4, FixedNow);
        var countBefore = settings.HostLimits.Count;

        settings.RememberConnections(invalidHost, 8, FixedNow);
        Assert.Equal(countBefore, settings.HostLimits.Count);
        Assert.Null(settings.ConnectionLimit(invalidHost, FixedNow));
    }

    [Fact]
    public void HostLimits_is_new_dictionary_instance_after_RememberConnections()
    {
        var settings = new AppSettings();

        var before = settings.HostLimits;
        settings.RememberConnections("example.com", 4, FixedNow);
        var after = settings.HostLimits;

        Assert.NotSame(before, after);
    }

    [Fact]
    public void More_than_MaxHostLimits_drops_oldest_until()
    {
        var settings = new AppSettings();

        // Host with earliest Until
        settings.RememberConnections("oldest.com", 4, FixedNow);

        // Add MaxHostLimits (200) distinct hosts with later Until
        for (int i = 1; i <= AppSettings.MaxHostLimits; i++)
        {
            settings.RememberConnections($"host{i}.com", 4, FixedNow.AddSeconds(i));
        }

        Assert.Equal(AppSettings.MaxHostLimits, settings.HostLimits.Count);
        Assert.Null(settings.ConnectionLimit("oldest.com", FixedNow));
        Assert.Equal(4, settings.ConnectionLimit($"host{AppSettings.MaxHostLimits}.com", FixedNow.AddSeconds(AppSettings.MaxHostLimits)));
    }

    [Fact]
    public void Round_trip_persists_host_limits_to_file()
    {
        var tempFile = Path.Combine(Setup.TempDir(), "settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = AppSettings.Load(tempFile);
            settings.RememberConnections("example.com", 6, FixedNow);

            var reloaded = AppSettings.Load(tempFile);
            Assert.Equal(6, reloaded.ConnectionLimit("example.com", FixedNow));
            Assert.Equal(6, reloaded.ConnectionLimit("https://www.EXAMPLE.com/path", FixedNow));
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
