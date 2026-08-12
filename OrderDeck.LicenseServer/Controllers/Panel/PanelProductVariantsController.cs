using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün varyantları (Faz 1a). Varyant kodu <c>ÜRÜNKODU-EKSEN1[-EKSEN2]</c>
/// biçiminde ve yalnız ASCII harf/rakam taşır — Faz 1c'nin barkot alfabesi
/// Code128 ve Code128 Türkçe harf kabul etmiyor.
///
/// Kodu tek bir yer kurar: <see cref="VariantCodeBuilder"/>. Kod türetilmiştir ve
/// ürün kodu değişince yenilenir; Faz 1c'de barkot yükü basım anında
/// <c>ProductVariant.Barcode</c>'a kopyalanıp dondurulur, okutma oradan
/// çözümlenir — yoksa yeniden adlandırma basılmış etiketleri geçersiz kılar.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/variants")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelProductVariantsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelProductVariantsController(LicenseDbContext db) => _db = db;

    // DİKKAT — positional record'da doğrulama attribute'u PARAMETREYE yazılır,
    // [property:] hedefiyle DEĞİL. MVC record'un birincil kurucusunu okuyor;
    // metadata property'ye taşınırsa çalışma zamanında istisna atıyor.
    //
    // Kod parçalarını ayrıca AxisCodeDeriver 4 karaktere kısaltıyor; buradaki
    // sınır kolonun kendisi (8). VariantCode istemciden GELMEZ, bu üçünden
    // türetilir ve yapı gereği 64'e sığar (bkz. CatalogLimits.VariantCode).
    public sealed record VariantRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? Axis1Value,
        [MaxLength(CatalogLimits.AxisCode)] string? Axis1Code,
        [MaxLength(CatalogLimits.AxisValue)] string? Axis2Value,
        [MaxLength(CatalogLimits.AxisCode)] string? Axis2Code,
        bool IsActive);

    [AllowStockStaff]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid productId, [FromBody] VariantRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var built = BuildSegments(product, req, out var error);
        if (error is not null) return error;

        var conflict = await VariantCodeTakenAsync(product.Id, built, excludeId: null, ct);
        if (conflict is not null) return conflict;

        var now = DateTimeOffset.UtcNow;
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = built.Axis1Value,
            Axis1Code = built.Axis1Code,
            Axis2Value = built.Axis2Value,
            Axis2Code = built.Axis2Code,
            VariantCode = built.VariantCode,
            IsActive = req.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ProductVariants.Add(variant);
        product.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Yarış: ön kontrolden sonra başka bir istek aynı kodu aldı (panelde
            // çift tıklama ya da iki sekme yeter). Sebebi SQL hata numarasından
            // ayıklamıyoruz — sağlayıcıya bağımlı olur, PostgreSQL göçünde
            // sessizce çürür; tekrar SORMAK hem bağımsız hem kesin.
            //
            // DİKKAT — bu üç satır uçtan uca test EDİLEMEZ: EF InMemory benzersiz
            // indeksi zorlamadığı için istisna testte hiç atılmıyor. Kararın
            // kendisi bu yüzden burada değil, iki yolun da çağırdığı
            // VariantCodeTakenAsync'te duruyor; testler onu ön kontrol
            // üzerinden geçiyor ve burası yalnız tesisat kalıyor.
            var raced = await VariantCodeTakenAsync(product.Id, built, variant.Id, ct);
            if (raced is not null) return raced;
            throw; // Benzersizlik değilse yutma — bilinmeyen veri hatası 500 olmalı.
        }

        return Created(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}", ToDto(variant));
    }

    public sealed record BulkRequest(List<VariantRequest> Items);
    public sealed record BulkResultDto(
        IReadOnlyList<PanelProductsController.VariantDto> Variants);

    /// <summary>
    /// Varyant üreteci 12–20 satır birden çıkarıyor. Bunları tek tek POST etmek,
    /// ortada bir hata olursa <b>yarım kurulmuş ürün</b> bırakır; kullanıcı
    /// tekrar denediğinde ilk yazılanlar çakışır ve o noktadan sonrası elle
    /// temizlik olur. Bu uç ya hepsini yazar ya hiçbirini.
    ///
    /// <b>Atomikliği nasıl sağlıyor:</b> açık bir <c>BeginTransaction</c> YOK.
    /// Bütün doğrulama ve çakışma kontrolü hiçbir şey yazmadan önce bitiyor,
    /// sonra tek bir <c>SaveChangesAsync</c> çağrılıyor — EF onu zaten tek
    /// işlemde gönderiyor. Açık işlem açmak testleri de bozardı: EF InMemory
    /// işlem desteklemiyor, <c>BeginTransaction</c> orada uyarı/istisna üretir.
    /// </summary>
    [AllowStockStaff]
    [HttpPost("bulk")]
    public async Task<IActionResult> CreateBulk(
        Guid productId, [FromBody] BulkRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var items = req.Items ?? [];
        if (items.Count == 0)
            return Problem(title: "empty-batch",
                detail: "Yazılacak varyant yok.", statusCode: 400);
        if (items.Count > CatalogLimits.MaxBulkVariants)
            return Problem(title: "batch-too-large",
                detail: $"Tek seferde en çok {CatalogLimits.MaxBulkVariants} varyant "
                      + "yazılabilir.", statusCode: 400);

        // 1) Hepsini doğrula. Tek satır bile geçmezse hiçbir şey yazılmaz.
        var built = new List<Segments>(items.Count);
        foreach (var item in items)
        {
            var segments = BuildSegments(product, item, out var error);
            if (error is not null) return error;
            built.Add(segments);
        }

        // 2) Parti İÇİ tekrar. Veritabanına sormadan yakalanır: üreteç aynı
        //    kombinasyonu iki kez üretmiş ya da iki farklı yazım aynı koda
        //    düşmüş olabilir ("Siyah" / "siyah"). Bu kontrol olmasaydı hata
        //    ancak benzersiz indeksten dönerdi — yani prod'da 500 olarak.
        for (var i = 0; i < built.Count; i++)
        for (var j = i + 1; j < built.Count; j++)
            if (string.Equals(built[i].VariantCode, built[j].VariantCode,
                    StringComparison.Ordinal))
                return Problem(title: "duplicate-in-batch",
                    detail: $"'{Describe(built[j].Axis1Value, built[j].Axis2Value)}' "
                          + $"listede birden fazla kez var ({built[j].VariantCode}).",
                    statusCode: 409);

        // 3) Var olanlarla çakışma — tekil uçla aynı kural, aynı metot.
        foreach (var segments in built)
        {
            var conflict = await VariantCodeTakenAsync(product.Id, segments, null, ct);
            if (conflict is not null) return conflict;
        }

        var now = DateTimeOffset.UtcNow;
        var variants = built.Zip(items, (segments, item) => new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = segments.Axis1Value,
            Axis1Code = segments.Axis1Code,
            Axis2Value = segments.Axis2Value,
            Axis2Code = segments.Axis2Code,
            VariantCode = segments.VariantCode,
            IsActive = item.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();

        _db.ProductVariants.AddRange(variants);
        product.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Create'teki yarışın aynısı (gerekçe orada). Tek fark: hangi satırın
            // çakıştığını bilmiyoruz, hepsini yeniden soruyoruz.
            foreach (var segments in built)
            {
                var raced = await VariantCodeTakenAsync(product.Id, segments, null, ct);
                if (raced is not null) return raced;
            }
            throw;
        }

        return Ok(new BulkResultDto(variants.Select(ToDto).ToList()));
    }

    [AllowStockStaff]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid productId, Guid id, [FromBody] VariantRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var variant = product.Variants.FirstOrDefault(v => v.Id == id);
        if (variant is null) return NotFound();

        var built = BuildSegments(product, req, out var error);
        if (error is not null) return error;

        var conflict = await VariantCodeTakenAsync(product.Id, built, id, ct);
        if (conflict is not null) return conflict;

        var now = DateTimeOffset.UtcNow;
        variant.Axis1Value = built.Axis1Value;
        variant.Axis1Code = built.Axis1Code;
        variant.Axis2Value = built.Axis2Value;
        variant.Axis2Code = built.Axis2Code;
        variant.VariantCode = built.VariantCode;
        variant.IsActive = req.IsActive;
        variant.UpdatedAt = now;
        product.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Create'teki yarışın aynısı (gerekçe orada); kendi satırı çakışma
            // sayılmasın diye dışlanıyor.
            var raced = await VariantCodeTakenAsync(product.Id, built, id, ct);
            if (raced is not null) return raced;
            throw;
        }

        return Ok(ToDto(variant));
    }

    [AllowStockStaff]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid productId, Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var variant = product.Variants.FirstOrDefault(v => v.Id == id);
        if (variant is null) return NotFound();

        _db.ProductVariants.Remove(variant);
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private readonly record struct Segments(
        string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode);

    /// <summary>
    /// Eksen değerlerini doğrular, kod parçalarını türetir ve varyant kodunu kurar.
    /// Hata varsa <paramref name="error"/> dolar; dönen değer o durumda anlamsızdır.
    /// </summary>
    private Segments BuildSegments(Product product, VariantRequest req, out IActionResult? error)
    {
        error = null;

        if (product.Axis1Name is null)
        {
            error = Problem(title: "product-has-no-axis",
                detail: "Eksensiz üründe varyant satırı elle eklenemez.", statusCode: 400);
            return default;
        }

        var axis1Value = Trim(req.Axis1Value);
        var axis2Value = Trim(req.Axis2Value);

        if (axis1Value is null || (product.Axis2Name is not null && axis2Value is null))
        {
            error = Problem(title: "missing-axis-value",
                detail: "Her eksen için bir değer girmelisin.", statusCode: 400);
            return default;
        }

        if (product.Axis2Name is null && axis2Value is not null)
        {
            error = Problem(title: "unexpected-axis-value",
                detail: "Bu ürünün ikinci ekseni yok.", statusCode: 400);
            return default;
        }

        var axis1Code = ResolveCode(req.Axis1Code, axis1Value);
        var axis2Code = axis2Value is null ? null : ResolveCode(req.Axis2Code, axis2Value);

        if (axis1Code.Length == 0 || axis2Code?.Length == 0)
        {
            error = Problem(title: "invalid-axis-code",
                detail: "Değerden ASCII kod türetilemedi; kodu elle gir.", statusCode: 400);
            return default;
        }

        var variantCode = VariantCodeBuilder.Build(product.Code, axis1Code, axis2Code);

        return new Segments(axis1Value, axis1Code, axis2Value, axis2Code, variantCode);
    }

    /// <summary>
    /// Kurulan kod bu üründe başka bir satırca tutuluyorsa uygun 409'u döndürür,
    /// yoksa null. İki bambaşka sebep tek slug'a düşmesin diye çakışan satırın
    /// KODUNA değil DEĞERLERİNE bakılır:
    /// <list type="bullet">
    /// <item>değerler aynı → gerçek tekrar (<c>duplicate-variant</c>); satır
    /// zaten var, yapılacak bir şey yok.</item>
    /// <item>değerler farklı → kod çakışması (<c>variant-code-collision</c>);
    /// "Kırmızı" ile "Kırmızılı" ikisi de KIRM'e düşüyor. Kullanıcı iki AYRI
    /// varyant istiyor ve hakkı da var — "zaten var" demek onu yanlış
    /// yönlendirir, çünkü kartta öyle bir değer görmüyor. Çare eksen kodunu
    /// elle girmek; mesaj bunu söylemeli.</item>
    /// </list>
    ///
    /// Hem <c>SaveChanges</c> ÖNCESİ ön kontrol hem SONRASI yarış sınıflandırması
    /// buradan geçiyor: iki ayrı kopya olsaydı biri değişip öbürü kalır, aynı
    /// çakışma isteğin zamanlamasına göre farklı cevap alırdı.
    ///
    /// Sorgu <c>AsNoTracking</c>: <see cref="DbUpdateException"/> sonrası context
    /// kirli, başarısız kayıt hâlâ <c>Added</c> durumunda takip ediliyor; izlenen
    /// sorgu kimlik çözümlemesiyle o kaydı geri getirip yanlış cevap verebilir.
    /// </summary>
    private async Task<IActionResult?> VariantCodeTakenAsync(
        Guid productId, Segments built, Guid? excludeId, CancellationToken ct)
    {
        var variantCode = built.VariantCode;

        var clash = await _db.ProductVariants
            .AsNoTracking()
            .Where(v => v.ProductId == productId
                        && v.VariantCode == variantCode
                        && (excludeId == null || v.Id != excludeId))
            .Select(v => new { v.Axis1Value, v.Axis2Value })
            .FirstOrDefaultAsync(ct);

        if (clash is null) return null;

        var incoming = Describe(built.Axis1Value, built.Axis2Value);

        if (SameValues(clash.Axis1Value, clash.Axis2Value, built))
            return Problem(title: "duplicate-variant",
                detail: $"'{incoming}' varyantı bu üründe zaten var.", statusCode: 409);

        return Problem(title: "variant-code-collision",
            detail: $"'{incoming}' ile mevcut "
                  + $"'{Describe(clash.Axis1Value, clash.Axis2Value)}' aynı koda "
                  + $"({variantCode}) düşüyor. Ayırmak için eksen kodunu elle gir.",
            statusCode: 409);
    }

    /// <summary>
    /// Varyantın kimliği eksen DEĞERLERİ; kıyas normalleştirilmiş biçimde yapılır
    /// çünkü "kırmızı" ile "Kırmızı" kullanıcı açısından aynı varyant — farklı
    /// yazım gerçek tekrardır, kod çakışması değil.
    ///
    /// Normalleştirici arama ile ORTAK (<see cref="SearchNormalizer"/>): kopyası
    /// yazılsaydı iki tanım zamanla ayrışırdı.
    /// </summary>
    private static bool SameValues(string? axis1Value, string? axis2Value, Segments built)
        => string.Equals(
               SearchNormalizer.Normalize(axis1Value),
               SearchNormalizer.Normalize(built.Axis1Value),
               StringComparison.Ordinal)
           && string.Equals(
               SearchNormalizer.Normalize(axis2Value),
               SearchNormalizer.Normalize(built.Axis2Value),
               StringComparison.Ordinal);

    /// <summary>
    /// Mesajlarda değer kodla değil, kullanıcının kartta GÖRDÜĞÜ hâliyle anılır;
    /// iki eksende "Siyah / M".
    /// </summary>
    private static string Describe(string? axis1Value, string? axis2Value)
        => axis2Value is null
            ? axis1Value ?? string.Empty
            : $"{axis1Value} / {axis2Value}";

    private static string ResolveCode(string? supplied, string displayValue)
    {
        var manual = AxisCodeDeriver.Derive(supplied);
        return manual.Length > 0 ? manual : AxisCodeDeriver.Derive(displayValue);
    }

    private Task<Product?> LoadProductAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PanelProductsController.VariantDto ToDto(ProductVariant v) => new(
        v.Id, v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
        v.VariantCode, v.Barcode, v.IsActive);

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
