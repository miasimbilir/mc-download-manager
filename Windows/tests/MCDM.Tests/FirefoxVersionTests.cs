using System.IO.Compression;
using System.Text;
using Xunit;

namespace MCDM.Tests;

public class FirefoxVersionTests
{
    private static void CreateZipWithEntry(string path, string entryName, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    [Fact]
    public void XpiVersion_reads_version_from_root_manifest()
    {
        var path = Path.Combine(Setup.TempDir(), "valid.xpi");
        try
        {
            CreateZipWithEntry(path, "manifest.json", """{"manifest_version":2,"version":"1.4.2","name":"x"}""");
            Assert.Equal("1.4.2", BrowserIntegration.XpiVersion(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void XpiVersion_returns_null_when_manifest_only_in_subfolder()
    {
        var path = Path.Combine(Setup.TempDir(), "subfolder.xpi");
        try
        {
            CreateZipWithEntry(path, "sub/manifest.json", """{"manifest_version":2,"version":"1.4.2","name":"x"}""");
            Assert.Null(BrowserIntegration.XpiVersion(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void XpiVersion_returns_null_for_missing_version_or_invalid_inputs()
    {
        var dir = Setup.TempDir();
        var noVersion = Path.Combine(dir, "no-version.xpi");
        var invalidJson = Path.Combine(dir, "invalid-json.xpi");
        var notZip = Path.Combine(dir, "not-zip.xpi");
        var missing = Path.Combine(dir, "missing.xpi");

        try
        {
            // no version property -> null
            CreateZipWithEntry(noVersion, "manifest.json", """{"manifest_version":2,"name":"x"}""");
            Assert.Null(BrowserIntegration.XpiVersion(noVersion));

            // invalid JSON -> null
            CreateZipWithEntry(invalidJson, "manifest.json", "{not-valid-json");
            Assert.Null(BrowserIntegration.XpiVersion(invalidJson));

            // a file that is not a zip -> null
            File.WriteAllText(notZip, "this is not a zip file content");
            Assert.Null(BrowserIntegration.XpiVersion(notZip));

            // missing file -> null
            Assert.Null(BrowserIntegration.XpiVersion(missing));

            // null path -> null
            Assert.Null(BrowserIntegration.XpiVersion(null));
        }
        finally
        {
            try { File.Delete(noVersion); } catch { }
            try { File.Delete(invalidJson); } catch { }
            try { File.Delete(notZip); } catch { }
        }
    }

    [Fact]
    public void BundledVersionFor_returns_bundled_version_for_non_firefox_browsers()
    {
        // BundledVersionFor("chrome") equals BundledVersion (whatever it is, may be null in the test environment)
        Assert.Equal(BrowserIntegration.BundledVersion, BrowserIntegration.BundledVersionFor("chrome"));
        Assert.Equal(BrowserIntegration.BundledVersion, BrowserIntegration.BundledVersionFor("edge"));
        Assert.Equal(BrowserIntegration.BundledVersion, BrowserIntegration.BundledVersionFor("opera"));
        Assert.Equal(BrowserIntegration.BundledVersion, BrowserIntegration.BundledVersionFor("brave"));
        Assert.Equal(BrowserIntegration.BundledVersion, BrowserIntegration.BundledVersionFor(null));
        Assert.Equal(BrowserIntegration.BundledVersion, BrowserIntegration.BundledVersionFor("unknown"));
    }

    [Fact]
    public void BundledVersionFor_returns_firefox_bundled_version_for_firefox()
    {
        // Ordinal case-insensitive match for firefox
        Assert.Equal(BrowserIntegration.FirefoxBundledVersion, BrowserIntegration.BundledVersionFor("firefox"));
        Assert.Equal(BrowserIntegration.FirefoxBundledVersion, BrowserIntegration.BundledVersionFor("Firefox"));
        Assert.Equal(BrowserIntegration.FirefoxBundledVersion, BrowserIntegration.BundledVersionFor("FIREFOX"));
    }
}
