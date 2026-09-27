using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Pages.Admin.BankaEslestirme;

/// <summary>Gölge mod görünürlüğü (spec §8/§9): son 30 gün gelen hareketler, öneri, insan kararı, elle eşle / kaldır, ölçüm
/// kutusu. Otomatik onay yok; bu sayfa Payment'a dokunmaz. [Authorize] yok — Program.cs `AuthorizeFolder("/Admin")`.
/// <para>Karar yalnız <see cref="PaymentMatchReconciler"/>'dan yazılır: hareketin formdaki lisansa ait olduğunu, verilmiş bir
/// kararın ezilmediğini ve eşzamanlılığı o denetler. Admin'e yalnız onun doğrulama mesajları
/// (<see cref="ObifinValidationException"/>: hareket/müşteri yok, zaten karara bağlı, çakışma) gösterilir; başka her istisna
/// programlama hatasıdır, yukarı gider (Obifin sayfasıyla aynı kural). Bildirime çevrilen hata günlüğe yalnız işleyici adı
/// ve istisna türüyle düşer (<see cref="LogHandled"/>).</para>
/// <para>Görünüme ham JSON ve IBAN/VKN hash'i çıkmaz (satır modeli onları taşımaz, sorgu okumaz); karşı IBAN yalnız
/// maskeli. Açıklama ve kanıt admin'e gösterilir. KVKK'yla silinmiş müşterinin kullanıcı adı gösterilmez. Audit ayrıntısı
/// yalnız kullanıcı adıdır (açıklama, tutar, IBAN girmez) ve taahhüt edilmiş kararın ardından
/// <see cref="CancellationToken.None"/> ile yazılır: istek o anda iptal edilse karar yazılmış, izi kaybolmuş olurdu.</para>
/// <para><b>Banka modülü kapalıyken de sayfa açılır</b> (<see cref="BankHasher.IsValidKey"/>): uyarı basılır, veri
/// gösterilir, her POST <see cref="BankHasher.DisabledMessage"/> ile döner ve hiçbir şey yazmaz. Bağdaştırıcı, eşleştirici
/// ve ölçüm <see cref="BankHasher"/> istemez; kurucuya enjekte edilmeleri sayfayı düşürmez.</para></summary>
public class IndexModel : PageModel
{
    public const int PageSize = 50;
    public const int WindowDays = 30;
    public const string UnknownUsernameMessage = "Kullanıcı adı bu lisansta bulunamadı.";
    public const string AmbiguousUsernameMessage = "Bu kullanıcı adı bu lisansta birden çok müşteride var; eşlenmedi.";
    /// <summary>KVKK'yla silinmiş müşterinin kullanıcı adı yerine.</summary>
    public const string PurgedCustomerLabel = "(silinmiş müşteri)";

    private readonly LicenseDbContext _db;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly PaymentMatchMetrics _metrics;
    private readonly IAuditService _audit;
    private readonly BankOptions _bank;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(LicenseDbContext db, PaymentMatchReconciler reconciler, PaymentMatchMetrics metrics, IAuditService audit,
        IOptions<BankOptions> bank, ILogger<IndexModel> log)
    { _db = db; _reconciler = reconciler; _metrics = metrics; _audit = audit; _bank = bank.Value; _log = log; }

    public sealed record LicenseOption(Guid Id, string Label);
    public sealed record Row(Guid TransactionId, DateTimeOffset OccurredAt, decimal Amount, string Currency, string BankaKodu,
        string? Description, string? CounterpartyIbanMasked, string? ProposedUsername, PaymentMatchLayer Layer, decimal Confidence,
        string? Evidence, PaymentMatchStatus? Status, string? ActualUsername, Guid? PaymentId)
    {
        /// <summary>İnsan kararına bağlı (elle ya da dekont onayıyla): kaldırılabilir, elle eşlenemez.</summary>
        public bool IsLinked => PaymentId is not null
            || Status is PaymentMatchStatus.ConfirmedByHuman or PaymentMatchStatus.Contradicted or PaymentMatchStatus.ManualOnly;
    }

    [BindProperty(SupportsGet = true)] public Guid LicenseId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;
    [BindProperty] public Guid TransactionId { get; set; }
    [BindProperty] public string? Username { get; set; }

    public List<LicenseOption> Licenses { get; private set; } = new();
    public PaymentMatchSummary? Summary { get; private set; }
    public List<Row> Rows { get; private set; } = new();
    public bool HasNext { get; private set; }

    /// <summary>Açılıştaki kararın aynısı (Program.cs): anahtar yoksa banka modülü kapalı.</summary>
    public bool BankDisabled => !BankHasher.IsValidKey(_bank.HashKey);

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Yalnız Obifin bağlantısı olan lisanslar: banka hareketi ancak onlarda var. Düz GET'te hiçbiri seçili gelmez; listede
        // olmayan lisans (?licenseId=) seçilmiş sayılmaz — seçim kutusu ile gösterilen veri hep aynı lisans.
        Licenses = await _db.Licenses.AsNoTracking()
            .Where(l => _db.ObifinConnections.Any(c => c.LicenseId == l.Id))
            .OrderBy(l => l.Customer.Email)
            .Select(l => new LicenseOption(l.Id, l.Customer.Email + " · " + l.LicenseKey)).ToListAsync(ct);
        if (!Licenses.Any(l => l.Id == LicenseId))
        {
            LicenseId = Guid.Empty;
            return;
        }
        if (PageNo < 1) PageNo = 1;

        Summary = await _metrics.ComputeAsync(LicenseId, WindowDays, ct);
        var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);
        // Ham JSON ve hash'ler okunmaz. Aynı anlı hareketler sayfalar arasında kaymasın: ikinci sıra Obifin kimliği (lisansta tekil).
        var txs = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == LicenseId && t.Direction == BankTransactionDirection.Incoming && t.OccurredAt >= since)
            .OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.ObifinId)
            .Skip((PageNo - 1) * PageSize).Take(PageSize + 1)
            .Select(t => new { t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.Description, t.CounterpartyIbanMasked })
            .ToListAsync(ct);
        HasNext = txs.Count > PageSize;
        if (HasNext) txs.RemoveAt(txs.Count - 1);
        var txIds = txs.Select(t => t.Id).ToList();
        var matches = await _db.PaymentMatches.AsNoTracking()
            .Where(m => m.LicenseId == LicenseId && txIds.Contains(m.BankTransactionId))
            .ToDictionaryAsync(m => m.BankTransactionId, ct);
        // Yalnız bu sayfanın önerdiği ya da bağlandığı müşteriler okunur.
        var customerIds = matches.Values.SelectMany(m => new[] { m.ProposedWpfCustomerId, m.ActualWpfCustomerId })
            .OfType<Guid>().Distinct().ToList();
        var names = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(c => c.LicenseId == LicenseId && customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Username, c.PurgedAt })
            .ToDictionaryAsync(c => c.Id, c => c.PurgedAt == null ? c.Username : PurgedCustomerLabel, ct);
        string? NameOf(Guid? id) => id is { } g && names.TryGetValue(g, out var u) ? u : null;
        Rows = txs.Select(t =>
        {
            matches.TryGetValue(t.Id, out var m);
            return new Row(t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.Description, t.CounterpartyIbanMasked,
                NameOf(m?.ProposedWpfCustomerId), m?.Layer ?? PaymentMatchLayer.None, m?.Confidence ?? 0m, m?.Evidence, m?.Status,
                NameOf(m?.ActualWpfCustomerId), m?.PaymentId);
        }).ToList();
    }

    /// <summary>Kullanıcı adı tam eşitlikle, yalnız formdaki lisansın silinmemiş müşterilerinde aranır. Aynı ad birden çok
    /// müşterideyse (ör. iki platform) hangisi olduğu tahmin edilmez.</summary>
    public async Task<IActionResult> OnPostManualMatchAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        var username = (Username ?? "").Trim();
        var customerIds = username.Length == 0
            ? []
            : await _db.WpfCustomerProjections.AsNoTracking()
                .Where(c => c.LicenseId == LicenseId && c.Username == username && c.PurgedAt == null)
                .Select(c => c.Id).ToListAsync(ct);
        if (customerIds.Count != 1)
        {
            TempData["Error"] = customerIds.Count == 0 ? UnknownUsernameMessage : AmbiguousUsernameMessage;
            return Back();
        }
        try
        {
            await _reconciler.ManualMatchAsync(LicenseId, TransactionId, customerIds[0], ct);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("ManualMatch", ex);
            TempData["Error"] = ex.Message;
            return Back();
        }
        // Karar kaydedildi: audit istek iptaliyle düşmesin.
        await _audit.LogAsync(AuditEvents.BankMatchManual, AuditTargets.BankTransaction, TransactionId.ToString(), new { username },
            CancellationToken.None);
        TempData["Success"] = "Eşlendi.";
        return Back();
    }

    public async Task<IActionResult> OnPostUnmatchAsync(CancellationToken ct)
    {
        if (BankDisabled) return BankDisabledResult();
        try
        {
            await _reconciler.UnmatchAsync(LicenseId, TransactionId, ct);
        }
        catch (ObifinValidationException ex)
        {
            LogHandled("Unmatch", ex);
            TempData["Error"] = ex.Message;
            return Back();
        }
        // Kaldırma kaydedildi: audit istek iptaliyle düşmesin.
        await _audit.LogAsync(AuditEvents.BankMatchUnmatch, AuditTargets.BankTransaction, TransactionId.ToString(), null,
            CancellationToken.None);
        TempData["Success"] = "Eşleme kaldırıldı: bu hareketten öğrenilen IBAN silindi, bağlı dekont varsa gap'e döndü, öneri yeniden hesaplandı.";
        return Back();
    }

    /// <summary>İşlemin yapıldığı lisans ve sayfaya döner.</summary>
    private RedirectToPageResult Back() => RedirectToPage(new { licenseId = LicenseId, pageNo = PageNo });

    private RedirectToPageResult BankDisabledResult()
    {
        TempData["Error"] = BankHasher.DisabledMessage;
        return Back();
    }

    /// <summary>Bildirime çevrilen hatanın sunucu izi: işleyici adı + istisna türü. Form değerleri (kullanıcı adı) yazılmaz.</summary>
    private void LogHandled(string handler, Exception ex)
        => _log.LogWarning("Banka eşleştirme admin {Handler} hatayı bildirime çevirdi: {ExceptionType}", handler, ex.GetType().Name);
}
