using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly IAuditService _audit;

    public LoginModel(LicenseDbContext db, PasswordHasher hasher, IAuditService audit)
    {
        _db = db;
        _hasher = hasher;
        _audit = audit;
    }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Kullanıcı adı gerekli")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Şifre gerekli")]
        public string Password { get; set; } = "";
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var admin = await _db.AdminUsers.FirstOrDefaultAsync(a => a.Username == Input.Username, ct);
        if (admin is null || !_hasher.Verify(admin.PasswordHash, Input.Password))
        {
            ErrorMessage = "Geçersiz kullanıcı adı veya şifre.";
            return Page();
        }

        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var claims = new[]
        {
            new Claim("sub", admin.Id.ToString()),
            new Claim("username", admin.Username)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync("AdminCookie", principal);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _audit.LogLoginAsync(admin.Id, admin.Username, ip, ct);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return LocalRedirect(ReturnUrl);
        return Redirect("/admin/");
    }
}
