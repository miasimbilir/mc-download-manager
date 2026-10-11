using System.Runtime.CompilerServices;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace MCDM.Tests;

internal static class Setup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "mcdm-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// Runs before any test touches AppPaths, so nothing lands in the real profile.
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("MCDM_DATA_DIR", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("MCDM_DOWNLOADS_DIR", Path.Combine(Root, "downloads"));
        Environment.SetEnvironmentVariable("MCDM_LANG", "en");
        // The app's users run Windows in Turkish; culture-sensitive casing or number parsing must not break protocol code.
        var tr = new System.Globalization.CultureInfo("tr-TR");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = tr;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = tr;
        SegmentedDownloader.BackoffScale = 0.02;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Root, true); } catch { } };
    }

    public static string TempDir()
    {
        var d = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static async Task Until(Func<bool> condition, int seconds = 30, string what = "condition")
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }
}
