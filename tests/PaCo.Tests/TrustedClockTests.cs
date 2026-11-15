using PaCo.Shared;

namespace PaCo.Tests;

public class TrustedClockTests
{
    private sealed class Fake
    {
        public DateTimeOffset Raw = new(2026, 11, 14, 14, 0, 0, TimeSpan.Zero);
        public TimeSpan Mono = TimeSpan.FromHours(1);

        public void Run(TimeSpan d) { Raw += d; Mono += d; }
    }

    private static TrustedClock Create(Fake f, DateTimeOffset last = default, double offset = 0, List<PaCo.Shared.I18n.LocText>? notes = null) =>
        new(() => f.Raw, () => f.Mono, last, offset, notes);

    [Fact]
    public void WindowsClockChangesWhileRunningAreIgnoredBackwards()
    {
        var f = new Fake();
        var notes = new List<PaCo.Shared.I18n.LocText>();
        var c = Create(f);
        var t0 = c.Now();
        f.Run(TimeSpan.FromMinutes(10));
        f.Raw -= TimeSpan.FromHours(5); // child (or anyone) sets the clock back
        var t1 = c.Now(notes);
        Assert.Equal(t0 + TimeSpan.FromMinutes(10), t1);
        Assert.Single(notes);
    }

    [Fact]
    public void ForwardJumpIsAcceptedButUnverifiedUntilServerConfirms()
    {
        var f = new Fake();
        var c = Create(f);
        c.Confirm(f.Raw);
        Assert.True(c.Verified);
        f.Raw += TimeSpan.FromDays(1); // clock set forward to get a "new day"
        var t = c.Now();
        Assert.False(c.Verified);
        Assert.Equal(f.Raw, t);
        var diff = c.Confirm(f.Raw - TimeSpan.FromDays(1)); // server knows the truth
        Assert.True(diff < -TimeSpan.FromHours(23));
        Assert.Equal(f.Raw - TimeSpan.FromDays(1), c.Now());
    }

    [Fact]
    public void ForwardThenBackIsUndoneEvenOffline()
    {
        var f = new Fake();
        var notes = new List<PaCo.Shared.I18n.LocText>();
        var c = Create(f);
        c.Confirm(f.Raw);
        var real = f.Raw;
        f.Raw += TimeSpan.FromDays(1); // set forward …
        c.Now(notes);
        f.Run(TimeSpan.FromMinutes(1));
        f.Raw -= TimeSpan.FromDays(1); // … and back (or time sync corrects it)
        var t = c.Now(notes);
        Assert.Equal(real + TimeSpan.FromMinutes(1), t);
        Assert.Empty(notes);
        // A further step back beyond the undone jump is ignored again.
        f.Raw -= TimeSpan.FromHours(3);
        Assert.Equal(real + TimeSpan.FromMinutes(1), c.Now(notes));
        Assert.Single(notes);
    }

    [Fact]
    public void BiosClockSetBackWhileOffCannotGoBeforeLastTrusted()
    {
        var f = new Fake();
        var notes = new List<PaCo.Shared.I18n.LocText>();
        var last = f.Raw + TimeSpan.FromHours(3);
        var c = Create(f, last, 0, notes);
        Assert.Equal(last, c.Now());
        Assert.Single(notes);
    }

    [Fact]
    public void ServerOffsetIsApplied()
    {
        var f = new Fake();
        var c = Create(f, default, 120);
        Assert.Equal(f.Raw.AddSeconds(120), c.Now());
    }

    [Fact]
    public void FutureDayUsageIsMergedIntoRealDay()
    {
        var child = Guid.NewGuid();
        var real = new DateOnly(2026, 11, 14);
        string Key(Guid c, DateOnly d) => $"{c:N}|{d:yyyy-MM-dd}";
        (Guid, DateOnly) Parse(string k) => (Guid.ParseExact(k[..32], "N"), DateOnly.ParseExact(k[33..], "yyyy-MM-dd"));
        var usage = new Dictionary<string, DayUsage>
        {
            [Key(child, real)] = new() { RegularSeconds = 3600 },
            [Key(child, real.AddDays(1))] = new()
            {
                RegularSeconds = 1800,
                Apps = new(StringComparer.OrdinalIgnoreCase) { ["minecraft"] = new AppUsage { DisplayName = "Minecraft", Seconds = 1800 } },
            },
        };
        Assert.Equal(1, TrustedClock.MergeFutureDays(usage, real, Parse, Key));
        Assert.Single(usage);
        Assert.Equal(5400, usage[Key(child, real)].RegularSeconds);
        Assert.Equal(1800, usage[Key(child, real)].Apps["minecraft"].Seconds);
    }
}
