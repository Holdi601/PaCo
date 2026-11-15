using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PaCo.Server.Data;
using PaCo.Server.Services;
using PaCo.Shared;

namespace PaCo.Server.Pages.Children;

public sealed class IndexModel(PaCoDb db, IFamilyContext family) : PageModel
{
    public IReadOnlyList<Child> Children { get; private set; } = [];

    public ILookup<Guid, DeviceAccount> Accounts { get; private set; } = Enumerable.Empty<DeviceAccount>().ToLookup(a => Guid.Empty);

    [BindProperty, Required(ErrorMessage = "Bitte einen Namen eingeben."), StringLength(60)]
    public string NewName { get; set; } = "";

    public async Task OnGetAsync()
    {
        Children = await db.Children.OrderBy(c => c.Name).ToListAsync();
        Accounts = (await db.DeviceAccounts.Include(a => a.Device).Where(a => a.ChildId != null).ToListAsync()).ToLookup(a => a.ChildId!.Value);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await OnGetAsync();
            return Page();
        }
        var child = new Child { FamilyId = family.FamilyId!.Value, Name = NewName.Trim(), Language = Ui.Lang };
        child.SetDays(Child.DefaultDays());
        child.SetWeb(new WebFilterPolicy());
        db.Children.Add(child);
        await db.SaveChangesAsync();
        TempData["Message"] = $"{child.Name} wurde angelegt. Passe jetzt die Regeln an.";
        return RedirectToPage("./Edit", new { id = child.Id });
    }
}
