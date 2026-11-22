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

public sealed class GroupInput
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public TimeGroupKind Kind { get; set; }

    [Range(1, 1440, ErrorMessage = "1 bis 1440 Minuten.")]
    public int? Minutes { get; set; }

    public string? Apps { get; set; }

    public string? Sites { get; set; }

    /// <summary>Programs picked from the list of installed programs (added to <see cref="Apps"/>).</summary>
    public List<string> AddApps { get; set; } = [];

    public bool Remove { get; set; }
}

public sealed class EditModel(PaCoDb db) : PageModel
{
    /// <summary>Per-app/site limits and learning time. Only used when <see cref="GroupsForm"/> is posted.</summary>
    [BindProperty]
    public List<GroupInput> Groups { get; set; } = [];

    [BindProperty]
    public bool GroupsForm { get; set; }

    /// <summary>Set by the "add group" buttons (preset id) – saves the form and adds the group.</summary>
    [BindProperty]
    public string? AddGroupPreset { get; set; }

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
    public bool LearningAfterExpiry { get; set; }

    [BindProperty]
    public bool LearningOutsideWindow { get; set; }

    /// <summary>Learning time per day (empty = unlimited).</summary>
    [BindProperty]
    [Range(1, 1440, ErrorMessage = "1 bis 1440 Minuten.")]
    public int? LearningMinutesPerDay { get; set; }

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

    [BindProperty]
    public bool TimePerDevice { get; set; }

    /// <summary>PCs of this child (id, name) – the time limit can be switched off per PC.</summary>
    public IReadOnlyList<(Guid Id, string Name)> ChildDevices { get; private set; } = [];

    /// <summary>PCs where the time limit applies. Only used when <see cref="TimeDevicesForm"/> is posted.</summary>
    [BindProperty]
    public List<Guid> TimeLimitDevices { get; set; } = [];

    [BindProperty]
    public bool TimeDevicesForm { get; set; }

    /// <summary>Tool ids (<see cref="Child.Tools"/>) that stay blocked. Only used when <see cref="ToolsForm"/> is posted.</summary>
    [BindProperty]
    public List<string> BlockedTools { get; set; } = [];

    [BindProperty]
    public bool ToolsForm { get; set; }

    /// <summary>Programs the child used in the last 30 days (process, display name) – blocked or allowed with a switch.</summary>
    public IReadOnlyList<(string Process, string Name)> KnownApps { get; private set; } = [];

    /// <summary>Programs installed on the child's PCs (Start menu) – pick list for learning apps and aids.</summary>
    public IReadOnlyList<InstalledApp> InstalledApps { get; private set; } = [];

    [BindProperty]
    public List<string> BlockedKnownApps { get; set; } = [];

    [BindProperty]
    public bool KnownAppsForm { get; set; }

    private static bool IsSpecialApp(string process) =>
        OtherBrowsers.Contains(process, StringComparer.OrdinalIgnoreCase) || Child.SystemTools.Contains(process, StringComparer.OrdinalIgnoreCase)
        || process.Equals(Child.StoreApp, StringComparison.OrdinalIgnoreCase);

    private async Task LoadKnownAppsAsync(Guid childId)
    {
        var deviceIds = await db.DeviceAccounts.Where(a => a.ChildId == childId).Select(a => a.DeviceId).Distinct().ToListAsync();
        ChildDevices = (await db.Devices.Where(d => deviceIds.Contains(d.Id) && !d.Revoked).ToListAsync())
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => (d.Id, d.Name)).ToList();

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var rows = await db.UsageDays.Where(u => u.ChildId == childId && u.Day >= since).ToListAsync();
        KnownApps = Services.PolicyBuilder.Aggregate(rows).Apps
            .Where(a => !IsSpecialApp(a.Key))
            .OrderByDescending(a => a.Value.Seconds).Take(40)
            .Select(a => (a.Key.ToLowerInvariant(), string.IsNullOrWhiteSpace(a.Value.DisplayName) ? a.Key : a.Value.DisplayName))
            .OrderBy(a => a.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
        InstalledApps = (await db.Devices.Where(d => deviceIds.Contains(d.Id) && !d.Revoked).Select(d => d.InstalledAppsJson).ToListAsync())
            .SelectMany(j => PaCoJson.Deserialize<InstalledApp[]>(string.IsNullOrEmpty(j) ? "[]" : j))
            .DistinctBy(a => a.Process, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
        child.LearningAfterExpiry = LearningAfterExpiry;
        child.LearningOutsideWindow = LearningAfterExpiry && LearningOutsideWindow;
        child.LearningMinutesPerDay = LearningMinutesPerDay;
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
        if (GroupsForm)
        {
            var old = child.GetTimeGroups().ToDictionary(g => g.Id);
            var names = KnownApps.ToDictionary(k => k.Process, k => k.Name, StringComparer.OrdinalIgnoreCase);
            var installed = InstalledApps.ToDictionary(a => a.Process, a => a, StringComparer.OrdinalIgnoreCase);
            var groups = new List<TimeGroup>();
            foreach (var g in Groups.Where(g => !g.Remove))
            {
                var groupApps = SplitLines(g.Apps).Select(NormalizeApp)
                    .Concat(g.AddApps.Select(NormalizeApp).Where(installed.ContainsKey))
                    .Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
                // Remember the program description for learning apps, so a renamed game does not count as learning time.
                var previous = old.TryGetValue(g.Id, out var o) ? o.AppDescriptions : new Dictionary<string, string>();
                // Description: as seen before, else from the usage statistics, else from the installed program itself.
                var descriptions = groupApps.Select(a => (a, d: previous.GetValueOrDefault(a) ?? names.GetValueOrDefault(a) ?? installed.GetValueOrDefault(a)?.Description))
                    .Where(x => !string.IsNullOrEmpty(x.d))
                    .ToDictionary(x => x.a, x => x.d!, StringComparer.OrdinalIgnoreCase);
                var (groupSites, groupPages) = ParseSites(g.Sites, g.Kind == TimeGroupKind.Free);
                groups.Add(new TimeGroup
                {
                    Id = g.Id == Guid.Empty ? Guid.NewGuid() : g.Id,
                    Name = string.IsNullOrWhiteSpace(g.Name) ? Ui.T("Eigene Gruppe") : g.Name.Trim()[..Math.Min(60, g.Name.Trim().Length)],
                    Kind = g.Kind == TimeGroupKind.Free ? TimeGroupKind.Free : TimeGroupKind.Limit,
                    MinutesPerDay = g.Kind == TimeGroupKind.Free ? null : g.Minutes,
                    Apps = groupApps,
                    Sites = groupSites,
                    Pages = groupPages.Keys.ToList(),
                    PageTitles = groupPages.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase),
                    AppDescriptions = descriptions,
                });
            }
            if (AddGroupPreset is not null && Services.TimeGroupPresets.Create(AddGroupPreset) is { } added) groups.Add(added);
            child.SetTimeGroups(groups);
        }
        if (TimeDevicesForm)
        {
            child.TimePerDevice = TimePerDevice;
            child.SetNoTimeLimitDevices(ChildDevices.Select(d => d.Id).Where(id => !TimeLimitDevices.Contains(id)));
        }

        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("Regeln für {0} gespeichert. Online-PCs übernehmen sie innerhalb von 30 Sekunden.", child.Name);
        return AddGroupPreset is null ? RedirectToPage(new { id }) : Redirect(Url.Page("./Edit", new { id }) + "#sec-groups");
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
        LearningAfterExpiry = c.LearningAfterExpiry;
        LearningOutsideWindow = c.LearningOutsideWindow;
        LearningMinutesPerDay = c.LearningMinutesPerDay;
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
        TimePerDevice = c.TimePerDevice;
        Groups = c.GetTimeGroups().Select(g => new GroupInput
        {
            Id = g.Id, Name = g.Name, Kind = g.Kind, Minutes = g.MinutesPerDay,
            Apps = string.Join("\n", g.Apps), Sites = string.Join("\n", g.Sites.Concat(g.Pages.Select(p =>
                g.PageTitles.TryGetValue(p, out var title) ? $"{title} | {TimeGroups.PageUrl(p)}" : TimeGroups.PageUrl(p)))),
        }).ToList();
        TimeLimitDevices = ChildDevices.Select(d => d.Id).Where(c.TimeLimitOn).ToList();
    }

    private static string CanonicalLanguage(string code) =>
        Languages.All.First(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Code;

    /// <summary>
    /// Lines of a group's "Webseiten" field: a whole site ("youtube.com") or – for learning groups – a single page or
    /// video ("https://youtu.be/abc", optionally "Brüche erklärt | https://…"). Lines are split at line breaks only.
    /// </summary>
    internal static (List<string> Sites, Dictionary<string, string?> Pages) ParseSites(string? text, bool allowPages)
    {
        var sites = new List<string>();
        var pages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bar = raw.LastIndexOf('|');
            var title = bar > 0 ? raw[..bar].Trim() : null;
            var address = bar > 0 ? raw[(bar + 1)..].Trim() : raw;
            if (allowPages && TimeGroups.NormalizePage(address) is { } page)
            {
                if (pages.Count < 200) pages[page] = string.IsNullOrWhiteSpace(title) ? null : title[..Math.Min(80, title.Length)];
                continue;
            }
            var site = NormalizeSite(address);
            if (site.Length > 0 && !sites.Contains(site, StringComparer.OrdinalIgnoreCase) && sites.Count < 200) sites.Add(site);
        }
        return (sites, pages);
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
