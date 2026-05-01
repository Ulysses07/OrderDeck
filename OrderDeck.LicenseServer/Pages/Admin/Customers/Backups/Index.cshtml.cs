using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, BackupStorageService storage, IAuditService audit)
    {
        _db = db;
        _storage = storage;
        _audit = audit;
    }

    public Customer? Customer { get; private set; }
    public List<CustomerBackup> Backups { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();

        Backups = await _db.CustomerBackups
            .Where(b => b.CustomerId == id)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, Guid backupId, CancellationToken ct)
    {
        var b = await _db.CustomerBackups.FirstOrDefaultAsync(
            x => x.Id == backupId && x.CustomerId == id, ct);
        if (b is null) return NotFound();

        _storage.DeleteBlob(b.BlobPath);
        _db.CustomerBackups.Remove(b);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(BackupAuditEvents.BackupDeleted,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { reason = "admin", customerId = id }, ct);

        return RedirectToPage("Index", new { id });
    }
}
