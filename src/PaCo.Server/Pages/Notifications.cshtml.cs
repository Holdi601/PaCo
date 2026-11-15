using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PaCo.Server.Data;
using PaCo.Server.Services;
using QRCoder;

namespace PaCo.Server.Pages;

/// <summary>Explains and sets up push notifications via ntfy for the parents of a family.</summary>
public sealed class NotificationsModel(PaCoDb db, ParentActions actions, Notifier notifier) : PageModel
{
    public bool Configured => notifier.Configured;

    public string ServerUrl { get; private set; } = "";

    public string Host { get; private set; } = "";

    public string Topic { get; private set; } = "";

    public string DeepLink { get; private set; } = "";

    public string QrDataUri { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var fam = await actions.GetFamilyAsync();
        if (fam.NtfyTopic is null)
        {
            fam.NtfyTopic = Notifier.NewTopic();
            await db.SaveChangesAsync();
        }
        Topic = fam.NtfyTopic;
        ServerUrl = notifier.PublicUrl ?? "";
        Host = ServerUrl.Replace("https://", "").Replace("http://", "");
        DeepLink = $"ntfy://{Host}/{Topic}";
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(DeepLink, QRCodeGenerator.ECCLevel.M);
        QrDataUri = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        var fam = await actions.GetFamilyAsync(ct);
        if (fam.NtfyTopic is null) return RedirectToPage();
        var (ok, msg) = await notifier.SendNowAsync(new PushMessage(fam.NtfyTopic, Ui.T("PaCo-Test ✅"),
            Ui.T("Wenn du das liest, kommen PaCo-Alarme auf diesem Handy an."), 3, null, "white_check_mark"), ct);
        TempData[ok ? "Message" : "Error"] = msg;
        return RedirectToPage();
    }

    /// <summary>New secret topic (e.g. if the old one was shared by mistake). All parents must subscribe again.</summary>
    public async Task<IActionResult> OnPostRotateAsync(CancellationToken ct)
    {
        var fam = await actions.GetFamilyAsync(ct);
        fam.NtfyTopic = Notifier.NewTopic();
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Neues Thema erzeugt. Bitte auf allen Eltern-Handys das alte Thema löschen und das neue abonnieren.";
        return RedirectToPage();
    }
}
