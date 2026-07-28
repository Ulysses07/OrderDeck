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

    // ── POST apply ──────────────────────────────────────────────────────────

    public sealed record ApplyRequest(
        Guid WpfCustomerId,
        decimal Amount,
        decimal ProductTotal,
        Guid? IdempotencyKey = null);

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

        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        // Sahiplik kontrolünden SONRA bakıyoruz: anahtar başka lisansa aitse
        // çağıran onun sonucunu görmemeli.
        if (req.IdempotencyKey is { } preKey)
        {
            var (replay, foreign) = await LookupAsync(licenseId, preKey, ct);
            if (foreign) return NotFound();
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

        _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = txId,
            LicenseId = licenseId,
            WpfCustomerId = req.WpfCustomerId,
            Amount = -appliedAmount,
            Kind = KindPurchaseDeduction,
            OriginalAmount = req.ProductTotal,
            Reason = null,
            CreatedByCustomerId = customerId,
            CreatedAt = now,
        });

        balance.Balance -= appliedAmount;
        balance.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (req.IdempotencyKey is not null)
        {
            // Aynı anahtarla yarışan iki istek: PK ihlali TÜM transaction'ı geri
            // alır (bakiye düşümü dahil), yani kaybeden taraf hiçbir iz bırakmaz.
            // Kazananın sonucunu oynatabiliyorsak yarış hikâyesi tutuyor demektir;
            // tutmuyorsa hata gerçek bir DB sorunudur, yutulmamalı.
            _db.ChangeTracker.Clear();
            var (winner, _) = await LookupAsync(licenseId, req.IdempotencyKey.Value, ct);
            if (winner is null) throw;
            _log.LogWarning(
                "Bakiye uygulama yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                req.IdempotencyKey, licenseId);
            return Ok(winner);
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
    /// <para><c>RemainingBalance</c> o anki gerçek bakiyedir (donmuş bir kopya
    /// değil): istemci bunu ekranda gösteriyor, eski bir değeri oynatmak
    /// operatöre yanlış bakiye gösterirdi. <c>AppliedAmount</c> ise ledger
    /// satırından gelir — "ne kadar düştü" cevabı değişmemeli.</para>
    /// </summary>
    private async Task<(ApplyResponse? Replay, bool Foreign)> LookupAsync(
        Guid licenseId, Guid key, CancellationToken ct)
    {
        var tx = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == key, ct);
        if (tx is null) return (null, false);

        if (tx.LicenseId != licenseId || tx.Kind != KindPurchaseDeduction)
        {
            _log.LogWarning(
                "Bakiye idempotency anahtarı başka bir kayda ait (key={Key}, license={LicenseId}, kind={Kind})",
                key, licenseId, tx.Kind);
            return (null, true);
        }

        var remaining = await _db.CustomerBalances
            .AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == tx.WpfCustomerId)
            .Select(b => (decimal?)b.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;

        _log.LogInformation(
            "Bakiye uygulama sonucu tekrar oynatıldı (key={Key}, license={LicenseId})", key, licenseId);
        return (new ApplyResponse(tx.Id, -tx.Amount, remaining), false);
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == callerId, ct);
    }
}
