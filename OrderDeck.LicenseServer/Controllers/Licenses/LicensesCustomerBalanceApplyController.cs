using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

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
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/customer-balance")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesCustomerBalanceApplyController : ControllerBase
{
    /// <summary>Bu ucun yazdığı ledger satırının türü. Idempotency oynatması
    /// yalnız bu türü kabul eder — iade satırları bu ucun anahtarı olamaz.</summary>
    private const string KindPurchaseDeduction = "purchase-deduction";

    private readonly LicenseDbContext _db;
    private readonly ILogger<LicensesCustomerBalanceApplyController> _log;

    public LicensesCustomerBalanceApplyController(
        LicenseDbContext db, ILogger<LicensesCustomerBalanceApplyController> log)
    {
        _db = db;
        _log = log;
    }

    public sealed record PreviewQuery(Guid WpfCustomerId);

    public sealed record PreviewResponse(
        Guid WpfCustomerId,
        decimal Balance,
        DateTimeOffset UpdatedAt);

    // ── GET preview ─────────────────────────────────────────────────────────

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var row = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Select(b => new PreviewResponse(b.WpfCustomerId, b.Balance, b.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        return Ok(row ?? new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow));
    }

    // ── GET scope (R4-03 uzlaştırma) ────────────────────────────────────────

    public sealed record ScopeResponse(
        Guid TransactionId,
        decimal AppliedAmount,
        decimal ProductTotal,
        DateTimeOffset CreatedAt);

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

        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var row = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .Where(t => t.LicenseId == licenseId
                && t.WpfCustomerId == wpfCustomerId
                && t.SaleScope == saleScope
                && t.Kind == KindPurchaseDeduction
                && !_db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Select(t => new ScopeResponse(t.Id, -t.Amount, t.OriginalAmount ?? 0m, t.CreatedAt))
            .FirstOrDefaultAsync(ct);

        return row is null ? NoContent() : Ok(row);
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

        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        // Sahiplik kontrolünden SONRA bakıyoruz: anahtar başka lisansa aitse
        // çağıran onun sonucunu görmemeli.
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign, conflict) = await LookupAsync(licenseId, req, preKey, ct);
            if (foreign) return NotFound();
            if (conflict)
                return Problem(title: "content-conflict", statusCode: 409,
                    detail: "Idempotency anahtarı farklı bir istekle kullanılmış.");
            if (replay is not null) return Ok(replay);
        }

        var balance = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId
                && b.WpfCustomerId == req.WpfCustomerId, ct);
        if (balance is null || balance.Balance <= 0)
            return Problem(title: "no-balance", statusCode: 409);

        // İstenen tutar bakiyeden fazla olamaz; sipariş tutarından da fazla
        // olamaz (mantıksızlık).
        var appliedAmount = Math.Min(Math.Min(req.Amount, balance.Balance), req.ProductTotal);
        if (appliedAmount <= 0)
            return Problem(title: "nothing-to-apply", statusCode: 409);

        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        // Anahtar VARSA ledger satırının PK'sı odur — rezervasyon budur.
        var txId = req.IdempotencyKey ?? Guid.NewGuid();

        var tx = new CustomerBalanceTransaction
        {
            Id = txId,
            LicenseId = licenseId,
            WpfCustomerId = req.WpfCustomerId,
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
                    return Problem(title: "no-balance", statusCode: 409);
                }
                appliedAmount = Math.Min(Math.Min(req.Amount, balance.Balance), req.ProductTotal);
                if (appliedAmount <= 0)
                {
                    _db.ChangeTracker.Clear();
                    return Problem(title: "nothing-to-apply", statusCode: 409);
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
                var (winner, _, conflict) = await LookupAsync(licenseId, req, req.IdempotencyKey.Value, ct);
                if (conflict)
                    return Problem(title: "content-conflict", statusCode: 409,
                        detail: "Idempotency anahtarı farklı bir istekle kullanılmış.");
                if (winner is null) throw;
                _log.LogWarning(
                    "Bakiye uygulama yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                    req.IdempotencyKey, licenseId);
                return Ok(winner);
            }
        }

        return Ok(new ApplyResponse(txId, appliedAmount, balance.Balance));
    }

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
    /// </summary>
    private async Task<(ApplyResponse? Replay, bool Foreign, bool ContentConflict)> LookupAsync(
        Guid licenseId, ApplyRequest req, Guid key, CancellationToken ct)
    {
        var tx = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == key, ct);
        if (tx is null) return (null, false, false);

        if (tx.LicenseId != licenseId || tx.Kind != KindPurchaseDeduction)
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
        if (tx.WpfCustomerId != req.WpfCustomerId
            || tx.OriginalAmount != req.ProductTotal
            || -tx.Amount > req.Amount)
        {
            _log.LogWarning(
                "Bakiye idempotency anahtarı farklı içerikle yeniden kullanıldı (key={Key}, license={LicenseId})",
                key, licenseId);
            return (null, false, true);
        }

        var remaining = await _db.CustomerBalances
            .AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == tx.WpfCustomerId)
            .Select(b => (decimal?)b.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;

        _log.LogInformation(
            "Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})", key, licenseId);
        return (new ApplyResponse(tx.Id, -tx.Amount, remaining), false, false);
    }

    // ── POST reverse (revizyon: eski düşümü geri al) ────────────────────────

    /// <summary>Spec K2: aynı satışın toplamı değişince WPF eski düşümü geri
    /// alıp yeni toplamla taze apply yapar. Panel'deki reverse'in WPF-yüzeyi
    /// ikizi — ama kapsamı dar: yalnız BU lisansın purchase-deduction satırı.
    /// Hakem N01 filtered-unique index (ReversesTransactionId): yarışan ikinci
    /// reverse SaveChanges'te unique ihlali alır → 409 already-reversed.
    /// İstemci 409 already-reversed'i BAŞARI sayar (geri alma zaten olmuş).</summary>
    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid licenseId, Guid transactionId, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

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

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == callerId, ct);
    }
}
