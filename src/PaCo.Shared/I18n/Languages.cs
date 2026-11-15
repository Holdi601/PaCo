using System.Globalization;

namespace PaCo.Shared.I18n;

public sealed record Language(string Code, string NativeName, string EnglishName);

/// <summary>All languages PaCo is translated into. German is the source language.</summary>
public static class Languages
{
    public const string Source = "de";

    public static readonly IReadOnlyList<Language> All =
    [
        // EU official languages
        new("bg", "Български", "Bulgarian"),
        new("cs", "Čeština", "Czech"),
        new("da", "Dansk", "Danish"),
        new("de", "Deutsch", "German"),
        new("el", "Ελληνικά", "Greek"),
        new("en", "English", "English"),
        new("es", "Español", "Spanish"),
        new("et", "Eesti", "Estonian"),
        new("fi", "Suomi", "Finnish"),
        new("fr", "Français", "French"),
        new("ga", "Gaeilge", "Irish"),
        new("hr", "Hrvatski", "Croatian"),
        new("hu", "Magyar", "Hungarian"),
        new("it", "Italiano", "Italian"),
        new("lt", "Lietuvių", "Lithuanian"),
        new("lv", "Latviešu", "Latvian"),
        new("mt", "Malti", "Maltese"),
        new("nl", "Nederlands", "Dutch"),
        new("pl", "Polski", "Polish"),
        new("pt", "Português", "Portuguese"),
        new("ro", "Română", "Romanian"),
        new("sk", "Slovenčina", "Slovak"),
        new("sl", "Slovenščina", "Slovenian"),
        new("sv", "Svenska", "Swedish"),
        // further European languages
        new("ca", "Català", "Catalan"),
        new("is", "Íslenska", "Icelandic"),
        new("lb", "Lëtzebuergesch", "Luxembourgish"),
        new("nb", "Norsk", "Norwegian"),
        new("sq", "Shqip", "Albanian"),
        new("sr", "Српски", "Serbian"),
        new("bs", "Bosanski", "Bosnian"),
        new("mk", "Македонски", "Macedonian"),
        new("uk", "Українська", "Ukrainian"),
        new("ru", "Русский", "Russian"),
        new("tr", "Türkçe", "Turkish"),
        // East Asian languages
        new("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new("zh-Hant", "繁體中文（台灣）", "Chinese (Traditional, Taiwan)"),
        new("ja", "日本語", "Japanese"),
        new("ko", "한국어", "Korean"),
    ];

    private static readonly HashSet<string> Codes = new(All.Select(l => l.Code), StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string? code) => code is not null && Codes.Contains(code);

    /// <summary>Maps any culture/locale name ("de-AT", "zh-TW", "no", "pt-BR", "zh-CN") to a supported language code.</summary>
    public static string Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return Source;
        var c = culture.Trim().Replace('_', '-');
        var lower = c.ToLowerInvariant();
        if (lower.StartsWith("zh"))
            return lower.Contains("hant") || lower.EndsWith("-tw") || lower.EndsWith("-hk") || lower.EndsWith("-mo") ? "zh-Hant" : "zh-Hans";
        if (lower is "no" or "nn" || lower.StartsWith("no-") || lower.StartsWith("nn-") || lower.StartsWith("nb")) return "nb";
        if (lower.StartsWith("sr")) return "sr";
        var two = lower.Split('-')[0];
        return IsSupported(two) ? All.First(l => l.Code.Equals(two, StringComparison.OrdinalIgnoreCase)).Code : "en";
    }

    /// <summary>.NET culture for formatting dates/numbers.</summary>
    public static CultureInfo Culture(string code)
    {
        var name = code switch
        {
            "zh-Hans" => "zh-CN",
            "zh-Hant" => "zh-TW",
            "nb" => "nb-NO",
            "sr" => "sr-Cyrl-RS",
            _ => code,
        };
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
