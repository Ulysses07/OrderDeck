using System.ComponentModel.DataAnnotations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin.Licenses;

public class IssueModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly LicenseIssuer _issuer;
    private readonly IAuditService _audit;
    private readonly AdminActionEmailService _adminEmail;

    public IssueModel(LicenseDbContext db, LicenseIssuer issuer, IAuditService audit, AdminActionEmailService adminEmail)
    {
        _db = db;
        _issuer = issuer;
        _audit = audit;
        _adminEmail = adminEmail;
    }

    [BindProperty]
    public IssueInput Input { get; set; } = new();

    public List<Sku> Skus { get; private set; } = new();
    public string? ErrorMessage { get; set; }

    public sealed class IssueInput
    {
        [Required(ErrorMessage = "E-posta gerekli")]
        [EmailAddress(ErrorMessage = "Geçerli e-posta gir")]
        public string CustomerEmail { get; set; } = "";

        [Required(ErrorMessage = "SKU seç")]
        public string SkuCode { get; set; } = "";

        [Range(1, 3650, ErrorMessage = "1-3650 arası gün")]
        public int? DurationDaysOverride { get; set; }

        [Range(1, 100, ErrorMessage = "1-100 arası slot")]
        public int? SlotsOverride { get; set; }
    }

    public async Task OnGetAsync(string? customerEmail, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(customerEmail))
            Input.CustomerEmail = customerEmail;
        Skus = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Skus = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
        if (!ModelState.IsValid) return Page();

        try
        {
            var result = await _issuer.IssueAsync(
                new LicenseIssuer.IssueRequest(Input.CustomerEmail, Input.SkuCode, Input.DurationDaysOverride, Input.SlotsOverride),
                ct);

            await _audit.LogAsync(AuditEvents.LicenseIssue, AuditTargets.License, result.LicenseKey,
                new { customerEmail = Input.CustomerEmail, skuCode = Input.SkuCode, durationDaysOverride = Input.DurationDaysOverride, slotsOverride = Input.SlotsOverride },
                ct);

            var custId = await _db.Customers.Where(c => c.Email == Input.CustomerEmail).Select(c => c.Id).FirstAsync(ct);
            await _adminEmail.NotifyLicenseIssuedAsync(custId, result.LicenseKey, Input.SkuCode, result.ExpiresAt, ct);

            TempData["Success"] = $"Lisans oluşturuldu: {result.LicenseKey}";
            return RedirectToPage("./Detail", new { key = result.LicenseKey });
        }
        catch (LicenseIssuer.IssueException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
