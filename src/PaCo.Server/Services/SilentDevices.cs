using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Server.Services;

/// <summary>
/// A PC that has not contacted the server for longer than the parents allow (default 7 days, per device, 0 = off)
/// triggers one push message: PaCo might have been switched off or removed. When the PC is back, the parents get a
/// second message. A PC that reported a proper uninstall is skipped (that already sent its own push).
/// </summary>
public static class SilentDevices
{
    public const int DefaultDays = 7;
    public const int MaxDays = 365;

    // i18n-keys:start shared
    public const string SilentEvent = "Kein Kontakt zum Server seit {0} – die Eltern wurden benachrichtigt.";
    public const string BackEvent = "Wieder Kontakt zum Server (davor zuletzt: {0}).";
    // i18n-keys:end

    // i18n-keys:start server
    private const string SilentTitle = "PaCo: {0} meldet sich nicht";
    private const string SilentText = "Letzter Kontakt: {1}. Wenn {0} seitdem benutzt wurde, wurde PaCo vielleicht abgeschaltet oder entfernt – bitte nachsehen.";
    private const string BackTitle = "PaCo: {0} meldet sich wieder";
    private const string BackText = "{0} hat wieder Kontakt zum Server (davor zuletzt: {1}).";
    // i18n-keys:end

    /// <summary>Sends the "no contact" push for every PC that has been silent too long (once per silence).</summary>
    public static async Task<int> CheckAsync(PaCoDb db, Notifier notifier, string? baseUrl, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.Devices.IgnoreQueryFilters()
            .Where(d => !d.Revoked && d.UninstalledUtc == null && d.SilentAlertDays > 0 && d.SilentAlertSentUtc == null && d.LastSeenUtc != null)
            .ToListAsync(ct);
        var silent = candidates.Where(d => now - d.LastSeenUtc!.Value >= TimeSpan.FromDays(d.SilentAlertDays)).ToList();
        foreach (var d in silent)
        {
            d.SilentAlertSentUtc = now;
            var fam = await db.Families.IgnoreQueryFilters().SingleAsync(f => f.Id == d.FamilyId, ct);
            var last = d.LastSeenUtc!.Value;
            db.DeviceEvents.Add(Event(d, now, DeviceEventKind.Silent, SilentEvent, Stamp(last, fam)));
            if (fam.NtfyTopic is { } topic)
            {
                var lang = await AlertTexts.PushLanguageAsync(db, d.FamilyId, ct);
                notifier.Enqueue(new PushMessage(topic, Ui.In(lang, SilentTitle, d.Name), Ui.In(lang, SilentText, d.Name, Local(last, fam, lang)),
                    4, baseUrl is null ? null : $"{baseUrl}/Devices", "warning"));
            }
        }
        if (silent.Count > 0) await db.SaveChangesAsync(ct);
        return silent.Count;
    }

    /// <summary>Called at every device contact before LastSeenUtc is updated: reports a PC that is back.</summary>
    public static async Task OnContactAsync(PaCoDb db, Notifier notifier, Device d, string? baseUrl, DateTimeOffset now, CancellationToken ct)
    {
        // Either warning (no contact for days, or on in the network but silent) ends with one "back" message.
        if (d.SilentAlertSentUtc is null && d.LanAlertSentUtc is null) return;
        d.SilentAlertSentUtc = null;
        d.LanAlertSentUtc = null;
        if (d.LastSeenUtc is not { } last) return;
        var fam = await db.Families.IgnoreQueryFilters().SingleAsync(f => f.Id == d.FamilyId, ct);
        db.DeviceEvents.Add(Event(d, now, DeviceEventKind.Info, BackEvent, Stamp(last, fam)));
        if (fam.NtfyTopic is { } topic)
        {
            var lang = await AlertTexts.PushLanguageAsync(db, d.FamilyId, ct);
            notifier.Enqueue(new PushMessage(topic, Ui.In(lang, BackTitle, d.Name), Ui.In(lang, BackText, d.Name, Local(last, fam, lang)),
                3, baseUrl is null ? null : $"{baseUrl}/Devices", "white_check_mark"));
        }
    }

    internal static DeviceEventEntity Event(Device d, DateTimeOffset now, DeviceEventKind kind, string template, string arg) => new()
    {
        FamilyId = d.FamilyId,
        DeviceId = d.Id,
        TimeUtc = now,
        Kind = kind,
        Message = SharedText.German(template, [arg]),
        Template = template,
        ArgsJson = PaCoJson.Serialize(new[] { arg }),
    };

    /// <summary>Language-neutral time in the family's time zone (stored in the device log).</summary>
    internal static string Stamp(DateTimeOffset t, Family fam) =>
        TimeZoneInfo.ConvertTime(t, FamilyClock.Resolve(fam.TimeZoneId)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Date and time in the family's time zone, formatted for the push language.</summary>
    internal static string Local(DateTimeOffset t, Family fam, string lang) =>
        TimeZoneInfo.ConvertTime(t, FamilyClock.Resolve(fam.TimeZoneId)).ToString("g", Languages.Culture(lang));
}
