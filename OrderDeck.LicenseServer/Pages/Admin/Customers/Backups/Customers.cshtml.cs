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
public sealed class CustomersModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupViewerService _viewer;
    private readonly IAuditService _audit;

    public CustomersModel(LicenseDbContext db, BackupViewerService viewer, IAuditService audit)
    {
        _db = db;
        _viewer = viewer;
        _audit = audit;
    }

    public OrderDeck.LicenseServer.Domain.Customer? Customer { get; private set; }
    public CustomerBackup? Backup { get; private set; }
    public PagedResult<CustomerRow>? Page_ { get; private set; }
    public string? Error { get; private set; }
    public string? Search { get; private set; }
    public int CurrentPage { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id, Guid backupId, int page = 1, string? search = null, CancellationToken ct = default)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();
        Backup = await _db.CustomerBackups.FirstOrDefaultAsync(
            b => b.Id == backupId && b.CustomerId == id, ct);
        if (Backup is null) return NotFound();

        Search = search;
        CurrentPage = Math.Max(1, page);

        try
        {
            await using var session = await _viewer.OpenAsync(backupId, ct);
            Page_ = await session.GetCustomersAsync(CurrentPage, search, ct);
        }
        catch (Exception ex) { Error = $"Yedek açılamadı: {ex.Message}"; }

        await _audit.LogAsync(BackupAuditEvents.BackupAccessed,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { customerId = id, viewType = "customers", page }, ct);
        return Page();
    }
}
