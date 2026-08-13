using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Stock;

/// <summary>
/// Yazıcıya giren tek sipariş: mutabakat durumu + iş zamanları.
///
/// İki zaman taşınır çünkü hareketin <c>OccurredAt</c>'i deltanın işaretine
/// bağlıdır: düşüm satış anında, telafi iptal anında olmuştur. Tek zaman
/// taşısaydık 1 Ağustos'ta satılıp 5 Ağustos'ta iptal edilen siparişin telafisi
/// 1 Ağustos'a yazılır ve aradaki her geçmişe dönük rapor yanlış çıkardı.
/// </summary>
/// <param name="State">Siparişin mutabakata giren güncel hâli.</param>
/// <param name="SoldAt">Satış anı — siparişin <c>AddedAt</c>'i.</param>
/// <param name="CancelledAt">
/// İptal anı; sipariş iptal değilse null. Null olduğu hâlde pozitif delta
/// üretilebilir (varyant yeniden bağlama) — o durumda olay <b>şimdi</b> olur.
/// </param>
public sealed record LedgerOrderInput(
    LedgerOrderState State,
    DateTimeOffset SoldAt,
    DateTimeOffset? CancelledAt);

/// <summary>
/// <see cref="StockLedgerReconciler"/>'ı veritabanına bağlar: mevcut hareketleri
/// okur, katalog kimliklerini doğrular, farkları hareket satırlarına çevirir.
///
/// <b>SaveChanges ÇAĞIRMAZ.</b> Çağıran (sipariş senkron ucu) siparişleri ve
/// hareketleri tek <c>SaveChanges</c>'te yazar — böylece "sipariş kaydedildi ama
/// hareket kaydedilmedi" diye bir ara durum oluşmaz.
/// </summary>
public sealed class StockLedgerWriter
{
    private readonly LicenseDbContext _db;
    private readonly ILogger<StockLedgerWriter> _log;

    public StockLedgerWriter(LicenseDbContext db, ILogger<StockLedgerWriter> log)
    {
        _db = db;
        _log = log;
    }

    public async Task ApplyAsync(
        Guid licenseId,
        IReadOnlyList<LedgerOrderInput> orders,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (orders.Count == 0) return;

        var orderIds = orders.Select(o => o.State.OrderId).Distinct().ToList();

        // Bu siparişlerin bugüne kadarki hareketleri, sipariş+anahtar bazında
        // TOPLANMIŞ hâlde. Toplam, mutabakatın tek girdisi.
        var existingRows = await _db.StockMovements
            .Where(m => m.LicenseId == licenseId
                        && m.OrderId != null
                        && orderIds.Contains(m.OrderId!.Value))
            .Select(m => new { m.OrderId, m.ProductId, m.ProductVariantId, m.Quantity })
            .ToListAsync(ct);

        var existingByOrder = existingRows
            .GroupBy(r => r.OrderId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<StockKey, int>)g
                    .GroupBy(r => new StockKey(r.ProductId, r.ProductVariantId))
                    .ToDictionary(x => x.Key, x => x.Sum(r => r.Quantity)));

        // Katalog kimliklerini doğrula. StockMovement GERÇEK FK taşıyor; var
        // olmayan bir id yazmaya kalkarsak tüm paket 500 olur.
        var productIds = orders
            .Select(o => o.State.ProductId).Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToList();
        var knownProducts = productIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Products
                .Where(p => p.LicenseId == licenseId && productIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct)).ToHashSet();

        var variantIds = orders
            .Select(o => o.State.ProductVariantId).Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToList();
        var knownVariants = variantIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await _db.ProductVariants
                .Where(v => v.LicenseId == licenseId && variantIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => v.ProductId, ct);

        // Sipariş bazında TEKİLLEŞTİR. existingByOrder veritabanından bir kez
        // okunur ve döngü içinde tazelenmez (SaveChanges'i çağıran biz değiliz),
        // yani aynı id ikinci turda yine eski toplamı görüp AYNI farkı bir daha
        // yazardı: zaten −1 duran sipariş pakette iki kez iptal gelirse bakiye 0
        // yerine +1 olurdu. Uç dışa açık bir HTTP API; invaryantın sahibi yazıcı.
        // Son giriş kazanır: payload sırası istemcinin niyet sırasıdır.
        foreach (var input in orders.GroupBy(o => o.State.OrderId).Select(g => g.Last()))
        {
            var state = Sanitize(input.State, licenseId, knownProducts, knownVariants);

            var existing = existingByOrder.TryGetValue(state.OrderId, out var e)
                ? e
                : new Dictionary<StockKey, int>();

            foreach (var delta in StockLedgerReconciler.Reconcile(state, existing))
            {
                _db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    ProductId = delta.Key.ProductId,
                    ProductVariantId = delta.Key.ProductVariantId,
                    Quantity = delta.QuantityDelta,
                    // İşaret gerekçeyi belirler: eksiye giden düşüm satıştır,
                    // artıya dönen her şey iptal/iadedir.
                    Reason = delta.QuantityDelta < 0
                        ? StockMovementReason.Sale
                        : StockMovementReason.CancelReturn,
                    OrderId = state.OrderId,
                    // İş zamanı da işarete bağlı: düşüm satış anında olmuştur.
                    // Telafi ise iptal anında — iptal yoksa (varyant yeniden
                    // bağlama: A'dan B'ye taşınan sipariş A için +1 üretir) olay
                    // tam da şimdi oluyor, o yüzden now'a düşülür.
                    OccurredAt = delta.QuantityDelta < 0
                        ? input.SoldAt
                        : input.CancelledAt ?? now,
                    CreatedAt = now,
                });
            }
        }
    }

    /// <summary>
    /// Katalogda bulunmayan kimlikleri eler. Bilinmeyen ürün → hiç hareket
    /// (satış yine geçerli, kart "tanımlı değil" der). Bilinmeyen ya da başka
    /// ürüne ait varyant → ürün seviyesine düşülür; spec zaten ürün seviyesi
    /// düşümü meşru sayıyor, burada varyant tahmin etmektense atfetmemeyi
    /// seçiyoruz.
    /// </summary>
    private LedgerOrderState Sanitize(
        LedgerOrderState state,
        Guid licenseId,
        HashSet<Guid> knownProducts,
        Dictionary<Guid, Guid> knownVariants)
    {
        if (state.ProductId is null) return state;

        if (!knownProducts.Contains(state.ProductId.Value))
        {
            _log.LogWarning(
                "Stok hareketi atlandı: bilinmeyen ürün {ProductId} (license={LicenseId}, order={OrderId})",
                state.ProductId, licenseId, state.OrderId);
            return state with { ProductId = null, ProductVariantId = null };
        }

        if (state.ProductVariantId is null) return state;

        if (!knownVariants.TryGetValue(state.ProductVariantId.Value, out var owner)
            || owner != state.ProductId.Value)
        {
            _log.LogWarning(
                "Varyant çözülemedi, ürün seviyesine düşülüyor: {VariantId} (license={LicenseId}, order={OrderId})",
                state.ProductVariantId, licenseId, state.OrderId);
            return state with { ProductVariantId = null };
        }

        return state;
    }
}
