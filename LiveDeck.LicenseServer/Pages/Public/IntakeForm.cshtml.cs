using System.ComponentModel.DataAnnotations;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace LiveDeck.LicenseServer.Pages.Public;

public class IntakeFormModel : PageModel
{
    private readonly IntakeFormService _service;
    private readonly WhatsAppLinkBuilder _linkBuilder;
    private readonly ILogger<IntakeFormModel> _log;

    public IntakeFormModel(
        IntakeFormService service,
        WhatsAppLinkBuilder linkBuilder,
        ILogger<IntakeFormModel> log)
    {
        _service = service;
        _linkBuilder = linkBuilder;
        _log = log;
    }

    [BindProperty(SupportsGet = true)]
    public string Slug { get; set; } = "";

    [BindProperty]
    public IntakeFormInput Input { get; set; } = new();

    public IntakeFormConfig? Config { get; private set; }

    public sealed class IntakeFormInput
    {
        [Required(ErrorMessage = "Kullanıcı adı gerekli")]
        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Ad Soyad gerekli")]
        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string FullName { get; set; } = "";

        [Required(ErrorMessage = "Adres gerekli")]
        [StringLength(500, ErrorMessage = "En fazla 500 karakter")]
        public string Address { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);
        return Page();
    }

    [EnableRateLimiting("intake-form-submit")]
    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken ct)
    {
        // Honeypot — bot doldurursa silent 200, persist YOK, redirect YOK
        if (!string.IsNullOrEmpty(Request.Form["website"]))
        {
            _log.LogInformation("Honeypot triggered for slug {Slug}", Slug);
            Config = await _service.GetActiveBySlugAsync(Slug, ct);
            if (Config is null) return StatusCode(StatusCodes.Status410Gone);
            return Page();
        }

        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);

        if (!ModelState.IsValid) return Page();

        await _service.SaveSubmissionAsync(
            Config.Id,
            Input.Username.Trim(),
            Input.FullName.Trim(),
            Input.Address.Trim(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct);

        var url = _linkBuilder.Build(
            Config.WhatsAppPhone,
            Input.Username.Trim(),
            Input.FullName.Trim(),
            Input.Address.Trim());
        return Redirect(url);
    }
}
