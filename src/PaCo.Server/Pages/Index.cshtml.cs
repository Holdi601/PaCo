using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PaCo.Server.Services;

namespace PaCo.Server.Pages;

/// <summary>Entry point: sends everyone to the right place.</summary>
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated != true) return RedirectToPage("/Account/Login");
        if (User.HasClaim(c => c.Type == PaCoClaims.Family)) return RedirectToPage("/Dashboard");
        if (User.IsInRole(PaCoRoles.SuperAdmin)) return RedirectToPage("/Admin/Index");
        return RedirectToPage("/Account/AccessDenied");
    }
}
