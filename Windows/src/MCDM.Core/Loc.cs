using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MCDM;

/// English is the source language; Turkish comes from Strings.tr.json (generated from the macOS
/// string catalog by scripts/extract-strings.py) plus Strings.tr.windows.json (Windows-only strings).
public static class Loc
{
    private static readonly Dictionary<string, string> tr = new(StringComparer.Ordinal);
    public static bool Turkish { get; private set; }

    static Loc()
    {
        var lang = Environment.GetEnvironmentVariable("MCDM_LANG");
        if (string.IsNullOrWhiteSpace(lang)) lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Turkish = string.Equals(lang, "tr", StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { "Strings.tr.json", "Strings.tr.windows.json" })
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) continue;
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                if (d != null) foreach (var (k, v) in d) tr[k] = v;
            }
            catch { }
        }
    }

    public static IReadOnlyDictionary<string, string> TurkishTable => tr;

    public static string T(string key) => Turkish && tr.TryGetValue(key, out var v) ? v : key;

    public static string T(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return T(key); }
    }
}

public static class Fmt
{
    /// Binary units with three significant digits, the way Explorer shows sizes.
    public static string Size(long n)
    {
        if (n < 0) return "?";
        if (n < 1024) return n + " " + Loc.T("bytes");
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = n / 1024.0;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        var c = CultureInfo.CurrentCulture;
        var num = v >= 100 ? v.ToString("0", c) : v >= 10 ? v.ToString("0.0", c) : v.ToString("0.00", c);
        return num + " " + units[i];
    }

    public static string Speed(double bps) => Loc.T("{0}/s", Size((long)Math.Max(0, bps)));

    public static string Duration(double seconds)
    {
        var s = (int)Math.Round(seconds);
        if (s < 60) return Loc.T("{0} s", s);
        if (s < 3600) return Loc.T("{0} min {1} s", s / 60, s % 60);
        return Loc.T("{0} h {1} min", s / 3600, s % 3600 / 60);
    }

    public static string Relative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var d = DateTime.Now - local;
        if (d.TotalSeconds < 60) return Loc.T("just now");
        if (d.TotalMinutes < 60) return Loc.T("{0} min ago", (int)d.TotalMinutes);
        if (d.TotalHours < 24 && local.Date == DateTime.Today) return Loc.T("{0} h ago", (int)d.TotalHours);
        if (local.Date == DateTime.Today.AddDays(-1)) return Loc.T("yesterday");
        return local.ToString("d", CultureInfo.CurrentCulture);
    }

    public static string DateTimeShort(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
