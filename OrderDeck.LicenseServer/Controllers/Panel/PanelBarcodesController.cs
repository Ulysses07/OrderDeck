using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Barkod numarası ayırma ucu. Panelde operatör bir varyanta yeni numara
/// vermek istediğinde kullanılır.
///
/// <para><b>Neden ayrı uç:</b> varyant yazma yolları boş barkodu zaten
/// dolduruyor; bu uç, kullanıcının numarayı YAZMADAN ÖNCE görüp
/// onaylayabilmesi için var (alan doldurulur, kaydet ayrı adımdır).</para>
///
/// <para>Ayırma burada kalıcıdır: dönen numaralar sayaçta tüketilir. Kullanıcı
/// kaydetmezse o numaralar boşa gider — kabul edildi, 10 hane 10 milyar
/// numara demek.</para>
/// </summary>
[ApiController]
[Route("api/panel/barcodes")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelBarcodesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly BarcodeAllocator _barcodes;

    public PanelBarcodesController(LicenseDbContext db, BarcodeAllocator barcodes)
    {
        _db = db;
        _barcodes = barcodes;
    }

    public sealed record NextBarcodesDto(IReadOnlyList<string> Barcodes);

    /// <summary>Tavan 200: <c>CatalogLimits.MaxBulkVariants</c> ile aynı sebep.
    /// Sayıyı tekrar yazmak yerine ona bağlıyoruz ki ikisi ayrışamasın.</summary>
    private const int MaxCount = CatalogLimits.MaxBulkVariants;

    [AllowStockStaff]
    [HttpPost("next")]
    public async Task<IActionResult> Next([FromQuery] int count, CancellationToken ct)
    {
        if (count <= 0 || count > MaxCount)
            return Problem(title: "invalid-count",
                detail: $"Adet 1 ile {MaxCount} arasında olmalı.", statusCode: 400);

        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var codes = await _barcodes.AllocateAsync(licenseId.Value, count, ct);

        // Bu uçta kaydetmek ZORUNLU: ayırıcı sayacı yalnız değiştiriyor. Aksi
        // hâlde numaralar yanıtta döner ama sayaç ilerlemez ve bir sonraki
        // istek aynı numaraları verirdi.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        // DbUpdateConcurrencyException DEĞİL, üst tür yakalanıyor: sayaç satırı
        // lisansta ilk kez oluşuyorsa yarışın kaybedeni RowVersion uyuşmazlığı
        // değil, birincil anahtar çakışması alır — o da DbUpdateException'ın
        // kendisidir. Dar yakalasaydık, 409'un var olma sebebi olan tam o
        // pencerede 500 dönerdi.
        //
        // Geniş yakalamak burada güvenli: bu eylem TEK satır yazıyor (sayaç),
        // dolayısıyla buradan çıkan her DbUpdateException o satır hakkındadır
        // ve iki başarısızlık biçiminin de çaresi aynı — tekrar dene.
        catch (DbUpdateException)
        {
            return Problem(title: "barcode-counter-busy",
                detail: "Aynı anda başka bir barkod işlemi yapıldı; tekrar dene.",
                statusCode: 409);
        }

        return Ok(new NextBarcodesDto(codes));
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
