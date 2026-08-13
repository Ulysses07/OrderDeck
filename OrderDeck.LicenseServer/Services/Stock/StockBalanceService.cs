using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Stock;

/// <summary>Bir anahtarın güncel bakiyesi. Negatif olabilir — bu bir hata değil.</summary>
public sealed record StockBalance(Guid ProductId, Guid? ProductVariantId, int Quantity);

/// <summary>
/// Defteri anahtar bazında toplar. Ayrı bir bakiye tablosu <b>yok</b>: iki yazan
/// yol (yayın senkronu + panel girişi) bir bakiye kolonunu kilitsiz güncelleyemez,
/// kilit de yayın hızını vurur. Toplam ise çakışmasız.
/// </summary>
public sealed class StockBalanceService
{
    private readonly LicenseDbContext _db;
    public StockBalanceService(LicenseDbContext db) => _db = db;

    /// <param name="licenseId">Bakiyesi istenen lisans; kapsam asla dışına çıkmaz.</param>
    /// <param name="productIds">Null veya boş ise lisansın tamamı.</param>
    /// <param name="ct">İptal jetonu.</param>
    public async Task<IReadOnlyList<StockBalance>> GetAsync(
        Guid licenseId,
        IReadOnlyCollection<Guid>? productIds,
        CancellationToken ct)
    {
        var q = _db.StockMovements.Where(m => m.LicenseId == licenseId);

        if (productIds is { Count: > 0 })
        {
            var ids = productIds.ToList();
            q = q.Where(m => ids.Contains(m.ProductId));
        }

        return await q
            .GroupBy(m => new { m.ProductId, m.ProductVariantId })
            .Select(g => new StockBalance(
                g.Key.ProductId, g.Key.ProductVariantId, g.Sum(m => m.Quantity)))
            .ToListAsync(ct);
    }
}
