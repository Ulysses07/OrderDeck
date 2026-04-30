using System.ComponentModel.DataAnnotations;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiveDeck.LicenseServer.Pages.Public;

public class PasswordResetModel : PageModel
{
    private readonly PasswordResetService _service;

    public PasswordResetModel(PasswordResetService service) => _service = service;

    [BindProperty]
    public PasswordResetInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public bool Success { get; set; }

    public sealed class PasswordResetInput
    {
        [Required]
        public Guid Token { get; set; }

        [Required(ErrorMessage = "Şifre gerekli")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "En az 8 karakter olmalı")]
        public string NewPassword { get; set; } = "";

        [Required(ErrorMessage = "Şifre tekrarı gerekli")]
        [Compare(nameof(NewPassword), ErrorMessage = "Şifreler eşleşmiyor")]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet(Guid? token)
    {
        if (token is null || token == Guid.Empty) return BadRequest();
        Input.Token = token.Value;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var result = await _service.CompleteResetAsync(Input.Token, Input.NewPassword, ct);
        switch (result)
        {
            case PasswordResetResult.Success:
                Success = true;
                return Page();
            case PasswordResetResult.PasswordTooShort:
                ErrorMessage = "Şifre en az 8 karakter olmalı.";
                return Page();
            default:
                ErrorMessage = "Bağlantı geçersiz veya süresi dolmuş. Lütfen yeni bir şifre sıfırlama talebi oluşturun.";
                return Page();
        }
    }
}
