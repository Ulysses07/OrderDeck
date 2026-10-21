using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Pages.Admin.BankaVeriTalepleri;

/// <summary>
/// IBAN ile banka verisi talepleri (KVKK K-7, Ö-11): talep aç, doğrulama yöntemini kaydet, hareketleri gör, erişim talebinde CSV
/// dışa aktar, silme talebinde sil ve kapat, sonuç kategorisiyle kapat ya da reddet. [Authorize] yok — Program.cs
/// <c>AuthorizeFolder("/Admin")</c>. Kurallar serviste (<see cref="BankSubjectRequestService"/>); sayfa onun
/// <see cref="ObifinValidationException"/>'larını (eşzamanlılık dahil) bildirime çevirir, yalnız uygulanabilir düğmeleri gösterir.
/// <para>Girilen IBAN yalnız istek belleğinde yaşar: sunucuda özetlenir; sayfaya, günlüğe, denetime girmez (sayfa yalnız
/// kısaltılmış hâlini gösterir). Talep açmak <see cref="BankHasher"/> ister (modül kapalıyken açılamaz, servis işleyicinin
/// içinde çözülür — kurucuda olsaydı sayfa anahtarsız 500 verirdi); arama, dışa aktarma, silme ve kapatma istemez.</para>
/// <para>Sayfanın her görüntülenmesi (liste de kısaltılmış IBAN gösterir) ve her işlem denetime yazılır (KVKK Ö-4): hedef
/// <see cref="AuditTargets.BankSubjectRequest"/> + sayfanın gösterdiği talep. Taahhüt edilmiş yazımın denetimi istek jetonuyla
/// DEĞİL <see cref="CancellationToken.None"/> ile yazılır. Oluşturan, doğrulayan ve kapatan sütunlarına yöneticinin kimliği
/// (<see cref="DataDestruction.ActorOf"/>) girer, kullanıcı adı değil (KVKK Ö-14: satır süresiz — karar 22).</para>
/// <para>Sistemin kapattığı talep (<see cref="BankSubjectResolution.DataDeleted"/>: bağlantı kaldırma, hizmet sonu, yayıncı
/// silmesi) son tarihine kadar açık listede "yanıt bekleniyor" ile kalır: talep kapandı ama başvurana yanıt yükümlülüğü sürer
/// (KVKK m. 13/2).</para>
/// </summary>
public class IndexModel : PageModel
{
    public const string PageName = "banka-veri-talepleri";
    public const string NoKindMessage = "Talep türünü seçin (erişim ya da silme).";

    private readonly LicenseDbContext _db;
    private readonly BankSubjectRequestService _requests;
    private readonly IServiceProvider _services;
    private readonly IAuditService _audit;
    private readonly BankOptions _bank;

    public IndexModel(LicenseDbContext db, BankSubjectRequestService requests, IServiceProvider services, IAuditService audit,
        IOptions<BankOptions> bank)
    { _db = db; _requests = requests; _services = services; _audit = audit; _bank = bank.Value; }

    public sealed record LicenseOption(Guid Id, string Label);

    /// <summary>Liste satırı. Kişisel veri yalnız kısaltılmış IBAN (kapanıştan 30 gün sonra boşalır).</summary>
    public sealed record RequestRow(Guid Id, string License, BankSubjectRequestKind Kind, string IbanMasked, DateTimeOffset ReceivedAt,
        DateTimeOffset DueAt, DateTimeOffset? ClosedAt, BankSubjectResolution Resolution, string? Outcome)
    {
        /// <summary>Açık ve son tarihi geçmiş.</summary>
        public bool Overdue(DateTimeOffset now) => ClosedAt is null && DueAt < now;

        /// <summary>Sistemin kapattığı, son tarihi geçmemiş talep: başvurana yanıt verilmeli.</summary>
        public bool AwaitingReply(DateTimeOffset now) => ClosedAt is not null && Resolution == BankSubjectResolution.DataDeleted
            && DueAt > now;
    }

    [BindProperty] public Guid LicenseId { get; set; }
    [BindProperty] public string? Iban { get; set; }
    /// <summary>Boş gelirse talep açılmaz (<see cref="NoKindMessage"/>): tür hakkı belirler, sessizce "erişim"e düşmesin.</summary>
    [BindProperty] public BankSubjectRequestKind? Kind { get; set; }
    /// <summary>Talebin ulaştığı TR günü; boşsa bugün (TR). İleri gün servisçe reddedilir.</summary>
    [BindProperty] public DateOnly? ReceivedOn { get; set; }
    [BindProperty] public BankSubjectVerification Verification { get; set; }
    /// <summary>Kapatmanın kategorisi (serbest metin yok).</summary>
    [BindProperty] public BankSubjectResolution Resolution { get; set; }

    public List<LicenseOption> Licenses { get; private set; } = new();
    public List<RequestRow> Requests { get; private set; } = new();
    public BankSubjectRequestService.Lookup? Selected { get; private set; }
    public bool ShowClosed { get; private set; }
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
    public DateOnly TodayTr => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, ObifinConnectionService.TrZone).DateTime);
    public string TodayTrIso => TodayTr.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Açılıştaki kararın aynısı (Program.cs): anahtar yoksa banka modülü kapalı, yeni talep açılamaz.</summary>
    public bool BankDisabled => !BankHasher.IsValidKey(_bank.HashKey);

    /// <summary>Doğrulama yönteminin görünen adı (seçici ve ayrıntı). Kişisel veri değil.</summary>
    public static string VerificationLabel(BankSubjectVerification method) => method switch
    {
        BankSubjectVerification.WetSignatureAndBankDocument => "Islak imzalı dilekçe + kimlik; IBAN banka belgesiyle",
        BankSubjectVerification.ElectronicSignatureAndBankDocument => "KEP / güvenli e-imza / mobil imza; IBAN banka belgesiyle",
        BankSubjectVerification.RegisteredContactAndBankDocument => "Kayıtlı iletişim kanalı; IBAN banka belgesiyle",
        _ => "Doğrulama kaydı yok",
    };

    public static string KindLabel(BankSubjectRequestKind kind) => kind == BankSubjectRequestKind.Erasure ? "Silme" : "Erişim";

    public async Task OnGetAsync(Guid? id, bool closed, CancellationToken ct)
    {
        ShowClosed = closed;
        Licenses = await _db.Licenses.AsNoTracking()
            .Where(l => _db.ObifinConnections.Any(c => c.LicenseId == l.Id))
            .OrderBy(l => l.Customer.Email)
            .Select(l => new LicenseOption(l.Id, l.Customer.Email + " · " + l.LicenseKey)).ToListAsync(ct);
        var now = Now;
        var query = _db.BankSubjectRequests.AsNoTracking();
        // Açık liste: açık talepler + sistemin kapattığı ve son tarihi geçmemiş talepler (yanıt bekleniyor; KVKK m. 13/2).
        query = closed
            ? query.Where(r => r.ClosedAt != null).OrderByDescending(r => r.ClosedAt)
            : query.Where(r => r.ClosedAt == null || (r.Resolution == BankSubjectResolution.DataDeleted && r.DueAt > now))
                .OrderBy(r => r.DueAt);
        var rows = await query.Take(100).ToListAsync(ct);
        var licenseIds = rows.Select(r => r.LicenseId).Distinct().ToList();
        var labels = await _db.Licenses.AsNoTracking().Where(l => licenseIds.Contains(l.Id))
            .Select(l => new { l.Id, Label = l.Customer.Email + " · " + l.LicenseKey }).ToDictionaryAsync(x => x.Id, x => x.Label, ct);
        Requests = rows.Select(r => new RequestRow(r.Id, labels.GetValueOrDefault(r.LicenseId, "(silinmiş lisans)"), r.Kind,
            r.IbanMasked, r.ReceivedAt, r.DueAt, r.ClosedAt, r.Resolution, r.Outcome)).ToList();
        if (id is { } requestId) Selected = await _requests.FindAsync(requestId, ct);

        // KVKK Ö-4: banka verisi (kısaltılmış IBAN, havale metni) gösteren sayfanın her görüntülenmesi denetime yazılır. Hedef
        // sayfanın gösterdiği talep, ham ?id= değil: bulunamayan talebin kimliği hedefe girmez. Ayrıntı yalnız sayfa adı ve talep
        // numarası.
        await _audit.LogAsync(AuditEvents.BankPageView, AuditTargets.BankSubjectRequest, Selected?.Request.Id.ToString(),
            Selected is { } s ? new { page = PageName, requestId = s.Request.Id } : (object)new { page = PageName }, ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (BankDisabled)
        {
            TempData["Error"] = BankHasher.DisabledMessage;
            return RedirectToPage();
        }
        if (Kind is not { } kind)
        {
            TempData["Error"] = NoKindMessage;
            return RedirectToPage();
        }
        BankSubjectRequest request;
        try
        {
            var hasher = _services.GetRequiredService<BankHasher>();
            // Yönetici sütunlarına kimlik, kullanıcı adı değil (KVKK Ö-14; satır süresiz — karar 22).
            request = await _requests.CreateAsync(hasher, LicenseId, Iban ?? "", kind, ReceivedOn ?? TodayTr,
                DataDestruction.ActorOf(User), ct);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
        await _audit.LogAsync(AuditEvents.BankSubjectCreate, AuditTargets.BankSubjectRequest, request.Id.ToString(),
            new { kind = request.Kind.ToString() }, CancellationToken.None);
        TempData["Success"] = $"Talep açıldı; son tarih {TimeZoneInfo.ConvertTime(request.DueAt, ObifinConnectionService.TrZone):dd.MM.yyyy}.";
        return RedirectToPage(new { id = request.Id });
    }

    /// <summary>KVKK Ö-11: kimlik ve IBAN sahipliğinin doğrulama YÖNTEMİ (kişisel veri değil). Dışa aktarma ve silmeden önce
    /// zorunlu.</summary>
    public async Task<IActionResult> OnPostVerifyAsync(Guid id)
    {
        try
        {
            await _requests.VerifyAsync(id, Verification, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectVerify, AuditTargets.BankSubjectRequest, id.ToString(),
            new { method = Verification.ToString() }, CancellationToken.None);
        TempData["Success"] = "Doğrulama yöntemi kaydedildi.";
        return RedirectToPage(new { id });
    }

    /// <summary>Yalnız doğrulanmış erişim talebinde (servis denetler: kapalı → tür → doğrulama).</summary>
    public async Task<IActionResult> OnPostExportAsync(Guid id, CancellationToken ct)
    {
        byte[] csv;
        try
        {
            csv = await _requests.ExportAsync(id, ct);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectExport, AuditTargets.BankSubjectRequest, id.ToString(), null,
            CancellationToken.None);
        return File(csv, "text/csv; charset=utf-8", $"banka-verisi-{id:N}.csv");
    }

    /// <summary>Yalnız doğrulanmış silme talebinde. İstek jetonu BİLEREK kullanılmaz: kaydedilmiş silmenin denetimi istek iptaliyle
    /// düşmesin.</summary>
    public async Task<IActionResult> OnPostEraseAsync(Guid id)
    {
        BankSubjectRequestService.EraseResult r;
        try
        {
            // Yapan: yöneticinin kimliği — imha kaydı budanmaz, kullanıcı adı girmez (Görev 9 incelemesi).
            r = await _requests.EraseAsync(id, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectErase, AuditTargets.BankSubjectRequest, id.ToString(),
            new { r.Transactions, r.IbanMemories, r.Matches, r.ManualMatchAudits }, CancellationToken.None);
        TempData["Success"] = $"Silindi: {r.Transactions} hareketin açıklaması/adı/ham kaydı, {r.IbanMemories} IBAN bağı, " +
            $"{r.Matches} eşleşme satırı, {r.ManualMatchAudits} elle eşleme denetim satırı. Talep kapatıldı. " +
            BankSubjectRequestService.OtherCopiesNotice;
        return RedirectToPage(new { id });
    }

    /// <summary>Talebi bir sonuç kategorisiyle kapatır (KVKK m. 13/3; Silme Yönetmeliği md. 12/1-c gerekçeli ret): yanıtlandı
    /// (yalnız doğrulanmış erişim talebi), doğrulanamadı (yalnız doğrulama kaydı yokken), kanuni gerekçeyle ret, geri çekildi. Kural
    /// serviste (<see cref="BankSubjectRequestService.CloseAsync"/>); kategori denetime yazılır, serbest metin yok.</summary>
    public async Task<IActionResult> OnPostCloseAsync(Guid id)
    {
        try
        {
            await _requests.CloseAsync(id, Resolution, DataDestruction.ActorOf(User), CancellationToken.None);
        }
        catch (ObifinValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage(new { id });
        }
        await _audit.LogAsync(AuditEvents.BankSubjectClose, AuditTargets.BankSubjectRequest, id.ToString(),
            new { resolution = Resolution.ToString() }, CancellationToken.None);
        TempData["Success"] = "Talep kapatıldı: " + BankSubjectRequestService.OutcomeText(Resolution);
        return RedirectToPage(new { id });
    }
}
