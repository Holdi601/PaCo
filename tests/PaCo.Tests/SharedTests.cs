using System.Security.Cryptography;
using PaCo.Shared;

namespace PaCo.Tests;

public class UnlockCodeTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly Guid Child = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 14);

    [Theory]
    [InlineData(15, 0)]
    [InlineData(480, 999)]
    [InlineData(0, 42)]
    public void Roundtrip(int minutes, int nonce)
    {
        var code = UnlockCodes.Generate(Key, Child, Today, minutes, nonce);
        Assert.Matches(@"^\d{5}-\d{5}$", code);
        Assert.True(UnlockCodes.TryValidate(Key, Child, Today, code, out var d));
        Assert.Equal(minutes, d.Minutes);
        Assert.Equal(nonce, d.Nonce);
        Assert.True(UnlockCodes.TryValidate(Key, Child, Today, " " + code.Replace("-", " ") + " ", out _));
    }

    [Fact]
    public void RejectsOtherDayChildKeyAndTampering()
    {
        var code = UnlockCodes.Generate(Key, Child, Today, 30, 7);
        Assert.False(UnlockCodes.TryValidate(Key, Child, Today.AddDays(1), code, out _));
        Assert.False(UnlockCodes.TryValidate(Key, Guid.NewGuid(), Today, code, out _));
        Assert.False(UnlockCodes.TryValidate(RandomNumberGenerator.GetBytes(32), Child, Today, code, out _));
        // Child tries to raise minutes from 30 (06) to 480 (96) keeping the MAC.
        Assert.False(UnlockCodes.TryValidate(Key, Child, Today, "96" + code[2..], out _));
        Assert.False(UnlockCodes.TryValidate(Key, Child, Today, "abc", out _));
        Assert.False(UnlockCodes.TryValidate(Key, Child, Today, code + "1", out _));
    }

    [Fact]
    public void ThrottleBlocksAfterFiveFailures()
    {
        var t = new UnlockThrottle();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++) t.RegisterFailure(now);
        Assert.Null(t.RemainingWait(now));
        t.RegisterFailure(now);
        Assert.Equal(TimeSpan.FromMinutes(1), t.RemainingWait(now));
        t.RegisterFailure(now);
        Assert.Equal(TimeSpan.FromMinutes(2), t.RemainingWait(now));
        for (var i = 0; i < 10; i++) t.RegisterFailure(now);
        Assert.Equal(TimeSpan.FromMinutes(60), t.RemainingWait(now));
        t.RegisterSuccess();
        Assert.Null(t.RemainingWait(now));
    }
}

public class PolicySigningTests
{
    [Fact]
    public void VerifiesAndRejectsTampering()
    {
        using var key = PolicySigning.CreateKey();
        var pem = PolicySigning.ExportPublicPem(key);
        var child = Guid.NewGuid();
        var doc = new PolicyDocument
        {
            DeviceId = Guid.NewGuid(),
            IssuedUtc = DateTimeOffset.UtcNow,
            Children =
            [
                new ChildPolicy
                {
                    ChildId = child, Name = "Lena",
                    RegularSecondsElsewhere = new Dictionary<DateOnly, long> { [new DateOnly(2026, 11, 14)] = 60 },
                },
            ],
        };
        var env = PolicySigning.Sign(doc, key);
        var back = PolicySigning.Verify<PolicyDocument>(env, pem);
        Assert.Equal("Lena", back.Children[0].Name);
        Assert.Equal(60, back.Children[0].RegularSecondsElsewhere[new DateOnly(2026, 11, 14)]);

        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(env.Payload)).Replace("\"paused\":false", "\"paused\":true");
        var forged = env with { Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) };
        Assert.Throws<PolicySignatureException>(() => PolicySigning.Verify<PolicyDocument>(forged, pem));

        using var other = PolicySigning.CreateKey();
        Assert.Throws<PolicySignatureException>(() => PolicySigning.Verify<PolicyDocument>(PolicySigning.Sign(doc, other), pem));
    }
}

public class TimeRulesTests
{
    private static readonly TimeZoneInfo Tz = FamilyClock.Resolve("Europe/Berlin");

    // Saturday 2026-11-14, local time = UTC+1
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 14, hour - 1, minute, 0, TimeSpan.Zero);

    private static ChildPolicy Policy(int? limit, TimeOnly? from = null, TimeOnly? to = null, params TimeGrant[] grants) => new()
    {
        ChildId = Guid.NewGuid(),
        Days = Enumerable.Repeat(new DayRule { LimitMinutes = limit, WindowStart = from, WindowEnd = to }, 7).ToArray(),
        Grants = grants,
    };

    [Fact]
    public void RegularBudgetRunsOut()
    {
        var p = Policy(60);
        var u = new DayUsage();
        var e = TimeRules.Evaluate(p, u, At(15), Tz);
        Assert.Equal(TimeSource.Regular, e.Source);
        Assert.Equal(3600, e.RemainingSeconds);
        TimeRules.Charge(u, e, 3600);
        e = TimeRules.Evaluate(p, u, At(16), Tz);
        Assert.False(e.Allowed);
        Assert.Equal(BlockReason.LimitReached, e.Reason);
    }

    [Fact]
    public void WindowAndRemainingUntilWindowEnd()
    {
        var p = Policy(120, new TimeOnly(14, 0), new TimeOnly(19, 0));
        var u = new DayUsage();
        Assert.Equal(BlockReason.OutsideWindow, TimeRules.Evaluate(p, u, At(13), Tz).Reason);
        Assert.Equal(1800, TimeRules.Evaluate(p, u, At(18, 30), Tz).RemainingSeconds);
        Assert.Equal(BlockReason.OutsideWindow, TimeRules.Evaluate(p, u, At(19), Tz).Reason);
    }

    [Fact]
    public void GrantUsedOnlyAfterRegularAndAlsoOutsideWindow()
    {
        var day = new DateOnly(2026, 11, 14);
        var grant = new TimeGrant { Id = Guid.NewGuid(), Day = day, Minutes = 30 };
        var p = Policy(60, new TimeOnly(14, 0), new TimeOnly(19, 0), grant);
        var u = new DayUsage();

        var e = TimeRules.Evaluate(p, u, At(15), Tz);
        Assert.Equal(TimeSource.Regular, e.Source);
        Assert.Equal(3600 + 1800, e.RemainingSeconds);
        TimeRules.Charge(u, e, 3600);

        e = TimeRules.Evaluate(p, u, At(20), Tz);
        Assert.Equal(TimeSource.Grant, e.Source);
        TimeRules.Charge(u, e, 1800);
        Assert.Equal(1800, u.GrantSeconds[grant.Id]);
        Assert.False(TimeRules.Evaluate(p, u, At(20, 30), Tz).Allowed);
    }

    [Fact]
    public void ExpiredGrantFromYesterdayIgnoredAndOtherDevicesCount()
    {
        var p = Policy(60, grants: new TimeGrant { Id = Guid.NewGuid(), Day = new DateOnly(2026, 11, 13), Minutes = 60 }) with
        {
            RegularSecondsElsewhere = new Dictionary<DateOnly, long> { [new DateOnly(2026, 11, 14)] = 3600 },
        };
        var e = TimeRules.Evaluate(p, new DayUsage(), At(15), Tz);
        Assert.False(e.Allowed);
    }

    [Fact]
    public void CodesAndParentLockAndPause()
    {
        var p = Policy(0);
        var u = new DayUsage { Codes = [new RedeemedCode { Nonce = 5, Minutes = 15 }] };
        var e = TimeRules.Evaluate(p, u, At(15), Tz);
        Assert.Equal(TimeSource.Code, e.Source);
        Assert.Equal(900, e.RemainingSeconds);
        TimeRules.Charge(u, e, 900);
        Assert.False(TimeRules.Evaluate(p, u, At(15, 15), Tz).Allowed);

        Assert.Equal(BlockReason.LockedByParent, TimeRules.Evaluate(Policy(null) with { LockedByParent = true }, new DayUsage(), At(15), Tz).Reason);
        Assert.Equal(TimeSource.Unrestricted, TimeRules.Evaluate(Policy(0) with { Paused = true }, new DayUsage(), At(15), Tz).Source);
    }

    [Fact]
    public void SetupPayloadRoundtripAfterExeBytes()
    {
        using var ms = new MemoryStream();
        ms.Write(RandomNumberGenerator.GetBytes(5000));
        ms.Write(SetupPayload.Encode(new SetupPayload { ServerUrl = "https://example.org:8790", PairingCode = "ABCD-EFGH" }));
        var p = SetupPayload.TryRead(ms);
        Assert.Equal("ABCD-EFGH", p?.PairingCode);
        using var plain = new MemoryStream(RandomNumberGenerator.GetBytes(100));
        Assert.Null(SetupPayload.TryRead(plain));
    }
}
