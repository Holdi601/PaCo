using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaCo.Shared;

/// <summary>
/// Offline-verifiable one-time unlock codes ("Freischaltcodes").
/// Format: 10 digits "MMNNN-CCCCC"
///   MM    = minutes / 5 (00 = unlimited for the rest of the day, 01..96 = 5..480 minutes)
///   NNN   = nonce, unique per child and day (server ensures uniqueness, client refuses reuse)
///   CCCCC = HMAC-SHA256(familyUnlockKey, child | day | MM | NNN) truncated to 5 digits
/// The code is bound to child and day, so it cannot be reused tomorrow or by a sibling. Brute force is prevented
/// by the client's attempt throttling (see <see cref="UnlockThrottle"/>).
/// </summary>
public static class UnlockCodes
{
    public const int MaxMinutes = 480;
    public const int MaxNonce = 999;

    public static IReadOnlyList<int> AllowedMinutes { get; } = [15, 30, 45, 60, 90, 120, 0];

    public static string Generate(ReadOnlySpan<byte> key, Guid childId, DateOnly day, int minutes, int nonce)
    {
        if (minutes < 0 || minutes > MaxMinutes || minutes % 5 != 0)
            throw new ArgumentOutOfRangeException(nameof(minutes), "Minuten müssen 0 (unbegrenzt) oder ein Vielfaches von 5 bis 480 sein.");
        if (nonce < 0 || nonce > MaxNonce)
            throw new ArgumentOutOfRangeException(nameof(nonce));

        var mm = minutes / 5;
        var mac = Mac(key, childId, day, mm, nonce);
        return string.Create(CultureInfo.InvariantCulture, $"{mm:00}{nonce:000}-{mac:00000}");
    }

    public readonly record struct Decoded(int Minutes, int Nonce);

    /// <summary>Validates a code typed by the child. Accepts spaces/dashes. Does NOT check reuse.</summary>
    public static bool TryValidate(ReadOnlySpan<byte> key, Guid childId, DateOnly day, string input, out Decoded decoded)
    {
        decoded = default;
        Span<char> digits = stackalloc char[10];
        var n = 0;
        foreach (var ch in input)
        {
            if (ch is ' ' or '-') continue;
            if (ch is < '0' or > '9' || n == 10) return false;
            digits[n++] = ch;
        }
        if (n != 10) return false;

        var mm = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        var nonce = int.Parse(digits[2..5], CultureInfo.InvariantCulture);
        var mac = int.Parse(digits[5..], CultureInfo.InvariantCulture);
        if (mm * 5 > MaxMinutes) return false;

        var expected = Mac(key, childId, day, mm, nonce);
        // Constant-time compare of the 5 digit MAC.
        Span<byte> a = stackalloc byte[4], b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(a, expected);
        BinaryPrimitives.WriteInt32BigEndian(b, mac);
        if (!CryptographicOperations.FixedTimeEquals(a, b)) return false;

        decoded = new Decoded(mm * 5, nonce);
        return true;
    }

    private static int Mac(ReadOnlySpan<byte> key, Guid childId, DateOnly day, int mm, int nonce)
    {
        var msg = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"paco-unlock-v1|{childId:N}|{FamilyClock.DayKey(day)}|{mm:00}|{nonce:000}"));
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, msg, hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 100_000);
    }
}

/// <summary>
/// Throttles unlock attempts: 5 free attempts, then an exponentially growing wait (1, 2, 4 … max 60 minutes).
/// With 100 000 possible MACs this makes guessing infeasible.
/// </summary>
public sealed class UnlockThrottle
{
    public int FailedAttempts { get; set; }

    public DateTimeOffset? BlockedUntilUtc { get; set; }

    public const int FreeAttempts = 5;

    public TimeSpan? RemainingWait(DateTimeOffset nowUtc) =>
        BlockedUntilUtc is { } until && until > nowUtc ? until - nowUtc : null;

    public void RegisterFailure(DateTimeOffset nowUtc)
    {
        FailedAttempts++;
        if (FailedAttempts >= FreeAttempts)
        {
            var exp = Math.Min(FailedAttempts - FreeAttempts, 6); // 1,2,4,8,16,32,60 min
            var minutes = Math.Min(60, 1 << exp);
            BlockedUntilUtc = nowUtc.AddMinutes(minutes);
        }
    }

    public void RegisterSuccess()
    {
        FailedAttempts = 0;
        BlockedUntilUtc = null;
    }
}
