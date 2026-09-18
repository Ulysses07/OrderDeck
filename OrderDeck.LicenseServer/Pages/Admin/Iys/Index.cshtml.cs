using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Pages.Admin.Iys;

/// <summary>
/// İYS onay boru hattının durumu. <b>Salt okunur.</b> Buradan İYS'ye kayıt
/// gönderilmez: elle push düğmesi, tek doğruluk kaynağı olan işlerin yanına
/// ikinci bir yol açar ve iki yolun sırası çakışırsa hangisinin yazdığı
/// belirsizleşir.
/// </summary>
public class IndexModel : PageModel
{
    private const int ListSize = 50;

    private readonly LicenseDbContext _db;
    public IndexModel(LicenseDbContext db) => _db = db;

    public sealed record Row(
        string Recipient,
        IysConsentStatus Status,
        IysPushState PushState,
        IysConsentStatus? Verified,
        DateTimeOffset? Deadline,
        DateTimeOffset? LastPushedAt,
        string? LastError);

    public sealed record BadPhone(string Raw, DateTimeOffset OccurredAt, string? SourceTable);

    /// <summary><see cref="BadPhone"/> ile aynı sınıf kayıp: satır açılmadı, yalnız
    /// olay var. <c>LicenseId</c> da gösteriliyor çünkü çözüm yayıncıya özel —
    /// hangi yayıncının Netgsm kurulumunun eksik olduğu görünmeden bu liste
    /// üzerinde işlem yapılamaz.</summary>
    public sealed record NoBrand(
        string Recipient, DateTimeOffset OccurredAt, string? SourceTable, Guid? LicenseId);

    public int PendingCount { get; private set; }
    public int PushedCount { get; private set; }
    public int ConfirmedCount { get; private set; }
    public int FailedCount { get; private set; }
    public int ExpiredCount { get; private set; }

    public List<Row> Urgent { get; private set; } = new();
    public List<Row> Problem { get; private set; } = new();
    public List<BadPhone> BadPhones { get; private set; } = new();
    public List<NoBrand> NoBrands { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var counts = await _db.IysConsents.AsNoTracking()
            .GroupBy(c => c.PushState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(IysPushState s) => counts.FirstOrDefault(c => c.State == s)?.Count ?? 0;
        PendingCount = Count(IysPushState.Pending);
        PushedCount = Count(IysPushState.Pushed);
        ConfirmedCount = Count(IysPushState.Confirmed);
        FailedCount = Count(IysPushState.Failed);
        ExpiredCount = Count(IysPushState.Expired);

        // Acil: 3 iş günlük pencereye 24 saatten az kalmış ve hâlâ
        // onaylanmamış. Bu satırlar son tarihi kaçırırsa onay geçersiz olur.
        var warnCutoff = now + IysConsentRecoveryJob.DeadlineWarning;
        Urgent = await _db.IysConsents.AsNoTracking()
            .Where(c => c.PushState != IysPushState.Confirmed
                        && c.PushState != IysPushState.Expired
                        && c.PushDeadline != null
                        && c.PushDeadline > now
                        && c.PushDeadline <= warnCutoff)
            .OrderBy(c => c.PushDeadline)
            .Take(ListSize)
            .Select(c => new Row(c.Recipient, c.Status, c.PushState, c.LastVerifiedStatus,
                c.PushDeadline, c.LastPushedAt, c.LastError))
            .ToListAsync(ct);

        // Sorunlu: düşmüş ya da penceresi kaçmış. Elle karar gerektirir —
        // kişiden yeni onay istemek dışında yapılabilecek bir şey yok.
        Problem = await _db.IysConsents.AsNoTracking()
            .Where(c => c.PushState == IysPushState.Failed
                        || c.PushState == IysPushState.Expired)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(ListSize)
            .Select(c => new Row(c.Recipient, c.Status, c.PushState, c.LastVerifiedStatus,
                c.PushDeadline, c.LastPushedAt, c.LastError))
            .ToListAsync(ct);

        // E.164'e çevrilemeyen numaralar: kayıt satırı açılmadı, yalnız olay
        // yazıldı. Sessizce atlamak 284 kaydı kaybetme biçimimizdi.
        BadPhones = await _db.IysConsentEvents.AsNoTracking()
            .Where(e => e.ErrorCode == "invalid-phone")
            .OrderByDescending(e => e.OccurredAt)
            .Take(ListSize)
            .Select(e => new BadPhone(e.Recipient, e.OccurredAt, e.SourceTable))
            .ToListAsync(ct);

        // Markası çözülemeyen onaylar: yayıncının doğrulanmış Netgsm hesabı
        // olmadığı için kayıt satırı açılmadı. Görünmez bırakılırsa 284 onayı
        // kaybettiren kör nokta yeni bir biçimde geri gelir — sayaçlarda da
        // çıkmaz, çünkü sayaçlar IysConsents tablosundan okunuyor.
        NoBrands = await _db.IysConsentEvents.AsNoTracking()
            .Where(e => e.ErrorCode == "no-brand")
            .OrderByDescending(e => e.OccurredAt)
            .Take(ListSize)
            .Select(e => new NoBrand(e.Recipient, e.OccurredAt, e.SourceTable, e.LicenseId))
            .ToListAsync(ct);
    }
}
