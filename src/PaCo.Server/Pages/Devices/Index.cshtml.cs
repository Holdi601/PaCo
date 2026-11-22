using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages.Devices;

public sealed class IndexModel(PaCoDb db, IFamilyContext family, ClientPackage client, IOptions<ServerOptions> options, TimeProvider clock) : PageModel
{
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromHours(24);

    public IReadOnlyList<Device> Devices { get; private set; } = [];

    public IReadOnlyList<Child> Children { get; private set; } = [];

    public bool ClientAvailable => client.Available;

    public string? ClientVersion => client.Version;

    public DateTimeOffset Now { get; private set; }

    public string ServerUrl => PublicUrl();

    [TempData]
    public string? ManualCode { get; set; }

    public async Task OnGetAsync()
    {
        Now = clock.GetUtcNow();
        Devices = await db.Devices.Include(d => d.Accounts).OrderBy(d => d.Revoked).ThenBy(d => d.Name).ToListAsync();
        Children = await db.Children.OrderBy(c => c.Name).ToListAsync();
    }

    /// <summary>Downloads PaCo-Setup.exe with server address and a fresh one-time pairing code built in.</summary>
    public async Task<IActionResult> OnPostDownloadAsync()
    {
        if (!client.Available)
        {
            TempData["Error"] = Ui.T("Das Installationsprogramm ist auf dem Server nicht vorhanden.");
            return RedirectToPage();
        }
        var code = await CreatePairingCodeAsync();
        Response.Headers.CacheControl = "no-store";
        return File(client.OpenPersonalised(PublicUrl(), code), "application/octet-stream", "PaCo-Setup.exe");
    }

    /// <summary>Shows a pairing code for manual entry (e.g. when the browser blocks the download).</summary>
    public async Task<IActionResult> OnPostCodeAsync()
    {
        ManualCode = await CreatePairingCodeAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAssignAsync(Guid accountId, Guid? childId)
    {
        var acc = await db.DeviceAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (acc is null) return NotFound();
        if (childId is { } cid && !await db.Children.AnyAsync(c => c.Id == cid)) return NotFound();
        acc.ChildId = childId;
        await db.SaveChangesAsync();
        var childName = childId is { } id ? (await db.Children.FirstAsync(c => c.Id == id)).Name : null;
        TempData["Message"] = childName is null
            ? Ui.F("Konto „{0}“ wird nicht mehr überwacht.", acc.UserName)
            : Ui.F("Konto „{0}“ gehört jetzt zu {1}. Online-PCs übernehmen das innerhalb von 30 Sekunden.", acc.UserName, childName);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid deviceId, string name)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId);
        if (d is null) return NotFound();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = Ui.T("Bitte einen Namen für den PC eingeben.");
            return RedirectToPage();
        }
        d.Name = name.Trim()[..Math.Min(100, name.Trim().Length)];
        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("PC heißt jetzt „{0}“.", d.Name);
        return RedirectToPage();
    }

    /// <summary>After how many days without contact the parents get a push message (0 = never).</summary>
    public async Task<IActionResult> OnPostSilentAsync(Guid deviceId, int days)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && !x.Revoked);
        if (d is null) return NotFound();
        d.SilentAlertDays = Math.Clamp(days, 0, SilentDevices.MaxDays);
        await db.SaveChangesAsync();
        TempData["Message"] = d.SilentAlertDays == 0
            ? Ui.F("{0}: keine Warnung bei fehlendem Kontakt.", d.Name)
            : Ui.F("{0}: Warnung, wenn {1} Tage lang kein Kontakt besteht.", d.Name, d.SilentAlertDays);
        return RedirectToPage();
    }

    /// <summary>Asks the device to install the newest client version at its next contact.</summary>
    public async Task<IActionResult> OnPostUpdateAsync(Guid deviceId)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && !x.Revoked);
        if (d is null) return NotFound();
        d.UpdateRequested = true;
        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("{0} installiert Version {1} beim nächsten Kontakt (online: innerhalb von 30 Sekunden).", d.Name, client.Version);
        return RedirectToPage();
    }

    public bool UpdateAvailable(Device d) => UpdateManifest.IsNewer(client.Version, d.ClientVersion);

    public async Task<IActionResult> OnPostRevokeAsync(Guid deviceId)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId);
        if (d is null) return NotFound();
        d.Revoked = true;
        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("{0} wurde entfernt. Der PC hebt beim nächsten Kontakt alle Sperren auf; PaCo kann dann über „Apps & Features“ deinstalliert werden.", d.Name);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid deviceId)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && x.Revoked);
        if (d is null) return NotFound();
        db.Devices.Remove(d);
        await db.SaveChangesAsync();
        TempData["Message"] = Ui.F("{0} und seine Statistik wurden gelöscht.", d.Name);
        return RedirectToPage();
    }

    private async Task<string> CreatePairingCodeAsync()
    {
        var code = Secrets.PairingCode();
        db.PairingCodes.Add(new PairingCode
        {
            FamilyId = family.FamilyId!.Value,
            CodeHash = Secrets.Sha256Hex(Secrets.NormalizeCode(code)),
            ExpiresUtc = clock.GetUtcNow().Add(PairingCodeLifetime),
            CreatedBy = ParentActions.UserId(User),
        });
        await db.SaveChangesAsync();
        return code;
    }

    private string PublicUrl() =>
        !string.IsNullOrWhiteSpace(options.Value.PublicUrl) ? options.Value.PublicUrl.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
}
