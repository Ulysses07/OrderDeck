using System.ComponentModel.DataAnnotations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin.Licenses;

public class DetailModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly IAuditService _audit;
    private readonly AdminActionEmailService _adminEmail;

    public DetailModel(LicenseDbContext db, IAuditService audit, AdminActionEmailService adminEmail)
    {
        _db = db;
        _audit = audit;
        _adminEmail = adminEmail;
    }

    public License? License { get; private set; }
    public Customer? Customer { get; private set; }
    public List<Activation> Activations { get; private set; } = new();
    public List<AuditLogEntry> AuditEntries { get; private set; } = new();

    [BindProperty]
    public RevokeInput RevokeForm { get; set; } = new();

    [BindProperty]
    public ExtendInput ExtendForm { get; set; } = new();

    public sealed class RevokeInput
    {
        [StringLength(500)]
        public string Reason { get; set; } = "";
    }

    public sealed class ExtendInput
    {
        [Range(1, 3650)]
        public int AdditionalDays { get; set; } = 30;
    }

    public async Task<IActionResult> OnGetAsync(string key, CancellationToken ct)
    {
        return await LoadAsync(key, ct);
    }

    public async Task<IActionResult> OnPostRevokeAsync(string key, CancellationToken ct)
    {
        var loadResult = await LoadAsync(key, ct);
        if (License is null) return loadResult;
        if (string.IsNullOrWhiteSpace(RevokeForm.Reason))
        {
            ModelState.AddModelError($"{nameof(RevokeForm)}.{nameof(RevokeForm.Reason)}", "Sebep gerekli");
            return Page();
        }

        License.RevokedAt = DateTimeOffset.UtcNow;
        License.RevokeReason = RevokeForm.Reason;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditEvents.LicenseRevoke, AuditTargets.License, key,
            new { reason = RevokeForm.Reason }, ct);
        await _adminEmail.NotifyLicenseRevokedAsync(License!.CustomerId, key, RevokeForm.Reason, ct);

        TempData["Success"] = "Lisans iptal edildi.";
        return RedirectToPage("./Detail", new { key });
    }

    public async Task<IActionResult> OnPostExtendAsync(string key, CancellationToken ct)
    {
        var loadResult = await LoadAsync(key, ct);
        if (License is null) return loadResult;
        if (ExtendForm.AdditionalDays < 1 || ExtendForm.AdditionalDays > 3650)
        {
            ModelState.AddModelError($"{nameof(ExtendForm)}.{nameof(ExtendForm.AdditionalDays)}", "1-3650 arası gün");
            return Page();
        }

        License.ExpiresAt = License.ExpiresAt.AddDays(ExtendForm.AdditionalDays);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditEvents.LicenseExtend, AuditTargets.License, key,
            new { additionalDays = ExtendForm.AdditionalDays }, ct);
        await _adminEmail.NotifyLicenseExtendedAsync(License!.CustomerId, key, License.ExpiresAt, ExtendForm.AdditionalDays, ct);

        TempData["Success"] = $"Lisans {ExtendForm.AdditionalDays} gün uzatıldı.";
        return RedirectToPage("./Detail", new { key });
    }

    private async Task<IActionResult> LoadAsync(string key, CancellationToken ct)
    {
        // Single round-trip via Include — Customer + Activations come along with
        // the License row instead of two follow-up queries. AsNoTracking because
        // POST handlers do their own tracked load before mutating.
        License = await _db.Licenses
            .AsNoTracking()
            .Include(l => l.Customer)
            .Include(l => l.Activations.OrderByDescending(a => a.ActivatedAt))
            .FirstOrDefaultAsync(l => l.LicenseKey == key, ct);
        if (License is null) return NotFound();

        Customer = License.Customer;
        Activations = License.Activations.ToList();

        AuditEntries = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.TargetType == "license" && a.TargetId == key)
            .OrderByDescending(a => a.OccurredAt)
            .Take(20)
            .ToListAsync(ct);

        // POST handlers mutate License.RevokedAt etc., so re-fetch a tracked
        // instance for those code paths. Detail render path keeps the cheap
        // no-tracking copy above.
        if (HttpContext.Request.Method != "GET")
        {
            License = await _db.Licenses.FirstAsync(l => l.LicenseKey == key, ct);
        }

        return Page();
    }
}
