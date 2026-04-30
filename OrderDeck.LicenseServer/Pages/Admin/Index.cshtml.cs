using LiveDeck.LicenseServer.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;

    public IndexModel(LicenseDbContext db) => _db = db;

    public int TotalCustomers { get; private set; }
    public int ActiveLicenses { get; private set; }
    public int ExpiredOrRevokedLicenses { get; private set; }
    public int ActiveActivations { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        TotalCustomers = await _db.Customers.CountAsync(ct);
        ActiveLicenses = await _db.Licenses.CountAsync(l => l.RevokedAt == null && l.ExpiresAt > now, ct);
        ExpiredOrRevokedLicenses = await _db.Licenses.CountAsync(l => l.RevokedAt != null || l.ExpiresAt <= now, ct);
        ActiveActivations = await _db.Activations.CountAsync(a => a.DeactivatedAt == null, ct);
    }
}
