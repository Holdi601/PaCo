using PaCo.Shared;

namespace PaCo.Tests;

public class TimeGroupTests
{
    private static readonly TimeGroup Videos = new() { Name = "Videos", Kind = TimeGroupKind.Limit, MinutesPerDay = 30, Sites = ["youtube.com"], Apps = ["netflix"] };

    private static readonly TimeGroup Learning = new()
    {
        Name = "Lernen", Kind = TimeGroupKind.Free, Apps = ["anton"], Sites = ["anton.app"],
        AppDescriptions = new Dictionary<string, string> { ["anton"] = "ANTON" },
    };

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", "www.youtube.com")]
    [InlineData("youtube.com/watch?v=1", "youtube.com")]
    [InlineData("m.YouTube.com", "m.youtube.com")]
    [InlineData("wie lange darf ich zocken", null)] // a search being typed
    [InlineData("about:blank", null)]
    [InlineData("", null)]
    public void HostFromAddressBar(string text, string? host) => Assert.Equal(host, TimeGroups.HostOf(text));

    [Fact]
    public void SitesMatchSubdomainsButNotLookalikes()
    {
        Assert.Single(TimeGroups.Matching([Videos], "msedge", "Microsoft Edge", "m.youtube.com"));
        Assert.Empty(TimeGroups.Matching([Videos], "msedge", "Microsoft Edge", "notyoutube.com"));
        Assert.Empty(TimeGroups.Matching([Videos], "msedge", "Microsoft Edge", "youtube.com.evil.example"));
    }

    [Fact]
    public void RenamedGameIsNoLearningTimeButLimitsStillApply()
    {
        // Real learning app.
        Assert.Single(TimeGroups.Matching([Learning], "anton", "ANTON", null));
        // A game copied to "anton.exe" keeps its own description → not learning time.
        Assert.Empty(TimeGroups.Matching([Learning], "anton", "Minecraft", null));
        // Limits match by name only – renaming does not escape a limit group either way.
        Assert.Single(TimeGroups.Matching([Videos], "netflix", "Something else", null));
    }

    [Fact]
    public void SecondsLeftCountsOtherPcs()
    {
        var day = new DateOnly(2026, 11, 22);
        var usage = new DayUsage();
        usage.Groups[Videos.Id] = 600;
        var elsewhere = new Dictionary<Guid, Dictionary<DateOnly, long>> { [Videos.Id] = new() { [day] = 900 } };
        Assert.Equal(30 * 60 - 600 - 900, TimeGroups.SecondsLeft(Videos, usage, elsewhere, day));
        usage.Groups[Videos.Id] = 5000;
        Assert.Equal(0, TimeGroups.SecondsLeft(Videos, usage, elsewhere, day));
        Assert.Null(TimeGroups.SecondsLeft(Learning, usage, elsewhere, day)); // learning time has no limit
    }
}
