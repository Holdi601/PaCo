using System.Text.Json;
using System.Text.RegularExpressions;
using PaCo.Shared.I18n;

namespace PaCo.Tests;

/// <summary>
/// Translation catalogs: every German source text used in code must be translated into every language, with the
/// same placeholders. Key lists are produced by tools/i18n/extract.py (same rules as <see cref="Extract"/>).
/// </summary>
public partial class I18nTests
{
    private static readonly string Root = FindRoot();

    private static readonly Dictionary<string, string> CatalogDirs = new()
    {
        ["shared"] = "src/PaCo.Shared/I18n",
        ["client"] = "src/PaCo.Client/I18n",
        ["server"] = "src/PaCo.Server/I18n",
    };

    public static TheoryData<string> CatalogNames => new(CatalogDirs.Keys);

    [Theory]
    [MemberData(nameof(CatalogNames))]
    public void KeyListsAreUpToDate(string catalog)
    {
        var listed = ReadKeys(catalog);
        var used = Extract()[catalog];
        Assert.True(used.SetEquals(listed),
            $"tools/i18n/keys/{catalog}.json is outdated – run: python tools/i18n/extract.py\n" +
            $"new: {string.Join(" | ", used.Except(listed).Take(10))}\nunused: {string.Join(" | ", listed.Except(used).Take(10))}");
    }

    [Theory]
    [MemberData(nameof(CatalogNames))]
    public void EveryLanguageTranslatesEveryKeyWithSamePlaceholders(string catalog)
    {
        var keys = ReadKeys(catalog);
        var problems = new List<string>();
        foreach (var lang in Languages.All.Where(l => l.Code != Languages.Source))
        {
            var path = Path.Combine(Root, CatalogDirs[catalog], $"{catalog}.{lang.Code}.json");
            if (!File.Exists(path)) { problems.Add($"{lang.Code}: file missing"); continue; }
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
            var missing = keys.Where(k => !table.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
            if (missing.Count > 0) problems.Add($"{lang.Code}: {missing.Count} missing, e.g. „{missing[0]}“");
            foreach (var (k, v) in table)
            {
                if (!keys.Contains(k)) continue;
                if (!Placeholders(k).SetEquals(Placeholders(v))) problems.Add($"{lang.Code}: placeholders differ in „{k}“ → „{v}“");
                try { _ = string.Format(v, Enumerable.Range(0, 10).Select(i => (object)i).ToArray()); }
                catch (FormatException) { problems.Add($"{lang.Code}: invalid format „{v}“"); }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
    }

    [Fact]
    public void KeywordListsExistForEveryLanguage()
    {
        var categories = Enum.GetNames<PaCo.Shared.AlertCategory>().Where(c => c != "Custom").ToHashSet();
        foreach (var lang in Languages.All)
        {
            var path = Path.Combine(Root, "src/PaCo.Shared/I18n", $"keywords.{lang.Code}.json");
            Assert.True(File.Exists(path), $"keywords.{lang.Code}.json missing");
            var lists = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))!;
            Assert.True(lists.Keys.All(categories.Contains), $"{lang.Code}: unknown category");
            Assert.True(lists.TryGetValue("SelfHarm", out var selfHarm) && selfHarm.Length >= 5, $"{lang.Code}: too few self-harm keywords");
        }
    }

    [Fact]
    public void LanguageNormalization()
    {
        Assert.Equal("de", Languages.Normalize("de-AT"));
        Assert.Equal("zh-Hant", Languages.Normalize("zh-TW"));
        Assert.Equal("zh-Hans", Languages.Normalize("zh-CN"));
        Assert.Equal("nb", Languages.Normalize("nn-NO"));
        Assert.Equal("pt", Languages.Normalize("pt-BR"));
        Assert.Equal("en", Languages.Normalize("xx"));
        Assert.Equal(39, Languages.All.Count);
    }

    // ---------- extraction (mirrors tools/i18n/extract.py) ----------

    private const string Lit = @"""(?:[^""\\\n]|\\.)*""";
    private const string Concat = Lit + @"(?:\s*\+\s*" + Lit + ")*";

    private static readonly Dictionary<string, (string[] Dirs, (Regex Re, bool RazorOnly)[] Patterns)> Rules = new()
    {
        ["client"] = (["src/PaCo.Client"],
        [
            (new Regex(@"\bL\.(?:T|F)\(\s*(" + Concat + ")"), false),
            (new Regex(@"\bL\.In\([^,()]+,\s*(" + Concat + ")"), false),
        ]),
        ["shared"] = (["src/PaCo.Shared", "src/PaCo.Client", "src/PaCo.Server"],
        [
            (new Regex(@"\bLocText\.Of\(\s*(" + Concat + ")"), false),
            (new Regex(@"\bSharedText\.(?:T|F)\([^,()]+,\s*(" + Concat + ")"), false),
            (new Regex(@"\bUi\.S\(\s*(" + Concat + ")"), false),
        ]),
        ["server"] = (["src/PaCo.Server"],
        [
            (new Regex(@"\bUi\.(?:T|F)\(\s*(" + Concat + ")"), false),
            (new Regex(@"\bUi\.In\([^,()]+,\s*(" + Concat + ")"), false),
            (new Regex(@"(?<![\w.])(?:T|F)\(\s*(" + Concat + ")"), true),
            (new Regex(@"\bErrorMessage\s*=\s*(" + Concat + ")"), false),
        ]),
    };

    private static readonly Regex Block = new(@"//\s*i18n-keys:start\s+(\w+)(.*?)//\s*i18n-keys:end", RegexOptions.Singleline);
    private static readonly Regex LitRe = new(Lit);
    private static readonly Regex ConcatRe = new(Concat);

    private static Dictionary<string, HashSet<string>> Extract()
    {
        var keys = Rules.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var (cat, (dirs, patterns)) in Rules)
            foreach (var f in SourceFiles(dirs))
            {
                var src = File.ReadAllText(f);
                foreach (var (re, razorOnly) in patterns)
                {
                    if (razorOnly && !f.EndsWith(".cshtml")) continue;
                    foreach (Match m in re.Matches(src)) keys[cat].Add(Join(m.Groups[1].Value));
                }
            }
        foreach (var f in SourceFiles(["src"]))
            foreach (Match m in Block.Matches(File.ReadAllText(f)))
                foreach (Match lit in ConcatRe.Matches(m.Groups[2].Value))
                    keys[m.Groups[1].Value].Add(Join(lit.Value));
        foreach (var k in keys.Values) k.Remove("");
        return keys;
    }

    private static IEnumerable<string> SourceFiles(string[] dirs) =>
        dirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".cshtml"))
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or "Migrations" or "lib"));

    private static string Join(string concat) => string.Concat(LitRe.Matches(concat).Select(m => Unescape(m.Value)));

    private static string Unescape(string lit) => Regex.Replace(lit[1..^1], @"\\(.)", m => m.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r", "0" => "\0", var c => c,
    });

    private static HashSet<string> ReadKeys(string catalog) =>
        JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Root, "tools/i18n/keys", catalog + ".json")))!.ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Placeholders(string s) =>
        PlaceholderRe().Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRe();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PaCo.slnx"))) return d.FullName;
        throw new InvalidOperationException("Repository root not found");
    }
}
