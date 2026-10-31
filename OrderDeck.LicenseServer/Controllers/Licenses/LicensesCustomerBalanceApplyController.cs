using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
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
///
/// <para><b>Ortak bakiye, havuz kipi (spec 2026-10-11 §4.0–§4.2).</b> Kip
/// sahiplik sorgusunun ikinci alanıdır (<c>License.PersonBalancePoolSince</c>);
/// boşsa her uç yukarıdaki gibi çalışır. Doluysa preview ve yeni yazım yapacak
/// apply, satış senkronu kapısından ayrı olarak <c>PersonBalance:MinClientVersion</c>
/// kapısından ve <c>memberIds</c> denetiminden geçer (yeniden oynatma bu iki
/// denetimden ÖNCE; kayıtlı anahtar engellenmez). Apply çekimi
/// <c>PersonBalance.DrawAsync</c>'e bırakır: çok bacaklı yazım, kişi kilidi,
/// her denemede anahtarın yeniden aranması ve §6.6 kapsam kuralı çapa
/// düzeyinde, deneme başına işlem ve kilitle. <c>scope</c>, <c>transactions/{id}</c>
/// ve <c>reverse</c>'e havuz kapısı konmaz.</para>
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
    private const string CodeBalanceBusy = "balance-busy";
    private const string CodeMemberIdsRequired = "member-ids-required";

    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly SalesSyncOptions _salesSync;
    private readonly PersonBalanceOptions _personBalance;
    private readonly OrderDeckMetrics _metrics;
    private readonly ILogger<LicensesCustomerBalanceApplyController> _log;

    public LicensesCustomerBalanceApplyController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        IOptions<SalesSyncOptions> salesSync, IOptions<PersonBalanceOptions> personBalance,
        OrderDeckMetrics metrics, ILogger<LicensesCustomerBalanceApplyController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _salesSync = salesSync.Value;
        _personBalance = personBalance.Value;
        _metrics = metrics;
        _log = log;
    }

    public sealed record PreviewQuery(Guid WpfCustomerId);

    /// <param name="PersonPool">Ortak bakiye (spec 2026-10-11 §4.1): havuz kipinde true. O zaman <c>Balance</c>
    /// kişinin havuzundan (<c>max(0, Σ)</c>), <c>UpdatedAt</c> P'nin son para hareketinden gelir (M9). Satır kipinde
    /// false: bakiye istenen satırınki.</param>
    public sealed record PreviewResponse(
        Guid WpfCustomerId,
        decimal Balance,
        DateTimeOffset UpdatedAt,
        bool PersonPool = false);

    // ── GET preview ─────────────────────────────────────────────────────────

    /// <summary>
    /// Önizleme: istemcinin mesaja yazacağı bakiye.
    ///
    /// <para><b>Satır kipi</b> (bayrak boş): bugünkü gibi, bakiye istenen Id'nin asıl kaydının satırı; satır yoksa
    /// 0. <paramref name="memberIds"/> bağlanır ama kullanılmaz, havuz kapısı yok. Guid'e bağlanamayan değer model
    /// bağlamada, kip okunmadan <c>[ApiController]</c>'ın 400 <c>ValidationProblem</c>'ını alır; bu satır kipinde de
    /// geçerli tek farktır (plan fark 18f).</para>
    ///
    /// <para><b>Havuz kipi</b> (spec 2026-10-11 §4.0, §4.1): satış senkronu kapısından sonra havuz sürüm kapısı
    /// (<c>PersonBalance:MinClientVersion</c>; eski, ayrıştırılamayan ya da olmayan sürüm, alt sınır tanımsızsa
    /// güncel sürüm de 409 <c>client-update-required</c>), sonra <paramref name="memberIds"/> (yoksa 400
    /// <c>member-ids-required</c>), sonra P ve kural 1–4 (<see cref="PoolPreviewAsync"/>). Sürüm
    /// <c>memberIds</c>'ten ÖNCE: üye listesi göndermeyen eski sürüm (v0.9.9) de 409 <c>client-update-required</c>
    /// alır (§6.2, §8.5; plan fark 3).</para>
    /// </summary>
    /// <param name="memberIds">WPF'in kişi üyeleri (W), tekrarlı sorgu parametresi <c>memberIds</c>. Yok ile boş aynı
    /// sayılır: ASP.NET verilmeyen üst düzey diziyi boş bağlar (plan fark 18f).</param>
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        [FromQuery(Name = "memberIds")] Guid[]? memberIds,
        CancellationToken ct)
    {
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "preview");

        // Bakiye asıl kayıtta (A5b). Yanıt istemcinin gönderdiği Id'yi taşır —
        // istemci yanıtı kendi Id'siyle eşleştirebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);

        if (pool)
        {
            // Plan fark 3: havuz sürüm kapısı memberIds'ten önce.
            if (!PoolClientMeetsMinimum()) return PoolClientUpdateRequired(licenseId, "preview");
            if (!HasMemberIds(memberIds)) return MemberIdsRequired(licenseId, "preview");
            return await PoolPreviewAsync(licenseId, wpfCustomerId, canonicalId, memberIds!, ct);
        }

        var row = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new PreviewResponse(wpfCustomerId, b.Balance, b.UpdatedAt, false))
            .FirstOrDefaultAsync(ct);

        return Ok(row ?? new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Havuz kipinde preview (spec 2026-10-11 §4.0 kural 1–4, §4.1). P <see cref="PersonBalance.ResolveDrawSetAsync"/>'ten
    /// gelir.
    /// <list type="bullet">
    ///   <item>Kural 1'in kesin kolu (ne a ne W'nin bir Id'si sunucuda) 200 <c>Balance = 0</c>,
    ///   <c>PersonPool = true</c>: satır kipinin "satır yoksa 0" yanıtı, yalnız bayrağı farklı (sahip kararı 1,
    ///   plan fark 19). Spec §4.0-1'in 409 <c>no-balance</c>'ı yalnız apply'da.</item>
    ///   <item>Öbür retler 409 <c>customer-not-synced</c> (<see cref="CustomerNotSynced"/>).</item>
    ///   <item>Aksi hâlde <c>Balance = max(0, Σ)</c>. Σ, P'nin bakiyeleri ile S∖P'nin eksi bakiyelerinin toplamıdır
    ///   (<see cref="PersonBalance.DrawSet.Pool"/>).</item>
    /// </list>
    ///
    /// <para><c>UpdatedAt</c> (M9, plan fark 18g): P'nin son para hareketinin <c>CreatedAt</c>'i. Böylece kişi
    /// kilidinin ilerlettiği jeton "güncellendi" zamanını oynatmaz. Hareket yoksa P'nin bakiye satırlarının en yeni
    /// <c>UpdatedAt</c>'i, o da yoksa şimdi. S∖P'nin kilitli eksileri Σ'ya girer, zamana girmez. Yanıttaki Id
    /// istekteki Id'dir (kopya olabilir, §4.1).</para>
    /// </summary>
    private async Task<IActionResult> PoolPreviewAsync(
        Guid licenseId, Guid requestId, Guid canonicalId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var resolved = await _person.ResolveDrawSetAsync(licenseId, canonicalId, memberIds, ct);
        if (resolved.Rejection is { } rejection)
            return rejection.Code switch
            {
                // Kural 1'in kesin kolu (spec §4.0-1): hiçbir üye sunucuda yok → para olamaz, kesin 0.
                // Sahip kararı 1 (29.10, (b)): preview 409 değil, satır kipinin "satır yoksa 0" yanıtı
                // PersonPool = true ile; bugünkü WPF ≤ 0'ı "bakiye yok" yazar. Apply 409 no-balance kalır.
                PersonBalance.CodeNoBalance =>
                    Ok(new PreviewResponse(requestId, 0m, DateTimeOffset.UtcNow, PersonPool: true)),
                _ => CustomerNotSynced(licenseId, "preview", rejection.RowIds),
            };

        var set = resolved.Set!;
        var ids = set.Draw.Select(m => m.WpfCustomerId).ToList();
        var updatedAt = await _person.LastMovementAtAsync(licenseId, ids, ct)
            ?? await _db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
                .MaxAsync(b => (DateTimeOffset?)b.UpdatedAt, ct)
            ?? DateTimeOffset.UtcNow;

        return Ok(new PreviewResponse(requestId, Math.Max(0m, set.Pool), updatedAt, PersonPool: true));
    }

    // ── GET scope (R4-03 uzlaştırma) ────────────────────────────────────────

    /// <param name="AppliedAmount">Grubun toplamı (<c>−Σ</c>, spec 2026-10-11 §4.3): çok bacaklı düşümde çapanın
    /// kendi payı 0 olabilir. Eski tek bacaklı satırda <c>−Amount</c>.</param>
    /// <param name="PersonPool">Lisans havuz kipindeyse (<c>PersonBalancePoolSince</c> dolu) true, satır kipinde
    /// false (spec 2026-10-11 §4.3). WPF birleştirme notunda kullanır (§6.1-d).</param>
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

        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
        // v0.9.10 bulduğu düşümü benimser ve toplam aynıysa önizleme ile
        // apply'ı hiç çağırmadan mesajı gönderir; kapı burada da olmalı.
        // Havuz sürüm kapısı burada yok (plan fark 4): uç salt okunur.
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "scope");

        // Düşüm asıl kayıtta (A5b: hareketler birleştirmede taşınır, yenileri
        // çözülmüş Id'yle yazılır). Ortak bakiye §4.3: yüklem iki kipte aynı
        // (çapa düzeyinde); havuz kipinde yalnız PersonPool = true.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, wpfCustomerId, ct);
        var row = await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, pool, ct);

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
    ///
    /// <para><paramref name="personPool"/> yalnız yanıt bayrağıdır
    /// (<see cref="ScopeResponse.PersonPool"/>); yüklemi değiştirmez. 409
    /// <c>scope-has-deduction</c>'ın uzantıları onu taşımaz.</para>
    /// </summary>
    private Task<ScopeResponse?> LatestScopeDeductionAsync(
        Guid licenseId, Guid canonicalCustomerId, string saleScope, bool personPool, CancellationToken ct) =>
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
                personPool))
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
    /// <param name="MemberIds">Ortak bakiye §4.0: havuz kipinde kişinin üye Id'leri (WPF'in bildiği hâliyle);
    /// satır kipinde okunmaz; eski istemci göndermez. Gövde alanı <c>memberIds</c>. Alanın yokluğu ile boş liste
    /// aynı sayılır (plan fark 18f).</param>
    public sealed record ApplyRequest(
        Guid WpfCustomerId,
        decimal Amount,
        decimal ProductTotal,
        Guid? IdempotencyKey = null,
        string? SaleScope = null,
        IReadOnlyList<Guid>? MemberIds = null);

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

        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, pool) = owned.Value;
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
            (ApplyResponse? Replay, bool Foreign, bool ContentConflict) found;
            try
            {
                found = await LookupAsync(licenseId, req, canonicalId, preKey, pool, ct);
            }
            // Havuz kipinde oynatma CustomerBalances'ı okur; çekimden sonraki son Lookup'la aynı karar. Satır kipi
            // bugünkü yol birebir (plan fark 8).
            catch (Exception ex) when (pool && PersonBalance.IsDeadlock(ex))
            {
                return ReplayDeadlocked(licenseId, preKey);
            }
            var (replay, foreign, conflict) = found;
            if (foreign) return NotFound();
            if (conflict) return ContentConflict();
            if (replay is not null) return Ok(replay);
        }

        // Ortak bakiye, havuz kipi (spec 2026-10-11 §4.0, §4.2): havuz sürüm kapısı yeniden oynatmadan SONRA (kayıtlı
        // anahtar client-update-required almaz, plan fark 4), memberIds denetiminden ÖNCE (memberIds göndermeyen eski
        // sürüm de 409 alır, plan fark 3). Çekim DrawAsync'ten geçer; §6.6 kapsam kuralı orada deneme başına.
        if (pool)
        {
            if (!PoolClientMeetsMinimum()) return PoolClientUpdateRequired(licenseId, "apply");
            if (!HasMemberIds(req.MemberIds)) return MemberIdsRequired(licenseId, "apply");
            return await ApplyPoolAsync(licenseId, req, canonicalId, mode, ct);
        }

        // Satır kipi, bugünkü yol birebir (plan fark 8). §6.6: anahtar yeni (yok ya da
        // defterde değil). Kural yalnız mod On'da ve kapsamlı istekte; kapsamsız istek ve
        // mod Off bugünkü yoldan.
        if (mode == SalesSyncMode.On && req.SaleScope is { } saleScope)
            return await ApplyOncePerScopeAsync(licenseId, req, canonicalId, saleScope, ct);

        return (await WriteDeductionAsync(licenseId, req, canonicalId, ct)).Result;
    }

    /// <summary>
    /// Ortak bakiye apply'ı, havuz kipi (spec 2026-10-11 §4.2). Çekim <c>PersonBalance.DrawAsync</c>'ten geçer: para
    /// <c>P</c>'den (§4.0-4, <c>W</c> = <c>memberIds</c>), çapa istekteki asıl kayıtta (payı 0 olabilir), bacaklar
    /// <c>P</c>'nin öbür üyelerinde; <c>S∖P</c>'nin eksileri havuza sayılır ve kilitlenir; tek <c>SaveChanges</c>.
    /// Çağıran izleyicide bir şey bırakmaz (sahiplik, asıl kayıt ve oynatma okumaları izlemesiz) — <c>DrawAsync</c>'in
    /// önkoşulu.
    /// <list type="bullet">
    ///   <item><b>Her denemenin başında</b> (kilitten sonra, okumalardan önce; <c>beforeAttempt</c>): anahtar varsa ve
    ///   deneme 2.+ ya da kapsam yolundaysa <see cref="LookupAsync"/> — aynı anahtarlı istek araya girip yazdıysa onun
    ///   sonucu oynatılır, havuz boşaldı diye <c>no-balance</c> dönülmez (§4.2 adım 6). Sonra kapsam yolunda
    ///   <see cref="LatestScopeDeductionAsync"/>: geri alınmamış çapa varsa 409 <c>scope-has-deduction</c>, tutar
    ///   <c>−Σ(grup)</c> (§4.2 "Her denemede", §15-2).</item>
    ///   <item><b><c>no-balance</c> ve <c>customer-not-synced</c>'ten önce</b> anahtar varsa son bir
    ///   <see cref="LookupAsync"/>: kapsamsız yolda 1. deneme kilitsiz ön oynatmaya dayanır; ondan sonra commit eden
    ///   aynı anahtarlı istek oynatılır, kayıtlı anahtar ret almaz (§4.2 adım 6'nın amacı). <c>DuplicateKey</c>'te de
    ///   kazanan aynı yoldan oynatılır (foreign 404). Bu Lookup'ta kilitlenme kurbanı (1205) 503 <c>balance-busy</c>
    ///   döner (<see cref="ReplayDeadlocked"/>; Apply'ın havuz kipindeki ön oynatmasında da).</item>
    ///   <item><b>Kapsam kuralı</b> yalnız satış senkronu mod On ∧ kapsam dolu (<c>legacy:</c> muaf değil, §15-18);
    ///   çapa düzeyinde (M10): aynı kişinin başka satırındaki çapa tetiklemez. Her deneme kendi işlemini açar ve
    ///   <see cref="ScopeLockResource"/> kilidini o işlemde alır; kilit denemeler arasında bırakılır, yüklem bu yüzden
    ///   her denemede yeniden koşar (plan fark 7). Kilit zaman aşımı 409 <c>stale</c>, hiçbir şey yazılmaz.</item>
    ///   <item>Üç çakışmadan sonra 503 <c>balance-busy</c> (WPF'in dayanıklılık katmanı aynı anahtarla yeniden dener);
    ///   aynı anahtarla PK yarışında bütün bacaklar geri alınır ve kazananın sonucu oynatılır.</item>
    /// </list>
    /// Yanıt: <c>TransactionId</c> = çapa, <c>AppliedAmount</c> = bütün bacakların toplamı,
    /// <c>RemainingBalance = max(0, Σ − applied)</c> (§4.2 adım 7).
    /// </summary>
    private async Task<IActionResult> ApplyPoolAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalId, SalesSyncMode mode, CancellationToken ct)
    {
        // §6.6: kural yalnız mod On'da ve kapsamlı istekte. Kapsam mod Off'ta da çapaya yazılır (satır yolu da yazar).
        var scope = mode == SalesSyncMode.On ? req.SaleScope : null;
        var request = new PersonBalance.DrawRequest(
            LicenseId: licenseId,
            AnchorRowId: canonicalId,
            // Anahtar VARSA çapanın PK'sı odur — rezervasyon budur (bugünkü sözleşme).
            AnchorTransactionId: req.IdempotencyKey ?? Guid.NewGuid(),
            AnchorIdIsIdempotencyKey: req.IdempotencyKey is not null,
            Kind: PersonBalance.KindPurchaseDeduction,
            // Sipariş tutarından fazla düşülmez; havuz sınırı DrawAsync'te (UpToPool).
            Requested: Math.Min(req.Amount, req.ProductTotal),
            Rule: PersonBalance.DrawAmountRule.UpToPool,
            MemberIds: req.MemberIds,
            OriginalAmount: req.ProductTotal,
            SaleScope: req.SaleScope,
            Reason: null,
            ReversesTransactionId: null,
            CreatedByCustomerId: User.GetTenantCustomerId(),
            ScopeLockResource: scope is null ? null : ScopeLockResource(licenseId, canonicalId, scope));

        IActionResult? stopped = null;

        async Task<bool> BeforeAttempt(int attempt, CancellationToken token)
        {
            // 1. denemenin kapsamsız Lookup'ı Apply'ın ön oynatmasıdır; kapsam yolunda 1. deneme de kilit altında
            // yeniden arar (aynı anahtarlı istek kilitte beklerken kazanan yazmış olabilir).
            if (req.IdempotencyKey is { } key && (attempt > 1 || scope is not null))
            {
                var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, key, pool: true, token);
                if (foreign)
                {
                    stopped = NotFound();
                    return true;
                }
                if (conflict)
                {
                    stopped = ContentConflict();
                    return true;
                }
                if (replay is not null)
                {
                    stopped = Ok(replay);
                    return true;
                }
            }

            if (scope is not null
                && await LatestScopeDeductionAsync(licenseId, canonicalId, scope, personPool: true, token) is { } existing)
            {
                stopped = ScopeHasDeduction(licenseId, existing);
                return true;
            }

            return false;
        }

        PersonBalance.DrawResult result;
        try
        {
            result = await _person.DrawAsync(request, BeforeAttempt, ct);
        }
        catch (SalesCommandRejection rejection)
        {
            // Kapsam kilidi zaman aşımı (§6.6): aynı kişinin aynı satışında başka bir apply sürüyor; deneme işlemi
            // commit edilmeden atıldı, hiçbir şey yazılmadı. İstemci yeniden dener.
            _log.LogInformation(
                "customer-balance/apply: kapsam kilidi alınamadı, 409 {Code} (license={LicenseId})",
                rejection.Code, licenseId);
            return Problem(title: rejection.Code, detail: rejection.Message, statusCode: rejection.StatusCode);
        }

        // Çekim ret döndü ama anahtar bu arada defterde olabilir: kayıtlı anahtar ret almaz, kazananın sonucu
        // oynatılır (§4.2, plan fark 18n). null → anahtar yok ya da defterde değil, ret geçerli. DuplicateKey de aynı
        // yolu kullanır: foreign 404 (ön oynatmayla aynı), 1205 503 (ReplayDeadlocked).
        async Task<IActionResult?> ReplayWrittenKeyAsync()
        {
            if (req.IdempotencyKey is not { } writtenKey) return null;
            try
            {
                var (replay, foreign, conflict) = await LookupAsync(
                    licenseId, req, canonicalId, writtenKey, pool: true, ct);
                if (foreign) return NotFound();
                if (conflict) return ContentConflict();
                return replay is null ? null : Ok(replay);
            }
            catch (Exception ex) when (PersonBalance.IsDeadlock(ex))
            {
                return ReplayDeadlocked(licenseId, writtenKey);
            }
        }

        // §8.6: çok bacaklı çekim (yalnız Drawn ve Legs > 1); yanıt değişmez.
        CountMultiLegDraw(result);
        switch (result.Status)
        {
            case PersonBalance.DrawStatus.Drawn:
                return Ok(new ApplyResponse(
                    result.AnchorTransactionId, result.Applied, Math.Max(0m, result.Pool - result.Applied)));
            case PersonBalance.DrawStatus.Stopped:
                return stopped!;
            case PersonBalance.DrawStatus.NoBalance:
                // §4.2 adım 6'nın amacı 1. denemede de: kapsamsız yolda 1. denemenin tek Lookup'ı kilitsiz ön
                // oynatmadır. Aynı anahtarlı istek ön oynatmadan sonra commit edip havuzu tükettiyse ne jeton
                // çakışması ne PK ihlali olur. Σ ≤ 0 okuması o commit'i gördüyse aynı commit'teki çapayı bu Lookup da
                // görür. Değilse bugünkü başlık; WPF bunu kesin "bakiye yok" sayar (§4.2 adım 2, §4.0-1 kesin kol).
                return await ReplayWrittenKeyAsync()
                    ?? Problem(title: PersonBalance.CodeNoBalance, statusCode: 409);
            case PersonBalance.DrawStatus.NothingToApply:
                return Problem(title: "nothing-to-apply", statusCode: 409);
            case PersonBalance.DrawStatus.CustomerNotSynced:
                // Aynı pencere: aynı anahtarlı istek ön oynatmadan sonra commit etti, bu arada üyelik ya da S∖P'nin bir
                // bakiyesi değişti (kural 3, 3b). Kazananın kendi çekimi bunu doğurmaz ama ilgisiz bir yazım doğurabilir.
                return await ReplayWrittenKeyAsync()
                    ?? CustomerNotSynced(licenseId, "apply", result.RejectedRowIds);
            case PersonBalance.DrawStatus.DuplicateKey:
            {
                // Aynı anahtarla yarışan iki istek: PK ihlali bütün bacaklarla birlikte geri alındı (DrawAsync
                // izleyiciyi temizledi), kaybeden hiçbir iz bırakmaz. Kazananın sonucunu oynatabiliyorsak yarış
                // hikâyesi tutuyor demektir; tutmuyorsa hata gerçek bir DB sorunudur, yutulmamalı.
                var winner = await ReplayWrittenKeyAsync()
                    ?? throw new InvalidOperationException("Bakiye apply: anahtar PK ihlali verdi ama kazanan bulunamadı");
                if (winner is OkObjectResult)
                    _log.LogWarning(
                        "Bakiye uygulama yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                        req.IdempotencyKey, licenseId);
                return winner;
            }
            case PersonBalance.DrawStatus.Busy:
                return BalanceBusy(licenseId, "apply");
            default:
                // Insufficient yalnız AllOrNothing'de, AlreadyReversed yalnız çekim türü iptalde (K5) döner.
                throw new System.Diagnostics.UnreachableException(
                    $"Bakiye apply: DrawAsync beklenmeyen sonuç döndü ({result.Status})");
        }
    }

    /// <summary>
    /// §6.6, DM6: kapsamda geri alınmamış düşüm var → 409 <c>scope-has-deduction {transactionId, appliedAmount,
    /// productTotal}</c>; istemci onu benimser (W3). <c>appliedAmount</c> grubun toplamıdır (<c>−Σ</c>, spec 2026-10-11
    /// §4.2, §15-2) — <see cref="LatestScopeDeductionAsync"/>'ten. Satır kipi (<see cref="ApplyOncePerScopeAsync"/>)
    /// ve havuz kipi (<see cref="ApplyPoolAsync"/>) aynı gövdeyi döner.
    /// </summary>
    private ObjectResult ScopeHasDeduction(Guid licenseId, ScopeResponse existing)
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
            // Satır kipinin yolu: oynatılan RemainingBalance asıl kaydın bakiyesi (havuz kipi bu yönteme girmez).
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, canonicalId, key, pool: false, ct);
            if (foreign) return NotFound();
            if (conflict) return ContentConflict();
            if (replay is not null) return Ok(replay);
        }

        // Satır kipinin yolu; yanıt bayrağı burada kullanılmaz (409 uzantıları PersonPool taşımaz).
        if (await LatestScopeDeductionAsync(licenseId, canonicalId, saleScope, personPool: false, ct) is { } existing)
        {
            return ScopeHasDeduction(licenseId, existing);
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
                    licenseId, req, canonicalId, req.IdempotencyKey.Value, pool: false, ct);
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
    /// satırından gelir — "ne kadar düştü" cevabı değişmemeli. Satır kipinde
    /// asıl kaydın bakiyesi; havuz kipinde (<paramref name="pool"/>) o anki
    /// havuz (spec 2026-10-11 §4.2): <c>memberIds</c> varsa kural 4'ün
    /// <c>Σ</c>'sı, yoksa <c>S</c>'nin toplamı, 409 kuralları uygulanmadan
    /// (plan fark 18c, <see cref="PersonBalance.PoolBalanceAsync"/>).</para>
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
    /// <param name="pool">Lisans havuz kipinde mi; yalnız oynatılan
    /// <c>RemainingBalance</c>'ı belirler. Foreign, A11 ve <c>−Σ</c> iki kipte aynı.</param>
    private async Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(
        Guid licenseId, ApplyRequest req, Guid canonicalCustomerId, Guid key, bool pool, CancellationToken ct)
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

        // Satır kipi: kalan bakiye asıl kayıttan (kopyanın satırı birleştirmede 0'lanır).
        // Havuz kipi: o anki havuz, 409 kuralları uygulanmadan — spec §4.2, plan fark 18c;
        // kayıtlı anahtar hiçbir zaman customer-not-synced almaz.
        var remaining = pool
            ? await _person.PoolBalanceAsync(licenseId, canonicalCustomerId, req.MemberIds, ct)
            : (await _db.CustomerBalances
                .AsNoTracking()
                .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalCustomerId)
                .Select(b => (decimal?)b.Balance)
                .FirstOrDefaultAsync(ct) ?? 0m);

        _log.LogInformation(
            "Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})", key, licenseId);
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
    /// <para>Ortak bakiye §4.5: Id çapaya çözülür ve çekim GRUP olarak geri
    /// alınır (<see cref="PersonBalance.ReverseGroupAsync"/>); eski tek bacaklı
    /// düşümün geri alması bugünkü satırla aynı. Üç çakışmadan sonra 503
    /// balance-busy (hiçbir şey yazılmadı); dayanıklılık katmanı aynı isteği
    /// yeniden dener. Yanıtı kaybolmuş başarılı bir geri almanın tekrarı 409
    /// already-reversed alır, istemci bunu başarı sayar.</para>
    ///
    /// <para>Mod On'da sürüm kapısı (§6.5-3): eski sürümün revizyonu başka
    /// bilgisayarın teslim edilmiş düşümünü geri alırdı, ardından yeniden
    /// apply'ı 409 alır. v0.9.10 bu 409'u başarısızlık sayar, geri alma niyeti
    /// diskte kalır ve mesaj gitmez.</para></summary>
    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        var owned = await OwnedLicenseModeAsync(licenseId, ct);
        if (owned is null) return NotFound();
        var (mode, _) = owned.Value;
        if (mode == SalesSyncMode.On && !ClientMeetsMinimum())
            return ClientUpdateRequired(licenseId, "reverse");

        var target = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.Kind == KindPurchaseDeduction, ct);
        var anchor = target is null ? null : await _person.AnchorOfAsync(target, ct);
        // Bacak çapasıyla aynı türü taşır (D4); tür denetimi bozuk veride bu ucun
        // düşüm dışı bir grubu geri almasını önler.
        if (anchor is null || anchor.Kind != KindPurchaseDeduction) return NotFound();

        return await _person.ReverseGroupAsync(licenseId, anchor.Id, User.GetTenantCustomerId(), ct) switch
        {
            PersonBalance.ReverseOutcome.Reversed => Ok(),
            PersonBalance.ReverseOutcome.AlreadyReversed => Problem(title: "already-reversed", statusCode: 409),
            PersonBalance.ReverseOutcome.Busy => BalanceBusy(licenseId, "reverse"),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Havuz kipinde apply'ın oynatma Lookup'ı (ön oynatma ya da çekimden sonraki son Lookup) kilitlenme kurbanı
    /// (1205) oldu: oynatma <c>PoolBalanceAsync</c> ile CustomerBalances'ı okur ve yazarların X kilidinin arkasında
    /// kalabilir; bu okuma <c>DrawAsync</c>'in korunan denemesinin dışında. Hiçbir şey yazılmadı → 503
    /// <c>balance-busy</c>, WPF aynı anahtarla yeniden dener (500 değil; panel iptalinin üyelik okumasıyla aynı karar,
    /// plan fark 18k). Servis bu kaynakta bir şey yazmadığı için ayırt edici satır burada.
    /// </summary>
    private ObjectResult ReplayDeadlocked(Guid licenseId, Guid key)
    {
        _log.LogInformation(
            "customer-balance/apply: oynatma Lookup'ı kilitlenme kurbanı (1205), 503 balance-busy "
            + "(key={Key}, license={LicenseId})",
            key, licenseId);
        return BalanceBusy(licenseId, "apply");
    }

    private ObjectResult BalanceBusy(Guid licenseId, string endpoint)
    {
        _metrics.BalanceBusy.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint));
        // Ayırt edici satır çağıranda: DrawAsync/ReverseGroupAsync'in Busy kolunda servisin uyarısı (neden ve çapayla),
        // servis dışı okuma kilitlenmesinde çağıranın kendi satırı (ReplayDeadlocked; panelde ReadDeadlocked). Burada
        // yalnız hangi ucun 503 döndüğü.
        _log.LogDebug(
            "customer-balance/{Endpoint}: 503 balance-busy (license={LicenseId})",
            endpoint, licenseId);
        return Problem(title: CodeBalanceBusy, statusCode: 503,
            detail: "Bakiye şu an başka bir işlemle güncelleniyor; yeniden deneyin.");
    }

    /// <summary>
    /// Çok bacaklı çekim sayacı (spec §8.6, plan S2 fark 18h): yalnız yazılmış (<c>Drawn</c>) ve birden çok satıra
    /// dokunan çekim; etiket <c>legs</c> çapa dahil yazılan hareket satırı sayısıdır. Yalnız çapaya yazılan çekim
    /// sayılmaz; <c>Drawn</c> dışındaki sonuçlarda <c>Legs</c> zaten 0.
    /// </summary>
    private void CountMultiLegDraw(PersonBalance.DrawResult result)
    {
        if (result.Status == PersonBalance.DrawStatus.Drawn && result.Legs > 1)
            _metrics.BalanceMultiLegDraws.Add(1, new KeyValuePair<string, object?>("legs", result.Legs));
    }

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

    /// <summary>Lisans çağırana aitse satış senkronu kipi ve ortak bakiye kipi (spec 2026-10-11 §4.0: havuz kipi ⇔
    /// <c>PersonBalancePoolSince</c> dolu); değilse null (uç 404 döner). Sahiplik, satış senkronu kipi ve ortak
    /// bakiye kipi tek sorguda; kip her istekte okunur.</summary>
    private async Task<(SalesSyncMode Mode, bool Pool)?> OwnedLicenseModeAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        var row = await _db.Licenses
            .Where(l => l.Id == licenseId && l.CustomerId == callerId)
            .Select(l => new { l.SalesSyncMode, Pool = l.PersonBalancePoolSince != null })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.SalesSyncMode, row.Pool);
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
        // §8.5-8.6: endpoint preview | scope | apply | reverse (scope: v0.9.10 önce onu sorar, R6-01).
        _metrics.BalanceClientUpdateRequired.Add(1,
            new KeyValuePair<string, object?>("gate", "sales-sync"),
            new KeyValuePair<string, object?>("endpoint", endpoint));
        _log.LogInformation(
            "customer-balance/{Endpoint}: mod On'da istemci alt sürümün altında ya da alt sürüm tanımsız, "
            + "409 client-update-required (license={LicenseId})",
            endpoint, licenseId);
        return Problem(title: CodeClientUpdateRequired, statusCode: 409,
            detail: "Bakiye işlemi için uygulamanın güncel sürümü gerekiyor.");
    }

    /// <summary>Havuz kipinde <c>memberIds</c> var mı. Yok (null) ile boş aynı: <c>a</c> her zaman W'ye eklendiği
    /// için boş liste anlamlı bir girdi değil (plan fark 18f).</summary>
    private static bool HasMemberIds(IReadOnlyCollection<Guid>? memberIds) => memberIds is { Count: > 0 };

    /// <summary>İstemci (<c>User-Agent</c>) <c>PersonBalance:MinClientVersion</c>'ı (Wh, spec §7.2) karşılıyor mu.
    /// Satış senkronu kapısıyla aynı karşılaştırma (<see cref="ClientVersion.IsAtLeast"/>, §15): kapalı-hata, sürüm
    /// bilinmiyor ya da ayrıştırılamıyorsa ya da alt sınır tanımsızsa false.</summary>
    private bool PoolClientMeetsMinimum() =>
        ClientVersion.IsAtLeast(
            ClientVersion.FromUserAgent(Request.Headers.UserAgent.ToString()),
            _personBalance.ParseMinClientVersion());

    /// <summary>
    /// Havuz kipinin 409 <c>client-update-required</c>'ı. Başlık ve metin satış senkronu kapısınınkiyle aynıdır;
    /// istemci ayırt etmez. Günlük ayırır:
    /// <list type="bullet">
    ///   <item>Alt sınır tanımsız ya da bozuksa yapılandırma hatası, <b>Error</b> (§7.2, plan fark 18j).</item>
    ///   <item>Aksi hâlde eski ya da sürümsüz istemci, Information: lisans ve ham sürüm (yoksa "yok"). Kapı
    ///   açıldıktan sonra dönen eski PC böyle görünür (§7.2 "çalışma anı koruması").</item>
    /// </list>
    /// </summary>
    private ObjectResult PoolClientUpdateRequired(Guid licenseId, string endpoint)
    {
        _metrics.BalanceClientUpdateRequired.Add(1,
            new KeyValuePair<string, object?>("gate", "person-balance"),
            new KeyValuePair<string, object?>("endpoint", endpoint));
        if (_personBalance.ParseMinClientVersion() is null)
            _log.LogError(
                "customer-balance/{Endpoint}: havuz kipinde PersonBalance:MinClientVersion tanımsız ya da "
                + "ayrıştırılamıyor, 409 client-update-required (license={LicenseId})",
                endpoint, licenseId);
        else
            _log.LogInformation(
                "customer-balance/{Endpoint}: havuz kipinde istemci sürümü {Version} alt sınırın altında ya da "
                + "bilinmiyor, 409 client-update-required (license={LicenseId})",
                endpoint, ClientVersion.RawFromUserAgent(Request.Headers.UserAgent.ToString()) ?? "yok", licenseId);
        return Problem(title: CodeClientUpdateRequired, statusCode: 409,
            detail: "Bakiye işlemi için uygulamanın güncel sürümü gerekiyor.");
    }

    /// <summary>Havuz kipinde üye listesi olmayan istek (spec §4.0): 400 <c>member-ids-required</c>. Sürüm
    /// kapısından SONRA çağrılır (plan fark 3).</summary>
    private ObjectResult MemberIdsRequired(Guid licenseId, string endpoint)
    {
        _log.LogInformation(
            "customer-balance/{Endpoint}: havuz kipinde memberIds yok, 400 member-ids-required (license={LicenseId})",
            endpoint, licenseId);
        return Problem(title: CodeMemberIdsRequired, statusCode: 400,
            detail: "Havuz kipinde kişinin üye listesi (memberIds) zorunlu.");
    }

    /// <summary>
    /// 409 <c>customer-not-synced</c> (spec §4.0 kural 1 ve 3, K2). Günlüğe lisans ve 409'a yol açan satırların Id'leri
    /// ("N", virgülle) yazılır, ad ve telefon yazılmaz (§5.5, plan fark 18i). Kalıcı bir uyuşmazlık (damgasız eski
    /// <c>GroupId</c>) bu satırlardan izlenir. Gövdeye uzantı eklenmez.
    /// </summary>
    private ObjectResult CustomerNotSynced(Guid licenseId, string endpoint, IReadOnlyList<Guid> rowIds)
    {
        _metrics.BalanceCustomerNotSynced.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint));
        _log.LogInformation(
            "customer-balance/{Endpoint}: 409 customer-not-synced (license={LicenseId}, rows={RowIds})",
            endpoint, licenseId, string.Join(",", rowIds.Select(id => id.ToString("N"))));
        return Problem(title: PersonBalance.CodeCustomerNotSynced, statusCode: 409,
            detail: "Kişinin bir üyesi sunucuya henüz ulaşmadı; senkron tamamlanınca yeniden deneyin.");
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == callerId, ct);
    }
}
