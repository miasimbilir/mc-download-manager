using System.Text.RegularExpressions;
using Xunit;

namespace MCDM.Tests;

public class LocalizationTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "MCDM.Core"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Windows/src"), "src");
    }

    /// Every string the UI shows must exist in the Turkish table; a missing one would surface as an
    /// English sentence in the middle of a Turkish window.
    [Fact]
    public void Every_ui_string_has_a_turkish_translation()
    {
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            if (file.EndsWith(".cs"))
                foreach (Match m in Regex.Matches(text, "Loc\\.T\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    keys.Add(Regex.Unescape(m.Groups[1].Value));
            else if (file.EndsWith(".axaml"))
                foreach (Match m in Regex.Matches(text, @"\{l:T '([^']*)'\}"))
                    keys.Add(m.Groups[1].Value.Replace("&#10;", "\n"));
        }
        // Picker labels in Settings ▸ Video are translated from tables rather than literals.
        keys.UnionWith(["MP4 (H.264/AAC, plays everywhere)", "Original (no conversion)", "Best available", "Audio only (M4A)", "Audio only (MP3)"]);
        Assert.True(keys.Count > 200, $"only {keys.Count} strings found — the scan is broken");
        var missing = keys.Where(k => !Loc.TurkishTable.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "No Turkish translation for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Translations_keep_their_placeholders()
    {
        foreach (var (en, tr) in Loc.TurkishTable)
        {
            static SortedSet<string> Holes(string s) => new(Regex.Matches(s, @"\{\d+\}").Select(m => m.Value));
            Assert.True(Holes(en).SetEquals(Holes(tr)), $"placeholders differ: “{en}” → “{tr}”");
        }
    }
}
