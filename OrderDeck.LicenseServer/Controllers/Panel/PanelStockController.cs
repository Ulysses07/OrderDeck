using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Stock;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panelin stok yüzü: bakiye okuma, mal kabul girişi, sayım düzeltmesi ve
/// hareket dökümü.
///
/// Tümü <see cref="AllowStockStaffAttribute"/> taşır — stok elemanının işi
/// tam olarak budur. Öznitelik yalnız metotlara konabiliyor (derleyici
/// zorluyor), yani yarın bu sınıfa eklenen bir uç kendiliğinden açık gelmez.
///
/// Spec'in "müşteri, sipariş, ödeme ve ciro bilgilerini göremez" kuralı
/// bozulmuyor: hareket dökümü ürün/miktar/sebep alanlarını döner, satışı yapan
/// müşteriyi değil.
/// </summary>
[ApiController]
[Route("api/panel/stock")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelStockController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly StockBalanceService _balances;

    public PanelStockController(LicenseDbContext db, StockBalanceService balances)
    {
        _db = db;
        _balances = balances;
    }

    /// <summary>Tek istekte sorulabilecek ürün sayısı. Katalog ölçeği lisans
    /// başına yüzler mertebesinde; panelin tek ekranda bundan fazlasına ihtiyacı
    /// yok. Sayı <c>AdminWhatsAppConversationsController</c> ile aynı deyimden.</summary>
    private const int MaxBalanceProductIds = 200;

    public sealed record StockBalanceDto(Guid ProductId, Guid? ProductVariantId, int Quantity);

    public sealed record StockMovementDto(
        Guid Id, Guid ProductId, Guid? ProductVariantId,
        int Quantity, int Reason, Guid? OrderId, string? Note,
        DateTimeOffset OccurredAt, DateTimeOffset CreatedAt);

    // DİKKAT — positional record'da doğrulama attribute'u PARAMETREYE yazılır,
    // [property:] hedefiyle DEĞİL. MVC record'un birincil kurucusunu okuyor;
    // metadata property'ye taşınırsa çalışma zamanında istisna atıyor
    // ("validation metadata must be associated with the constructor parameter").
    public sealed record CreateEntryRequest(
        Guid ProductId,
        Guid? ProductVariantId,
        [Range(1, 100_000)] int Quantity,
        [MaxLength(CatalogLimits.MovementNote)] string? Note);

    public sealed record CreateCountRequest(
        Guid ProductId,
        Guid? ProductVariantId,
        [Range(0, 1_000_000)] int CountedQuantity,
        [MaxLength(CatalogLimits.MovementNote)] string? Note);

    /// <summary>
    /// Bakiyeler. <c>productId</c> birden çok kez verilebilir; hiç verilmezse
    /// lisansın tamamı döner.
    /// </summary>
    [HttpGet("balances")]
    [AllowStockStaff]
    public async Task<IActionResult> Balances(
        [FromQuery] Guid[]? productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NoLicense();

        // Tavan aşılınca sessizce kırpmak yerine reddediliyor: kırpılan istek
        // eksik bakiye döner ve panelin bunu anlamasının bir yolu yoktur.
        if (productId is { Length: > MaxBalanceProductIds })
            return Problem(title: "too-many-products",
                detail: $"En çok {MaxBalanceProductIds} ürün sorulabilir.",
                statusCode: 400);

        var rows = await _balances.GetAsync(licenseId.Value, productId, ct);

        return Ok(rows
            .Select(b => new StockBalanceDto(b.ProductId, b.ProductVariantId, b.Quantity))
            .ToList());
    }

    /// <summary>Hareket dökümü — en yeni önce.</summary>
    [HttpGet("movements")]
    [AllowStockStaff]
    public async Task<IActionResult> Movements(
        [FromQuery] Guid productId,
        [FromQuery] Guid? productVariantId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NoLicense();

        take = Math.Clamp(take, 1, 500);

        // Stok elemanı sipariş bilgisi göremez (sınıf özetindeki kural).
        // OrderId siparişe götüren bir tutamaç, o yüzden ona boş dönüyor —
        // uç kapanmıyor, yalnız bu alan maskeleniyor (maliyet maskesiyle aynı).
        var hideOrder = User.GetOperatorRole() == OperatorRoles.Stock;

        var q = _db.StockMovements
            .Where(m => m.LicenseId == licenseId.Value && m.ProductId == productId);
        if (productVariantId is not null)
            q = q.Where(m => m.ProductVariantId == productVariantId);

        var rows = await q
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(take)
            .Select(m => new StockMovementDto(
                m.Id, m.ProductId, m.ProductVariantId,
                m.Quantity, (int)m.Reason, hideOrder ? null : m.OrderId, m.Note,
                m.OccurredAt, m.CreatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Mal kabul / stok girişi.</summary>
    [HttpPost("entries")]
    [AllowStockStaff]
    public async Task<IActionResult> CreateEntry(
        [FromBody] CreateEntryRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NoLicense();

        var target = await ResolveTargetAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct);
        if (target is null) return NotFound(TargetProblem());

        var now = DateTimeOffset.UtcNow;
        _db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = req.ProductId,
            ProductVariantId = req.ProductVariantId,
            Quantity = req.Quantity,
            Reason = StockMovementReason.Entry,
            Note = req.Note,
            OccurredAt = now,
            CreatedAt = now,
            CreatedByOperatorId = User.GetOperatorId(),
        });
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created,
            await CurrentBalanceAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct));
    }

    /// <summary>
    /// Sayım. İstemci <b>sayılan adedi</b> gönderir, sunucu farkı yazar.
    ///
    /// Neden fark: aradaki saniyelerde bir satış düşmüş olabilir. İstemci farkı
    /// hesaplasaydı o satışı sessizce ezerdi. Fark sıfırsa hiçbir satır yazılmaz
    /// — defter gürültüyle şişmesin.
    /// </summary>
    [HttpPost("counts")]
    [AllowStockStaff]
    public async Task<IActionResult> CreateCount(
        [FromBody] CreateCountRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NoLicense();

        var target = await ResolveTargetAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct);
        if (target is null) return NotFound(TargetProblem());

        var current = await CurrentBalanceAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct);
        var delta = req.CountedQuantity - current.Quantity;

        if (delta == 0) return Ok(current);

        var now = DateTimeOffset.UtcNow;
        _db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = req.ProductId,
            ProductVariantId = req.ProductVariantId,
            Quantity = delta,
            Reason = StockMovementReason.CountAdjustment,
            Note = req.Note,
            OccurredAt = now,
            CreatedAt = now,
            CreatedByOperatorId = User.GetOperatorId(),
        });
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created,
            await CurrentBalanceAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct));
    }

    // ─── yardımcılar ──────────────────────────────────────────────────

    /// <summary>Kardeş panel controller'larının ortak yanıtı. 404 DEĞİL: panel
    /// "lisansın yok" ile "ürün yok"u ayırt edebilmeli, yoksa kullanıcıya yanlış
    /// şey söyler.</summary>
    private IActionResult NoLicense()
        => Problem(title: "no-active-license", statusCode: 400);

    private ProblemDetails TargetProblem() => new()
    {
        Title = "stock-target-not-found",
        Detail = "Ürün veya varyant bulunamadı.",
        Status = StatusCodes.Status404NotFound,
    };

    /// <summary>
    /// Ürünün (ve verilmişse varyantın) bu lisansa ait olduğunu doğrular.
    /// Varyantın <c>ProductId</c>'si de kontrol edilir: başka ürünün varyantına
    /// giriş yapmak sessiz veri bozulmasıdır.
    /// </summary>
    private async Task<bool?> ResolveTargetAsync(
        Guid licenseId, Guid productId, Guid? variantId, CancellationToken ct)
    {
        var productExists = await _db.Products
            .AnyAsync(p => p.Id == productId && p.LicenseId == licenseId, ct);
        if (!productExists) return null;

        if (variantId is null) return true;

        var variantOk = await _db.ProductVariants
            .AnyAsync(v => v.Id == variantId
                        && v.LicenseId == licenseId
                        && v.ProductId == productId, ct);
        return variantOk ? true : null;
    }

    private async Task<StockBalanceDto> CurrentBalanceAsync(
        Guid licenseId, Guid productId, Guid? variantId, CancellationToken ct)
    {
        var rows = await _balances.GetAsync(licenseId, new[] { productId }, ct);
        var match = rows.FirstOrDefault(b => b.ProductVariantId == variantId);
        return new StockBalanceDto(productId, variantId, match?.Quantity ?? 0);
    }

    /// <summary>
    /// Müşterinin aktif lisansı. Gövde <see cref="PanelLicenseScope"/> ile
    /// birebir aynıydı; kopya bırakılırsa iki tanım zamanla ayrışır ve stok
    /// ekranı kardeşlerinden farklı bir lisans seçmeye başlar.
    /// </summary>
    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
        => PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
}
