using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün kartı (Faz 1a). Kart iki eksen taşır; her eksenin <b>adı</b> ve
/// <b>rolü</b> ürüne özeldir (satıcı ekseni barkotla sabitlenir, izleyici ekseni
/// yorumdan gelir). İkisi de kapatılabilir.
///
/// Eksensiz ürün de tek bir varyant satırı taşır (<c>VariantCode = Code</c>) —
/// böylece Faz 1b'de stok hareketi her zaman bir varyanta bağlanabilir.
/// </summary>
[ApiController]
[Route("api/panel/products")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[AllowStockStaff]
public sealed class PanelProductsController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;

    public PanelProductsController(LicenseDbContext db, IBroadcastMediaStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    // DİKKAT — positional record'da doğrulama attribute'u PARAMETREYE yazılır,
    // [property:] hedefiyle DEĞİL. MVC record'un birincil kurucusunu okuyor;
    // metadata property'ye taşınırsa çalışma zamanında istisna atıyor
    // ("validation metadata must be associated with the constructor parameter").
    public sealed record UpsertRequest(
        [MaxLength(CatalogLimits.ProductName)] string Name,
        [MaxLength(CatalogLimits.ProductCode)] string? Code,
        Guid? CategoryId,
        decimal DefaultPrice,
        decimal? Cost,
        [MaxLength(CatalogLimits.AxisName)] string? Axis1Name,
        AxisRole? Axis1Role,
        [MaxLength(CatalogLimits.AxisName)] string? Axis2Name,
        AxisRole? Axis2Role);

    public sealed record VariantDto(
        Guid Id,
        string? Axis1Value,
        string? Axis1Code,
        string? Axis2Value,
        string? Axis2Code,
        string VariantCode,
        string? Barcode,
        bool IsActive);

    public sealed record ProductDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        decimal DefaultPrice,
        decimal? Cost,
        string? Axis1Name,
        AxisRole? Axis1Role,
        string? Axis2Name,
        AxisRole? Axis2Role,
        string? PhotoObjectKey,
        bool IsArchived,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<VariantDto> Variants);

    public sealed record ProductRowDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        decimal DefaultPrice,
        bool IsArchived,
        string? PhotoObjectKey,
        int VariantCount,
        DateTimeOffset UpdatedAt);

    public sealed record ProductPageDto(IReadOnlyList<ProductRowDto> Items, int Total);

    public sealed record NextCodeDto(string Code);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? categoryId,
        [FromQuery] string? q,
        [FromQuery] bool includeArchived,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var query = _db.Products.Where(p => p.LicenseId == licenseId.Value);

        if (!includeArchived)
            query = query.Where(p => !p.IsArchived);

        if (categoryId is not null)
        {
            var path = await _db.Categories
                .Where(c => c.Id == categoryId.Value && c.LicenseId == licenseId.Value)
                .Select(c => c.Path)
                .FirstOrDefaultAsync(ct);

            if (path is null)
                return Problem(title: "category-not-found",
                    detail: "Kategori bulunamadı.", statusCode: 400);

            var subtree = await _db.Categories
                .Where(c => c.LicenseId == licenseId.Value && c.Path.StartsWith(path))
                .Select(c => c.Id)
                .ToListAsync(ct);

            query = query.Where(p => p.CategoryId != null && subtree.Contains(p.CategoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            var codeNeedle = needle.ToUpperInvariant();
            query = query.Where(p => p.Name.Contains(needle) || p.Code.Contains(codeNeedle));
        }

        var total = await query.CountAsync(ct);

        var size = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // Hesap long'da yapılıyor: int aritmetiğinde ?page=2147483647 taşıp
        // NEGATİF bir atlamaya dönüşüyordu → "OFFSET -N ROWS" → SQL hatası, 500.
        // Bozuk pageSize gibi bozuk page de reddedilmeyip KIRPILIYOR; uç nokta
        // tek bir davranışta kalsın diye 400 tercih edilmedi.
        var skip = (int)Math.Clamp(((long)page - 1) * size, 0, int.MaxValue);

        var rows = await query
            .OrderBy(p => p.Code)
            .Skip(skip)
            .Take(size)
            .Select(p => new ProductRowDto(
                p.Id, p.CategoryId, p.Code, p.Name, p.DefaultPrice, p.IsArchived,
                p.PhotoObjectKey, p.Variants.Count, p.UpdatedAt))
            .ToListAsync(ct);

        return Ok(new ProductPageDto(rows, total));
    }

    [HttpGet("next-code")]
    public async Task<IActionResult> NextCode(CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var codes = await _db.Products
            .Where(p => p.LicenseId == licenseId.Value)
            .Select(p => p.Code)
            .ToListAsync(ct);

        return Ok(new NextCodeDto(CatalogCodeSequence.Next(codes)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        return Ok(ToDto(product));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var invalid = Validate(req);
        if (invalid is not null) return invalid;

        var categoryError = await ValidateCategoryAsync(req.CategoryId, licenseId.Value, ct);
        if (categoryError is not null) return categoryError;

        var code = NormalizeCode(req.Code);
        if (code.Length == 0)
        {
            var codes = await _db.Products
                .Where(p => p.LicenseId == licenseId.Value)
                .Select(p => p.Code)
                .ToListAsync(ct);
            code = CatalogCodeSequence.Next(codes);
        }
        else if (await _db.Products.AnyAsync(
                     p => p.LicenseId == licenseId.Value && p.Code == code, ct))
        {
            return Problem(title: "duplicate-code",
                detail: $"'{code}' kodu zaten kullanılıyor.", statusCode: 409);
        }

        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            CategoryId = req.CategoryId,
            Code = code,
            Name = req.Name.Trim(),
            DefaultPrice = req.DefaultPrice,
            Cost = req.Cost,
            Axis1Name = Trim(req.Axis1Name),
            Axis1Role = Trim(req.Axis1Name) is null ? null : req.Axis1Role,
            Axis2Name = Trim(req.Axis2Name),
            Axis2Role = Trim(req.Axis2Name) is null ? null : req.Axis2Role,
            IsArchived = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Products.Add(product);

        if (product.Axis1Name is null)
            _db.ProductVariants.Add(BuildAutoVariant(product, now));

        await _db.SaveChangesAsync(ct);

        var saved = await LoadAsync(product.Id, licenseId.Value, ct);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, ToDto(saved!));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpsertRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        var invalid = Validate(req);
        if (invalid is not null) return invalid;

        var categoryError = await ValidateCategoryAsync(req.CategoryId, licenseId.Value, ct);
        if (categoryError is not null) return categoryError;

        var code = NormalizeCode(req.Code);
        if (code.Length == 0) code = product.Code;
        if (code != product.Code && await _db.Products.AnyAsync(
                p => p.LicenseId == licenseId.Value && p.Code == code, ct))
        {
            return Problem(title: "duplicate-code",
                detail: $"'{code}' kodu zaten kullanılıyor.", statusCode: 409);
        }

        var newAxis1 = Trim(req.Axis1Name);
        var newAxis2 = Trim(req.Axis2Name);

        // Rol, adı boş olan eksende null'lanır. Bu normalleştirme hem aşağıdaki
        // atamayı hem de kıyaslamayı besliyor — tek kaynaktan; ikisi ayrışırsa
        // hiçbir şeyi değiştirmeyen bir kaydetme "değişti" görünüp 409'a düşer.
        var newRole1 = newAxis1 is null ? null : req.Axis1Role;
        var newRole2 = newAxis2 is null ? null : req.Axis2Role;

        // Eksen KİMLİĞİ = (ad, rol) ikilisi; varyant değerleri eksene konumla bağlı,
        // referansla değil. Bu yüzden kural bilerek katı: dört alandan herhangi biri
        // değişirse, değerli varyant varken tümü reddedilir.
        //
        // Daha dar bir kural (yalnız takas + rol değişimi engelle, yeniden adlandırmaya
        // izin ver) BİLEREK seçilmedi: yazım düzeltme ("Renkk"→"Renk") ile anlam
        // değiştirme ("Renk"→"Beden") string olarak AYIRT EDİLEMEZ; ayırmayı deneyen
        // her kural sezgiseldir ve vaka eklendikçe çürür. Bedeli de yok — kapı yalnız
        // değerli varyant varken kapanır, kart yeni açıkken yeniden adlandırma bedava.
        var axisIdentityChanged =
            !string.Equals(product.Axis1Name, newAxis1, StringComparison.Ordinal)
            || !string.Equals(product.Axis2Name, newAxis2, StringComparison.Ordinal)
            || product.Axis1Role != newRole1
            || product.Axis2Role != newRole2;

        if (axisIdentityChanged)
        {
            var hasValued = product.Variants.Any(
                v => v.Axis1Value is not null || v.Axis2Value is not null);
            if (hasValued)
                return Problem(title: "axis-in-use",
                    detail: "Eksenin adı ya da rolü, dolu varyantlar dururken "
                          + "değiştirilemez (eksen açıp kapatmak da dahil). "
                          + "Önce varyantları silmelisin.",
                    statusCode: 409);

            _db.ProductVariants.RemoveRange(product.Variants.ToList());
            product.Variants.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        product.CategoryId = req.CategoryId;
        product.Code = code;
        product.Name = req.Name.Trim();
        product.DefaultPrice = req.DefaultPrice;
        product.Cost = req.Cost;
        product.Axis1Name = newAxis1;
        product.Axis1Role = newRole1;
        product.Axis2Name = newAxis2;
        product.Axis2Role = newRole2;
        product.UpdatedAt = now;

        // Eksensiz ürün her zaman tek bir otomatik varyant taşır (eksen yeni
        // kapatıldıysa satır az önce silinmiş olabilir).
        if (product.Axis1Name is null && product.Variants.Count == 0)
        {
            // Önce navigasyona, sonra DbSet'e: EF'in fixup'ı koleksiyonda zaten varsa
            // bir daha eklemez. Ters sırada aynı satır listeye İKİ kez giriyor ve
            // yanıt DTO'su varyantı çift gösteriyordu (DB'de tek satır vardı).
            var created = BuildAutoVariant(product, now);
            product.Variants.Add(created);
            _db.ProductVariants.Add(created);
        }

        SyncVariantCodes(product, now);

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(product));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        var photoKey = product.PhotoObjectKey;

        _db.ProductVariants.RemoveRange(product.Variants);
        _db.Products.Remove(product);
        await _db.SaveChangesAsync(ct);

        // Sıra kasıtlı: ÖNCE DB commit, SONRA R2 silme. Tersi olsaydı commit
        // başarısız olduğunda hâlâ duran ürünün fotoğrafı silinmiş olurdu —
        // yani kurtarılamaz veri kaybı. Bu sırayla en kötü hâl, ikisi arasında
        // süreç ölürse kovada kalan bir yetim nesne; onu da gecelik
        // ProductPhotoOrphanCleanupJob süpürüyor.
        //
        // Bu inline silme tek başına YETMEZ: DB'ye hiç yazılmamış anahtarlar
        // (presigned yükleme yapılıp Attach edilmeyen dosyalar) ve lisans
        // cascade'iyle giden ürünler buradan geçmez. Kovayı listeleyen
        // mutabakat işi o yüzden var.
        if (!string.IsNullOrWhiteSpace(photoKey))
            await _storage.DeleteAsync(photoKey, ct);

        return NoContent();
    }

    private IActionResult? Validate(UpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-name",
                detail: "Ürün adı boş olamaz.", statusCode: 400);

        if (req.DefaultPrice < 0 || req.Cost < 0)
            return Problem(title: "invalid-price",
                detail: "Fiyat ve maliyet negatif olamaz.", statusCode: 400);

        var axis1 = Trim(req.Axis1Name);
        var axis2 = Trim(req.Axis2Name);

        if (axis1 is null && axis2 is not null)
            return Problem(title: "axis-order",
                detail: "İkinci eksen için önce birinci ekseni tanımlamalısın.", statusCode: 400);

        if ((axis1 is not null && req.Axis1Role is null)
            || (axis2 is not null && req.Axis2Role is null))
            return Problem(title: "missing-axis-role",
                detail: "Her eksenin rolü seçilmeli (satıcı ya da izleyici).", statusCode: 400);

        if (axis1 is not null && axis2 is not null && req.Axis1Role == req.Axis2Role)
            return Problem(title: "duplicate-axis-role",
                detail: "İki eksene aynı rol verilemez.", statusCode: 400);

        return null;
    }

    private async Task<IActionResult?> ValidateCategoryAsync(
        Guid? categoryId, Guid licenseId, CancellationToken ct)
    {
        if (categoryId is null) return null;

        var exists = await _db.Categories.AnyAsync(
            c => c.Id == categoryId.Value && c.LicenseId == licenseId, ct);

        return exists
            ? null
            : Problem(title: "category-not-found", detail: "Kategori bulunamadı.", statusCode: 400);
    }

    private static ProductVariant BuildAutoVariant(Product product, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = product.LicenseId,
        ProductId = product.Id,
        VariantCode = VariantCodeBuilder.Build(product.Code, null, null),
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// <c>VariantCode</c> türetilmiş bir değer ve türetmenin sahibi ürün kartı:
    /// ürün kodu değiştiğinde bayat kalmasın diye TÜM varyantlar yeniden
    /// hesaplanır (eksensiz otomatik satır da bunun sıradan bir hâli).
    ///
    /// <c>Barcode</c>'a bilerek dokunulmaz — o ayrı ve değişmez fiziksel kimlik;
    /// ürün adı/kodu değişse de rafta duran etiket geçerli kalmalı.
    /// </summary>
    private static void SyncVariantCodes(Product product, DateTimeOffset now)
    {
        foreach (var variant in product.Variants)
        {
            var code = VariantCodeBuilder.Build(
                product.Code, variant.Axis1Code, variant.Axis2Code);
            if (variant.VariantCode == code) continue;

            variant.VariantCode = code;
            variant.UpdatedAt = now;
        }
    }

    private Task<Product?> LoadAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private static string NormalizeCode(string? code)
        => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProductDto ToDto(Product p) => new(
        p.Id, p.CategoryId, p.Code, p.Name, p.DefaultPrice, p.Cost,
        p.Axis1Name, p.Axis1Role, p.Axis2Name, p.Axis2Role,
        p.PhotoObjectKey, p.IsArchived, p.CreatedAt, p.UpdatedAt,
        p.Variants
            .OrderBy(v => v.VariantCode, StringComparer.Ordinal)
            .Select(v => new VariantDto(
                v.Id, v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
                v.VariantCode, v.Barcode, v.IsActive))
            .ToList());

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
