using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin;

[EnableRateLimiting("admin-login")]
public class LoginModel : PageModel
{
    private readonly AdminLoginService _login;
    private readonly IAuditService _audit;

    public LoginModel(AdminLoginService login, IAuditService audit)
    {
        _login = login;
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

        var result = await _login.AuthenticateAsync(Input.Username, Input.Password, ct);
        if (result.Outcome == AdminLoginService.Outcome.LockedOut)
        {
            // Kilit sebebi açıkça söyleniyor: bu tek operatörlü bir panel ve
            // "neden giremiyorum" sorusu destek çağrısına dönüşüyor. Kullanıcı
            // adının varlığını ele veriyor, ama o bilgi zaten yanıt süresinden
            // sızıyor (bilinmeyen ad Argon2 çalıştırmadan hemen dönüyor).
            var minutes = Math.Max(1, (int)Math.Ceiling(
                (result.LockedUntil!.Value - DateTimeOffset.UtcNow).TotalMinutes));
            ErrorMessage = $"Çok fazla hatalı deneme. {minutes} dakika sonra tekrar deneyin.";
            return Page();
        }

        if (!result.IsSuccess)
        {
            ErrorMessage = "Geçersiz kullanıcı adı veya şifre.";
            return Page();
        }

        var admin = result.Admin!;
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
