using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin.Activations;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly ActivationManager _activations;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, ActivationManager activations, IAuditService audit)
    {
        _db = db;
        _activations = activations;
        _audit = audit;
    }

    public License? License { get; private set; }
    public List<Activation> Items { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string licenseKey, CancellationToken ct)
    {
        return await LoadAsync(licenseKey, ct);
    }

    public async Task<IActionResult> OnPostDeactivateAsync(string licenseKey, Guid id, CancellationToken ct)
    {
        var activation = await _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (activation is null) return NotFound();

        var ok = await _activations.ForceDeactivateAsync(id, ct);
        if (ok)
        {
            await _audit.LogAsync(AuditEvents.ActivationForceDeactivate, AuditTargets.Activation, id.ToString(),
                new { hardwareFingerprint = activation.HardwareFingerprint, licenseKey = activation.License.LicenseKey }, ct);
            TempData["Success"] = "Aktivasyon iptal edildi.";
        }
        return RedirectToPage("./Index", new { licenseKey });
    }

    private async Task<IActionResult> LoadAsync(string licenseKey, CancellationToken ct)
    {
        // Render-only path; the deactivate handler does its own tracked load above.
        License = await _db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.LicenseKey == licenseKey, ct);
        if (License is null) return NotFound();
        Items = await _db.Activations
            .AsNoTracking()
            .Where(a => a.LicenseId == License.Id)
            .OrderByDescending(a => a.ActivatedAt)
            .ToListAsync(ct);
        return Page();
    }
}
