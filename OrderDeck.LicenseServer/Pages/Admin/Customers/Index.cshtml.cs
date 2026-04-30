using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers;

public class IndexModel : PageModel
{
    private const int PageSize = 25;
    private readonly LicenseDbContext _db;

    public IndexModel(LicenseDbContext db) => _db = db;

    public List<Customer> Customers { get; private set; } = new();
    public string? Search { get; private set; }
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync(string? search, int page, CancellationToken ct)
    {
        Search = search;
        CurrentPage = page < 1 ? 1 : page;

        var query = _db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.Email.Contains(search));

        var total = await query.CountAsync(ct);
        TotalPages = (int)Math.Ceiling(total / (double)PageSize);

        Customers = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}
