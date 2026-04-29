using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Skus;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    public IndexModel(LicenseDbContext db) => _db = db;

    public List<Sku> Items { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Items = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
    }
}
