using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Stock;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF'in stok durumunu artımlı çektiği uç. <b>Ham defter satırı döndürmez</b>:
/// WPF ürün/varyant başına tek satır tutar ve ekranda
/// <c>sunucu bakiyesi − yerel bekleyen hareketler</c> gösterir. Bütün hareket
/// geçmişini indirmek WPF'in yerel tablosunu sonsuza büyütür, üstüne defteri
/// toplama işini ikinci kez uygulatırdı.
///
/// İmleç yine de <b>hareketler</b> üstünde koşar, çünkü toplanabilir bir "bakiye
/// satırı" yok — bakiye bir toplamdır, kendi <c>UpdatedAt</c>'i olan bir kaydı
/// yoktur. Akış: imleçten sonraki hareketleri sayfala → değişen anahtarları
/// bul → o anahtarların bakiyesini <b>sorgu anında yeniden hesapla</b>.
///
/// Dönen miktar bu yüzden bir fark değil, o anın <b>mutlak bakiyesidir</b>.
/// Sayfa bir anahtarın hareketlerini ortasından kesse bile gönderilen sayı
/// doğrudur; anahtar sonraki sayfada tekrar görünür. İstemci <b>upsert</b>
/// eder, toplamaz.
///
/// İmleç <see cref="Domain.StockMovement.CreatedAt"/> (sunucu yazma anı)
/// üstünde, <see cref="Domain.StockMovement.OccurredAt"/> üstünde DEĞİL:
/// çevrimdışı satılan sipariş geçmişe dönük bir <c>OccurredAt</c> ile geliyor ve
/// iş zamanı imleci onu sessizce atlardı.
///
/// İmleç <b>bileşik</b> (<c>since</c> + <c>sinceId</c>): tek senkron paketindeki
/// bütün hareketler aynı <c>CreatedAt</c> damgasını taşıyor, <c>take</c> sınırı
/// bu eşitlik kümesinin ortasından kesebilir. Yalnız zaman imleci olsaydı kesilen
/// satırların anahtarları eski bakiyede donup kalırdı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/stock")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfStockPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly StockBalanceService _balances;

    public LicensesWpfStockPullController(LicenseDbContext db, StockBalanceService balances)
    {
        _db = db;
        _balances = balances;
    }

    public sealed record StockBalancePullItem(
        Guid ProductId,
        Guid? ProductVariantId,
        int Quantity);

    /// <param name="Balances">Bu sayfada değişen anahtarların <b>mutlak</b> bakiyeleri.</param>
    /// <param name="CursorCreatedAt">Bir sonraki çağrıda <c>since</c> olarak gönderilecek değer.</param>
    /// <param name="CursorId">Bir sonraki çağrıda <c>sinceId</c> olarak gönderilecek değer.</param>
    /// <param name="HasMore">Sayfa dolduysa true — istemci hemen tekrar çağırmalı.</param>
    public sealed record StockBalancePullResponse(
        IReadOnlyList<StockBalancePullItem> Balances,
        DateTimeOffset CursorCreatedAt,
        Guid CursorId,
        bool HasMore);

    /// <param name="licenseId">Stok bakiyesi çekilecek lisans.</param>
    /// <param name="since">Son alınan sayfanın <c>cursorCreatedAt</c>'i; ilk çekmede çok eski bir tarih.</param>
    /// <param name="sinceId">Son alınan sayfanın <c>cursorId</c>'si; ilk çekmede boş GUID.</param>
    /// <param name="take">Taranacak <b>hareket</b> sayısı (dönen bakiye satırı sayısı değil). Varsayılan 500, üst sınır 1000.</param>
    /// <param name="ct">İstek iptal jetonu.</param>
    [HttpGet("balances/since")]
    public async Task<IActionResult> BalancesSince(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] Guid sinceId,
        [FromQuery] int take = 500,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 1000);

        var page = await _db.StockMovements
            .Where(m => m.LicenseId == licenseId
                        && (m.CreatedAt > since
                            || (m.CreatedAt == since && m.Id.CompareTo(sinceId) > 0)))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(take)
            .Select(m => new { m.Id, m.CreatedAt, m.ProductId, m.ProductVariantId })
            .ToListAsync(ct);

        // Boş sayfa istemcinin imlecini geri sarmaz — aynen iade edilir.
        if (page.Count == 0)
            return Ok(new StockBalancePullResponse([], since, sinceId, false));

        var touched = page
            .Select(p => new StockKey(p.ProductId, p.ProductVariantId))
            .ToHashSet();
        var productIds = page.Select(p => p.ProductId).Distinct().ToList();

        // Bakiye tam olarak burada yeniden hesaplanıyor: sayfanın dışında kalan
        // hareketler de toplama dahil, yani dönen sayı mutlak ve güncel.
        var balances = (await _balances.GetAsync(licenseId, productIds, ct))
            .Where(b => touched.Contains(new StockKey(b.ProductId, b.ProductVariantId)))
            .Select(b => new StockBalancePullItem(b.ProductId, b.ProductVariantId, b.Quantity))
            .ToList();

        var last = page[^1];
        return Ok(new StockBalancePullResponse(
            balances, last.CreatedAt, last.Id, page.Count == take));
    }
}
