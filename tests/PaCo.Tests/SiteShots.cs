using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;
using PaCo.Shared.I18n;

namespace PaCo.Tests;

/// <summary>
/// Not a real test: renders website pages with made-up demo data as static HTML for the screenshots on the
/// project website (site/). Runs only when the environment variable PACO_SHOTS names an output folder;
/// tools/site/shots.ps1 then photographs the files with headless Edge.
/// </summary>
public partial class WebFlowTests
{
    [Fact]
    public async Task RenderDemoPagesForProjectSite()
    {
        var outDir = Environment.GetEnvironmentVariable("PACO_SHOTS");
        if (string.IsNullOrEmpty(outDir)) return;

        var (b, fam) = await ParentOfNewFamilyAsync("demo@example.org");
        var tz = FamilyClock.Resolve("Europe/Berlin");
        var now = DateTimeOffset.UtcNow;
        var today = FamilyClock.Day(now, tz);
        Guid mia, ben, laptop;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
            var family = db.Families.Single(f => f.Id == fam);
            family.Name = "Sommer";
            var cMia = new Child { FamilyId = fam, Name = "Mia" };
            var cBen = new Child { FamilyId = fam, Name = "Ben", ExpiryAction = ExpiryAction.Logoff };
            cMia.SetDays(Enumerable.Range(0, 7).Select(d => new DayRule
            {
                LimitMinutes = d is 0 or 6 ? 120 : 90, WindowStart = new TimeOnly(8, 0), WindowEnd = new TimeOnly(19, 30),
            }).ToArray());
            cMia.SetBlockedApps(["fortnite"]);
            db.Children.AddRange(cMia, cBen);
            var d1 = new Device { FamilyId = fam, Name = "Mias Laptop", MachineName = "LAPTOP-MIA", ClientVersion = "1.5.0", OsVersion = "Windows 10 22H2", LastSeenUtc = now, TokenHash = Secrets.Sha256Hex(Secrets.DeviceToken()), SecurityCheckedUtc = now.AddHours(-3) };
            var d2 = new Device { FamilyId = fam, Name = "Gaming-PC", MachineName = "BEN-PC", ClientVersion = "1.5.0", OsVersion = "Windows 11 24H2", LastSeenUtc = now.AddHours(-5), TokenHash = Secrets.Sha256Hex(Secrets.DeviceToken()) };
            d1.SecurityJson = PaCoJson.Serialize(new[]
            {
                Finding("child-standard", SecuritySeverity.Ok, "Kinderkonto „{0}“ ist ein Standardkonto.", "mia"),
                Finding("av-defender", SecuritySeverity.Ok, "Virenschutz (Microsoft Defender) ist aktiv und aktuell."),
                Finding("fw-on", SecuritySeverity.Ok, "Windows-Firewall ist eingeschaltet."),
                Finding("smartscreen-ok", SecuritySeverity.Ok, "SmartScreen warnt vor gefährlichen Downloads."),
            });
            d1.LiveJson = PaCoJson.Serialize(new[]
            {
                new ChildLiveStatus { ChildId = cMia.Id, Allowed = true, RemainingSeconds = 37 * 60, UsedTodaySeconds = 53 * 60, Active = true, WindowsUser = "mia", CurrentApp = "Minecraft" },
            });
            d1.ActiveChildrenJson = PaCoJson.Serialize(new[] { cMia.Id });
            d1.LocationMode = LocationMode.OnRequest;
            db.Devices.AddRange(d1, d2);
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = d1.Id, Sid = "S-1-5-21-1-1-1-1001", UserName = "mia", FullName = "Mia", ChildId = cMia.Id });
            db.DeviceAccounts.Add(new DeviceAccount { FamilyId = fam, DeviceId = d2.Id, Sid = "S-1-5-21-2-2-2-1001", UserName = "ben", FullName = "Ben", ChildId = cBen.Id });
            var rnd = new Random(7);
            for (var i = 0; i < 14; i++)
            {
                var day = today.AddDays(-i);
                db.UsageDays.Add(Usage(fam, d1.Id, cMia.Id, day, i == 0 ? 53 : 45 + rnd.Next(55), rnd));
                db.UsageDays.Add(Usage(fam, d2.Id, cBen.Id, day, i == 0 ? 98 : 60 + rnd.Next(70), rnd));
            }
            Event(db, fam, d1.Id, cMia.Id, now.AddMinutes(-50), DeviceEventKind.ServiceStarted, "PaCo-Dienst gestartet (Version {0})", "1.5.0");
            Event(db, fam, d1.Id, cMia.Id, now.AddHours(-20), DeviceEventKind.Locked, "Bildschirmzeit aufgebraucht – Sperrbildschirm");
            Event(db, fam, d1.Id, cMia.Id, now.AddHours(-19.8), DeviceEventKind.Unlocked, "Freischaltcode eingelöst: für {0} Minuten", 30);
            Event(db, fam, d1.Id, cMia.Id, now.AddHours(-26), DeviceEventKind.AppBlocked, "Gesperrtes Programm beendet: {0}", "Fortnite");
            Event(db, fam, d2.Id, cBen.Id, now.AddHours(-6), DeviceEventKind.ClockTampering, "Windows-Uhr wurde um {0} Minuten zurückgestellt – wird ignoriert.", 120);
            db.Alerts.Add(new AlertEntity
            {
                FamilyId = fam, ChildId = cBen.Id, DeviceId = d2.Id, TimeUtc = now.AddHours(-7), Category = AlertCategory.Grooming,
                Keyword = "sag es niemandem", App = "Discord", Source = "Fenstertitel", Snippet = "… das bleibt unter uns, sag es niemandem, ok? …",
            });
            db.Locations.Add(new DeviceLocationEntity
            {
                FamilyId = fam, DeviceId = d1.Id, TimeUtc = now.AddMinutes(-12), Latitude = 52.5163, Longitude = 13.3777,
                AccuracyMeters = 35, Source = "WLAN (BeaconDB)", Ssid = "FRITZ!Box 7590",
            });
            await db.SaveChangesAsync();
            (mia, ben, laptop) = (cMia.Id, cBen.Id, d1.Id);
        }

        string[] langs = ["de", "en"];
        (string Name, string Url)[] pages =
        [
            ("dashboard", "/Dashboard"), ("rules", $"/Children/Edit/{mia}"), ("stats", $"/Children/Stats/{mia}"),
            ("devices", "/Devices"), ("alerts", "/Alerts"), ("location", $"/Devices/Location/{laptop}"),
            ("log", $"/Devices/Log/{laptop}"), ("notifications", "/Notifications"),
        ];
        Directory.CreateDirectory(outDir);
        foreach (var lang in langs)
        {
            if (lang == "en")
            {
                // English screenshots get English demo content.
                using var scope = _f.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PaCoDb>();
                foreach (var a in db.Alerts.IgnoreQueryFilters().Where(a => a.FamilyId == fam))
                {
                    a.Keyword = "don't tell anyone";
                    a.Snippet = "… this stays between us, don't tell anyone ok? …";
                }
                foreach (var d in db.Devices.IgnoreQueryFilters().Where(d => d.FamilyId == fam))
                    d.Name = d.Name == "Mias Laptop" ? "Mia's laptop" : "Gaming PC";
                await db.SaveChangesAsync();
            }
            var page = await b.GetStringAsync("/Account/Manage");
            await PostFormAsync(b, "/Language", page, new() { ["lang"] = lang, ["returnUrl"] = "/Dashboard" });
            foreach (var (name, url) in pages)
                await File.WriteAllTextAsync(Path.Combine(outDir, $"{name}.{lang}.html"), await b.GetStringAsync(url));
        }
        // Login page (anonymous) in both languages.
        foreach (var lang in langs)
        {
            var anon = Browser(lang);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"login.{lang}.html"), await anon.GetStringAsync("/Account/Login"));
        }
        _ = ben;
    }

    private static SecurityFinding Finding(string code, SecuritySeverity sev, string template, params object[] args)
    {
        var t = LocText.Of(template, args);
        return new SecurityFinding(code, sev, t.German) { Template = t.Template, Args = t.Args };
    }

    private static UsageDay Usage(Guid fam, Guid device, Guid child, DateOnly day, int minutes, Random rnd)
    {
        var apps = new Dictionary<string, AppUsage>
        {
            ["minecraft"] = new() { DisplayName = "Minecraft", Seconds = minutes * 60 * 45 / 100 },
            ["chrome"] = new() { DisplayName = "Google Chrome", Seconds = minutes * 60 * 25 / 100 },
            ["winword"] = new() { DisplayName = "Microsoft Word", Seconds = minutes * 60 * 15 / 100 },
            ["spotify"] = new() { DisplayName = "Spotify", Seconds = minutes * 60 * 15 / 100 },
        };
        return new UsageDay { FamilyId = fam, DeviceId = device, ChildId = child, Day = day, RegularSeconds = minutes * 60, AppsJson = PaCoJson.Serialize(apps) };
    }

    private static void Event(PaCoDb db, Guid fam, Guid device, Guid child, DateTimeOffset time, DeviceEventKind kind, string template, params object[] args)
    {
        var t = LocText.Of(template, args);
        db.DeviceEvents.Add(new DeviceEventEntity
        {
            FamilyId = fam, DeviceId = device, ChildId = child, TimeUtc = time, Kind = kind,
            Message = t.German, Template = t.Template, ArgsJson = PaCoJson.Serialize(t.Args),
        });
    }
}
