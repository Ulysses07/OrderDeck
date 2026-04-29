using System.Security.Claims;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiveDeck.LicenseServer.Pages.Admin;

public class LogoutModel : PageModel
{
    private readonly IAuditService _audit;

    public LogoutModel(IAuditService audit) => _audit = audit;

    public IActionResult OnGet() => RedirectToPage("/Admin/Login");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value;
        var username = User.FindFirst("username")?.Value;
        if (Guid.TryParse(sub, out var adminId) && !string.IsNullOrEmpty(username))
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _audit.LogLogoutAsync(adminId, username, ip, ct);
        }
        await HttpContext.SignOutAsync("AdminCookie");
        return RedirectToPage("/Admin/Login");
    }
}
