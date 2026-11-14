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

    public static readonly IReadOnlyDictionary<AlertCategory, string[]> BuiltIn = new Dictionary<AlertCategory, string[]>
    {
        [AlertCategory.SelfHarm] =
        [
            "suizid*", "selbstmord*", "suicide*", "suicidal", "umbringen", "mich umbringen", "bring mich um", "will sterben", "will nicht mehr leben",
            "nicht mehr leben", "lebensmüde", "das leben nehmen", "ritzen", "ritze mich", "selbstverletz*", "überdosis", "schmerzlos sterben",
            "wie bringe ich mich", "kill myself", "want to die", "self harm", "selfharm", "cutting myself", "end my life", "kys",
        ],
        [AlertCategory.Drugs] =
        [
            "drogen kaufen", "gras kaufen", "weed kaufen", "kokain", "koks", "crystal meth", "meth", "ecstasy", "mdma", "lsd", "heroin",
            "ketamin", "tilidin", "xanax", "lean trinken", "dealer", "buy weed", "cocaine",
        ],
        [AlertCategory.Violence] =
        [
            "amoklauf", "amok laufen", "waffe kaufen", "pistole kaufen", "bombe bauen", "school shooting", "ich bring dich um",
            "abstechen", "erschießen", "messer mitnehmen",
        ],
        [AlertCategory.Sexual] =
        [
            "porno*", "porn*", "nacktbilder", "nacktfotos", "nudes", "onlyfans", "sexcam", "sex cam", "hentai", "xxx", "nsfw",
        ],
        [AlertCategory.Bullying] =
        [
            "du opfer", "keiner mag dich", "niemand mag dich", "bring dich um", "töte dich", "fett und hässlich", "hurensohn", "missgeburt",
            "spast", "behindert bist", "alle hassen dich",
        ],
        [AlertCategory.Grooming] =
        [
            "sag es niemandem", "sag niemandem", "sags niemandem", "unser geheimnis", "unser kleines geheimnis", "schick mir ein bild",
            "schick mir ein foto", "schick mal ein foto", "schick mal ein bild", "bist du allein", "bist du alleine", "sind deine eltern da",
            "wo wohnst du", "treffen wir uns", "lass uns treffen", "lösch den chat", "lösch die nachricht", "zeig mal dich",
            "don't tell anyone", "dont tell anyone", "send me a pic", "are you alone", "our secret",
        ],
        [AlertCategory.Extremism] =
        [
            "heil hitler", "sieg heil", "88 heil", "white power", "rassenkrieg", "ungläubige töten",
        ],
    };

    public sealed class Matcher
    {
        private readonly List<(AlertCategory Cat, string Display, string[] Words, bool Prefix)> _rules = [];

        public Matcher(AlertSettings settings)
        {
            if (!settings.Enabled) return;
            foreach (var cat in settings.Categories.Distinct())
                if (BuiltIn.TryGetValue(cat, out var list))
                    foreach (var k in list) Add(cat, k);
            foreach (var k in settings.CustomKeywords) Add(AlertCategory.Custom, k);
        }

        public bool IsEmpty => _rules.Count == 0;

        private void Add(AlertCategory cat, string keyword)
        {
            var k = keyword.Trim();
            if (k.Length < 2) return;
            var prefix = k.EndsWith('*');
            var norm = Normalize(k.TrimEnd('*'), out _);
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;
            _rules.Add((cat, k.TrimEnd('*'), words, prefix));
        }

        /// <summary>Returns at most one match per keyword.</summary>
        public List<AlertMatch> Find(string text, int snippetRadius = 60)
        {
            var result = new List<AlertMatch>();
            if (_rules.Count == 0 || string.IsNullOrWhiteSpace(text)) return result;
            var norm = Normalize(text, out var map);
            var tokens = Tokenize(norm);
            foreach (var rule in _rules)
            {
                var at = IndexOf(tokens, rule.Words, rule.Prefix);
                if (at < 0) continue;
                var start = map[tokens[at].Start];
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

    /// <summary>Lower-case, umlauts/ß folded, leetspeak mapped, everything else non-alphanumeric → space.
    /// <paramref name="map"/>[i] = index in the original text for normalized position i.</summary>
    public static string Normalize(string text, out int[] map)
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
                case 'ä': Put("ae", i); break;
                case 'ö': Put("oe", i); break;
                case 'ü': Put("ue", i); break;
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
                    if (char.IsLetterOrDigit(c)) Put(c.ToString(), i);
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

    public const string HelpText =
        "Wenn es dir gerade nicht gut geht: Du bist nicht allein. Nummer gegen Kummer 116 111 (kostenlos, anonym) " +
        "oder TelefonSeelsorge 0800 111 0 111 – rund um die Uhr. Im Notfall: 112.";
}
