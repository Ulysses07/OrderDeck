using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Customers;

public class DetailModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly IAuditService _audit;

    public DetailModel(LicenseDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public Customer? Customer { get; private set; }
    public List<License> Licenses { get; private set; } = new();
    public List<AuditLogEntry> AuditEntries { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();

        Licenses = await _db.Licenses
            .Where(l => l.CustomerId == id)
            .OrderByDescending(l => l.IssuedAt)
            .ToListAsync(ct);

        var licenseKeys = Licenses.Select(l => l.LicenseKey).ToList();
        AuditEntries = await _db.AuditLogs
            .Where(a => (a.TargetType == "customer" && a.TargetId == id.ToString())
                     || (a.TargetType == "license" && a.TargetId != null && licenseKeys.Contains(a.TargetId)))
            .OrderByDescending(a => a.OccurredAt)
            .Take(20)
            .ToListAsync(ct);

        return Page();
    }

    public async Task<IActionResult> OnPostConfirmEmailAsync(Guid id, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return NotFound();
        if (customer.EmailConfirmedAt is null)
        {
            customer.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(AuditEvents.CustomerConfirmEmail, AuditTargets.Customer, id.ToString(), null, ct);
            TempData["Success"] = "Müşteri e-posta adresi doğrulandı.";
        }
        return RedirectToPage("./Detail", new { id });
    }
}
