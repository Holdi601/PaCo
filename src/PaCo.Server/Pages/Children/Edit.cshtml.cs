using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Shared;

namespace PaCo.Server.Pages.Children;

public sealed class DayInput
{
    public bool Unlimited { get; set; }

    [Range(0, 1440, ErrorMessage = "0 bis 1440 Minuten.")]
    public int? LimitMinutes { get; set; }

    public bool AllDay { get; set; }

    public TimeOnly? From { get; set; }

    public TimeOnly? To { get; set; }
}

public sealed class EditModel(PaCoDb db) : PageModel
{
    /// <summary>Preset site lists parents can add with one click.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SitePresets = new Dictionary<string, string[]>
    {
        ["Soziale Netzwerke"] = ["tiktok.com", "instagram.com", "snapchat.com", "facebook.com", "x.com", "twitter.com", "threads.net", "bsky.app"],
        ["Video-Plattformen (außer YouTube)"] = ["twitch.tv", "kick.com", "dailymotion.com", "vimeo.com"],
        ["YouTube komplett"] = ["youtube.com", "youtu.be", "youtube-nocookie.com"],
        ["Online-Spiele im Browser"] = ["poki.com", "crazygames.com", "miniclip.com", "y8.com", "friv.com", "kizi.com"],
        ["Glücksspiel"] = ["bet365.com", "tipico.de", "bwin.de", "interwetten.com", "pokerstars.de", "stake.com", "lottoland.com"],
        ["Anonyme Chats"] = ["omegle.com", "chatroulette.com", "ome.tv", "chathub.cam", "emeraldchat.com"],
    };

    /// <summary>Browsers our policies do not cover – blocking them closes the loophole.</summary>
    public static readonly string[] OtherBrowsers = ["opera", "opera_gx", "brave", "vivaldi", "waterfox", "librewolf", "chromium", "tor", "yandex", "maxthon", "palemoon"];

    public Guid Id { get; set; }

    [BindProperty, Required(ErrorMessage = "Bitte einen Namen eingeben."), StringLength(60)]
    public string Name { get; set; } = "";

    [BindProperty]
    public ExpiryAction ExpiryAction { get; set; }

    [BindProperty]
    public bool Paused { get; set; }

    /// <summary>Index = DayOfWeek (0 = Sunday).</summary>
    [BindProperty]
    public List<DayInput> Days { get; set; } = [];

    // Optional fields must be nullable – otherwise ASP.NET treats them as [Required].
    [BindProperty]
    public string? BlockedApps { get; set; }

    [BindProperty]
    public bool BlockOtherBrowsers { get; set; }

    [BindProperty]
    public string? BlockedSites { get; set; }

    [BindProperty]
    public List<string> AddPresets { get; set; } = [];

    [BindProperty]
    public bool ForceSafeSearch { get; set; }

    [BindProperty]
    public bool YouTubeRestricted { get; set; }

    [BindProperty]
    public bool BlockPrivateBrowsing { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        Load(child);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        Id = id;

        if (Days.Count != 7) ModelState.AddModelError("", "Ungültiger Wochenplan.");
        for (var i = 0; i < Days.Count; i++)
        {
            var d = Days[i];
            if (!d.Unlimited && d.LimitMinutes is null)
                ModelState.AddModelError($"Days[{i}].LimitMinutes", $"{Services.Format.WeekdayNames[i]}: Bitte Minuten angeben oder „unbegrenzt“ wählen.");
            if (!d.AllDay && (d.From is null || d.To is null || d.From >= d.To))
                ModelState.AddModelError($"Days[{i}].From", $"{Services.Format.WeekdayNames[i]}: Die Uhrzeit „von“ muss vor „bis“ liegen.");
        }
        if (!ModelState.IsValid) return Page();

        child.Name = Name.Trim();
        child.ExpiryAction = ExpiryAction;
        child.Paused = Paused;
        child.SetDays(Days.Select(d => new DayRule
        {
            LimitMinutes = d.Unlimited ? null : d.LimitMinutes,
            WindowStart = d.AllDay ? null : d.From,
            WindowEnd = d.AllDay ? null : d.To,
        }).ToArray());

        var apps = SplitLines(BlockedApps).Select(NormalizeApp).Where(a => a.Length > 0).ToList();
        if (BlockOtherBrowsers) apps.AddRange(OtherBrowsers);
        else apps.RemoveAll(a => OtherBrowsers.Contains(a));
        child.SetBlockedApps(apps.Distinct(StringComparer.OrdinalIgnoreCase).Take(200));

        var sites = SplitLines(BlockedSites).Select(NormalizeSite).Where(s => s.Length > 0).ToList();
        foreach (var p in AddPresets.Where(SitePresets.ContainsKey)) sites.AddRange(SitePresets[p]);
        child.SetWeb(new WebFilterPolicy
        {
            BlockedSites = sites.Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToArray(),
            ForceSafeSearch = ForceSafeSearch,
            YouTubeRestricted = YouTubeRestricted,
            BlockPrivateBrowsing = BlockPrivateBrowsing,
        });

        await db.SaveChangesAsync();
        TempData["Message"] = $"Regeln für {child.Name} gespeichert. Online-PCs übernehmen sie innerhalb von 30 Sekunden.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        db.Children.Remove(child);
        await db.SaveChangesAsync();
        TempData["Message"] = $"{child.Name} wurde gelöscht. Zugeordnete Windows-Konten werden nicht mehr überwacht.";
        return RedirectToPage("./Index");
    }

    private void Load(Child c)
    {
        Id = c.Id;
        Name = c.Name;
        ExpiryAction = c.ExpiryAction;
        Paused = c.Paused;
        Days = c.GetDays().Select(d => new DayInput
        {
            Unlimited = d.LimitMinutes is null,
            LimitMinutes = d.LimitMinutes ?? 120,
            AllDay = d.WindowStart is null || d.WindowEnd is null,
            From = d.WindowStart ?? new TimeOnly(7, 0),
            To = d.WindowEnd ?? new TimeOnly(20, 0),
        }).ToList();
        var apps = c.GetBlockedApps();
        BlockOtherBrowsers = OtherBrowsers.All(b => apps.Contains(b, StringComparer.OrdinalIgnoreCase));
        BlockedApps = string.Join("\n", apps.Where(a => !OtherBrowsers.Contains(a, StringComparer.OrdinalIgnoreCase)));
        var web = c.GetWeb();
        BlockedSites = string.Join("\n", web.BlockedSites);
        ForceSafeSearch = web.ForceSafeSearch;
        YouTubeRestricted = web.YouTubeRestricted;
        BlockPrivateBrowsing = web.BlockPrivateBrowsing;
    }

    private static IEnumerable<string> SplitLines(string? s) =>
        (s ?? "").Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"Discord.exe" / "C:\…\Discord.exe" → "discord".</summary>
    internal static string NormalizeApp(string s)
    {
        var name = Path.GetFileName(s.Trim().Trim('"'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ' ').ToArray()).Trim().ToLowerInvariant();
    }

    /// <summary>"https://www.TikTok.com/foo" → "tiktok.com".</summary>
    internal static string NormalizeSite(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (t.Contains("://")) t = t[(t.IndexOf("://", StringComparison.Ordinal) + 3)..];
        t = t.Split('/', '?', '#')[0];
        if (t.StartsWith("www.")) t = t[4..];
        return new string(t.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '*').ToArray());
    }
}
