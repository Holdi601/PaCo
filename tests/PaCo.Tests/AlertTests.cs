using PaCo.Shared;

namespace PaCo.Tests;

public class AlertTests
{
    private static readonly AlertKeywords.Matcher All = new(new AlertSettings());

    [Theory]
    [InlineData("wie kann man suizid begehen - Google Suche", AlertCategory.SelfHarm)]
    [InlineData("SELBSTMORDGEDANKEN was tun", AlertCategory.SelfHarm)] // prefix match
    [InlineData("ich will nicht mehr leben", AlertCategory.SelfHarm)]
    [InlineData("su1z1d", AlertCategory.SelfHarm)] // leetspeak
    [InlineData("Lebensmüde – YouTube", AlertCategory.SelfHarm)] // umlaut
    [InlineData("hey, sag es niemandem okay?", AlertCategory.Grooming)]
    [InlineData("schick mir   ein Bild von dir", AlertCategory.Grooming)]
    [InlineData("don't tell anyone", AlertCategory.Grooming)]
    [InlineData("wo kann man gras kaufen", AlertCategory.Drugs)]
    public void Detects(string text, AlertCategory expected)
    {
        var hits = All.Find(text);
        Assert.Contains(hits, h => h.Category == expected);
    }

    [Theory]
    [InlineData("Minecraft – Bauen mit Freunden")]
    [InlineData("Mathe Hausaufgaben Bruchrechnen")]
    [InlineData("Sportschuhe kaufen")]
    [InlineData("Thema: Methoden der Datenanalyse")] // "meth" must not match inside "methoden"
    public void NoFalsePositives(string text) => Assert.Empty(All.Find(text));

    [Theory]
    [InlineData("我真的想自杀了，怎么办", "自杀")]          // Chinese, no spaces
    [InlineData("もう死にたいと思っている", "死にたい")]      // Japanese
    [InlineData("요즘 정말 죽고싶어", "죽고 싶어")]          // Korean, keyword with space, text without
    public void CjkKeywordsMatchWithoutWordBoundaries(string text, string keyword)
    {
        var m = new AlertKeywords.Matcher(new AlertSettings { Categories = [], CustomKeywords = [keyword], Languages = [] });
        var hit = Assert.Single(m.Find(text));
        Assert.Equal(keyword, hit.Keyword);
        Assert.Contains(keyword.Replace(" ", "")[..1], hit.Snippet);
    }

    [Theory]
    [InlineData("samobojstwo", "samobójstwo")]      // Polish without accents
    [InlineData("αυτοκτονία", "αυτοκτονια")] // Greek with/without tonos
    [InlineData("SUICIDIO", "suicidio")]
    public void AccentsAreIgnored(string text, string keyword)
    {
        var m = new AlertKeywords.Matcher(new AlertSettings { Categories = [], CustomKeywords = [keyword], Languages = [] });
        Assert.Single(m.Find(text));
    }

    [Fact]
    public void LanguagesSelectKeywordLists()
    {
        var onlyEn = new AlertKeywords.Matcher(new AlertSettings { Languages = ["en"] });
        Assert.Empty(onlyEn.Find("ich will nicht mehr leben"));
        Assert.NotEmpty(onlyEn.Find("i want to die"));
    }

    [Fact]
    public void OverlappingPhrasesOfSameCategoryReportedOnce()
    {
        var hits = All.Find("Ich will nicht mehr leben. Und er hat gesagt: sag es niemandem.");
        Assert.Single(hits, h => h.Category == AlertCategory.SelfHarm);
        Assert.Equal("will nicht mehr leben", hits.Single(h => h.Category == AlertCategory.SelfHarm).Keyword);
        Assert.Single(hits, h => h.Category == AlertCategory.Grooming);
    }

    [Fact]
    public void SnippetIsShortAndAroundTheHit()
    {
        var text = new string('a', 500) + " ich will sterben " + new string('b', 500);
        var hit = Assert.Single(All.Find(text));
        Assert.Contains("will sterben", hit.Snippet);
        Assert.True(hit.Snippet.Length < 140);
    }

    [Fact]
    public void CategoriesCustomWordsAndDisabled()
    {
        var onlyCustom = new AlertKeywords.Matcher(new AlertSettings { Categories = [], CustomKeywords = ["Fortnite", "geheim*"] });
        Assert.Empty(onlyCustom.Find("suizid"));
        Assert.Equal(AlertCategory.Custom, Assert.Single(onlyCustom.Find("Fortnite spielen")).Category);
        Assert.Single(onlyCustom.Find("das ist geheimnisvoll"));
        Assert.True(new AlertKeywords.Matcher(new AlertSettings { Enabled = false }).IsEmpty);
    }
}
