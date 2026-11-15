using System.Globalization;
using System.Text;

namespace PaCo.Shared;

public enum AlertCategory
{
    SelfHarm,
    Drugs,
    Violence,
    Sexual,
    Bullying,
    Grooming,
    Extremism,
    Custom,
}

/// <summary>Per-child alarm settings (part of the signed policy).</summary>
public sealed record AlertSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Also scan the visible text of the active window (chats, web pages). Otherwise only window titles.</summary>
    public bool ScanWindowText { get; init; } = true;

    /// <summary>Show the child a help hint (counselling hotlines) when self-harm words appear.</summary>
    public bool ShowHelpToChild { get; init; } = true;

    public IReadOnlyList<AlertCategory> Categories { get; init; } =
        [AlertCategory.SelfHarm, AlertCategory.Drugs, AlertCategory.Violence, AlertCategory.Sexual, AlertCategory.Bullying, AlertCategory.Grooming, AlertCategory.Extremism];

    /// <summary>Own words/phrases of the parents. A trailing * matches word beginnings ("selbstmord*").</summary>
    public IReadOnlyList<string> CustomKeywords { get; init; } = [];

    /// <summary>Languages whose built-in keyword lists are used (codes from <see cref="I18n.Languages"/>).</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["de", "en"];
}

/// <summary>One match, reported by the device. Contains only the hit and a short snippet – never full texts.</summary>
public sealed record AlertReport
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ChildId { get; init; }

    public DateTimeOffset TimeUtc { get; init; }

    public AlertCategory Category { get; init; }

    public string Keyword { get; init; } = "";

    public string App { get; init; } = "";

    public string? WindowTitle { get; init; }

    public string Snippet { get; init; } = "";

    /// <summary>"Fenstertitel" or "Bildschirmtext".</summary>
    public string Source { get; init; } = "";
}

public readonly record struct AlertMatch(AlertCategory Category, string Keyword, string Snippet);

/// <summary>
/// Built-in keyword lists and a tolerant matcher: case-insensitive, umlaut/ß folding, common leetspeak
/// ("su1z1d"), punctuation and repeated spaces ignored, whole words or word beginnings (trailing *).
/// </summary>
public static class AlertKeywords
{
    // i18n-keys:start shared
    public static string Label(AlertCategory c) => c switch
    {
        AlertCategory.SelfHarm => "Suizid & Selbstverletzung",
        AlertCategory.Drugs => "Drogen",
        AlertCategory.Violence => "Gewalt & Waffen",
        AlertCategory.Sexual => "Sexuelle Inhalte",
        AlertCategory.Bullying => "Mobbing & Beleidigungen",
        AlertCategory.Grooming => "Grooming (Erwachsene, die Kinder ködern)",
        AlertCategory.Extremism => "Extremismus",
        _ => "Eigene Stichwörter",
    };
    // i18n-keys:end

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<AlertCategory, string[]>> Lists = new();

    /// <summary>Built-in keyword list of one language (embedded "I18n/keywords.{lang}.json"); empty if none.</summary>
    public static IReadOnlyDictionary<AlertCategory, string[]> ForLanguage(string language) => Lists.GetOrAdd(language, lang =>
    {
        using var s = typeof(AlertKeywords).Assembly.GetManifestResourceStream($"PaCo.Shared.I18n.keywords.{lang}.json");
        if (s is null) return new Dictionary<AlertCategory, string[]>();
        var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(s) ?? [];
        return raw.Where(kv => Enum.TryParse<AlertCategory>(kv.Key, out _))
            .ToDictionary(kv => Enum.Parse<AlertCategory>(kv.Key), kv => kv.Value);
    });

    /// <summary>German + English lists (compatibility / tests).</summary>
    public static IReadOnlyDictionary<AlertCategory, string[]> BuiltIn =>
        ForLanguage("de").Concat(ForLanguage("en")).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).ToArray());

    /// <summary>Scripts without spaces between words: keywords are matched as substrings there.</summary>
    internal static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ'      // Hiragana, Katakana
            or >= '㐀' and <= '鿿'    // CJK ideographs (+ ext. A)
            or >= '가' and <= '힯'    // Hangul syllables
            or >= 'ᄀ' and <= 'ᇿ'    // Hangul Jamo
            or >= '豈' and <= '﫿';   // CJK compatibility

    public sealed class Matcher
    {
        private readonly List<(AlertCategory Cat, string Display, string[] Words, string[] Plain, bool Prefix)> _rules = [];
        private readonly List<(AlertCategory Cat, string Display, string Compact)> _cjkRules = [];

        public Matcher(AlertSettings settings)
        {
            if (!settings.Enabled) return;
            var languages = settings.Languages.Count > 0 ? settings.Languages : ["de", "en"];
            foreach (var lang in languages.Distinct())
            {
                var lists = ForLanguage(lang);
                foreach (var cat in settings.Categories.Distinct())
                    if (lists.TryGetValue(cat, out var list))
                        foreach (var k in list) Add(cat, k);
            }
            foreach (var k in settings.CustomKeywords) Add(AlertCategory.Custom, k);
        }

        public bool IsEmpty => _rules.Count == 0 && _cjkRules.Count == 0;

        public int Count => _rules.Count + _cjkRules.Count;

        private void Add(AlertCategory cat, string keyword)
        {
            var k = keyword.Trim();
            if (k.Length < 2 && !k.Any(IsCjk)) return;
            var prefix = k.EndsWith('*');
            var norm = Normalize(k.TrimEnd('*'), out _);
            if (norm.Any(IsCjk))
            {
                // Chinese/Japanese/Korean: no word boundaries – match the keyword (without spaces) as substring.
                var compact = norm.Replace(" ", "");
                if (compact.Length > 0) _cjkRules.Add((cat, k.TrimEnd('*'), compact));
                return;
            }
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;
            var plain = Normalize(k.TrimEnd('*'), out _, germanDigraphs: false).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (plain.Length != words.Length) plain = words;
            _rules.Add((cat, k.TrimEnd('*'), words, plain, prefix));
        }

        /// <summary>Returns at most one match per keyword.</summary>
        public List<AlertMatch> Find(string text, int snippetRadius = 60)
        {
            var result = new List<AlertMatch>();
            if (IsEmpty || string.IsNullOrWhiteSpace(text)) return result;
            var norm = Normalize(text, out var map);
            if (_cjkRules.Count > 0 && norm.Any(IsCjk))
            {
                // Compact text (spaces removed) with a map back to the original positions.
                var compact = new StringBuilder(norm.Length);
                var cmap = new List<int>(norm.Length);
                for (var i = 0; i < norm.Length; i++)
                {
                    if (norm[i] == ' ') continue;
                    compact.Append(norm[i]);
                    cmap.Add(map[i]);
                }
                var c = compact.ToString();
                var taken = new List<(AlertCategory Cat, int From, int To)>();
                foreach (var rule in _cjkRules.OrderByDescending(r => r.Compact.Length))
                {
                    var at = c.IndexOf(rule.Compact, StringComparison.Ordinal);
                    if (at < 0) continue;
                    var to = at + rule.Compact.Length - 1;
                    if (taken.Any(t => t.Cat == rule.Cat && at <= t.To && to >= t.From)) continue;
                    taken.Add((rule.Cat, at, to));
                    result.Add(new AlertMatch(rule.Cat, rule.Display, Snippet(text, cmap[at], snippetRadius / 2)));
                }
            }
            if (_rules.Count == 0) return result;
            var tokens = Tokenize(norm);
            // Second view with ä/ö/ü → a/o/u: "lebensmuede" (German typing) and "ongyilkos" (Hungarian without accents)
            // must both match. Word boundaries are identical in both views, so token indices line up.
            var plainNorm = Normalize(text, out var plainMap, germanDigraphs: false);
            var plainTokens = Tokenize(plainNorm);
            var aligned = plainTokens.Count == tokens.Count;
            // Longest phrases first, so "will nicht mehr leben" wins over the overlapping "nicht mehr leben".
            var covered = new List<(AlertCategory Cat, int From, int To)>();
            foreach (var rule in _rules.OrderByDescending(r => r.Words.Length))
            {
                var at = IndexOf(tokens, rule.Words, rule.Prefix);
                var start = at >= 0 ? map[tokens[at].Start] : 0;
                if (at < 0 && aligned)
                {
                    at = IndexOf(plainTokens, rule.Plain, rule.Prefix);
                    if (at >= 0) start = plainMap[plainTokens[at].Start];
                }
                if (at < 0) continue;
                var to = at + rule.Words.Length - 1;
                if (covered.Any(c => c.Cat == rule.Cat && at <= c.To && to >= c.From)) continue;
                covered.Add((rule.Cat, at, to));
                result.Add(new AlertMatch(rule.Cat, rule.Display, Snippet(text, start, snippetRadius)));
            }
            return result;
        }

        private static int IndexOf(List<(int Start, string Word)> tokens, string[] words, bool prefix)
        {
            for (var i = 0; i + words.Length <= tokens.Count; i++)
            {
                var ok = true;
                for (var j = 0; j < words.Length && ok; j++)
                {
                    var last = j == words.Length - 1;
                    ok = last && prefix ? tokens[i + j].Word.StartsWith(words[j], StringComparison.Ordinal) : tokens[i + j].Word == words[j];
                }
                if (ok) return i;
            }
            return -1;
        }
    }

    private static List<(int Start, string Word)> Tokenize(string norm)
    {
        var list = new List<(int, string)>();
        var i = 0;
        while (i < norm.Length)
        {
            while (i < norm.Length && norm[i] == ' ') i++;
            var s = i;
            while (i < norm.Length && norm[i] != ' ') i++;
            if (i > s) list.Add((s, norm[s..i]));
        }
        return list;
    }

    /// <summary>
    /// Strips accents from Latin, Greek and Cyrillic letters (é→e, ą→a, ł→l, ά→α), because children often type
    /// without them. CJK and Hangul are left untouched (decomposing Hangul would break matching).
    /// </summary>
    private static string Fold(char c)
    {
        switch (c)
        {
            case 'ł': return "l";
            case 'đ': return "d";
            case 'ð': return "d";
            case 'ø': return "o";
            case 'ħ': return "h";
            case 'ı': return "i";
            case 'æ': return "ae";
            case 'œ': return "oe";
            case 'þ': return "th";
        }
        if (c < '\u00C0' || !(c <= '\u052F' || c is >= '\u1E00' and <= '\u1FFF')) return c.ToString();
        var d = c.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.Length == 0 ? c.ToString() : sb.ToString();
    }

    /// <summary>Lower-case, umlauts/ß folded, leetspeak mapped, everything else non-alphanumeric → space.
    /// <paramref name="map"/>[i] = index in the original text for normalized position i.</summary>
    public static string Normalize(string text, out int[] map, bool germanDigraphs = true)
    {
        var sb = new StringBuilder(text.Length + 16);
        var m = new List<int>(text.Length + 16);
        void Put(string s, int origin)
        {
            foreach (var ch in s)
            {
                if (ch == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
                sb.Append(ch);
                m.Add(origin);
            }
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            switch (c)
            {
                case 'ä' when germanDigraphs: Put("ae", i); break;
                case 'ö' when germanDigraphs: Put("oe", i); break;
                case 'ü' when germanDigraphs: Put("ue", i); break;
                case 'ß': Put("ss", i); break;
                case '0': Put("o", i); break;
                case '1': Put("i", i); break;
                case '3': Put("e", i); break;
                case '4': Put("a", i); break;
                case '5': Put("s", i); break;
                case '7': Put("t", i); break;
                case '@': Put("a", i); break;
                case '$': Put("s", i); break;
                case '\'': break; // "don't" → "dont"
                default:
                    if (char.IsLetterOrDigit(c)) Put(Fold(c), i);
                    else Put(" ", i);
                    break;
            }
        }
        map = m.ToArray();
        return sb.ToString().Trim();
    }

    private static string Snippet(string text, int index, int radius)
    {
        var s = Math.Max(0, index - radius);
        var e = Math.Min(text.Length, index + radius);
        var snip = text[s..e].Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (snip.Contains("  ")) snip = snip.Replace("  ", " ");
        return (s > 0 ? "…" : "") + snip.Trim() + (e < text.Length ? "…" : "");
    }

    // i18n-keys:start shared
    public const string HelpText =
        "Wenn es dir gerade nicht gut geht: Du bist nicht allein. Nummer gegen Kummer 116 111 (kostenlos, anonym) " +
        "oder TelefonSeelsorge 0800 111 0 111 – rund um die Uhr. Im Notfall: 112.";
    // i18n-keys:end
}
