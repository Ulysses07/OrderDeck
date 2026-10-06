using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App'in shopper-registered customers'ı (otomatik oluşturulan
/// WpfCustomerProjection rows) çekmesi için. Mevcut sync endpoint
/// WPF → server outbound; bu da inbound (server → WPF) pull.
/// <c>since</c> eski sürümler içindir (yalnız asıl kayıtlar);
/// <c>changes</c> çoklu bilgisayar senkronunun rowversion imleçli akışı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/wpf-customers")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfCustomersPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly TcknProtector _tckn;
    private readonly ILogger<LicensesWpfCustomersPullController> _logger;

    public LicensesWpfCustomersPullController(
        LicenseDbContext db, TcknProtector tckn, ILogger<LicensesWpfCustomersPullController> logger)
    {
        _db = db;
        _tckn = tckn;
        _logger = logger;
    }

    /// <param name="FullName">Boşsa takma ad (DisplayName) gider: eski
    /// istemci bu alanı yerel satırın takma adı olarak açar, yeni istemci ise
    /// takma adı ayrı gönderdiği için FullName boş kalabilir.</param>
    /// <param name="PurgedAt">
    /// KVKK silme talebiyle sunucudaki kişisel alanlar temizlendiyse dolu.
    /// Bu satırlar yanıttan ELENMİYOR, işaretlenerek gönderiliyor: yayıncının
    /// kendi bilgisayarındaki kopyayı ancak bu işaret temizletebilir (WPF'in
    /// sunucudan silme haberi alacağı başka bir kanal yok). Elenselerdi silme
    /// yayıncının diskinde sonsuza kadar kalırdı.
    ///
    /// Alanın SONA eklenmesi kasıtlı — sahadaki eski kurulumlar (v0.8.0 ve
    /// öncesi) bilinmeyen JSON alanını yok sayar; bu ekleme onları kırmaz,
    /// yalnızca temizlik dalını çalıştıramazlar.
    /// </param>
    public sealed record WpfCustomerPullItem(
        Guid Id,
        string Platform,
        string Username,
        string? FullName,
        string? Phone,
        string? Address,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? PurgedAt);

    /// <summary>
    /// Bileşik imleç (<c>since</c> + <c>sinceId</c>), kararlılık ufkunun
    /// altında; gerekçe <see cref="ReverseSyncCursor"/>'da. WPF bir sonraki
    /// imleci sayfanın son satırından okur — yanıt sırası <c>(UpdatedAt, Id)</c>.
    /// take default 100, max 500.
    /// </summary>
    [HttpGet("since")]
    public async Task<IActionResult> Since(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] Guid sinceId = default,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);
        var horizon = ReverseSyncCursor.Horizon();

        // Kopya (MergedIntoId dolu) A5b'nin sorgu filtresiyle zaten gizli; açık
        // koşul belge için: eski istemci kopyayı kişisel alanları boş ikinci bir
        // müşteri olarak eklerdi. Kopyaları yalnız `changes` akışı taşır.
        var rows = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId
                        && p.MergedIntoId == null
                        && p.UpdatedAt <= horizon
                        && (p.UpdatedAt > since
                            || (p.UpdatedAt == since && p.Id.CompareTo(sinceId) > 0)))
            .OrderBy(p => p.UpdatedAt).ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new WpfCustomerPullItem(
                p.Id, p.Platform, p.Username,
                p.FullName ?? p.DisplayName, p.Phone, p.Address,
                p.UpdatedAt, p.PurgedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <param name="Tckn">DÜZ metin — sunucuda şifreli tutulur, burada çözülür.</param>
    /// <remarks>Her birimin damgası ayrı (bkz. CustomerSyncFields). FullName
    /// olduğu gibi gider: eski sürümün takma ad yedeğini (R3-02) ayıklamak
    /// istemcinin işi (Bölüm C notu 5).</remarks>
    public sealed record WpfCustomerChangeItem(
        Guid Id, string Platform, string Username, Guid? MergedIntoId, DateTimeOffset? PurgedAt,
        string? FullName, DateTimeOffset? FullNameChangedAt,
        string? DisplayName, DateTimeOffset? DisplayNameChangedAt,
        string? GroupId, DateTimeOffset? GroupIdChangedAt,
        string? Address, string? City, string? District, DateTimeOffset? AddressChangedAt,
        bool RecipientPaysActive, DateTimeOffset? RecipientPaysChangedAt,
        string? Phone, DateTimeOffset? PhoneChangedAt,
        string? Email, DateTimeOffset? EmailChangedAt,
        string? Tckn, DateTimeOffset? TcknChangedAt,
        bool WhatsAppConsent, DateTimeOffset? WhatsAppConsentChangedAt,
        bool SmsConsent, DateTimeOffset? SmsConsentChangedAt,
        bool IsBlacklisted, string? BlacklistReason, DateTimeOffset? BlacklistedAt, DateTimeOffset? BlacklistChangedAt,
        string? Notes, DateTimeOffset? NotesChangedAt,
        long ChangeSeq);

    /// <param name="CursorReset">İstemcinin imleci geçersizdi (eksi ya da
    /// ufkun üstü) ve sayfa BAŞTAN verildi. İstemci tam yeniden indirme yapar
    /// ve push imlecini de geri sarar (veritabanı yedekten dönmüşse sunucu son
    /// gönderilenleri kaybetmiş olabilir). Sona eklendi: eski okuyucular yok
    /// sayar.</param>
    public sealed record WpfCustomerChangesPage(
        List<WpfCustomerChangeItem> Items, long NextAfterSeq, bool CursorReset = false);

    /// <summary>
    /// Çoklu bilgisayar senkronunun değişiklik akışı: asıl kayıtlar tam
    /// alanlarıyla, kopyalar yönlendirme (<c>MergedIntoId</c>), silinenler
    /// <c>PurgedAt</c> ile. İmleç sunucunun rowversion'ı; istemci saati yok.
    ///
    /// Ufuk <c>MIN_ACTIVE_ROWVERSION()</c>: henüz commit olmamış bir işlem
    /// daha KÜÇÜK bir rowversion almış olabilir. Ufkun üstünü vermek, o işlem
    /// commit olunca imlecin gerisinde kalıp satırın hiç inmemesi demek.
    ///
    /// <para><b>Geçersiz imleç sıfırlanır.</b> Normal işleyişte dönen imleç
    /// hep ufkun altındadır ve ufuk hiç gerilemez; eksi imleç (büyük-endian
    /// baytları her rowversion'dan BÜYÜK karşılaştırılır) ya da ufkun üstündeki
    /// imleç (veritabanı değişti: .bak geri yüklemesi rowversion sayacını geri
    /// sarar, bacpac/kopya hepsini yeniden üretir) her sayfayı boş döndürür ve
    /// o bilgisayar sessizce değişiklik almayı bırakırdı. Sayfa baştan verilir
    /// ve <see cref="WpfCustomerChangesPage.CursorReset"/> bunu söyler; baştan
    /// yeniden indirme güvenli — damga kuralları yeniden uygulamayı etkisiz
    /// kılar.</para>
    /// </summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes(
        Guid licenseId,
        [FromQuery] long afterSeq = 0,
        [FromQuery] int take = 500,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);
        var horizon = await _db.Database
            .SqlQueryRaw<long>("SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint) AS [Value]")
            .SingleAsync(ct);

        var cursorReset = afterSeq < 0 || afterSeq >= horizon;
        if (cursorReset)
        {
            _logger.LogWarning(
                "Müşteri değişiklik akışı: geçersiz imleç {AfterSeq} (ufuk {Horizon}), baştan veriliyor (lisans {LicenseId}) — veritabanı yedekten dönmüş ya da kopyalanmış olabilir",
                afterSeq, horizon, licenseId);
            afterSeq = 0;
        }

        // IgnoreQueryFilters ŞART: kopyalar varsayılan sorgulardan gizli (A5b)
        // ama akış onları yönlendirme olarak taşımalı — öbür bilgisayarlar
        // yerel satırlarını asıl kayda ancak böyle taşır. Filtre kalkınca lisans
        // sınırını yalnız aşağıdaki açık LicenseId koşulu çiziyor.
        var rows = await _db.WpfCustomerProjections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.ChangeSeq > afterSeq && p.ChangeSeq < horizon)
            .OrderBy(p => p.ChangeSeq)
            .Take(take)
            .ToListAsync(ct);

        // TCKN veritabanında şifreli; çözme EF sorgusuna çevrilemez, bellekte.
        // Çözülemeyen değer (anahtar kaybı) null gider. Bilgisayarlar bir birimi
        // yalnız damgası kendilerininkinden YENİYSE yazar; anahtar kaybında
        // mevcut satırların TCKN damgası değişmediği için yerel kopyalar bu
        // null ile silinmez.
        var items = rows.Select(p => new WpfCustomerChangeItem(
                p.Id, p.Platform, p.Username, p.MergedIntoId, p.PurgedAt,
                p.FullName, p.FullNameChangedAt,
                p.DisplayName, p.DisplayNameChangedAt,
                p.GroupId, p.GroupIdChangedAt,
                p.Address, p.City, p.District, p.AddressChangedAt,
                p.RecipientPaysActive, p.RecipientPaysChangedAt,
                p.Phone, p.PhoneChangedAt,
                p.Email, p.EmailChangedAt,
                _tckn.Unprotect(p.TcknProtected), p.TcknChangedAt,
                p.WhatsAppConsent, p.WhatsAppConsentChangedAt,
                p.SmsConsent, p.SmsConsentChangedAt,
                p.IsBlacklisted, p.BlacklistReason, p.BlacklistedAt, p.BlacklistChangedAt,
                p.Notes, p.NotesChangedAt,
                p.ChangeSeq))
            .ToList();

        var next = items.Count == 0 ? afterSeq : items[^1].ChangeSeq;
        return Ok(new WpfCustomerChangesPage(items, next, cursorReset));
    }
}
