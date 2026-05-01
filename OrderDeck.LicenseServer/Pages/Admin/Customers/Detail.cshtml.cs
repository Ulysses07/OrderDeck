using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers;

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
        // Customer + Licenses in a single round-trip. AsNoTracking — render-only path.
        Customer = await _db.Customers
            .AsNoTracking()
            .Include(c => c.Licenses.OrderByDescending(l => l.IssuedAt))
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();

        Licenses = Customer.Licenses.ToList();

        var licenseKeys = Licenses.Select(l => l.LicenseKey).ToList();
        AuditEntries = await _db.AuditLogs
            .AsNoTracking()
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
