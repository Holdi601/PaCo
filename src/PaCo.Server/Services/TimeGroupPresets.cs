using PaCo.Shared;

namespace PaCo.Server.Services;

/// <summary>Ready-made groups parents can add with one click and then adjust (name, minutes, apps, sites).</summary>
public static class TimeGroupPresets
{
    // Names are German keys, translated into the parent's language when the group is created.
    // i18n-keys:start server
    public const string Games = "Spiele";
    public const string Videos = "Videos & Streaming";
    public const string Social = "Soziale Netzwerke & Chats";
    public const string Learning = "Lernen (zählt nicht als PC-Zeit)";
    public const string Custom = "Eigene Gruppe";
    // i18n-keys:end

    public static readonly IReadOnlyList<(string Id, string Name, TimeGroupKind Kind, int? Minutes, string[] Apps, string[] Sites)> All =
    [
        ("games", Games, TimeGroupKind.Limit, 60,
            ["minecraft.windows", "robloxplayerbeta", "fortniteclient-win64-shipping"],
            ["roblox.com", "poki.com", "crazygames.com", "miniclip.com", "friv.com", "y8.com"]),
        ("videos", Videos, TimeGroupKind.Limit, 60,
            [],
            ["youtube.com", "youtu.be", "twitch.tv", "netflix.com", "disneyplus.com", "primevideo.com", "joyn.de"]),
        ("social", Social, TimeGroupKind.Limit, 30,
            ["discord", "whatsapp", "telegram"],
            ["tiktok.com", "instagram.com", "snapchat.com", "discord.com", "web.whatsapp.com", "facebook.com", "x.com"]),
        ("learning", Learning, TimeGroupKind.Free, null,
            [],
            ["anton.app", "khanacademy.org", "scratch.mit.edu", "duolingo.com", "sofatutor.com", "bettermarks.com", "learningapps.org", "code.org", "wikipedia.org"]),
        ("custom", Custom, TimeGroupKind.Limit, 30, [], []),
    ];

    public static TimeGroup? Create(string presetId)
    {
        var p = All.FirstOrDefault(x => x.Id == presetId);
        if (p.Id is null) return null;
        return new TimeGroup { Name = Ui.T(p.Name), Kind = p.Kind, MinutesPerDay = p.Minutes, Apps = p.Apps, Sites = p.Sites };
    }
}
