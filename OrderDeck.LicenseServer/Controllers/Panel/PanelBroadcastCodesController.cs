using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.Shared.Text;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayın kodları: operatörün canlı yayında söylediği, izleyicinin yoruma
/// yazdığı kod. Ürünün stok kodundan (<c>SK00001</c>) apayrı.
///
/// <para><b>Neden ayrı controller:</b> kaynak ayrı ve kuralları ayrı —
/// <c>PanelProductsController</c> zaten 750+ satır ve buranın tek kuralı
/// ("kod bir daha asla devredilmez") ürün kartının kurallarıyla hiç
/// kesişmiyor.</para>
///
/// <para><b>Silme ucu YOK.</b> Kod serbest bırakılamaz: eski yayın
/// videosundaki kodu bugün yazan izleyicinin siparişi, kod devredilmiş olsaydı
/// yanlış ürüne düşerdi. Kod değişikliği bu yüzden güncelleme değil, yeni satır
/// — eski satır kodu rezerve tutmaya devam eder.</para>
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/broadcast-codes")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelBroadcastCodesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelBroadcastCodesController(LicenseDbContext db) => _db = db;

    public sealed record BroadcastCodeDto(
        string? SellerAxisValue, string Code, DateTimeOffset CreatedAt, bool IsCurrent);

    // Doğrulama attribute'ları positional record'un PARAMETRESİNE yazılıyor;
    // deponun yerleşik kalıbı bu (bkz. PanelProductVariantsController içindeki
    // VariantRequest). [property:] hedefiyle ne olacağını denemedik — kalıptan
    // sapmamak için burada da parametre üstünde duruyorlar.
    public sealed record BroadcastCodeRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? SellerAxisValue,
        [MaxLength(CatalogLimits.BroadcastCode)] string? Code);

    /// <summary>
    /// Ürünün <b>tüm</b> yayın kodu geçmişi, en yeni başta. Satıcı ekseni değeri
    /// başına en yeni satır <c>IsCurrent: true</c> gelir — düzenlenebilir güncel
    /// kod odur; geri kalanı emeklidir.
    ///
    /// <para><b>Emekliler bilerek gönderiliyor:</b> satır asla silinmediği için
    /// emekli kod kalıcı olarak rezervedir ve aynı kod başka bir yere yazılmak
    /// istendiğinde 409 <c>broadcast-code-taken</c> üretir. Emekliler
    /// gönderilmezse operatör o kodun nerede kullanıldığını görebileceği hiçbir
    /// ekrana sahip olmaz — teşhis edilemez bir çakışma kalır.</para>
    ///
    /// <para>Aynı verinin öteki tüketicisi olan WPF çekme ucu
    /// (<c>LicensesWpfCatalogPullController</c>) emeklileri zaten gönderiyor;
    /// bu uç da artık aynı tabloyu aynı şekilde anlatıyor.</para>
    /// </summary>
    [AllowStockStaff]
    [HttpGet]
    public async Task<IActionResult> Get(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var owns = await _db.Products
            .AnyAsync(p => p.Id == productId && p.LicenseId == licenseId.Value, ct);
        if (!owns) return NotFound();

        var rows = await _db.ProductBroadcastCodes.AsNoTracking()
            .Where(x => x.LicenseId == licenseId.Value && x.ProductId == productId)
            .OrderByDescending(x => x.CreatedAt)
            // Tie-break KEYFÎ ama KARARLI; amacı belirlilik, anlam değil.
            // Id rastgele bir Guid ve bu ThenBy SQL'e çevriliyor — Guid
            // sıralaması SQL Server ile PostgreSQL'de aynı değil, yani göçte
            // eşit CreatedAt'li iki satırın sırası değişebilir. Kabul edildi:
            // buraya düşmek için aynı tick'e iki kod yazılması gerekir.
            .ThenByDescending(x => x.Id)
            .ToListAsync(ct);

        // "Güncel olan hangisi" kuralını panel değil SUNUCU söylüyor: panel
        // "listede ilk gelen günceldir" diye kendi hesaplasaydı kuralın iki
        // tanımı olur ve zamanla ayrışırdı. Aynı karar bu depoda bir kez daha
        // verildi — CodeNormalized de tele konuyor (gerekçesi
        // CatalogBroadcastCodeDto'nun doc'unda): kural sunucuda tanımlı,
        // telde taşınıyor.
        //
        // Hesap bellekte: satırlar zaten çekildi ve sıra en yeni başta, yani
        // bir satıcı ekseni değerinin İLK görülen satırı güncel, sonrakiler
        // emekli. HashSet.Add ilk görülene true, tekrarına false döner.
        //
        // Döngü bilerek açık yazıldı, Select'e gömülmedi: Add bir YAN ETKİ ve
        // LINQ ertelenmiş çalışıyor — projeksiyon ikinci kez sayılsaydı (bir
        // refactor'ın kolayca yapabileceği şey) her satır emekli dönerdi.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var history = new List<BroadcastCodeDto>(rows.Count);
        foreach (var x in rows)
            history.Add(new BroadcastCodeDto(
                x.SellerAxisValue, x.Code, x.CreatedAt,
                IsCurrent: seen.Add(SearchNormalizer.Normalize(x.SellerAxisValue))));

        return Ok(history);
    }

    /// <summary>
    /// Bir satıcı ekseni değerine kod atar. Gövde tek satır taşır (toplu değil):
    /// panel kutuları tek tek kaydediyor ve bir kutunun 409'u ötekileri
    /// geri almamalı.
    /// </summary>
    [AllowStockStaff]
    [HttpPut]
    public async Task<IActionResult> Put(
        Guid productId, [FromBody] BroadcastCodeRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId && p.LicenseId == licenseId.Value, ct);
        if (product is null) return NotFound();

        var code = (req.Code ?? string.Empty).Trim();
        if (code.Length == 0)
            return Problem(title: "missing-code",
                detail: "Yayın kodu boş olamaz.", statusCode: 400);

        // En az bir harf ya da rakam ŞART. Normalize'ın boş dönmesine
        // güvenilemez: SearchNormalizer noktalamayı BİLEREK koruyor, yani
        // "---" normalize edildiğinde de dolu kalır. Böyle bir kod canlı
        // yorumda hiçbir zaman eşleşmez ama kaydedilince kalıcı olarak
        // rezerve olur (satır asla silinmiyor) — hem yanlış güven verir hem
        // geri alınamaz çöp bırakır.
        if (!code.Any(char.IsLetterOrDigit))
            return Problem(title: "invalid-code",
                detail: "Yayın kodu en az bir harf ya da rakam içermeli.", statusCode: 400);

        var normalized = SearchNormalizer.Normalize(code);

        var sellerValue = ResolveSellerAxisValue(product, req.SellerAxisValue, out var axisError);
        if (axisError is not null) return axisError;

        var now = DateTimeOffset.UtcNow;

        var existing = await _db.ProductBroadcastCodes
            .FirstOrDefaultAsync(
                x => x.LicenseId == licenseId.Value && x.CodeNormalized == normalized, ct);

        if (existing is not null)
        {
            if (!IsSameTarget(existing, product.Id, sellerValue)) return CodeTaken();

            // Aynı hedefe aynı kod: yeni satır AÇMA (benzersiz indeks zaten
            // reddederdi), var olanı güncel yap.
            existing.Code = code;
            existing.SellerAxisValue = sellerValue;
            // ProductBroadcastCode'un "kod değişikliği güncelleme değil, yeni
            // satır" kuralıyla çelişmiyor: o kural kodun HEDEFİ değiştiğinde
            // geçerli (IsSameTarget onu zaten yukarıda eledi). Aynı hedefe
            // aynı kodun yeniden yazılması yeni bir atama değil, var olan
            // atamanın tazelenmesi — ilk atama anının kaybı bu yüzden zararsız.
            existing.CreatedAt = now;
            await _db.SaveChangesAsync(ct);
            return Ok(new BroadcastCodeDto(
                existing.SellerAxisValue, existing.Code, existing.CreatedAt, IsCurrent: true));
        }

        var row = new ProductBroadcastCode
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = product.Id,
            SellerAxisValue = sellerValue,
            Code = code,
            CreatedAt = now,
        };
        _db.ProductBroadcastCodes.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Yarış: ön kontrolden sonra başka bir istek aynı kodu aldı. Sebebi
            // SQL hata numarasından ayıklamıyoruz — sağlayıcıya bağımlı olur ve
            // PostgreSQL göçünde sessizce çürür; tekrar SORMAK bağımsız ve kesin.
            //
            // DİKKAT — bu dal uçtan uca test EDİLEMEZ: EF InMemory benzersiz
            // indeksi zorlamıyor, istisna testte hiç atılmıyor. Kararın kendisi
            // bu yüzden burada değil, iki yolun da çağırdığı IsSameTarget +
            // CodeTaken ikilisinde duruyor.
            var raced = await _db.ProductBroadcastCodes.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.LicenseId == licenseId.Value
                         && x.CodeNormalized == normalized
                         && x.Id != row.Id, ct);
            if (raced is not null) return CodeTaken();
            throw; // Benzersizlik değilse yutma — bilinmeyen veri hatası 500 olmalı.
        }

        return Ok(new BroadcastCodeDto(
            row.SellerAxisValue, row.Code, row.CreatedAt, IsCurrent: true));
    }

    /// <summary>
    /// Kayıtlı kod ile gelen hedef aynı mı. Ürün <b>ve</b> satıcı ekseni değeri
    /// eşleşmeli — aynı üründe "Siyah"ın kodunu "Kırmızı"ya kaydırmak da
    /// devretmektir.
    /// </summary>
    private static bool IsSameTarget(
        ProductBroadcastCode existing, Guid productId, string? sellerAxisValue)
        => existing.ProductId == productId
           && string.Equals(
               SearchNormalizer.Normalize(existing.SellerAxisValue),
               SearchNormalizer.Normalize(sellerAxisValue),
               StringComparison.Ordinal);

    private IActionResult CodeTaken()
        => Problem(title: "broadcast-code-taken",
            detail: "Bu yayın kodu daha önce kullanılmış.", statusCode: 409);

    /// <summary>
    /// Gelen satıcı ekseni değerini doğrular ve ürün kartındaki <b>kanonik</b>
    /// yazımına çevirir (kullanıcı "siyah" yazsa da kayda "Siyah" girer) —
    /// böylece panelde kod, varyant listesindeki değerle aynı metin altında
    /// görünür.
    /// </summary>
    private string? ResolveSellerAxisValue(
        Product product, string? supplied, out IActionResult? error)
    {
        error = null;
        var trimmed = string.IsNullOrWhiteSpace(supplied) ? null : supplied.Trim();

        if (product.SellerAxis == 0)
        {
            if (trimmed is not null)
                error = Problem(title: "unexpected-seller-axis-value",
                    detail: "Bu ürünün satıcı ekseni yok; yayın kodu ürünün "
                          + "tamamına verilir.", statusCode: 400);
            return null;
        }

        if (trimmed is null)
        {
            error = Problem(title: "missing-seller-axis-value",
                detail: "Yayın kodu bir satıcı ekseni değerine bağlanmalı.",
                statusCode: 400);
            return null;
        }

        var norm = SearchNormalizer.Normalize(trimmed);
        var match = product.Variants
            .Select(product.SellerAxisValueOf)
            .Where(value => value is not null)
            .FirstOrDefault(value =>
                string.Equals(SearchNormalizer.Normalize(value), norm, StringComparison.Ordinal));

        if (match is null)
        {
            var axisName = product.SellerAxis == 1 ? product.Axis1Name : product.Axis2Name;
            error = Problem(title: "unknown-seller-axis-value",
                detail: $"'{trimmed}' bu üründe bir {axisName} değeri değil.",
                statusCode: 400);
            return null;
        }

        return match;
    }

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
