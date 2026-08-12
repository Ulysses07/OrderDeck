using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Kategori ağacı (Faz 1a). Sınırsız derinlik; ürün ağacın herhangi bir
/// seviyesine bağlanabilir.
///
/// Yol id tabanlı tutulduğu için alt ağaç sorgusu tek <c>StartsWith</c>;
/// recursive CTE yok.
/// </summary>
[ApiController]
[Route("api/panel/categories")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[AllowStockStaff]
public sealed class PanelCategoriesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelCategoriesController(LicenseDbContext db) => _db = db;

    public sealed record CreateRequest(string Name, Guid? ParentCategoryId, int SortOrder);
    public sealed record UpdateRequest(string Name, int SortOrder, bool IsActive);
    public sealed record MoveRequest(Guid? ParentCategoryId);

    public sealed record CategoryDto(
        Guid Id,
        Guid? ParentCategoryId,
        string Name,
        string Path,
        int Depth,
        int SortOrder,
        bool IsActive);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var rows = await _db.Categories
            .Where(c => c.LicenseId == licenseId.Value)
            .OrderBy(c => c.Path)
            .ToListAsync(ct);

        return Ok(rows.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-name",
                detail: "Kategori adı boş olamaz.", statusCode: 400);

        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        string? parentPath = null;
        if (req.ParentCategoryId is not null)
        {
            parentPath = await _db.Categories
                .Where(c => c.Id == req.ParentCategoryId.Value && c.LicenseId == licenseId.Value)
                .Select(c => c.Path)
                .FirstOrDefaultAsync(ct);

            if (parentPath is null)
                return Problem(title: "parent-not-found",
                    detail: "Üst kategori bulunamadı.", statusCode: 400);
        }

        var now = DateTimeOffset.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ParentCategoryId = req.ParentCategoryId,
            Name = req.Name.Trim(),
            SortOrder = req.SortOrder,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        category.Path = CategoryPathService.BuildPath(parentPath, category.Id);

        _db.Categories.Add(category);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/panel/categories/{category.Id}", ToDto(category));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-name",
                detail: "Kategori adı boş olamaz.", statusCode: 400);

        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var category = await FindAsync(id, licenseId.Value, ct);
        if (category is null) return NotFound();

        category.Name = req.Name.Trim();
        category.SortOrder = req.SortOrder;
        category.IsActive = req.IsActive;
        category.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(category));
    }

    /// <summary>
    /// Kategoriyi başka bir ebeveynin altına (ya da köke) taşır ve ALT AĞACIN
    /// TAMAMININ yolunu yeniden yazar. Kendi alt ağacına taşıma reddedilir.
    /// </summary>
    [HttpPut("{id:guid}/parent")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var category = await FindAsync(id, licenseId.Value, ct);
        if (category is null) return NotFound();

        string? newParentPath = null;
        if (req.ParentCategoryId is not null)
        {
            newParentPath = await _db.Categories
                .Where(c => c.Id == req.ParentCategoryId.Value && c.LicenseId == licenseId.Value)
                .Select(c => c.Path)
                .FirstOrDefaultAsync(ct);

            if (newParentPath is null)
                return Problem(title: "parent-not-found",
                    detail: "Üst kategori bulunamadı.", statusCode: 400);
        }

        if (CategoryPathService.WouldCreateCycle(category.Path, newParentPath))
            return Problem(title: "category-cycle",
                detail: "Bir kategori kendi alt ağacına taşınamaz.", statusCode: 400);

        var oldPrefix = category.Path;
        var newPrefix = CategoryPathService.BuildPath(newParentPath, category.Id);

        // Alt ağacın TAMAMI (kendisi dahil) — tek StartsWith ile bulunur.
        var subtree = await _db.Categories
            .Where(c => c.LicenseId == licenseId.Value && c.Path.StartsWith(oldPrefix))
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var node in subtree)
        {
            node.Path = CategoryPathService.Rebase(node.Path, oldPrefix, newPrefix);
            node.UpdatedAt = now;
        }

        category.ParentCategoryId = req.ParentCategoryId;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(category));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var category = await FindAsync(id, licenseId.Value, ct);
        if (category is null) return NotFound();

        var hasChildren = await _db.Categories
            .AnyAsync(c => c.ParentCategoryId == id, ct);
        if (hasChildren)
            return Problem(title: "category-has-children",
                detail: "Önce alt kategorileri taşı ya da sil.", statusCode: 409);

        var hasProducts = await _db.Products.AnyAsync(p => p.CategoryId == id, ct);
        if (hasProducts)
            return Problem(title: "category-has-products",
                detail: "Bu kategoride ürün var.", statusCode: 409);

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<Category?> FindAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Categories.FirstOrDefaultAsync(
            c => c.Id == id && c.LicenseId == licenseId, ct);

    private static CategoryDto ToDto(Category c) => new(
        c.Id, c.ParentCategoryId, c.Name, c.Path,
        Depth: c.Path.Count(ch => ch == '/') - 2,
        c.SortOrder, c.IsActive);

    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        return _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
