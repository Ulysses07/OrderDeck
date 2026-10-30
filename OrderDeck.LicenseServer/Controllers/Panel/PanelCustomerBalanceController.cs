using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncı paneli — müşteri bakiye yönetimi (iade/refund kredisi).
///
/// Senaryolar:
///   - Hatalı ürün (refund-full): tam tutar bakiye olarak eklenir
///   - Müşteri iadesi (refund-net): tutar − kargo bakiye olarak eklenir
///   - Yanlış kayıt → reversal: önceki transaction'ı geri al
///   - Manuel ayar: yayıncı serbest miktar +/− ekleme
///
/// Bakiye DÜŞÜLMESİ bu controller'da değil — WPF "Ödeme iste" anında
/// LicensesCustomerBalanceApplyController'a (sonraki) çağrı yapılır.
///
/// Rotadaki Id bir kopya olabilir (A5b: eski bağlantı/sekme ya da eski
/// sürümün Id'si): her uç onu önce asıl kayda çözer, işlem asıl kayıtla
/// yapılır — kopyaya asla bakiye satırı açılmaz.
/// </summary>
[ApiController]
[Route("api/panel/customers/{wpfCustomerId:guid}/balance")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelCustomerBalanceController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;
    private readonly ILogger<PanelCustomerBalanceController> _log;

    public PanelCustomerBalanceController(
        LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person,
        ILogger<PanelCustomerBalanceController> log)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
        _log = log;
    }

    public sealed record BalanceDto(
        Guid WpfCustomerId,
        Guid LicenseId,
        decimal Balance,
        DateTimeOffset UpdatedAt);

    public sealed record TransactionDto(
        Guid Id,
        decimal Amount,
        string Kind,
        decimal? OriginalAmount,
        decimal? ShippingDeducted,
        string? Reason,
        Guid? ReversesTransactionId,
        DateTimeOffset CreatedAt);

    /// <summary>Ortak bakiye (spec 2026-10-11 §4.6): kişinin bir üyesi — WPF'in Ayır onayı kullanır; panel yok sayar.</summary>
    public sealed record MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);

    public sealed record BalanceDetailsResponse(
        BalanceDto Balance,
        TransactionDto[] Transactions,
        MemberDto[] Members);

    // ── GET — mevcut bakiye + son N transaction ─────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Get(
        Guid wpfCustomerId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take < 1 || take > 200) take = 50;

        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        // Ortak bakiye kipi lisans başına, her istekte okunur (spec 2026-10-11 §4.0). Havuz kipi ayrı yöntemde;
        // satır kipinin yolu aşağıda S1'deki gibi.
        if (await _person.IsPoolModeAsync(licenseId, ct))
            return Ok(await PoolDetailsAsync(wpfCustomerId, licenseId, canonicalId, take, ct));

        // Ortak bakiye satır kipi (bayrak boş; spec 2026-10-11 §4.6): bakiye ve hareketler rota satırınınki, hareketler
        // gruplara toplanmış; take grup sayar. UpdatedAt son para hareketidir (M9): grup değişimi kancasının ve
        // birleştiricinin jeton dokunuşu "güncellendi" zamanını oynatmaz.
        var row = await _db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new { b.Balance, b.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        var lastMovement = await _person.LastMovementAtAsync(licenseId, [canonicalId], ct);
        // Yanıttaki Id rotadaki Id'dir (önceki davranış); bakiye asıl kaydınki.
        var balance = new BalanceDto(wpfCustomerId, licenseId, row?.Balance ?? 0m,
            lastMovement ?? row?.UpdatedAt ?? DateTimeOffset.UtcNow);

        var history = await _person.HistoryAsync(licenseId, [canonicalId], take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.Reason, h.ReversesTransactionId, h.CreatedAt))
            .ToArray();

        // Members (§4.6): iki kipte de S üzerinden, üye başına satır bakiyesi (silinmişler dahil).
        var person = await _person.MembersAsync(licenseId, canonicalId, ct);
        var members = person.Members.Select(m => new MemberDto(m.WpfCustomerId, m.Balance, m.Purged)).ToArray();

        return Ok(new BalanceDetailsResponse(balance, transactions, members));
    }

    // ── POST refund-full ────────────────────────────────────────────────────

    public sealed record RefundFullRequest(decimal Amount, string? Reason);

    [HttpPost("refund-full")]
    public async Task<IActionResult> RefundFull(
        Guid wpfCustomerId,
        [FromBody] RefundFullRequest req,
        CancellationToken ct)
    {
        if (req.Amount <= 0) return Problem(title: "invalid-amount", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        await ApplyTransactionAsync(licenseId, canonicalId, customerId,
            amount: req.Amount,
            kind: "refund-full",
            originalAmount: req.Amount,
            shippingDeducted: null,
            reason: TrimReason(req.Reason),
            reverses: null,
            disallowNegative: false,
            ct: ct);

        return Ok();
    }

    // ── POST refund-net (kargo düşülmüş iade) ───────────────────────────────

    public sealed record RefundNetRequest(
        decimal OriginalAmount,
        decimal ShippingDeducted,
        string? Reason);

    [HttpPost("refund-net")]
    public async Task<IActionResult> RefundNet(
        Guid wpfCustomerId,
        [FromBody] RefundNetRequest req,
        CancellationToken ct)
    {
        if (req.OriginalAmount <= 0)
            return Problem(title: "invalid-amount", statusCode: 400);
        if (req.ShippingDeducted < 0 || req.ShippingDeducted >= req.OriginalAmount)
            return Problem(title: "invalid-shipping", statusCode: 400);

        var netAmount = req.OriginalAmount - req.ShippingDeducted;
        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        await ApplyTransactionAsync(licenseId, canonicalId, customerId,
            amount: netAmount,
            kind: "refund-net",
            originalAmount: req.OriginalAmount,
            shippingDeducted: req.ShippingDeducted,
            reason: TrimReason(req.Reason),
            reverses: null,
            disallowNegative: false,
            ct: ct);

        return Ok();
    }

    // ── POST manual-adjustment ──────────────────────────────────────────────

    public sealed record ManualAdjustmentRequest(decimal Amount, string Reason);

    [HttpPost("manual-adjustment")]
    public async Task<IActionResult> ManualAdjustment(
        Guid wpfCustomerId,
        [FromBody] ManualAdjustmentRequest req,
        CancellationToken ct)
    {
        if (req.Amount == 0) return Problem(title: "invalid-amount", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Problem(title: "reason-required", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        // Negatif manuel ayar bakiyeyi sıfırın altına düşürmesin — kontrol
        // ApplyTransactionAsync'in retry döngüsünün İÇİNDE yapılır (F02):
        // buradaki ayrı bir ön okuma, eşzamanlı yazımlarda bayat değere bakardı.
        var applied = await ApplyTransactionAsync(licenseId, canonicalId, customerId,
            amount: req.Amount,
            kind: "manual-adjustment",
            originalAmount: null,
            shippingDeducted: null,
            reason: TrimReason(req.Reason),
            reverses: null,
            disallowNegative: true,
            ct: ct);
        if (!applied) return Problem(title: "insufficient-balance", statusCode: 409);

        return Ok();
    }

    // ── POST reverse (yanlış kayıt iptal) ───────────────────────────────────

    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid wpfCustomerId,
        Guid transactionId,
        CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var (licenseId, canonicalId, valid) = await ResolveAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        var target = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.LicenseId == licenseId, ct);
        if (target is null) return NotFound();

        // Ortak bakiye S1 tabanı (spec 2026-10-11 §4.9): çekim grubuna ait hedef iki kipte de grup olarak ele
        // alınır — tek satırı geri almak yarım bir geri alma bırakırdı.
        if (target.DrawGroupId is { } anchorId)
            return await ReverseDrawGroupAsync(licenseId, canonicalId, customerId, anchorId, target, ct);

        // Eski, tek satırlık hedef: bugünkü yol, birebir (satır kipi).
        if (target.WpfCustomerId != canonicalId) return NotFound();

        // Daha önce reverse edilmiş mi? Bu ön kontrol hızlı yol — yarışta iki
        // istek de buradan geçebilir (check-then-insert). Asıl hakem aşağıdaki
        // filtered unique index (N01): kaybeden SaveChanges'te unique ihlali
        // alır ve 409'a çevrilir.
        var alreadyReversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == transactionId, ct);
        if (alreadyReversed) return Problem(title: "already-reversed", statusCode: 409);

        // Reversal balance'ı sıfırın altına düşürmesin — kontrol retry
        // döngüsünün içinde (F02, bkz. ManualAdjustment).
        var reverseAmount = -target.Amount;
        bool applied;
        try
        {
            applied = await ApplyTransactionAsync(licenseId, canonicalId, customerId,
                amount: reverseAmount,
                kind: "reversal",
                originalAmount: null,
                shippingDeducted: null,
                reason: $"Reverse of {transactionId:N}",
                reverses: transactionId,
                disallowNegative: true,
                ct: ct);
        }
        catch (DbUpdateException ex) when (PersonBalance.IsDuplicateReversal(ex))
        {
            // N01: eşzamanlı ikinci reverse — index hakemliğini kaybetti.
            // Transaction geri alındı, bakiyeye hiçbir şey yazılmadı.
            return Problem(title: "already-reversed", statusCode: 409);
        }
        if (!applied) return Problem(title: "insufficient-balance", statusCode: 409);

        return Ok();
    }

    /// <summary>
    /// Çekim grubuna ait hedef (spec §4.9, M8). Üyelik: grubun herhangi bir satırı rota satırında olmalı (S1 kip
    /// bayrağını okumaz → satır kipi koşulu; havuz kipinin S(rota) genişlemesi S2'de). Panel listesi grubu çapa
    /// Id'siyle gösterdiği için (§4.6) çapa başka satırda olsa da rota satırındaki bacaktan açılan iptal bulunur;
    /// <c>disable</c> sonrası düzeltme yolu da budur. Düşüm ve elle eksi grubu grup olarak geri alınır
    /// (<see cref="PersonBalance.ReverseGroupAsync"/>, §4.5); <c>reversal</c> grupları 409 <c>not-reversible</c>.
    /// </summary>
    private async Task<IActionResult> ReverseDrawGroupAsync(
        Guid licenseId, Guid canonicalId, Guid customerId, Guid anchorId, CustomerBalanceTransaction target,
        CancellationToken ct)
    {
        var group = await _db.CustomerBalanceTransactions
            .AsNoTracking()
            .Where(t => t.LicenseId == licenseId && (t.Id == anchorId || t.DrawGroupId == anchorId))
            .ToListAsync(ct);
        if (!group.Any(t => t.WpfCustomerId == canonicalId)) return NotFound();
        // Çapa kendi grubundadır (DrawGroupId = Id, §3.2). Öyle bir satır yoksa bacak yetimdir — verify sayar
        // (§7.3-7); DrawGroupId'si boş bir satırı çapa saymak yalnız onu geri alıp bacağı açıkta bırakırdı (D5).
        var anchor = group.FirstOrDefault(t => t.Id == anchorId && t.DrawGroupId == anchorId);
        if (anchor is null) return NotFound();

        // Bacak çapasıyla aynı türü taşır (D4); tür farkı bozuk veride başka bir grubu geri almayı önler.
        if (!PersonBalance.IsGroupReversible(anchor) || target.Kind != anchor.Kind)
            return Problem(title: "not-reversible", statusCode: 409,
                detail: "Bu hareket bir çekim grubunun iptali; iptal edilemez.");

        switch (await _person.ReverseGroupAsync(licenseId, anchorId, customerId, ct))
        {
            case PersonBalance.ReverseOutcome.Reversed:
                return Ok();
            case PersonBalance.ReverseOutcome.AlreadyReversed:
                return Problem(title: "already-reversed", statusCode: 409);
            case PersonBalance.ReverseOutcome.Busy:
                // Uyarı servisin (neden ve çapayla); burada yalnız hangi ucun 503 döndüğü.
                _log.LogDebug(
                    "panel balance/reverse: 503 balance-busy (license={LicenseId})", licenseId);
                return Problem(title: "balance-busy", statusCode: 503,
                    detail: "Bakiye şu an başka bir işlemle güncelleniyor; yeniden deneyin.");
            default:
                return NotFound();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Rotadaki Id → (lisans, asıl kayıt). WpfCustomerProjection → License →
    /// CustomerId (yayıncı) eşleşmesiyle çağıranın bu projeksiyona sahip olduğu
    /// doğrulanır (kiracı yalıtımı) — kopya Id'si için kopyanın KENDİ
    /// lisansından. Kopya asıl kayda çözülür (A5b); işlemler asıl kayıtla.
    /// </summary>
    private async Task<(Guid licenseId, Guid canonicalId, bool valid)> ResolveAsync(
        Guid wpfCustomerId, Guid callerCustomerId, CancellationToken ct)
        => await _customerIds.LocateForTenantAsync(callerCustomerId, wpfCustomerId, ct) is { } located
            ? (located.LicenseId, located.CanonicalId, true)
            : (Guid.Empty, Guid.Empty, false);

    /// <summary>
    /// GET, havuz kipi (spec 2026-10-11 §4.6, §6.1-f, M9, K3). <c>Balance = Σ_S</c>: eksi olabilir — borç operatöre
    /// görünmeli (D3); silinmiş üyeler dahil. Hareketler S'nin bütün üyelerinden gruplara toplanmış: grup tutarı bu
    /// kişideki bacakların toplamı (ayırmadan sonra öbür kişide kalan bacak sayılmaz), take grup sayar. UpdatedAt
    /// S'nin son para hareketi; S'de hiç hareket yoksa S'nin bakiye satırlarının en yenisi, o da yoksa şimdi.
    /// <c>Members</c> aynı okumadan (ikinci üyelik okuması yok); kişinin hangi üyesinden açılırsa açılsın aynı toplam
    /// ve aynı liste. Salt okuma, kilit yok (READ COMMITTED, ayrı sorgular — satır kipinde de öyle).
    /// </summary>
    /// <param name="requestId">Rotadaki Id — yanıtta o döner (kopya Id'si olabilir; satır kipiyle aynı).</param>
    private async Task<BalanceDetailsResponse> PoolDetailsAsync(
        Guid requestId, Guid licenseId, Guid canonicalId, int take, CancellationToken ct)
    {
        var person = await _person.MembersAsync(licenseId, canonicalId, ct);
        var ids = person.Members.Select(m => m.WpfCustomerId).ToList();

        var lastMovement = await _person.LastMovementAtAsync(licenseId, ids, ct);
        var updatedAt = lastMovement
            ?? await _db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
                .MaxAsync(b => (DateTimeOffset?)b.UpdatedAt, ct)
            ?? DateTimeOffset.UtcNow;
        var balance = new BalanceDto(requestId, licenseId, person.Members.Sum(m => m.Balance), updatedAt);

        // Panel yayıncının kendisi: açıklama maskesiz.
        var history = await _person.HistoryAsync(licenseId, ids, take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.Reason, h.ReversesTransactionId, h.CreatedAt))
            .ToArray();

        var members = person.Members.Select(m => new MemberDto(m.WpfCustomerId, m.Balance, m.Purged)).ToArray();
        return new BalanceDetailsResponse(balance, transactions, members);
    }

    /// <summary>
    /// Ledger satırı yazar + CustomerBalance.Balance'ı günceller (tek transaction).
    /// CustomerBalance yoksa oluşturur.
    ///
    /// F02 (2026-09-09 denetimi): Balance okuma-hesapla-yazma ile güncellenir;
    /// UpdatedAt concurrency token'ı sayesinde eşzamanlı bir yazım araya girerse
    /// SaveChanges DbUpdateConcurrencyException fırlatır. O durumda satır DB'den
    /// yeniden yüklenir, delta tekrar uygulanır ve (istenmişse) negatif-bakiye
    /// kontrolü GÜNCEL değer üzerinden tekrarlanır. Böylece hem cache ledger
    /// toplamından kopamaz hem de yetersiz-bakiye kuralı yarışta delinemez.
    /// </summary>
    /// <returns>false = işlem bakiyeyi sıfırın altına düşürürdü, hiçbir şey yazılmadı.</returns>
    private async Task<bool> ApplyTransactionAsync(
        Guid licenseId, Guid wpfCustomerId, Guid createdByCustomerId,
        decimal amount, string kind, decimal? originalAmount, decimal? shippingDeducted,
        string? reason, Guid? reverses, bool disallowNegative, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var now = DateTimeOffset.UtcNow;

        _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = wpfCustomerId,
            Amount = amount,
            Kind = kind,
            OriginalAmount = originalAmount,
            ShippingDeducted = shippingDeducted,
            Reason = reason,
            ReversesTransactionId = reverses,
            CreatedByCustomerId = createdByCustomerId,
            CreatedAt = now,
        });

        var existing = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId, ct);
        if (existing is null)
        {
            if (disallowNegative && amount < 0) return false;
            _db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Balance = amount,
                UpdatedAt = now,
            });
            // Yeni satır insert'i token'la korunmaz; eşzamanlı iki "ilk yazım"
            // unique (LicenseId, WpfCustomerId) index'ine takılır → gürültülü
            // DbUpdateException (500), istemci yeniden dener. Bilinçli tercih.
            await _db.SaveChangesAsync(ct);
            return true;
        }

        existing.Balance += amount;
        existing.UpdatedAt = now;

        for (var attempt = 1; ; attempt++)
        {
            if (disallowNegative && existing.Balance < 0) return false;
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                // Araya başka yazım girdi: bakiyeyi DB'deki güncel değere çek,
                // deltayı yeniden uygula. Added durumundaki ledger satırı
                // izlenmeye devam eder, sonraki SaveChanges'te yine insert edilir.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
                existing.Balance += amount;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private static string? TrimReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }
}
