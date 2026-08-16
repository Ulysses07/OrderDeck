using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;
using OrderDeck.Shared.Text;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün varyantları (Faz 1a). Varyantın <b>kodu yoktur</b>: kimliği
/// <c>Id</c>, kullanıcıya görünen adı eksen değerleridir ("Siyah · M").
/// Yayında söylenen kod ürün + satıcı ekseni seviyesinde ve ayrı bir
/// kaynakta (<see cref="ProductBroadcastCode"/>) yaşıyor.
///
/// Benzersizlik normalize eksen değerlerinde
/// (<c>Axis1ValueNorm</c>, <c>Axis2ValueNorm</c>) — türetilmiş kısaltmalarda
/// değil. Barkod yükü varyant YARATILIRKEN atanır (basım anında değil): boş
/// gönderilirse sunucu lisans sayacından doldurur, benzersizliği
/// (LicenseId, Barcode) indeksi korur.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/variants")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelProductVariantsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly BarcodeAllocator _barcodes;

    public PanelProductVariantsController(LicenseDbContext db, BarcodeAllocator barcodes)
    {
        _db = db;
        _barcodes = barcodes;
    }

    // Doğrulama attribute'ları positional record'un PARAMETRESİNE yazılıyor;
    // deponun kalıbı bu. [property:] hedefiyle ne olacağını denemedik —
    // kalıptan sapmamak için parametre üstünde duruyorlar.
    public sealed record VariantRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? Axis1Value,
        [MaxLength(CatalogLimits.AxisValue)] string? Axis2Value,
        bool IsActive,
        [MaxLength(CatalogLimits.Barcode)] string? Barcode = null);

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

        var conflict = await VariantValuesTakenAsync(product.Id, built, excludeId: null, ct);
        if (conflict is not null) return conflict;

        // Hatayı değil DEĞERİ soruyoruz: böylece derleyici barcode'u buradan
        // sonra null-olmayan sayıyor ve aşağıdaki üç kullanım susturma
        // gerektirmiyor. Değer null ise hata dolu olmak zorunda.
        var (barcode, barcodeError) = await ResolveBarcodeAsync(
            product.LicenseId, req.Barcode, ct);
        if (barcode is null) return barcodeError!;

        var barcodeConflict = await BarcodeTakenAsync(
            product.LicenseId, barcode, excludeId: null, ct);
        if (barcodeConflict is not null) return barcodeConflict;

        var now = DateTimeOffset.UtcNow;
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = built.Axis1Value,
            Axis2Value = built.Axis2Value,
            Barcode = barcode,
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
            // Yarış: ön kontrolden sonra başka bir istek aynı kırılımı aldı
            // (panelde çift tıklama ya da iki sekme yeter). Sebebi SQL hata numarasından
            // ayıklamıyoruz — sağlayıcıya bağımlı olur, PostgreSQL göçünde
            // sessizce çürür; tekrar SORMAK hem bağımsız hem kesin.
            //
            // DİKKAT — bu üç satır uçtan uca test EDİLEMEZ: EF InMemory benzersiz
            // indeksi zorlamadığı için istisna testte hiç atılmıyor. Kararın
            // kendisi bu yüzden burada değil, iki yolun da çağırdığı
            // VariantValuesTakenAsync'te duruyor; testler onu ön kontrol
            // üzerinden geçiyor ve burası yalnız tesisat kalıyor.
            //
            // excludeId null — toplu yolla (CreateBulk) aynı: satır yazılamadığı
            // için veritabanında dışlanacak bir kayıt yok. Kendi Id'sini geçmek
            // etkisiz ama aynı soruyu iki farklı biçimde sormak olurdu.
            var raced = await VariantValuesTakenAsync(product.Id, built, excludeId: null, ct);
            if (raced is not null) return raced;
            var racedBarcode = await BarcodeTakenAsync(
                product.LicenseId, barcode, excludeId: null, ct);
            if (racedBarcode is not null) return racedBarcode;
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
        //    kombinasyonu iki kez üretmiş ya da iki farklı yazım aynı değere
        //    normalize olmuş olabilir ("Siyah" / "siyah"). Bu kontrol olmasaydı
        //    hata ancak benzersiz indeksten dönerdi — yani prod'da 500 olarak.
        for (var i = 0; i < built.Count; i++)
        for (var j = i + 1; j < built.Count; j++)
            if (string.Equals(built[i].Axis1ValueNorm, built[j].Axis1ValueNorm,
                    StringComparison.Ordinal)
                && string.Equals(built[i].Axis2ValueNorm, built[j].Axis2ValueNorm,
                    StringComparison.Ordinal))
                return Problem(title: "duplicate-in-batch",
                    detail: $"'{Describe(built[j].Axis1Value, built[j].Axis2Value)}' "
                          + "listede birden fazla kez var.",
                    statusCode: 409);

        // 3) Var olanlarla çakışma — tekil uçla aynı kural, aynı metot.
        foreach (var segments in built)
        {
            var conflict = await VariantValuesTakenAsync(product.Id, segments, null, ct);
            if (conflict is not null) return conflict;
        }

        // 4) Elle yazılmış barkodların HEPSİ, ayırmadan ÖNCE doğrulanır.
        //    Sıra keyfî değil: geçersiz bir satır yüzünden buradan dönülürse
        //    sayaç hiç dokunulmamış olur. (Erken dönüşte SaveChanges
        //    çalışmadığı için ilerlemiş sayaç zaten diske yazılmazdı — ama o
        //    güvence isteğin şekline bağlı bir tesadüf; ayırmayı doğrulamanın
        //    ARDINA almak "istek başına tam bir ayırma" kuralını yapısal kılar.)
        var explicitCodes = new string?[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (Trim(items[i].Barcode) is null) continue;

            var (resolved, barcodeError) = await ResolveBarcodeAsync(
                product.LicenseId, items[i].Barcode, ct);
            // Değer üzerinden soruyoruz ki derleyici daraltabilsin: null bir
            // yuva aşağıda "boş, ayırma gerekiyor" demek: hatayı değerden
            // ayrı sorsaydık, hatasız gelen bir null autoCount'u şişirirdi.
            if (resolved is null) return barcodeError!;
            explicitCodes[i] = resolved;
        }

        // Tek ayırma çağrısı: ayırıcı yalnız KAYDEDİLMİŞ satırları görüyor,
        // parti içinde ikinci kez çağırmak aynı numaraları verirdi.
        var autoCount = explicitCodes.Count(c => c is null);
        var pool = new Queue<string>(
            await _barcodes.AllocateAsync(product.LicenseId, autoCount, ct));

        // Konum eşlemesi korunuyor: barcodes[i] ↔ items[i] ↔ built[i]. Aşağıdaki
        // Select((segments, i) => …) bu hizaya güveniyor; listenin uzunluğu ve
        // sırası items ile birebir aynı kalmalı.
        var barcodes = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++)
            barcodes.Add(explicitCodes[i] ?? pool.Dequeue());

        // Parti içi tekrar — eksen değerlerindekiyle aynı gerekçe: benzersiz
        // indeksten dönen hata prod'da 500 olurdu.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in barcodes)
            if (!seen.Add(code))
                return Problem(title: "duplicate-barcode-in-batch",
                    detail: $"'{code}' barkodu listede birden fazla kez var.",
                    statusCode: 409);

        foreach (var code in barcodes)
        {
            var barcodeConflict = await BarcodeTakenAsync(
                product.LicenseId, code, excludeId: null, ct);
            if (barcodeConflict is not null) return barcodeConflict;
        }

        var now = DateTimeOffset.UtcNow;
        var variants = built.Select((segments, i) => new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = segments.Axis1Value,
            Axis2Value = segments.Axis2Value,
            Barcode = barcodes[i],
            IsActive = items[i].IsActive,
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
                var raced = await VariantValuesTakenAsync(product.Id, segments, null, ct);
                if (raced is not null) return raced;
            }
            foreach (var code in barcodes)
            {
                var racedBarcode = await BarcodeTakenAsync(
                    product.LicenseId, code, excludeId: null, ct);
                if (racedBarcode is not null) return racedBarcode;
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

        var conflict = await VariantValuesTakenAsync(product.Id, built, id, ct);
        if (conflict is not null) return conflict;

        // Boş gönderilen barkod, MEVCUT değeri korur. "Sil" anlamına gelseydi
        // panelde alanı temizleyen bir yanlış tıklama basılı etiketi
        // geçersizleştirirdi; barkodsuz varyant zaten var olamaz.
        //
        // Mevcut değer BOŞSA (kolon henüz nullable; barkod öncesi yazılmış eski
        // satırlar) burası onu doldurmuyor — bilerek. Eski satırların iyileştirmesi
        // bu planın ilerleyen görevindeki NOT NULL göçüne ve onunla gelen
        // geri-doldurmaya ait. Güncelleme yolu böylece saf kalıyor: yalnız
        // ÇAĞIRANIN istediğini yapıyor, yan etkiyle veri onarmıyor. Aksi hâlde
        // "sadece IsActive'i kapat" isteği sessizce barkod da atardı.
        var requestedBarcode = Trim(req.Barcode);
        if (requestedBarcode is not null
            && !string.Equals(requestedBarcode, variant.Barcode, StringComparison.Ordinal))
        {
            var (resolved, barcodeError) = await ResolveBarcodeAsync(
                product.LicenseId, requestedBarcode, ct);
            if (resolved is null) return barcodeError!;

            var barcodeConflict = await BarcodeTakenAsync(
                product.LicenseId, resolved, id, ct);
            if (barcodeConflict is not null) return barcodeConflict;

            variant.Barcode = resolved;
        }

        // Eski satıcı değeri, atamalardan ÖNCE okunmalı: aşağıdaki satırlar
        // variant'ı yerinde değiştiriyor.
        var oldSellerNorm = SearchNormalizer.Normalize(product.SellerAxisValueOf(variant));

        var now = DateTimeOffset.UtcNow;
        variant.Axis1Value = built.Axis1Value;
        variant.Axis2Value = built.Axis2Value;
        variant.IsActive = req.IsActive;
        variant.UpdatedAt = now;
        product.UpdatedAt = now;

        // Satıcı ekseni değeri yeniden adlandırıldıysa yayın kodunu da taşı.
        // Kod, ürün + satıcı ekseni DEĞERİNE bağlı; değer değişip kod yerinde
        // kalsaydı kod hiçbir kırılıma çözülemez hâle gelirdi.
        //
        // Şart: eski değeri taşıyan BAŞKA varyant kalmamış olmalı. Kalmışsa bu
        // yeniden adlandırma değil, tek satırın başka değere geçirilmesidir ve
        // eski kod hâlâ geçerli bir kırılımı gösteriyor.
        //
        // Aynı SaveChanges içinde: ayrı bir kaydetme, arada düşen bir istekte
        // kodu sahipsiz bırakırdı.
        var newSellerNorm = SearchNormalizer.Normalize(product.SellerAxisValueOf(variant));
        if (product.SellerAxis != 0
            && oldSellerNorm.Length > 0
            && !string.Equals(oldSellerNorm, newSellerNorm, StringComparison.Ordinal))
        {
            var stillUsed = product.Variants.Any(v =>
                v.Id != variant.Id
                && string.Equals(
                    SearchNormalizer.Normalize(product.SellerAxisValueOf(v)),
                    oldSellerNorm, StringComparison.Ordinal));

            if (!stillUsed)
            {
                var newSellerValue = product.SellerAxisValueOf(variant);
                // LicenseId süzgeci burada gereksiz — ürün sahiplik kontrolünden
                // geçti ve ProductId kiracıyı zaten belirliyor. Yine de duruyor:
                // bu depoda yayın kodu sorguları İSTİSNASIZ kiracıyla süzülüyor
                // (bkz. PanelBroadcastCodesController) ve tek bir istisna, çok
                // kiracılı bir sistemde okuyanı "demek ki şart değilmiş"e götürür.
                var affected = await _db.ProductBroadcastCodes
                    .Where(x => x.LicenseId == product.LicenseId
                                && x.ProductId == product.Id)
                    .ToListAsync(ct);

                foreach (var codeRow in affected)
                    if (string.Equals(
                            SearchNormalizer.Normalize(codeRow.SellerAxisValue),
                            oldSellerNorm, StringComparison.Ordinal))
                        codeRow.SellerAxisValue = newSellerValue;
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Create'teki yarışın aynısı (gerekçe orada); kendi satırı çakışma
            // sayılmasın diye dışlanıyor.
            var raced = await VariantValuesTakenAsync(product.Id, built, id, ct);
            if (raced is not null) return raced;

            // Barkod da aynı işlemde yazılıyor; yarışı Create/CreateBulk ile
            // birebir aynı biçimde sınıflandırıyoruz, yoksa aynı çakışma uca
            // göre farklı cevap alırdı. Fark yalnız excludeId: bu satır zaten
            // veritabanında duruyor, kendi barkodu çakışma sayılmamalı.
            var racedBarcode = await BarcodeTakenAsync(
                product.LicenseId, variant.Barcode, id, ct);
            if (racedBarcode is not null) return racedBarcode;
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

        // Restrict FK zaten engelliyor; bu kontrol 500 yerine Türkçe açıklama
        // dönmek için. Sahiplik doğrulandıktan SONRA çalışıyor — aksi hâlde
        // başka lisansın varyantının defteri 409/404 farkından sızardı.
        var hasMovements = await _db.StockMovements
            .AnyAsync(m => m.ProductVariantId == id, ct);
        if (hasMovements)
            return Problem(title: "variant-has-stock-movements",
                detail: "Bu varyantın stok hareketleri var; silinemez. Pasife alabilirsiniz.",
                statusCode: 409);

        _db.ProductVariants.Remove(variant);
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private readonly record struct Segments(
        string? Axis1Value, string? Axis2Value,
        string Axis1ValueNorm, string Axis2ValueNorm);

    /// <summary>
    /// Eksen değerlerini doğrular ve karşılaştırma biçimlerini kurar.
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

        // Normalleştirici arama, benzersizlik ve canlı eşleştirme ile ORTAK
        // (SearchNormalizer): kopyası yazılsaydı tanımlar zamanla ayrışırdı.
        // Kolonun kendisi SaveChanges zincirinde de aynı fonksiyonla doluyor;
        // buradaki hesap yalnız ÖN kontrol sorgusu için.
        return new Segments(
            axis1Value, axis2Value,
            SearchNormalizer.Normalize(axis1Value),
            SearchNormalizer.Normalize(axis2Value));
    }

    /// <summary>
    /// Code128 yalnız ASCII 32-126 basar. Türkçe harf ya da kontrol karakteri
    /// içeren bir yük, yazıcıya gitse okunamayan sembol üretirdi — kabulü
    /// burada, yazma yolunun başında kesiyoruz.
    /// </summary>
    private static bool IsPrintableCode128(string s) =>
        s.All(c => c >= ' ' && c <= '~');

    /// <summary>
    /// Barkod yükünü hazırlar: elle yazılmışsa doğrular, boşsa sayaçtan ayırır.
    /// Hata, geri callback ile değil dönen ikilinin <c>Error</c> alanıyla
    /// bildirilir — böylece çağıranın kontrolü atlaması hâlinde <c>Value</c>
    /// null kaldığı için derleyici uyarabiliyor; callback kalıbında her çağrı
    /// yerinde bir <c>!</c> gerekiyordu ve bu güvenceyi susturuyordu.
    ///
    /// <para><b>Boş = hata değil:</b> kural "kullanıcı barkod yazsın" değil,
    /// "barkodsuz varyant var olmasın". Sunucu boşluğu kendisi doldurunca
    /// kural ihlal edilemez hâle geliyor ve ileride gelecek Excel toplu
    /// içe aktarımı barkod sütunu boş bir dosyayla da çalışabiliyor.</para>
    ///
    /// <para>Uzunluk burada KONTROL EDİLMİYOR: <c>VariantRequest.Barcode</c>
    /// üzerindeki <c>[MaxLength]</c> sayesinde taşan girdi <c>[ApiController]</c>
    /// model doğrulamasında 400 alıyor ve eylem gövdesine hiç ulaşmıyor.
    /// Buradaki ikinci kontrol ölü koddu.</para>
    /// </summary>
    private async Task<(string? Value, IActionResult? Error)> ResolveBarcodeAsync(
        Guid licenseId, string? requested, CancellationToken ct)
    {
        var trimmed = Trim(requested);
        if (trimmed is null)
        {
            var allocated = await _barcodes.AllocateAsync(licenseId, 1, ct);
            return (allocated[0], null);
        }

        if (!IsPrintableCode128(trimmed))
            return (null, Problem(title: "barcode-not-printable",
                detail: "Barkod yalnız İngiliz alfabesi harfleri, rakam ve "
                      + "temel noktalama içerebilir (Code128).",
                statusCode: 400));

        return (trimmed, null);
    }

    /// <summary>
    /// Bu barkod lisansta zaten kullanılıyorsa 409 döndürür, yoksa null.
    /// <c>VariantValuesTakenAsync</c> ile aynı gerekçe: hem SaveChanges öncesi
    /// ön kontrol hem sonrası yarış sınıflandırması tek metottan geçsin.
    /// </summary>
    private async Task<IActionResult?> BarcodeTakenAsync(
        Guid licenseId, string barcode, Guid? excludeId, CancellationToken ct)
    {
        var exists = await _db.ProductVariants
            .AsNoTracking()
            .AnyAsync(v => v.LicenseId == licenseId
                           && v.Barcode == barcode
                           && (excludeId == null || v.Id != excludeId), ct);

        if (!exists) return null;

        return Problem(title: "duplicate-barcode",
            detail: $"'{barcode}' barkodu başka bir varyantta kullanılıyor.",
            statusCode: 409);
    }

    /// <summary>
    /// Bu kırılım üründe zaten varsa 409 döndürür, yoksa null.
    ///
    /// <para>Tek bir çakışma türü kaldı: <c>duplicate-variant</c>. Eski
    /// <c>variant-code-collision</c> dalı, türetilmiş kısaltmaların yapay
    /// çakışmasıydı ("Kırmızı" ve "Kırmızılı" ikisi de KIRM) — benzersizlik
    /// değerin kendisine taşındığı için o durum artık çakışma değil.</para>
    ///
    /// <para>Hem <c>SaveChanges</c> ÖNCESİ ön kontrol hem SONRASI yarış
    /// sınıflandırması buradan geçiyor: iki ayrı kopya olsaydı biri değişip
    /// öbürü kalır, aynı çakışma isteğin zamanlamasına göre farklı cevap
    /// alırdı.</para>
    ///
    /// <para>Sorgu <c>AsNoTracking</c>: <see cref="DbUpdateException"/> sonrası
    /// ChangeTracker'da başarısız varyant hâlâ <c>Added</c> durumunda kalır.
    /// <c>AnyAsync</c> şu an <c>EXISTS</c>'e çevrildiği ve entity materyalize
    /// etmediği için kimlik çözümlemesi devreye girmiyor — yani pratikte fark
    /// etmiyor. Yine de kalıyor: sorgu ileride <c>FirstOrDefault</c>'a dönerse
    /// izlenen kirli kayıt sonuca karışıp yanlış cevap verir.</para>
    /// </summary>
    private async Task<IActionResult?> VariantValuesTakenAsync(
        Guid productId, Segments built, Guid? excludeId, CancellationToken ct)
    {
        var axis1 = built.Axis1ValueNorm;
        var axis2 = built.Axis2ValueNorm;

        var exists = await _db.ProductVariants
            .AsNoTracking()
            .AnyAsync(v => v.ProductId == productId
                           && v.Axis1ValueNorm == axis1
                           && v.Axis2ValueNorm == axis2
                           && (excludeId == null || v.Id != excludeId), ct);

        if (!exists) return null;

        return Problem(title: "duplicate-variant",
            detail: $"'{Describe(built.Axis1Value, built.Axis2Value)}' varyantı "
                  + "bu üründe zaten var.",
            statusCode: 409);
    }

    /// <summary>
    /// Mesajlarda değer kodla değil ham eksen değeriyle anılır; iki eksende
    /// "Siyah / M".
    ///
    /// <para>DİKKAT — anılan hâl <b>bu istekte yazılan</b> değerdir, çakışan
    /// satırın kayıtlı hâli değil: çakışan satır artık veritabanından geri
    /// okunmuyor. Yani kullanıcı "  kirmizi  " yazarsa kartta "Kırmızı" görünse
    /// bile mesaj "'kirmizi' zaten var" der. Kabul edildi: normalize eşleşme
    /// zaten yazımdan bağımsız ve kullanıcı kendi yazdığını tanır.</para>
    /// </summary>
    private static string Describe(string? axis1Value, string? axis2Value)
        => axis2Value is null
            ? axis1Value ?? string.Empty
            : $"{axis1Value} / {axis2Value}";

    private Task<Product?> LoadProductAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PanelProductsController.VariantDto ToDto(ProductVariant v) => new(
        v.Id, v.Axis1Value, v.Axis2Value, v.Barcode, v.IsActive);

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
