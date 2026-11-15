using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Shared;
using PaCo.Shared.I18n;

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
    // Preset names are form values (German key) and translated for display.
    // i18n-keys:start server
    private const string PresetSocial = "Soziale Netzwerke";
    private const string PresetVideo = "Video-Plattformen (außer YouTube)";
    private const string PresetYouTube = "YouTube komplett";
    private const string PresetGames = "Online-Spiele im Browser";
    private const string PresetGambling = "Glücksspiel";
    private const string PresetChats = "Anonyme Chats";
    // i18n-keys:end

    /// <summary>Preset site lists parents can add with one click.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SitePresets = new Dictionary<string, string[]>
    {
        [PresetSocial] = ["tiktok.com", "instagram.com", "snapchat.com", "facebook.com", "x.com", "twitter.com", "threads.net", "bsky.app"],
        [PresetVideo] = ["twitch.tv", "kick.com", "dailymotion.com", "vimeo.com"],
        [PresetYouTube] = ["youtube.com", "youtu.be", "youtube-nocookie.com"],
        [PresetGames] = ["poki.com", "crazygames.com", "miniclip.com", "y8.com", "friv.com", "kizi.com"],
        [PresetGambling] = ["bet365.com", "tipico.de", "bwin.de", "interwetten.com", "pokerstars.de", "stake.com", "lottoland.com"],
        [PresetChats] = ["omegle.com", "chatroulette.com", "ome.tv", "chathub.cam", "emeraldchat.com"],
    };

    /// <summary>Browsers our policies do not cover – blocking them closes the loophole.</summary>
    public static readonly string[] OtherBrowsers = ["opera", "opera_gx", "brave", "vivaldi", "waterfox", "librewolf", "chromium", "tor", "yandex", "maxthon", "palemoon"];

    public Guid Id { get; set; }

    [BindProperty, Required(ErrorMessage = "Bitte einen Namen eingeben."), StringLength(60)]
    public string Name { get; set; } = "";

    /// <summary>Language on the child's PC (lock screen, notices, alarm keyword list). Empty = keep the saved one.</summary>
    [BindProperty]
    public string? ChildLanguage { get; set; }

    /// <summary>Choices for <see cref="ChildLanguage"/>, by native name.</summary>
    public static IReadOnlyList<Language> LanguageOptions { get; } =
        Languages.All.OrderBy(l => l.NativeName, StringComparer.InvariantCultureIgnoreCase).ToList();

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

    [BindProperty]
    public bool BlockDevTools { get; set; }

    [BindProperty]
    public bool AlertsEnabled { get; set; }

    [BindProperty]
    public bool ScanWindowText { get; set; }

    [BindProperty]
    public bool ShowHelpToChild { get; set; }

    [BindProperty]
    public List<AlertCategory> AlertCategories { get; set; } = [];

    [BindProperty]
    public string? CustomKeywords { get; set; }

    [BindProperty]
    public bool NotifyChildOnScreenshot { get; set; }

    [BindProperty]
    public bool BlockSystemTools { get; set; }

    [BindProperty]
    public bool BlockStore { get; set; }

    /// <summary>Tool ids (<see cref="Child.Tools"/>) that stay blocked. Only used when <see cref="ToolsForm"/> is posted.</summary>
    [BindProperty]
    public List<string> BlockedTools { get; set; } = [];

    [BindProperty]
    public bool ToolsForm { get; set; }

    /// <summary>Programs the child used in the last 30 days (process, display name) – blocked or allowed with a switch.</summary>
    public IReadOnlyList<(string Process, string Name)> KnownApps { get; private set; } = [];

    [BindProperty]
    public List<string> BlockedKnownApps { get; set; } = [];

    [BindProperty]
    public bool KnownAppsForm { get; set; }

    private static bool IsSpecialApp(string process) =>
        OtherBrowsers.Contains(process, StringComparer.OrdinalIgnoreCase) || Child.SystemTools.Contains(process, StringComparer.OrdinalIgnoreCase)
        || process.Equals(Child.StoreApp, StringComparison.OrdinalIgnoreCase);

    private async Task LoadKnownAppsAsync(Guid childId)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var rows = await db.UsageDays.Where(u => u.ChildId == childId && u.Day >= since).ToListAsync();
        KnownApps = Services.PolicyBuilder.Aggregate(rows).Apps
            .Where(a => !IsSpecialApp(a.Key))
            .OrderByDescending(a => a.Value.Seconds).Take(40)
            .Select(a => (a.Key.ToLowerInvariant(), string.IsNullOrWhiteSpace(a.Value.DisplayName) ? a.Key : a.Value.DisplayName))
            .OrderBy(a => a.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        await LoadKnownAppsAsync(id);
        Load(child);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        Id = id;

        if (Days.Count != 7) ModelState.AddModelError("", Ui.T("Ungültiger Wochenplan."));
        for (var i = 0; i < Days.Count; i++)
        {
            var d = Days[i];
            if (!d.Unlimited && d.LimitMinutes is null)
                ModelState.AddModelError($"Days[{i}].LimitMinutes", Ui.F("{0}: Bitte Minuten angeben oder „unbegrenzt“ wählen.", Services.Format.DayName((DayOfWeek)i)));
            if (!d.AllDay && (d.From is null || d.To is null || d.From >= d.To))
                ModelState.AddModelError($"Days[{i}].From", Ui.F("{0}: Die Uhrzeit „von“ muss vor „bis“ liegen.", Services.Format.DayName((DayOfWeek)i)));
        }
        if (!string.IsNullOrEmpty(ChildLanguage) && !Languages.IsSupported(ChildLanguage))
            ModelState.AddModelError(nameof(ChildLanguage), Ui.T("Bitte eine Sprache aus der Liste wählen."));
        await LoadKnownAppsAsync(id);
        if (!ModelState.IsValid) return Page();

        child.Name = Name.Trim();
        // Saved languages are picked up by the policy each PC fetches on its next sync, like every other rule.
        if (!string.IsNullOrEmpty(ChildLanguage)) child.Language = CanonicalLanguage(ChildLanguage);
        child.ExpiryAction = ExpiryAction;
        child.Paused = Paused;
        child.SetDays(Days.Select(d => new DayRule
        {
            LimitMinutes = d.Unlimited ? null : d.LimitMinutes,
            WindowStart = d.AllDay ? null : d.From,
            WindowEnd = d.AllDay ? null : d.To,
        }).ToArray());

        var apps = SplitLines(BlockedApps).Select(NormalizeApp).Where(a => a.Length > 0).ToList();
        if (KnownAppsForm)
        {
            // Switch list of used programs: only these decide for the listed programs.
            var known = KnownApps.Select(k => k.Process).ToHashSet(StringComparer.OrdinalIgnoreCase);
            apps.AddRange(BlockedKnownApps.Select(NormalizeApp).Where(known.Contains));
        }
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
            // Older forms without the switch keep the saved value.
            BlockDevTools = ToolsForm ? BlockDevTools : child.GetWeb().BlockDevTools,
        });

        child.SetAlerts(new AlertSettings
        {
            Enabled = AlertsEnabled,
            ScanWindowText = ScanWindowText,
            ShowHelpToChild = ShowHelpToChild,
            Categories = AlertCategories.Where(c => c != AlertCategory.Custom).Distinct().ToArray(),
            CustomKeywords = SplitLines(CustomKeywords).Where(k => k.Length is >= 2 and <= 100).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray(),
        });
        child.NotifyChildOnScreenshot = NotifyChildOnScreenshot;
        if (ToolsForm)
        {
            child.BlockSystemTools = true;
            child.SetUnblockedTools(Child.Tools.Select(t => t.Id).Where(id => !BlockedTools.Contains(id)));
        }
        else
        {
            child.BlockSystemTools = BlockSystemTools;
        }
        child.BlockStore = BlockStore;

        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("Regeln für {0} gespeichert. Online-PCs übernehmen sie innerhalb von 30 Sekunden.", child.Name);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return NotFound();
        db.Children.Remove(child);
        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("{0} wurde gelöscht. Zugeordnete Windows-Konten werden nicht mehr überwacht.", child.Name);
        return RedirectToPage("./Index");
    }

    private void Load(Child c)
    {
        Id = c.Id;
        Name = c.Name;
        ChildLanguage = Languages.IsSupported(c.Language) ? CanonicalLanguage(c.Language) : Languages.Source;
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
        var knownSet = KnownApps.Select(k => k.Process).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BlockedKnownApps = apps.Where(knownSet.Contains).ToList();
        BlockedApps = string.Join("\n", apps.Where(a => !OtherBrowsers.Contains(a, StringComparer.OrdinalIgnoreCase) && !knownSet.Contains(a)));
        var web = c.GetWeb();
        BlockedSites = string.Join("\n", web.BlockedSites);
        ForceSafeSearch = web.ForceSafeSearch;
        YouTubeRestricted = web.YouTubeRestricted;
        BlockPrivateBrowsing = web.BlockPrivateBrowsing;
        BlockDevTools = web.BlockDevTools;
        var alerts = c.GetAlerts();
        AlertsEnabled = alerts.Enabled;
        ScanWindowText = alerts.ScanWindowText;
        ShowHelpToChild = alerts.ShowHelpToChild;
        AlertCategories = alerts.Categories.ToList();
        CustomKeywords = string.Join("\n", alerts.CustomKeywords);
        NotifyChildOnScreenshot = c.NotifyChildOnScreenshot;
        BlockSystemTools = c.BlockSystemTools;
        BlockStore = c.BlockStore;
        BlockedTools = c.BlockedToolIds().ToList();
    }

    private static string CanonicalLanguage(string code) =>
        Languages.All.First(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Code;

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
