using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Services.Sync.Commands;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF "Ödeme iste" akışı için bakiye uygulama endpoint'i. Yayıncı WhatsApp
/// mesajı atmadan önce burayı çağırır:
///   - "Bu müşterinin şu kadar bakiyesi var mı, ne kadarı uygulanabilir?" sorusu
///   - Onay → ledger'a purchase-deduction düşülür
///
/// İstemci (WPF) önce <c>preview</c> ile balance'ı çeker, mesajı oluştururken
/// gösterir, kullanıcı onaylayıp WhatsApp Desktop açıldıktan sonra <c>apply</c>
/// ile commit eder.
///
/// <para><b>Idempotency sözleşmesi:</b> istemci gövdede bir
/// <c>idempotencyKey</c> gönderirse o anahtar ledger satırının PK'sı olur ve
/// aynı anahtarla gelen ikinci istek bakiyeyi <b>tekrar düşürmez</b> — ilk
/// uygulamanın sonucu aynen geri döner. Buna ihtiyaç var çünkü WPF'in HttpClient
/// dayanıklılık katmanı (<c>AddStandardResilienceHandler</c>) 5xx/ağ hatasında
/// POST'u da yeniden deniyor; burası ise gerçek para düşüyor. Anahtar
/// gönderilmezse (alan hiç yoksa) eski davranış sürer; boş Guid ise 400.</para>
///
/// <para><b>Neden ayrı rezervasyon tablosu yok</b> (WhatsApp gönderiminin
/// aksine): ledger satırının eklenmesi ile bakiyenin düşürülmesi <b>tek</b>
/// <c>SaveChanges</c> içinde, yani tek transaction'da oluyor. Dolayısıyla PK'nın
/// kendisi rezervasyondur: yarışan ikinci istek unique ihlaliyle tamamen geri
/// alınır, "yarısı yazıldı" hâli imkânsız. WhatsApp'ta rezervasyon şart çünkü
/// orada araya <b>dış</b> bir yan etki (Graph çağrısı) giriyor ve geri
/// alınamıyor.</para>
///
/// <para><b>Kopya Id'si (A5b):</b> eski sürüm birleştirmeden sonra da
/// kopyanın Id'siyle gelir; bakiye ise asıl kayıttadır (birleştirici
/// kopyanınkini 0'layıp asıl satıra ekler). preview/scope/apply istekteki
/// Id'yi önce <see cref="CustomerIdResolver"/>'dan geçirir; kopyaya hiçbir
/// hareket ya da bakiye yazılmaz.</para>
///
/// <para><b>Satış senkronu (spec 2026-10-13 §4.7, §6.5-3, §6.6).</b> Lisansın
/// kipi sahiplik denetimiyle birlikte okunur. Mod Off'ta hiçbir şey değişmez:
/// kapı yok, işlem ve kilit yok. Mod On'da:
/// <list type="bullet">
///   <item>preview, <c>/scope</c>, <c>reverse</c> ve apply, istemci
///   <c>SalesSync:MinClientVersion</c>'dan eskiyse ya da alt sürüm tanımsızsa
///   bakiyeye dokunmadan 409 <c>client-update-required</c> döner
///   (kapalı-hata). Eski sürüm GUID'li kişide mesajı bu yüzden göndermez;
///   kira ve sunucu toplamı onu kapsamazdı. <c>/scope</c> ve <c>reverse</c>
///   eski sürümün önizlemeden önce girdiği para yollarıdır.
///   <c>transactions/{id}</c> kapısızdır: eski sürüm onun hatasında diskteki
///   sonuçla devam eder (R6-02), kapı yalnız panel iadesini görmesini
///   engellerdi.</item>
///   <item>Kapsamlı ve anahtarı yeni apply, asıl kayıt×kapsam uygulama kilidi
///   altında <c>/scope</c>'un yüklemini koşar: geri alınmamış düşüm varsa 409
///   <c>scope-has-deduction</c> (DM6, M8). Ayrı tablo yok; kural defterin
///   kendisini okur ve imaj geri alınsa da bayat durum bırakmaz.</item>
/// </list></para>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/customer-balance")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesCustomerBalanceApplyController : ControllerBase
{
    /// <summary>Bu ucun yazdığı ledger satırının türü. Idempotency oynatması
    /// yalnız bu türü kabul eder — iade satırları bu ucun anahtarı olamaz.</summary>
    private const string KindPurchaseDeduction = "purchase-deduction";

    private const string CodeClientUpdateRequired = "client-update-required";
    private const string CodeScopeHasDeduction = "scope-has-deduction";

    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly SalesSyncOptions _salesSync;
    private readonly ILogger<LicensesCustomerBalanceApplyController> _log;

    public LicensesCustomerBalanceApplyController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        IOptions<SalesSyncOptions> salesSync,
        ILogger<LicensesCustomerBalanceApplyController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _salesSync = salesSync.Value;
        _log = log;
    }

    public sealed record PreviewQuery(Guid WpfCustomerId);

    /// <param name="PersonPool">Ortak bakiye (spec 2026-10-11 §4.1): havuz kipinde true. S1 kip bayrağını okumaz,
    /// hep false döner — satır kipi, bakiye istenen satırınki.</param>
    public sealed record PreviewResponse(
        Guid WpfCustomerId,
        decimal Balance,
        DateTimeOffset UpdatedAt,
        bool PersonPool = false);

    // ── GET preview ─────────────────────────────────────────────────────────

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        CancellationToken ct)
    {
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "preview");

        // Bakiye asıl kayıtta (A5b). Yanıt istemcinin gönderdiği Id'yi taşır —
        // istemci yanıtı kendi Id'siyle eşleştirebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);
        var row = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new PreviewResponse(wpfCustomerId, b.Balance, b.UpdatedAt, false))
            .FirstOrDefaultAsync(ct);

        return Ok(row ?? new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow));
    }

    // ── GET scope (R4-03 uzlaştırma) ────────────────────────────────────────

    /// <param name="AppliedAmount">Grubun toplamı (<c>−Σ</c>, spec 2026-10-11 §4.3): çok bacaklı düşümde çapanın
    /// kendi payı 0 olabilir. Eski tek bacaklı satırda <c>−Amount</c>.</param>
    /// <param name="PersonPool">Havuz kipinde true; S1'de hep false (WPF birleştirme notunda kullanır, §6.1-d).</param>
    public sealed record ScopeResponse(
        Guid TransactionId,
        decimal AppliedAmount,
        decimal ProductTotal,
        DateTimeOffset CreatedAt,
        bool PersonPool = false);

    /// <summary>
    /// R4-03: "bu müşterinin bu satışında zaten bir düşüm var mı?" — salt okunur.
    ///
    /// <para>İstemci taze bir işi <c>apply</c> etmeden ÖNCE burayı sorar. Yerel
    /// yedek geri yüklendiğinde satışın yerel kimliği (idempotency anahtarı) yok
    /// olur ama uzak defter düşümü hatırlamaya devam eder; anahtarsız istemci onu
    /// oynatamadığı için aynı satışı ikinci kez düşerdi. Kapsam sunucuda
    /// durduğundan istemci düşümü <b>tanıyıp benimseyebiliyor</b>.</para>
    ///
    /// <para><b>Geri alınmamış</b> satır aranıyor: revizyon akışı eski düşümü
    /// reverse edip taze bir tane yazar, geri alınmışı benimsemek iptal edilmiş
    /// bir düşümü diriltirdi. Kapsam başına geri alınmamış satır pratikte en
    /// fazla bir tanedir (istemci yeniden uygulamadan önce daima geri alır);
    /// yine de en <b>yeni</b>si seçiliyor — beklenmedik bir çoklukta eski satırı
    /// benimsemek, istemcinin üzerine yazacağı tutarı yanlış yerden okumak olur.
    /// <c>CreatedAt</c> eşitliğinde <c>Id</c> ikinci sıralama anahtarı: aynı
    /// isteğin iki çağrısı aynı cevabı vermeli.</para>
    ///
    /// <para>Kayıt yoksa <b>204</b> — istemci bugünkü akışta kalır. Bu, alanın
    /// yeni olmasından ötürü kapsamsız yazılmış geçmiş satırlar için de geçerli
    /// ve <b>zararsız</b>: davranış R4-03 öncesine düşer.</para>
    ///
    /// <para>Mod On'da preview ve apply ile aynı sürüm kapısı (§6.5-3). Bu uç
    /// salt okunur, ama v0.9.10 anahtarsız işte önce burayı sorar ve bulduğu
    /// düşümü benimser. Toplam aynıysa önizleme ve apply hiç çağrılmaz; kapı
    /// olmasa başka bilgisayarın teslim ettiği istek ikinci kez giderdi.
    /// v0.9.10 bu ucun hatasında mesajı engeller (R6-01).</para>
    /// </summary>
    [HttpGet("scope")]
    public async Task<IActionResult> Scope(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        [FromQuery] string? saleScope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(saleScope)
            || saleScope.Length > CustomerBalanceTransaction.SaleScopeMaxLength)
            return Problem(title: "invalid-sale-scope", statusCode: 400,
                detail: $"Kapsam boş olamaz ve {CustomerBalanceTransaction.SaleScopeMaxLength} "
                    + "karakteri aşamaz.");

        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        // v0.9.10 bulduğu düşümü benimser ve toplam aynıysa önizleme ile
        // apply'ı hiç çağırmadan mesajı gönderir; kapı burada da olmalı.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "scope");

        // Düşüm asıl kayıtta (A5b: hareketler birleştirmede taşınır, yenileri
        // çözülmüş Id'yle yazılır).
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);
        var row = await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, ct);

        return row is null ? NoContent() : Ok(row);
    }

    /// <summary>
    /// <c>/scope</c>'un yüklemi; apply'ın kapsam kuralı (§6.6) da aynı sorguyu
    /// koşar: aynı asıl kayıt, aynı kapsam, <c>purchase-deduction</c>, geri
    /// alınmamış; en yenisi (<c>CreatedAt</c>, sonra <c>Id</c>). Tek sorgu: uç
    /// "düşüm yok" dediği hâlde kuralın 409 vermesi (ya da tersi) olamaz.
    ///
    /// <para><b>Ortak bakiye (spec 2026-10-11 §4.2, §4.3):</b> yalnız çapalar
    /// aranır (bacaklar kapsam taşımaz; süzgeç savunma) ve tutar grubun
    /// toplamıdır — 409 <c>scope-has-deduction</c>'ın <c>appliedAmount</c>'u da
    /// buradan gelir. Çapa düzeyinde kalır: aynı kişinin başka satırındaki çapa
    /// bu yüklemi tetiklemez (M10). Çapanın geri alma satırı grubun tamamını
    /// temsil eder (D5, grup tek işlemde geri alınır). Eski tek bacaklı satırda
    /// grup yalnız kendisidir; S2 çapasında <c>DrawGroupId = Id</c> olduğundan
    /// çapa toplamda bir kez sayılır.</para>
    /// </summary>
    private Task<ScopeResponse?> LatestScopeDeductionAsync(
        Guid licenseId, Guid canonicalCustomerId, string saleScope, CancellationToken ct) =>
        _db.CustomerBalanceTransactions
            .AsNoTracking()
            .Where(t => t.LicenseId == licenseId
                && t.WpfCustomerId == canonicalCustomerId
                && t.SaleScope == saleScope
                && t.Kind == KindPurchaseDeduction
                && (t.DrawGroupId == null || t.DrawGroupId == t.Id)
                && !_db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new ScopeResponse(
                t.Id,
                -_db.CustomerBalanceTransactions
                    .Where(g => g.LicenseId == licenseId && (g.Id == t.Id || g.DrawGroupId == t.Id))
                    .Sum(g => g.Amount),
                t.OriginalAmount ?? 0m,
                t.CreatedAt,
                false))
            .FirstOrDefaultAsync(ct);

    // ── GET transactions/{id} (R6-02 durum ucu) ─────────────────────────────

    public sealed record TransactionStatusResponse(
        Guid TransactionId,
        decimal AppliedAmount,
        bool Reversed);

    /// <summary>
    /// R6-02: "kayıtlı bu düşüm hâlâ geçerli mi?" — salt okunur.
    ///
    /// <para>İstemci <b>Applied</b> bir işi tekrar paylaşmadan önce burayı
    /// sorar: panel iadesi düşümü geri aldıysa aynı fişi "bakiye düşüldü" diye
    /// yeniden paylaşmak müşteriye olmayan bir indirimi tekrar yazdırır.</para>
    ///
    /// <para>Kapsam ucuyla farkı: işlem kimliğiyle sorulur. Kapsamsız yazılmış
    /// eski (033 dönemi) satırlar kapsam sorgusunda görünmez; oradan sorulsa
    /// 204 "dışarıdan iade edilmiş" sanılır ve bir sonraki tıklama ikinci kez
    /// düşerdi. İşlem kimliği istemcinin idempotency anahtarının kendisi
    /// olduğundan o satırlar için de doğru cevap verir.</para>
    ///
    /// <para>Yalnız <c>purchase-deduction</c> satırları tanınır; iade/yükleme
    /// satırının kimliğiyle sorulursa <b>404</b> — istemci bunu "sunucu bu
    /// düşümü tanımıyor" diye okur ve paylaşımı durdurur.</para>
    ///
    /// <para><b>Ortak bakiye (spec 2026-10-11 §4.4):</b> bacak Id'si çapaya
    /// çözülür, tutar grubun toplamı (<c>−Σ</c>), <c>Reversed</c> çapanın geri
    /// alma satırı. Eski tek bacaklı satırda üçü de bugünkü gibi.</para>
    /// </summary>
    [HttpGet("transactions/{transactionId:guid}")]
    public async Task<IActionResult> TransactionStatus(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var tx = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.Kind == KindPurchaseDeduction, ct);
        // Ortak bakiye §4.4: bacak çapasına çözülür; çapası olmayan (yetim)
        // bacak tanınmayan düşüm sayılır — verify onu ayrıca sayar.
        var anchor = tx is null ? null : await _person.AnchorOfAsync(tx, ct);
        // R9-F03: başlıklı 404 — istemci "işlem gerçekten yok"u yalnız bu
        // başlıkla kabul eder. Gövdesiz 404 (eski sunucu, rota yok) anahtar
        // silmeye yol açmamalı: istemci onu uç-hatası sayar ve diskteki
        // sonuçla devam eder. Başlığı değiştirme — istemci sözleşmesi.
        if (anchor is null)
            return Problem(
                title: "transaction-not-found",
                detail: "Bu kimlikle kayıtlı bir düşüm işlemi yok.",
                statusCode: 404);

        // Grup tek işlemde geri alınır (D5): çapanın geri alma satırı grubun
        // tamamını temsil eder.
        var reversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == anchor.Id, ct);

        return Ok(new TransactionStatusResponse(anchor.Id, -await _person.GroupSumAsync(anchor, ct), reversed));
    }

    // ── POST apply ──────────────────────────────────────────────────────────

    /// <param name="SaleScope">R4-03: satışın kalıcı kimliği
    /// (<c>"session:{id}"</c> | <c>"cumulative"</c> | <c>"legacy:{key}"</c>).
    /// Opsiyonel — eski istemciler göndermez, davranışları değişmez.</param>
    public sealed record ApplyRequest(
        Guid WpfCustomerId,
        decimal Amount,
        decimal ProductTotal,
        Guid? IdempotencyKey = null,
        string? SaleScope = null);

    public sealed record ApplyResponse(
        Guid TransactionId,
        decimal AppliedAmount,
        decimal RemainingBalance);

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(
        Guid licenseId,
        [FromBody] ApplyRequest req,
        CancellationToken ct)
    {
        if (req.Amount <= 0) return Problem(title: "invalid-amount", statusCode: 400);
        if (decimal.Round(req.Amount, 2) != req.Amount
            || decimal.Round(req.ProductTotal, 2) != req.ProductTotal)
            return Problem(title: "invalid-money-scale", statusCode: 400,
                detail: "Amount ve ProductTotal kuruş hassasiyetini aşamaz.");

        // Boş Guid "anahtar yok" demek DEĞİL: istemci bozuk bir anahtar
        // üretmişse idempotency sessizce kapanır ve para yolunda çift düşüm
        // serbest kalır. Alanı hiç göndermemek (null) eski davranışı korur.
        if (req.IdempotencyKey == Guid.Empty)
            return Problem(title: "invalid-idempotency-key", statusCode: 400,
                detail: "Idempotency anahtarı boş Guid olamaz.");

        // R4-03: kapsam satışın kimliği olacak; sessizce kırpmak iki farklı
        // satışı aynı kimliğe indirger. Boş/boşluk metin de kimlik değildir —
        // "alan yok" ile "alan boş" arasındaki farkı istemci hatası sayıyoruz.
        if (req.SaleScope is not null
            && (string.IsNullOrWhiteSpace(req.SaleScope)
                || req.SaleScope.Length > CustomerBalanceTransaction.SaleScopeMaxLength))
            return Problem(title: "invalid-sale-scope", statusCode: 400,
                detail: $"Kapsam boş olamaz ve {CustomerBalanceTransaction.SaleScopeMaxLength} "
                    + "karakteri aşamaz.");

        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        // §6.5-3: bakiyenin her okumasından (oynatma dahil) önce.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "apply");

        // Kopya Id'si asıl kayda çözülür (A5b): bakiye araması, yazılan hareket
        // ve idempotency karşılaştırması asıl kayıtla.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, req.WpfCustomerId, ct);

        // Sahiplik kontrolünden SONRA bakıyoruz: anahtar başka lisansa aitse
        // çağıran onun sonucunu görmemeli.
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, preKey, ct);
            if (foreign) return NotFound();
            if (conflict) return ContentConflict();
            if (replay is not null) return Ok(replay);
        }

        // §6.6: anahtar yeni (yok ya da defterde değil). Kural yalnız mod On'da
        // ve kapsamlı istekte; kapsamsız istek ve mod Off bugünkü yoldan.
        if (mode == SalesSyncMode.On && req.SaleScope is { } saleScope)
            return await ApplyOncePerScopeAsync(licenseId, req, canonicalId, saleScope, ct);

        return (await WriteDeductionAsync(licenseId, req, canonicalId, ct)).Result;
    }

    /// <summary>
    /// Spec 2026-10-13 §6.6 — mod On'da kapsam başına tek geçerli düşüm. Tek
    /// işlemde, asıl kayıt×kapsam uygulama kilidi altında
    /// (<see cref="ScopeLockResource"/>):
    /// <list type="number">
    ///   <item>Anahtar yeniden aranır: aynı anahtarlı eşzamanlı istek kilitte
    ///   beklerken kazanan yazmış olabilir. O istek oynatılır ya da
    ///   <c>content-conflict</c> alır; kendi düşümünü "kapsamda düşüm var"
    ///   diye görmez.</item>
    ///   <item><c>/scope</c>'un yüklemi (<see cref="LatestScopeDeductionAsync"/>):
    ///   geri alınmamış düşüm varsa 409 <c>scope-has-deduction
    ///   {transactionId, appliedAmount, productTotal}</c> — istemci onu
    ///   benimser (W3).</item>
    ///   <item>Yoksa bugünkü yazım yolu, aynı işlemde.</item>
    /// </list>
    /// <c>legacy:</c> kapsamı muaf değildir (§15-18): miras işin tekrarı asıl
    /// kapsamla gelir ve iki iş aynı düşümü sahiplenmemeli. Kural satır (çapa)
    /// düzeyindedir; başka üyedeki izi kira ve #506'nın birleştirme sorusu
    /// yakalar (§6.4, §6.6).
    ///
    /// <para><b>Kilit zaman aşımı 409 <c>stale</c></b>
    /// (<see cref="SalesCommandLock"/>): aynı kişinin aynı satışında başka bir
    /// apply sürüyor; hiçbir şey yazılmaz, istemci yeniden dener.</para>
    ///
    /// <para>InMemory'de (testler) işlem ve kilit yok; yüklem aynı.</para>
    /// </summary>
    private async Task<IActionResult> ApplyOncePerScopeAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalId, string saleScope, CancellationToken ct)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            await SalesCommandLock.AcquireAsync(
                _db, ScopeLockResource(licenseId, canonicalId, saleScope), SalesCommandLock.DefaultTimeout, ct);
        }
        catch (SalesCommandRejection rejection)
        {
            _log.LogInformation(
                "customer-balance/apply: kapsam kilidi alınamadı, 409 {Code} (license={LicenseId})",
                rejection.Code, licenseId);
            return Problem(title: rejection.Code, detail: rejection.Message, statusCode: rejection.StatusCode);
        }

        if (req.IdempotencyKey is { } key)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, key, ct);
            if (foreign) return NotFound();
            if (conflict) return ContentConflict();
            if (replay is not null) return Ok(replay);
        }

        if (await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, ct) is { } existing)
        {
            _log.LogInformation(
                "customer-balance/apply: kapsamda geri alınmamış düşüm var, 409 scope-has-deduction "
                + "(license={LicenseId}, transaction={TransactionId})",
                licenseId, existing.TransactionId);
            var problem = Problem(title: CodeScopeHasDeduction, statusCode: 409,
                detail: "Bu satışta geri alınmamış bir bakiye düşümü var.");
            var details = (ProblemDetails)problem.Value!;
            details.Extensions["transactionId"] = existing.TransactionId;
            details.Extensions["appliedAmount"] = existing.AppliedAmount;
            details.Extensions["productTotal"] = existing.ProductTotal;
            return problem;
        }

        var (result, wrote) = await WriteDeductionAsync(licenseId, req, canonicalId, ct);
        if (wrote && transaction is not null) await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Düşümün bugünkü yazım yolu: bakiye okunur, uygulanabilir tutar
    /// hesaplanır, ledger satırı ile bakiye tek <c>SaveChanges</c>'te yazılır
    /// (F02 yeniden deneme döngüsü, aynı anahtar yarışında PK oynatması).
    /// <c>Wrote</c> yalnız taze bir düşüm yazıldığında true; çağıran açık
    /// işlemi ancak o zaman commit eder.
    /// </summary>
    private async Task<(IActionResult Result, bool Wrote)> WriteDeductionAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalId, CancellationToken ct)
    {
        var balance = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId
                && b.WpfCustomerId == canonicalId, ct);
        if (balance is null || balance.Balance <= 0)
            return (Problem(title: "no-balance", statusCode: 409), false);

        // İstenen tutar bakiyeden fazla olamaz; sipariş tutarından da fazla
        // olamaz (mantıksızlık).
        var appliedAmount = Math.Min(Math.Min(req.Amount, balance.Balance), req.ProductTotal);
        if (appliedAmount <= 0)
            return (Problem(title: "nothing-to-apply", statusCode: 409), false);

        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        // Anahtar VARSA ledger satırının PK'sı odur — rezervasyon budur.
        var txId = req.IdempotencyKey ?? Guid.NewGuid();

        var tx = new CustomerBalanceTransaction
        {
            Id = txId,
            LicenseId = licenseId,
            WpfCustomerId = canonicalId,
            Amount = -appliedAmount,
            Kind = KindPurchaseDeduction,
            OriginalAmount = req.ProductTotal,
            SaleScope = req.SaleScope,
            Reason = null,
            CreatedByCustomerId = customerId,
            CreatedAt = now,
        };
        _db.CustomerBalanceTransactions.Add(tx);

        balance.Balance -= appliedAmount;
        balance.UpdatedAt = now;

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                break;
            }
            // Sıralama önemli: DbUpdateConcurrencyException, DbUpdateException'dan
            // türer — önce o yakalanmalı, yoksa idempotency dalı yutar.
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                // F02 (2026-09-09 denetimi): balance satırını araya giren bir
                // yazım değiştirdi (ör. eşzamanlı iade ya da ikinci bir apply).
                // Güncel değeri yükle, uygulanabilir tutarı YENİDEN hesapla —
                // eski appliedAmount bayat okumaya dayanıyordu ve bakiyeyi
                // sıfırın altına düşürebilirdi.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);

                if (balance.Balance <= 0)
                {
                    _db.ChangeTracker.Clear();
                    return (Problem(title: "no-balance", statusCode: 409), false);
                }
                appliedAmount = Math.Min(Math.Min(req.Amount, balance.Balance), req.ProductTotal);
                if (appliedAmount <= 0)
                {
                    _db.ChangeTracker.Clear();
                    return (Problem(title: "nothing-to-apply", statusCode: 409), false);
                }
                tx.Amount = -appliedAmount;
                balance.Balance -= appliedAmount;
                balance.UpdatedAt = DateTimeOffset.UtcNow;
            }
            catch (DbUpdateException) when (req.IdempotencyKey is not null)
            {
                // Aynı anahtarla yarışan iki istek: PK ihlali TÜM transaction'ı geri
                // alır (bakiye düşümü dahil), yani kaybeden taraf hiçbir iz bırakmaz.
                // Kazananın sonucunu oynatabiliyorsak yarış hikâyesi tutuyor demektir;
                // tutmuyorsa hata gerçek bir DB sorunudur, yutulmamalı.
                _db.ChangeTracker.Clear();
                var (winner, _, conflict) = await LookupAsync(
                    licenseId, req, canonicalId, req.IdempotencyKey.Value, ct);
                if (conflict) return (ContentConflict(), false);
                if (winner is null) throw;
                _log.LogWarning(
                    "Bakiye uygulama yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                    req.IdempotencyKey, licenseId);
                return (Ok(winner), false);
            }
        }

        return (Ok(new ApplyResponse(txId, appliedAmount, balance.Balance)), true);
    }

    private ObjectResult ContentConflict() =>
        Problem(title: "content-conflict", statusCode: 409,
            detail: "Idempotency anahtarı farklı bir istekle kullanılmış.");

    /// <summary>
    /// Anahtarın daha önce uygulanıp uygulanmadığına bakar.
    ///
    /// <para><c>Replay</c>: aynı lisansa ait bir düşüm satırı bulunduysa ilk
    /// sonucun aynısı. <c>Foreign</c>: anahtar var ama <b>başka</b> bir lisansın
    /// ya da başka türden (iade vb.) bir satırının kimliği — bu durumda çağıran
    /// ne o satırı görmeli ne de anahtarı yeniden kullanabilmeli. Foreign'i ayrı
    /// ele almasak PK ihlali yakalanır, oynatacak sonuç bulunamaz ve istek 500
    /// olurdu; oysa bu istemci hatası, sunucu hatası değil.</para>
    ///
    /// <para><c>ContentConflict</c>: anahtar bu lisansın düşüm satırı ama yeni
    /// istek ilk isteğin aynısı değil (A11). Oynatmak yanlış satışa düşüm bağlar;
    /// hiçbir yan etki olmadan reddedilmeli.</para>
    ///
    /// <para><c>RemainingBalance</c> o anki gerçek bakiyedir (donmuş bir kopya
    /// değil): istemci bunu ekranda gösteriyor, eski bir değeri oynatmak
    /// operatöre yanlış bakiye gösterirdi. <c>AppliedAmount</c> ise ledger
    /// satırından gelir — "ne kadar düştü" cevabı değişmemeli.</para>
    ///
    /// <para><b>Ortak bakiye (spec 2026-10-11 §4.2 "İdempotency ve A11"):</b>
    /// anahtar her zaman bir ÇAPANIN Id'sidir; bulunan satır bacaksa
    /// (<c>DrawGroupId</c> dolu ve ≠ Id) foreign sayılır → 404 (savunma:
    /// istemcinin ürettiği Guid'in sunucunun bacak Id'sine denk gelmesi pratikte
    /// imkânsız). Tutar karşılaştırması ve oynatılan <c>AppliedAmount</c> grubun
    /// toplamıdır (<c>−Σ</c>); <c>sameCustomer</c> ve <c>OriginalAmount</c>
    /// çapada, değişmez (M10). Eski tek bacaklı satırda grup yalnız kendisi:
    /// davranış bugünküyle aynı.</para>
    /// </summary>
    /// <param name="canonicalCustomerId">İstekteki müşteri Id'sinin asıl kaydı
    /// (A5b, <see cref="CustomerIdResolver"/>).</param>
    private async Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalCustomerId, Guid key, CancellationToken ct)
    {
        var tx = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == key, ct);
        if (tx is null) return (null, false, false);

        if (tx.LicenseId != licenseId || tx.Kind != KindPurchaseDeduction || !PersonBalance.IsAnchor(tx))
        {
            _log.LogWarning(
                "Bakiye idempotency anahtarı başka bir kayda ait (key={Key}, license={LicenseId}, kind={Kind})",
                key, licenseId, tx.Kind);
            return (null, true, false);
        }

        // A11: anahtar bu lisansın düşüm satırı ama istek İLK isteğin aynısı
        // değil. Oynatmak yanlış satışa düşüm bağlar; hiçbir yan etki olmadan
        // reddet. Amount için tolerans: istemci replay'de Amount=ProductTotal
        // gönderir; ilk düşümden KÜÇÜK bir Amount ise gerçek bir çelişkidir.
        // decimal eşitliği değer tabanlıdır: 250m == 250.00m → true. SQL decimal(18,2)
        // round-trip ölçek ekleyebilir ama değeri değiştiremez; yanlış çelişki üretmez.
        // R4-03 notu: kapsam (SaleScope) bu karşılaştırmaya BİLEREK girmiyor.
        // Sürüm yükselten bir istemci, kapsamsız yazılmış bir satırın anahtarını
        // artık kapsamla replay eder; kapsamı çelişki saysaydık o iş kalıcı
        // olarak kilitlenirdi (Seçenek A'nın düştüğü tuzağın aynısı). Kimliği
        // zaten müşteri + tutar bağlıyor, kapsamın eklenmesi bir şey kazandırmaz:
        // anahtarı olmayan (geri yüklenmiş) istemci onu zaten replay edemez.
        // A5b: müşteri KİŞİ düzeyinde karşılaştırılır — iki taraf da asıl kayda
        // çözülür. Birleştirmeden önce yazılmış düşümün hareketi asıl kayda
        // taşınır, eski sürüm aynı isteği kopya Id'siyle tekrarlar: bu çelişki
        // değil, aynı kişinin aynı isteği. Hareketin kendi Id'si yalnız istekle
        // farklıysa çözülür (çelişki yolunda tek ek sorgu).
        // Ortak bakiye §4.2: tutar grubun toplamı (çapanın payı 0 olabilir);
        // eski tek bacaklı satırda kendi tutarı, sorgu yok.
        var applied = -await _person.GroupSumAsync(tx, ct);
        var sameCustomer = tx.WpfCustomerId == canonicalCustomerId
            || await _customerIds.CanonicalOfAsync(licenseId, tx.WpfCustomerId, ct) == canonicalCustomerId;
        if (!sameCustomer
            || tx.OriginalAmount != req.ProductTotal
            || applied > req.Amount)
        {
            _log.LogWarning(
                "Bakiye idempotency anahtarı farklı içerikle yeniden kullanıldı (key={Key}, license={LicenseId})",
                key, licenseId);
            return (null, false, true);
        }

        // Kalan bakiye asıl kayıttan (kopyanın satırı birleştirmede 0'lanır).
        var remaining = await _db.CustomerBalances
            .AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalCustomerId)
            .Select(b => (decimal?)b.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;

        _log.LogInformation(
            "Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})", key, licenseId);
        // remaining satır kipinde bugünkü gibi asıl kaydın bakiyesi; havuz Σ'sı S2.
        return (new ApplyResponse(tx.Id, applied, remaining), false, false);
    }

    // ── POST reverse (revizyon: eski düşümü geri al) ────────────────────────

    /// <summary>Spec K2: aynı satışın toplamı değişince WPF eski düşümü geri
    /// alıp yeni toplamla taze apply yapar. Panel'deki reverse'in WPF-yüzeyi
    /// ikizi — ama kapsamı dar: yalnız BU lisansın purchase-deduction satırı.
    /// Hakem N01 filtered-unique index (ReversesTransactionId): yarışan ikinci
    /// reverse SaveChanges'te unique ihlali alır → 409 already-reversed.
    /// İstemci 409 already-reversed'i BAŞARI sayar (geri alma zaten olmuş).
    ///
    /// <para>Mod On'da sürüm kapısı (§6.5-3): eski sürümün revizyonu başka
    /// bilgisayarın teslim edilmiş düşümünü geri alırdı, ardından yeniden
    /// apply'ı 409 alır. v0.9.10 bu 409'u başarısızlık sayar, geri alma niyeti
    /// diskte kalır ve mesaj gitmez.</para></summary>
    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        var mode = await OwnedLicenseModeAsync(licenseId, ct);
        if (mode is null) return NotFound();
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "reverse");

        var original = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.Kind == KindPurchaseDeduction, ct);
        if (original is null) return NotFound();

        // Hızlı yol ön kontrolü; asıl hakem N01 index'i (aşağıdaki catch).
        var alreadyReversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == transactionId, ct);
        if (alreadyReversed) return Problem(title: "already-reversed", statusCode: 409);

        var balance = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId
                && b.WpfCustomerId == original.WpfCustomerId, ct);
        // Apply bakiye satırı olmadan düşüm yazmaz; satır silinmiyor da.
        if (balance is null) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var reverseAmount = -original.Amount; // düşüm negatif → geri alma pozitif

        _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = original.WpfCustomerId,
            Amount = reverseAmount,
            Kind = "reversal",
            OriginalAmount = null,
            Reason = $"Reverse of {transactionId:N}",
            ReversesTransactionId = transactionId,
            CreatedByCustomerId = User.GetTenantCustomerId(),
            CreatedAt = now,
        });

        balance.Balance += reverseAmount;
        balance.UpdatedAt = now;

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                return Ok();
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                // F02 kalıbı: bakiyeyi güncel değere çek, deltayı yeniden uygula.
                // Geri alma para EKLER — negatif kontrolü gerekmez.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
                balance.Balance += reverseAmount;
                balance.UpdatedAt = DateTimeOffset.UtcNow;
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                // N01: eşzamanlı ikinci reverse yarışı kaybetti; transaction
                // geri alındı, bakiyeye hiçbir şey yazılmadı.
                _db.ChangeTracker.Clear();
                return Problem(title: "already-reversed", statusCode: 409);
            }
        }
    }

    /// <summary>N01 hakemi (PanelCustomerBalanceController'daki ile aynı):
    /// 2601/2627 = unique ihlali; index adı filtresi, başka unique yarışlarının
    /// aynı koda karışmasını önler.</summary>
    private static bool IsDuplicateReversal(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("ReversesTransactionId", StringComparison.Ordinal);

    /// <summary>
    /// Kapsam kuralının kilidi (§6.6): <c>bal:{lisans}:{asıl kayıt}:{kapsam}</c>.
    /// Kapsam küçük harfe katlanır ve sondaki boşluğu atılır:
    /// <c>sp_getapplock</c> adları ikili karşılaştırır, <c>SaleScope</c>'un SQL
    /// eşitliği ise harf duyarsızdır ve sondaki boşluğu yok sayar; kilit
    /// yüklemin eşit saydığı iki kapsamı ayrı tutmamalı. Ad en çok
    /// 4 + 32 + 1 + 32 + 1 + 128 = 198 karakter (<c>sp_getapplock</c> sınırı
    /// 255).
    /// </summary>
    public static string ScopeLockResource(Guid licenseId, Guid canonicalCustomerId, string saleScope) =>
        $"bal:{licenseId:N}:{canonicalCustomerId:N}:{saleScope.ToLowerInvariant().TrimEnd(' ')}";

    /// <summary>Lisans çağırana aitse kipi; değilse null (uç 404 döner). Sahiplik
    /// ve kip tek sorguda.</summary>
    private async Task<SalesSyncMode?> OwnedLicenseModeAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .Where(l => l.Id == licenseId && l.CustomerId == callerId)
            .Select(l => (SalesSyncMode?)l.SalesSyncMode)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>İstemci (<c>User-Agent</c>) <c>SalesSync:MinClientVersion</c>'ı
    /// karşılıyor mu. Kapalı-hata: sürüm bilinmiyorsa ya da alt sürüm
    /// tanımsızsa false (<see cref="ClientVersion.IsAtLeast"/>).</summary>
    private bool ClientMeetsMinimum() =>
        ClientVersion.IsAtLeast(
            ClientVersion.FromUserAgent(Request.Headers.UserAgent.ToString()),
            _salesSync.ParseMinClientVersion());

    private ObjectResult ClientUpdateRequired(Guid licenseId, string endpoint)
    {
        _log.LogInformation(
            "customer-balance/{Endpoint}: mod On'da istemci alt sürümün altında ya da alt sürüm tanımsız, "
            + "409 client-update-required (license={LicenseId})",
            endpoint, licenseId);
        return Problem(title: CodeClientUpdateRequired, statusCode: 409,
            detail: "Bakiye işlemi için uygulamanın güncel sürümü gerekiyor.");
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == callerId, ct);
    }
}
