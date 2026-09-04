using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace p4g64.accessibility.Native.Text;

/// <summary>
/// Which glyph table the game's text uses. P4G PC ships ONE glyph numbering per script family;
/// the CJK families reuse the same glyph numbers for different characters, so the decoder must
/// load the table that matches the game's language. Tables are the AtlusScriptCompiler charsets
/// (bundled flat in the mod folder as P4G_*.tsv).
/// </summary>
internal static class GameLanguage
{
    /// <summary>Settings-menu order. 0 = Auto (detect from Steam), then the five tables.</summary>
    internal static readonly string[] ChoiceLabels =
    {
        "Auto", "English and European", "Japanese", "Simplified Chinese", "Traditional Chinese", "Korean",
    };

    private static readonly string[] TableFiles =
    {
        "P4G_EFIGS.tsv", "P4G_EFIGS.tsv", "P4G_JP.tsv", "P4G_CHS.tsv", "P4G_CHT.tsv", "P4G_Korean.tsv",
    };

    /// <summary>The table file chosen at startup (for the log / settings description).</summary>
    internal static string ActiveTable { get; private set; } = "P4G_EFIGS.tsv";
    internal static string DetectedSteamLanguage { get; private set; } = "";

    /// <summary>Resolve the table file for a settings choice (0 = Auto).</summary>
    internal static string ResolveTable(int choice)
    {
        if (choice < 0 || choice >= TableFiles.Length) choice = 0;
        string file;
        if (choice == 0)
        {
            DetectedSteamLanguage = DetectSteamLanguage();
            file = TableForSteamLanguage(DetectedSteamLanguage);
        }
        else file = TableFiles[choice];
        ActiveTable = file;
        return file;
    }

    /// <summary>Steam language id → table. Unknown / European → EFIGS.</summary>
    internal static string TableForSteamLanguage(string steamLang) => steamLang switch
    {
        "japanese" => "P4G_JP.tsv",
        "schinese" => "P4G_CHS.tsv",
        "tchinese" => "P4G_CHT.tsv",
        "koreana" or "korean" => "P4G_Korean.tsv",
        _ => "P4G_EFIGS.tsv",
    };

    /// <summary>
    /// The game's OWN language setting: Steam's per-app choice in steamapps/appmanifest_1113000.acf
    /// ("UserConfig" → "language"); when absent, the Steam client language from the registry;
    /// when that fails too, "english".
    /// </summary>
    internal static string DetectSteamLanguage()
    {
        try
        {
            // <library>/steamapps/common/Persona 4 Golden/P4G.exe → <library>/steamapps/appmanifest_1113000.acf
            var gameDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            var steamapps = Path.GetFullPath(Path.Combine(gameDir, "..", ".."));
            var acf = Path.Combine(steamapps, "appmanifest_1113000.acf");
            if (File.Exists(acf))
            {
                var text = File.ReadAllText(acf);
                var lang = ParseAcfLanguage(text);
                if (lang != null) return lang;
            }
        }
        catch (Exception e) { Utils.Log($"[Language] appmanifest read failed: {e.Message}"); }

        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "Language", null) is string s
                && !string.IsNullOrWhiteSpace(s))
                return s.Trim().ToLowerInvariant();
        }
        catch (Exception e) { Utils.Log($"[Language] registry read failed: {e.Message}"); }

        return "english";
    }

    private static readonly Regex UserConfigLang = new(
        "\"UserConfig\"\\s*\\{[^}]*?\"language\"\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AnyLang = new("\"language\"\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Prefer the UserConfig block (the player's explicit choice); else any language key.</summary>
    internal static string? ParseAcfLanguage(string acfText)
    {
        var m = UserConfigLang.Match(acfText);
        if (!m.Success) m = AnyLang.Match(acfText);
        return m.Success ? m.Groups[1].Value.Trim().ToLowerInvariant() : null;
    }
}
