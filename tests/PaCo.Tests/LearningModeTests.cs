using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Tests;

public class LearningModeTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123XYZ_-&t=42", "youtube.com/watch?v=abc123XYZ_-")]
    [InlineData("https://youtu.be/abc123XYZ_-?si=tracking", "youtube.com/watch?v=abc123XYZ_-")]
    [InlineData("youtube.com/shorts/abc123", "youtube.com/watch?v=abc123")]
    [InlineData("https://www.youtube-nocookie.com/embed/abc123", "youtube.com/watch?v=abc123")]
    [InlineData("https://m.youtube.com/watch?v=abc123", "youtube.com/watch?v=abc123")]
    [InlineData("https://www.Example.com/Mathe/Brueche/?x=1#teil2", "example.com/mathe/brueche")]
    [InlineData("anton.app", null)]               // a whole site, not a page
    [InlineData("https://youtube.com/", null)]
    [InlineData("https://youtube.com/watch?v=<script>", "youtube.com/watch")] // no valid video id → only a path
    [InlineData("javascript:alert(1)", null)]
    [InlineData("wie geht bruchrechnen", null)]
    public void PagesAreNormalized(string input, string? expected) => Assert.Equal(expected, TimeGroups.NormalizePage(input));

    [Fact]
    public void OnlyTheAllowedVideoAndSubPagesMatch()
    {
        const string video = "youtube.com/watch?v=abc123";
        Assert.True(TimeGroups.PageMatches("https://www.youtube.com/watch?v=abc123&t=10", video));
        Assert.False(TimeGroups.PageMatches("https://www.youtube.com/watch?v=other1", video));
        Assert.False(TimeGroups.PageMatches("https://www.youtube.com/", video));
        Assert.False(TimeGroups.PageMatches("https://www.youtube.com/watch?v=abc123x", video));
        Assert.True(TimeGroups.PageMatches("https://example.com/mathe/brueche/aufgabe-1", "example.com/mathe"));
        Assert.False(TimeGroups.PageMatches("https://example.com/mathematik", "example.com/mathe"));
        Assert.False(TimeGroups.PageMatches("https://example.com.evil.test/mathe", "example.com/mathe"));
    }

    private static ChildPolicy Child(bool allow = true, bool outside = false, bool lockedByParent = false) => new()
    {
        Name = "Kim", LearningAfterExpiry = allow, LearningOutsideWindow = outside, LockedByParent = lockedByParent,
        Groups =
        [
            new TimeGroup { Name = "Lernen", Kind = TimeGroupKind.Free, Apps = ["calculatorapp"], Sites = ["anton.app"], Pages = ["youtube.com/watch?v=abc123"] },
            new TimeGroup { Name = "Videos", Kind = TimeGroupKind.Limit, MinutesPerDay = 30, Sites = ["youtube.com"] },
        ],
    };

    private static Evaluation Blocked(BlockReason reason) => new() { Source = TimeSource.Blocked, Reason = reason };

    [Fact]
    public void LearningModeOnlyWhenTheParentsAllowIt()
    {
        Assert.True(TimeGroups.LearningModeAvailable(Child(), Blocked(BlockReason.LimitReached)));
        Assert.False(TimeGroups.LearningModeAvailable(Child(allow: false), Blocked(BlockReason.LimitReached)));
        // Outside the allowed hours only with the extra switch.
        Assert.False(TimeGroups.LearningModeAvailable(Child(), Blocked(BlockReason.OutsideWindow)));
        Assert.True(TimeGroups.LearningModeAvailable(Child(outside: true), Blocked(BlockReason.OutsideWindow)));
        // "Jetzt sperren" by the parents is always a full lock.
        Assert.False(TimeGroups.LearningModeAvailable(Child(outside: true, lockedByParent: true), Blocked(BlockReason.LockedByParent)));
        // Without any learning group there is nothing to learn with.
        Assert.False(TimeGroups.LearningModeAvailable(Child() with { Groups = [] }, Blocked(BlockReason.LimitReached)));
    }

    [Fact]
    public void LearningTimeIsLimitedPerDayOverAllPcs()
    {
        var day = new DateOnly(2026, 11, 22);
        var usage = new DayUsage();
        var unlimited = Child();
        Assert.Null(TimeGroups.LearningSecondsLeft(unlimited, usage, day));

        // 60 minutes a day: 20 used here, 30 on the other PC → 10 left.
        var limited = Child() with
        {
            LearningMinutesPerDay = 60,
            GroupSecondsElsewhere = new Dictionary<Guid, Dictionary<DateOnly, long>> { [TimeGroups.LearningBudgetId] = new() { [day] = 30 * 60 } },
        };
        usage.Groups[TimeGroups.LearningBudgetId] = 20 * 60;
        Assert.Equal(10 * 60, TimeGroups.LearningSecondsLeft(limited, usage, day));
        Assert.True(TimeGroups.LearningModeAvailable(limited, Blocked(BlockReason.LimitReached), 10 * 60));

        // Used up: no more learning mode for today.
        usage.Groups[TimeGroups.LearningBudgetId] = 31 * 60;
        Assert.Equal(0, TimeGroups.LearningSecondsLeft(limited, usage, day));
        Assert.False(TimeGroups.LearningModeAvailable(limited, Blocked(BlockReason.LimitReached), 0));
    }

    [Fact]
    public void BrowserAllowListHasLearningSitesAndOnlyTheAllowedVideoEmbeds()
    {
        var list = TimeGroups.LearningAllowList(Child());
        Assert.Contains("anton.app", list);
        Assert.Contains("youtube.com/watch?v=abc123", list);
        Assert.Contains("youtube.com/embed/abc123", list);
        Assert.Contains("youtube-nocookie.com/embed/abc123", list);
        Assert.DoesNotContain("youtube.com", list); // the limited "Videos" group is no learning content
        // EU cookie consent before the first video: allowed, otherwise the allowed video could not be watched.
        Assert.Contains("consent.youtube.com", list);
        Assert.True(TimeGroups.IsLearningAddress(Child(), "consent.youtube.com", "https://consent.youtube.com/m?continue=x"));
        var noVideo = Child() with { Groups = [new TimeGroup { Name = "Lernen", Kind = TimeGroupKind.Free, Sites = ["anton.app"] }] };
        Assert.DoesNotContain("consent.youtube.com", TimeGroups.LearningAllowList(noVideo));
        Assert.False(TimeGroups.IsLearningAddress(noVideo, "consent.youtube.com", "https://consent.youtube.com/m"));
        Assert.True(TimeGroups.IsLearningAddress(Child(), "www.youtube.com", "https://www.youtube.com/watch?v=abc123"));
        Assert.False(TimeGroups.IsLearningAddress(Child(), "www.youtube.com", "https://www.youtube.com/watch?v=zzz999"));
        Assert.True(TimeGroups.IsLearningAddress(Child(), "anton.app", "https://anton.app/de/mathe"));
    }
}

public partial class WebFlowTests
{
    [Fact]
    public async Task ParentsPickAidsFromTheInstalledPrograms()
    {
        var (b, _, child, device, api) = await FamilyWithDeviceAsync("aids@test.local");
        var sync = await api.PostAsJsonAsync(ApiRoutes.Sync, new SyncRequest
        {
            InstalledApps =
            [
                new InstalledApp { Process = "GeoGebra", Name = "GeoGebra Classic", Description = "GeoGebra Classic 6" },
                new InstalledApp { Process = "notepad", Name = "Editor", Description = "Notepad" },
                new InstalledApp { Process = "<script>", Name = "x" },
            ],
        }, PaCoJson.Options);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        var edit = await b.GetStringAsync($"/Children/Edit/{child}");
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", TokenRegex().Match(edit).Groups[1].Value), new("Name", "Kim"), new("ExpiryAction", "LockScreen"),
            new("GroupsForm", "true"), new("Groups[0].Name", "Hilfsmittel"), new("Groups[0].Kind", "Free"),
            new("Groups[0].AddApps", "geogebra"), new("Groups[0].AddApps", "notinstalled"),
        };
        for (var i = 0; i < 7; i++) { form.Add(new($"Days[{i}].LimitMinutes", "60")); form.Add(new($"Days[{i}].AllDay", "true")); }
        Assert.Equal(HttpStatusCode.Redirect, (await b.PostAsync($"/Children/Edit/{child}", new FormUrlEncodedContent(form))).StatusCode);

        // The group now offers the installed programs as a pick list (names only as text, never as markup).
        edit = WebUtility.HtmlDecode(await b.GetStringAsync($"/Children/Edit/{child}"));
        Assert.Contains("GeoGebra Classic (geogebra)", edit);
        Assert.DoesNotContain("<option value=\"<script>", edit);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
        var dev = db.Devices.IgnoreQueryFilters().Single(x => x.Id == device);
        var group = (await scope.ServiceProvider.GetRequiredService<PolicyBuilder>().BuildAsync(dev, default)).Children.Single().Groups.Single();
        Assert.Equal(["geogebra"], group.Apps); // only programs that are really installed can be picked
        // Never used yet, but the description comes from the installed program → counts as learning app right away.
        Assert.Equal("GeoGebra Classic 6", group.AppDescriptions["geogebra"]);
        Assert.True(TimeGroups.IsLearningApp(new ChildPolicy { Groups = [group] }, "geogebra", "GeoGebra Classic 6"));
    }

    [Fact]
    public async Task ParentsSetUpLearningModeWithAidsPagesAndVideos()
    {
        var (b, _, child, device, _) = await FamilyWithDeviceAsync("learning@test.local");
        ChildPolicy Policy()
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var dev = db.Devices.IgnoreQueryFilters().Single(x => x.Id == device);
            return scope.ServiceProvider.GetRequiredService<PolicyBuilder>().BuildAsync(dev, default).GetAwaiter().GetResult().Children.Single();
        }

        var edit = await b.GetStringAsync($"/Children/Edit/{child}");
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", TokenRegex().Match(edit).Groups[1].Value), new("Name", "Kim"), new("ExpiryAction", "LockScreen"),
            new("LearningAfterExpiry", "true"), new("LearningOutsideWindow", "true"), new("LearningMinutesPerDay", "45"), new("GroupsForm", "true"),
            new("Groups[0].Name", "Hausaufgaben"), new("Groups[0].Kind", "Free"), new("Groups[0].Apps", "CalculatorApp.exe"),
            new("Groups[0].Sites", "anton.app\nBrüche, einfach erklärt | https://youtu.be/abc123XYZ_-\nhttps://www.example.com/mathe/"),
            new("Groups[1].Name", "TikTok"), new("Groups[1].Kind", "Limit"), new("Groups[1].Minutes", "15"), new("Groups[1].Sites", "https://www.tiktok.com/@someone"),
        };
        for (var i = 0; i < 7; i++) { form.Add(new($"Days[{i}].LimitMinutes", "60")); form.Add(new($"Days[{i}].AllDay", "true")); }
        Assert.Equal(HttpStatusCode.Redirect, (await b.PostAsync($"/Children/Edit/{child}", new FormUrlEncodedContent(form))).StatusCode);

        var p = Policy();
        Assert.True(p.LearningAfterExpiry);
        Assert.True(p.LearningOutsideWindow);
        Assert.Equal(45, p.LearningMinutesPerDay);
        var learn = p.Groups.Single(g => g.Kind == TimeGroupKind.Free);
        Assert.Equal(["calculatorapp"], learn.Apps);
        Assert.Equal(["anton.app"], learn.Sites);
        Assert.Equal(["youtube.com/watch?v=abc123XYZ_-", "example.com/mathe"], learn.Pages);
        Assert.Equal("Brüche, einfach erklärt", learn.PageTitles["youtube.com/watch?v=abc123XYZ_-"]);
        // A limit group stays a whole site – there, pages could not be blocked one by one.
        Assert.Equal(["tiktok.com"], p.Groups.Single(g => g.Kind == TimeGroupKind.Limit).Sites);

        // The form shows the pages again, with their names.
        edit = WebUtility.HtmlDecode(await b.GetStringAsync($"/Children/Edit/{child}"));
        Assert.Contains("Brüche, einfach erklärt | https://www.youtube.com/watch?v=abc123XYZ_-", edit);
        Assert.Contains("Lernmodus", edit);
    }
}
