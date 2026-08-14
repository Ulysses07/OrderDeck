using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Catalog;
using OrderDeck.Shared.Text;

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
        Guid? CategoryId,
        decimal DefaultPrice,
        decimal? Cost,
        [MaxLength(CatalogLimits.ShelfLocation)] string? ShelfLocation,
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
        string? ShelfLocation,
        string? Axis1Name,
        AxisRole? Axis1Role,
        string? Axis2Name,
        AxisRole? Axis2Role,
        IReadOnlyList<PanelProductPhotoController.PhotoDto> Photos,
        bool IsArchived,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<VariantDto> Variants);

    public sealed record ProductRowDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        string? ShelfLocation,
        decimal DefaultPrice,
        bool IsArchived,
        string? CoverUrl,
        int VariantCount,
        DateTimeOffset UpdatedAt);

    public sealed record ProductPageDto(IReadOnlyList<ProductRowDto> Items, int Total);

    /// <summary>
    /// Stok elemanı maliyeti ne görür ne yazar (spec: "ciro bilgilerini
    /// göremez" — alış maliyeti kârın ta kendisi). Kart sahibi (Customer
    /// token) ve <c>staff</c> operatörü etkilenmez.
    ///
    /// Maskelenen alan için AYRI bir DTO tipi bilerek yapılmadı: sözleşme ikiye
    /// bölünürse panel aynı ucun iki şeklini bilmek zorunda kalır. Tek şekli
    /// koruyup alanı <c>null</c> döndürmek hem istemciyi hem OpenAPI'yi sade
    /// tutuyor.
    /// </summary>
    private bool HidesCost => User.GetOperatorRole() == OperatorRoles.Stock;

    [AllowStockStaff]
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

            // Alt ağaç filtresi TEK ifadede kalıyor: alt kategori id'lerini
            // istemciye çekip geri `IN (@p0, @p1, …)` olarak göndermek hem
            // fazladan bir gidiş-dönüş, hem de SQL Server'ın 2100 parametre
            // sınırına çarpma riski — derin bir ağaçta sorgu tamamen patlardı.
            query = query.Where(p => _db.Categories.Any(c =>
                c.Id == p.CategoryId
                && c.LicenseId == licenseId.Value
                && c.Path.StartsWith(path)));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // Aranan iğne de saklanan değer de AYNI normalleştiriciden geçiyor →
            // eşleşme veritabanının collation'ından bağımsız (SQL Server duyarsız,
            // PostgreSQL duyarlı; göçte davranış değişmesin).
            //
            // `Code` sistem tarafından üretilir (SK00001…) ve normalleştirilmemiş
            // sayısal bir sonek içerir; bu nedenle kod araması da needle üzerinden
            // Contains ile çalışır — ayrı bir normalleştirme adımı gerekmez.
            var needle = SearchNormalizer.Normalize(q);
            if (needle.Length > 0)
                query = query.Where(
                    p => p.NameSearch.Contains(needle) || p.Code.Contains(needle));
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
                p.Id, p.CategoryId, p.Code, p.Name, p.ShelfLocation, p.DefaultPrice, p.IsArchived,
                null /* CoverUrl — aşağıda doldurulacak */, p.Variants.Count, p.UpdatedAt))
            .ToListAsync(ct);

        // Sayfadaki ürünlerin kapakları TEK sorguda çekiliyor; ürün başına sorgu
        // (N+1) atılmıyor. Presigned URL üretimi ağ çağrısı değil, yerel HMAC
        // imzalama — 50 satır için maliyeti ihmal edilebilir. Bu yüzden panele
        // anahtar değil, doğrudan kullanılabilir URL dönüyoruz ve panel ikinci
        // tur istek atmıyor.
        //
        // GroupBy + First() SQL Server'a çevrilemeyebilir; bu yüzden belleğe alıp
        // gruplamak tercih edildi. Sayfa başına en fazla 50×4 = 200 satır; bu
        // ölçekte bellek gruplaması N+1 sorgusundan çok daha ucuz.
        var ids = rows.Select(r => r.Id).ToList();
        var coverKeys = (await _db.ProductPhotos.AsNoTracking()
            .Where(p => ids.Contains(p.ProductId))
            .Select(p => new { p.ProductId, p.ObjectKey, p.SortOrder })
            .ToListAsync(ct))
            .GroupBy(p => p.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p.SortOrder).First().ObjectKey);

        var items = new List<ProductRowDto>(rows.Count);
        foreach (var row in rows)
        {
            var coverUrl = coverKeys.TryGetValue(row.Id, out var key)
                ? await _storage.CreateDownloadUrlAsync(key, ct)
                : null;
            items.Add(row with { CoverUrl = coverUrl });
        }

        return Ok(new ProductPageDto(items, total));
    }

    public sealed record AxisValuesDto(string Name, IReadOnlyList<string> Values);

    private const int MaxAxisValueSuggestions = 100;

    /// <summary>
    /// Aynı lisansta bu eksen adı altında daha önce kullanılmış değerler.
    /// Eksen değerleri ürüne özel tutuluyor; bu uç olmadan her yeni üründe
    /// S/M/L/XL yeniden yazılmak zorunda.
    ///
    /// <b>Öneridir, zorlayıcı değil.</b> Eksen adı eşleşmesi tam eşitlik:
    /// harf duyarlılığı veritabanının collation'ına kalıyor (SQL Server
    /// duyarsız, PostgreSQL duyarlı olacak). Bu bilinçli — eşleşmeyen ad
    /// yalnız <i>daha az öneri</i> demek, yanlış veri demek değil. Bunun için
    /// ayrı bir normalleştirilmiş kolon açmak, kazandığından fazlasına mal
    /// olurdu.
    /// </summary>
    [AllowStockStaff]
    [HttpGet("axis-values")]
    public async Task<IActionResult> AxisValues(
        [FromQuery] string? name, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var axisName = (name ?? string.Empty).Trim();
        if (axisName.Length == 0)
            return Problem(title: "missing-axis-name",
                detail: "Eksen adı gerekli.", statusCode: 400);

        var fromAxis1 = await _db.ProductVariants.AsNoTracking()
            .Where(v => v.LicenseId == licenseId
                        && v.Axis1Value != null
                        && v.Product.Axis1Name == axisName)
            .Select(v => v.Axis1Value!)
            .Distinct()
            .ToListAsync(ct);

        var fromAxis2 = await _db.ProductVariants.AsNoTracking()
            .Where(v => v.LicenseId == licenseId
                        && v.Axis2Value != null
                        && v.Product.Axis2Name == axisName)
            .Select(v => v.Axis2Value!)
            .Distinct()
            .ToListAsync(ct);

        // Son tekilleştirme bellekte: "Siyah" ile "siyah" kullanıcı için aynı
        // öneri. Ölçüt arama ile ORTAK normalleştirici, kopyası yazılmıyor.
        var values = fromAxis1.Concat(fromAxis2)
            .GroupBy(SearchNormalizer.Normalize, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAxisValueSuggestions)
            .ToList();

        return Ok(new AxisValuesDto(axisName, values));
    }

    [AllowStockStaff]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        return Ok(await ToDtoAsync(product, ct));
    }

    [AllowStockStaff]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var invalid = Validate(req);
        if (invalid is not null) return invalid;

        var categoryError = await ValidateCategoryAsync(req.CategoryId, licenseId.Value, ct);
        if (categoryError is not null) return categoryError;

        // Kod SİSTEMİN: istemci gövdesinde yok, buradan üretiliyor. Operatörün
        // yayında söylediği kod ayrı bir kavram (ProductBroadcastCode).
        var codes = await _db.Products
            .Where(p => p.LicenseId == licenseId.Value)
            .Select(p => p.Code)
            .ToListAsync(ct);
        var code = StockCodeSequence.Next(codes);

        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            CategoryId = req.CategoryId,
            Code = code,
            Name = req.Name.Trim(),
            DefaultPrice = req.DefaultPrice,
            // Update'teki gibi bir korumaya gerek yok: doğrulama geçtiyse stok
            // rolünde req.Cost zaten null ve yeni kartın maliyeti doğal olarak boş.
            Cost = req.Cost,
            ShelfLocation = Trim(req.ShelfLocation),
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

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Yarış: iki istek aynı anda sıradaki numarayı okudu ve aynı kodu
            // üretti (çift tıklama). Sebebi SQL hata numarasından (2601/2627)
            // ayıklamıyoruz: sağlayıcıya bağımlı olur ve PostgreSQL göçünde
            // sessizce çürür. Tekrar SORMAK hem sağlayıcıdan bağımsız hem kesin.
            //
            // Yeniden deneme (kodu tazeleyip tekrar kaydetme) BİLEREK yapılmadı:
            // istisna sonrası entity'leri detach edip yeniden kurmak gerekirdi ve
            // o yol EF InMemory'de hiç çalışmaz (benzersiz indeks zorlanmıyor) —
            // yani hiç test edilemeyen bir kurtarma kodu eklerdik. Operatör
            // kaydete bir daha basınca yeni numara üretilir.
            // SaveChanges patladıktan sonra ChangeTracker'da başarısız ürün hâlâ
            // Added durumunda kalır. AsNoTracking olmazsa EF bu kalıntı entity'yi
            // sonuçla karıştırabilir; AnyAsync şu an EXISTS'e döndüğü için pratikte
            // fark etmez, ama ileride FirstOrDefault'a dönüşürse yanlış cevap verir.
            var raced = await _db.Products.AsNoTracking().AnyAsync(
                p => p.LicenseId == licenseId.Value && p.Code == code && p.Id != product.Id, ct);
            if (raced)
                return Problem(title: "code-race",
                    detail: "Ürün kodu üretilirken çakışma oldu. Lütfen tekrar kaydet.",
                    statusCode: 409);
            throw; // Benzersizlik değilse (örn. eşzamanlı silinen kategorinin FK'sı)
                   // yutma — bilinmeyen veri hatası 500 olarak görünmeli.
        }

        var saved = await LoadAsync(product.Id, licenseId.Value, ct);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, await ToDtoAsync(saved!, ct));
    }

    [AllowStockStaff]
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

            // Eksensiz kartın otomatik varyantı hasValued'a TAKILMAZ (BuildAutoVariant
            // eksen değeri doldurmuyor, ikisi de null) — ama o varyantın defteri
            // olabilir: stok elemanı eksensiz ürüne pekâlâ mal kabul girer. Bu
            // satırlar duruyorken RemoveRange, Restrict FK'sına çarpıp 500'e düşerdi
            // ve ürün bir daha ASLA eksen kazanamazdı (hareket de silinemez).
            //
            // Kontrolün yeri kasıtlı: RemoveRange'ten ÖNCE ve sahiplik/lisans
            // doğrulandıktan SONRA. Aşağı kaydırılırsa silme zaten denenmiş olur —
            // Türkçe 409 yerine yine 500 döneriz. Yukarı taşınırsa başka lisansın
            // defteri 409/404 farkından sızar.
            var variantIds = product.Variants.Select(v => v.Id).ToList();
            var hasMovements = await _db.StockMovements
                .AnyAsync(m => m.ProductVariantId != null
                               && variantIds.Contains(m.ProductVariantId.Value), ct);
            if (hasMovements)
                return Problem(title: "axis-in-use-stock",
                    detail: "Bu ürünün stok hareketleri var; eksen yapısı artık "
                          + "değiştirilemez (eksen açıp kapatmak da dahil). "
                          + "Hareketler geçmiş satışların dayanağı olduğu için "
                          + "silinemez. Farklı bir eksen yapısı gerekiyorsa yeni "
                          + "bir ürün kartı açmalısın.",
                    statusCode: 409);

            _db.ProductVariants.RemoveRange(product.Variants.ToList());
            product.Variants.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        product.CategoryId = req.CategoryId;
        product.Name = req.Name.Trim();
        product.DefaultPrice = req.DefaultPrice;
        // Stok elemanı maliyeti göremediği için gövdeye de koyamaz; gelen null'ı
        // "sil" diye okumak, sadece adını düzelten bir tur-gidiş-dönüşte gerçek
        // maliyeti sessizce siler. Doğrulama bunu yakalayamaz — positional
        // record'da "null gönderildi" ile "alan hiç gönderilmedi" ayırt edilemez.
        // Bu rolde alan hiç dokunulmadan bırakılıyor.
        product.Cost = HidesCost ? product.Cost : req.Cost;
        product.ShelfLocation = Trim(req.ShelfLocation);
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

        // Kod artık burada değişmiyor; benzersizlik yarışı yalnız Create'te olabilir.
        await _db.SaveChangesAsync(ct);

        return Ok(await ToDtoAsync(product, ct));
    }

    [AllowStockStaff]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        // Restrict FK zaten silmeyi engelliyor; buradaki kontrol kullanıcıya
        // 500 yerine Türkçe bir açıklama vermek için. Defteri olan ürün
        // silinemez — silinseydi geçmiş satışların dayanağı kaybolurdu.
        //
        // Kontrolün yeri kasıtlı: sahiplik doğrulandıktan SONRA. Önce olsaydı
        // başka lisansın ürününde hareket olup olmadığı 409/404 farkından
        // sızardı.
        var hasMovements = await _db.StockMovements
            .AnyAsync(m => m.ProductId == id, ct);
        if (hasMovements)
            return Problem(title: "product-has-stock-movements",
                detail: "Bu ürünün stok hareketleri var; silinemez. Arşivleyebilirsiniz.",
                statusCode: 409);

        // Galeri fotoğraflarının anahtarlarını DB commit öncesinde toplayıyoruz
        // — commit sonrası satırlar gider, anahtara erişemeyiz.
        var galleryKeys = await _db.ProductPhotos
            .Where(p => p.ProductId == product.Id)
            .Select(p => p.ObjectKey)
            .ToListAsync(ct);

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
        foreach (var key in galleryKeys)
            await _storage.DeleteAsync(key, ct);

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

        // Slug bilerek "stock-staff-forbidden"dan ayrı: o "bu uç tamamen kapalı"
        // demek, bu ise "uç açık ama şu alan yasak". Panel ikisini ayrı ele
        // alabilmeli (biri sayfayı gizler, öbürü tek girdiyi).
        if (HidesCost && req.Cost is not null)
            return Problem(title: "cost-forbidden",
                detail: "Stok elemanı maliyet bilgisini göremez ve değiştiremez.",
                statusCode: 403);

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

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// <see cref="ProductDto"/> YALNIZ burada kuruluyor; maliyet maskesi de o
    /// yüzden burada. Çağrı yerine (Get/Create/Update) yazılan bir maske, yarın
    /// eklenen dördüncü çağrıda unutulurdu — kural atlanamayacağı tek noktada.
    /// Metot bu yüzden static değil: rol <c>User</c>'dan okunuyor.
    /// </summary>
    private async Task<ProductDto> ToDtoAsync(Product p, CancellationToken ct)
    {
        var photos = await _db.ProductPhotos.AsNoTracking()
            .Where(x => x.ProductId == p.Id)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);

        var photoDtos = new List<PanelProductPhotoController.PhotoDto>(photos.Count);
        foreach (var photo in photos)
            photoDtos.Add(new PanelProductPhotoController.PhotoDto(
                photo.Id, photo.ObjectKey, photo.ContentType, photo.SizeBytes,
                photo.Width, photo.Height, photo.SortOrder,
                await _storage.CreateDownloadUrlAsync(photo.ObjectKey, ct)));

        return new ProductDto(
            p.Id, p.CategoryId, p.Code, p.Name, p.DefaultPrice,
            HidesCost ? null : p.Cost,
            p.ShelfLocation,
            p.Axis1Name, p.Axis1Role, p.Axis2Name, p.Axis2Role,
            photoDtos, p.IsArchived, p.CreatedAt, p.UpdatedAt,
            p.Variants
                .OrderBy(v => v.VariantCode, StringComparer.Ordinal)
                .Select(v => new VariantDto(
                    v.Id, v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
                    v.VariantCode, v.Barcode, v.IsActive))
                .ToList());
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
