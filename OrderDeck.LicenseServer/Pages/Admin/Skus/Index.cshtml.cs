using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin.Skus;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    public IndexModel(LicenseDbContext db) => _db = db;

    public List<Sku> Items { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Items = await _db.Skus.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct);
    }
}
