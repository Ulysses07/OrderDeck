using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Privacy;
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

    /// <summary>Yayıncının veri sorumlusu bilgileri (KVKK K-3); girilmediyse null.</summary>
    public DataControllerView? DataController { get; private set; }
    [BindProperty] public string? DcLegalName { get; set; }
    [BindProperty] public string? DcPostalAddress { get; set; }
    [BindProperty] public string? DcKvkkEmail { get; set; }
    [BindProperty] public string? DcKepAddress { get; set; }

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

        DataController = await DataControllerProfiles.ReadAsync(_db, id, ct);
        return Page();
    }

    /// <summary>KVKK K-3: yayıncının veri sorumlusu bilgileri (formda ve Shopper'da gösterilir).</summary>
    public async Task<IActionResult> OnPostDataControllerAsync(Guid id, CancellationToken ct)
    {
        if (!await _db.Customers.AnyAsync(c => c.Id == id, ct)) return NotFound();
        if (DataControllerProfiles.Validate(new DataControllerInput(DcLegalName, DcPostalAddress, DcKvkkEmail, DcKepAddress),
                out var valid) is { } error)
        {
            TempData["Error"] = error;
            return RedirectToPage("./Detail", new { id });
        }
        try
        {
            // Yapan: yöneticinin kimliği, kullanıcı adı değil (KVKK Ö-14; Görev 9 kuralı) — sürüm satırı yayıncı silinene kadar
            // durur.
            await DataControllerProfiles.UpsertAsync(_db, id, valid, "admin:" + DataDestruction.ActorOf(User), ct);
        }
        catch (DataControllerConflictException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage("./Detail", new { id });
        }
        await _audit.LogAsync(AuditEvents.CustomerDataControllerUpdate, AuditTargets.Customer, id.ToString(), null, ct);
        TempData["Success"] = "Veri sorumlusu bilgileri kaydedildi.";
        return RedirectToPage("./Detail", new { id });
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
