using System.Globalization;
using Xunit;

namespace MCDM.Tests;

public class SitesTests
{
    [Theory]
    [InlineData("WWW.Example.COM", "example.com")]
    [InlineData("https://www.wetransfer.com/downloads/x?y=1", "wetransfer.com")]
    [InlineData("sub.example.com", "sub.example.com")]
    [InlineData("örnek.com.tr", "xn--rnek-4qa.com.tr")]
    [InlineData("ftp://example.com", null)]
    [InlineData("example.com/path", null)]
    [InlineData("example.com:8080", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("www", "www")]
    public void Key_parses_and_normalises_hosts_and_urls(string? input, string? expected)
    {
        Assert.Equal(expected, Sites.Key(input));
    }

    [Fact]
    public void Key_handles_punycode_and_www_dot()
    {
        Assert.Equal("xn--rnek-4qa.com.tr", Sites.Key("örnek.com.tr"));
        Assert.Equal(Sites.Key("http://örnek.com.tr/"), Sites.Key("örnek.com.tr"));
        Assert.StartsWith("xn--", Sites.Key("örnek.com.tr")!);

        var wwwDot = Sites.Key("www.");
        Assert.True(wwwDot is null or "www");
    }

    [Fact]
    public void Key_respects_turkish_culture_casing()
    {
        // Under tr-TR, "I".ToLower() is "ı". Key must use ToLowerInvariant to keep ASCII "i".
        Assert.Equal("tr-TR", CultureInfo.CurrentCulture.Name);
        Assert.Equal("ı", "I".ToLower());
        Assert.Equal("istanbul.com", Sites.Key("WWW.ISTANBUL.COM"));
        Assert.Equal("istanbul.com", Sites.Key("ISTANBUL.COM"));
        Assert.Equal("istanbul.com", Sites.Key("https://WWW.ISTANBUL.COM/path"));
    }

    [Theory]
    [InlineData("example.com.", "example.com")]
    [InlineData("http://example.com./", "example.com")]
    [InlineData("www.example.com.", "example.com")]
    [InlineData("https://www.example.com./downloads", "example.com")]
    public void Key_strips_single_trailing_dot(string input, string expected)
    {
        Assert.Equal(expected, Sites.Key(input));
    }

    [Fact]
    public void Key_rejects_hosts_longer_than_253_chars()
    {
        // 63 + 1 + 63 + 1 + 60 + 1 + 60 + 1 + 3 = 253 chars
        var host253 = $"{new string('a', 63)}.{new string('b', 63)}.{new string('c', 60)}.{new string('d', 60)}.com";
        Assert.Equal(253, host253.Length);
        Assert.Equal(host253, Sites.Key(host253));

        // 63 + 1 + 63 + 1 + 61 + 1 + 60 + 1 + 3 = 254 chars
        var host254 = $"{new string('a', 63)}.{new string('b', 63)}.{new string('c', 61)}.{new string('d', 60)}.com";
        Assert.Equal(254, host254.Length);
        Assert.Null(Sites.Key(host254));
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("example .com")]
    [InlineData("example.com\\path")]
    [InlineData("example.com?query")]
    [InlineData("example.com#fragment")]
    public void Key_rejects_malformed_bare_hosts(string input)
    {
        Assert.Null(Sites.Key(input));
    }

    [Fact]
    public void Listed_matches_exact_hosts_and_subdomains()
    {
        string[] list = ["example.com"];
        Assert.True(Sites.Listed("example.com", list));
        Assert.True(Sites.Listed("sub.example.com", list));
        Assert.True(Sites.Listed("deep.sub.example.com", list));
        Assert.False(Sites.Listed("notexample.com", list));
        Assert.False(Sites.Listed("example.com.not", list));
    }

    [Fact]
    public void Listed_normalises_www_on_either_side()
    {
        Assert.True(Sites.Listed("www.example.com", ["example.com"]));
        Assert.True(Sites.Listed("example.com", ["www.example.com"]));
        Assert.True(Sites.Listed("www.example.com", ["www.example.com"]));
        Assert.True(Sites.Listed("sub.example.com", ["www.example.com"]));
        Assert.True(Sites.Listed("www.sub.example.com", ["example.com"]));
    }

    [Fact]
    public void Listed_matches_urls_and_skips_invalid_list_items()
    {
        Assert.True(Sites.Listed("https://example.com/downloads/1", ["example.com"]));
        Assert.True(Sites.Listed("http://sub.example.com/path?query=1", ["example.com"]));
        Assert.False(Sites.Listed("https://notexample.com/downloads/1", ["example.com"]));

        // Invalid list items ignored
        Assert.True(Sites.Listed("example.com", ["", "   ", "ftp://bad", "invalid:80", "example.com"]));
        Assert.False(Sites.Listed("other.com", ["", "   ", "ftp://bad", "invalid:80"]));

        // Null or empty inputs
        Assert.False(Sites.Listed("example.com", null));
        Assert.False(Sites.Listed("example.com", []));
        Assert.False(Sites.Listed(null, ["example.com"]));
        Assert.False(Sites.Listed("", ["example.com"]));
    }

    [Fact]
    public void AnyListed_checks_multiple_urls_skipping_nulls()
    {
        string[] list = ["example.com"];
        Assert.True(Sites.AnyListed(list, null, "https://sub.example.com/file", null));
        Assert.False(Sites.AnyListed(list, null, "other.com", null));
        Assert.False(Sites.AnyListed(null, "example.com"));
        Assert.False(Sites.AnyListed(list));
        Assert.False(Sites.AnyListed(list, null, null));
        Assert.True(Sites.AnyListed(list, "https://notexample.com", "ftp://example.com", "https://example.com"));
    }

    [Fact]
    public void With_adds_and_removes_sites_with_ordering_and_deduplication()
    {
        // Add
        Assert.Equal(["example.com"], Sites.With([], "example.com", true));

        // Add duplicate (no change)
        Assert.Equal(["example.com"], Sites.With(["example.com"], "example.com", true));
        Assert.Equal(["example.com"], Sites.With(["example.com"], "WWW.EXAMPLE.COM", true));
        Assert.Equal(["example.com"], Sites.With(["example.com"], "https://www.example.com/x", true));

        // Remove
        Assert.Equal(["other.com"], Sites.With(["example.com", "other.com"], "example.com", false));
        Assert.Empty(Sites.With(["example.com"], "WWW.EXAMPLE.COM", false));

        // Remove missing
        Assert.Equal(["example.com"], Sites.With(["example.com"], "missing.com", false));

        // Sorted output (ordinal)
        Assert.Equal(["a.com", "b.com", "z.com"], Sites.With(["z.com", "b.com"], "a.com", true));

        // Invalid host leaves list unchanged (normalised the same way)
        Assert.Equal(["a.com", "b.com"], Sites.With(["b.com", "a.com"], "invalid:host", true));
        Assert.Equal(["a.com", "b.com"], Sites.With(["b.com", "a.com"], null, false));
        Assert.Equal(["a.com", "b.com"], Sites.With(["b.com", "   ", "a.com"], "", true));

        // Null list
        Assert.Equal(["example.com"], Sites.With(null, "example.com", true));
        Assert.Empty(Sites.With(null, "example.com", false));
        Assert.Empty(Sites.With(null, null, false));
    }

    [Fact]
    public void With_respects_max_entries_cap()
    {
        var items = Enumerable.Range(1, 500).Select(i => $"site{i:D4}.com").ToList();
        var full = Sites.With(items, null, false);
        Assert.Equal(Sites.MaxEntries, full.Count);

        var added = Sites.With(full, "newsite.com", true);
        Assert.Equal(Sites.MaxEntries, added.Count);
        Assert.DoesNotContain("newsite.com", added);

        var removed = Sites.With(added, "site0001.com", false);
        Assert.Equal(499, removed.Count);
        Assert.DoesNotContain("site0001.com", removed);
    }

    [Fact]
    public void HandOffTarget_selects_target_based_on_http_status_and_referer()
    {
        var url = new Uri("https://download.example/file.zip");
        var referer = "https://page.example/download";

        // 403 + https referer → referer
        Assert.Equal(new Uri(referer), Sites.HandOffTarget(url, referer, 403));

        // 403 + no referer → url
        Assert.Equal(url, Sites.HandOffTarget(url, null, 403));
        Assert.Equal(url, Sites.HandOffTarget(url, "", 403));
        Assert.Equal(url, Sites.HandOffTarget(url, "   ", 403));

        // 403 + "javascript:alert(1)" referer → url
        Assert.Equal(url, Sites.HandOffTarget(url, "javascript:alert(1)", 403));

        // 500 + referer → url
        Assert.Equal(url, Sites.HandOffTarget(url, referer, 500));

        // null status → url
        Assert.Equal(url, Sites.HandOffTarget(url, referer, null));

        // 410 and 404 and 401 → referer
        Assert.Equal(new Uri(referer), Sites.HandOffTarget(url, referer, 410));
        Assert.Equal(new Uri(referer), Sites.HandOffTarget(url, referer, 404));
        Assert.Equal(new Uri(referer), Sites.HandOffTarget(url, referer, 401));
        Assert.Equal(new Uri("http://page.example/download"), Sites.HandOffTarget(url, "http://page.example/download", 401));
    }

    [Fact]
    public void AppSettings_persists_browser_only_sites()
    {
        var file = Path.Combine(Setup.TempDir(), "settings.json");
        var s = AppSettings.Load(file);
        Assert.Empty(s.BrowserOnlySites);

        s.SetBrowserOnly("https://www.WeTransfer.com/x", true);
        Assert.Equal(["wetransfer.com"], s.BrowserOnlySites);

        var reloaded = AppSettings.Load(file);
        Assert.Equal(["wetransfer.com"], reloaded.BrowserOnlySites);

        s.SetBrowserOnly("https://www.WeTransfer.com/x", false);
        Assert.Empty(s.BrowserOnlySites);

        var reloaded2 = AppSettings.Load(file);
        Assert.Empty(reloaded2.BrowserOnlySites);
    }
}
