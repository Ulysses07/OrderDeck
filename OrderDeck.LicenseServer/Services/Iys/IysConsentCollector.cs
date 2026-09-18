using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ticari ileti gönderim kapısı. <b>Tek yer</b> — her gönderim yolu buradan
/// geçmeli, yoksa fail-closed kuralı yalnız bir yolda uygulanır.
/// </summary>
public static class IysConsentGate
{
    /// <summary>
    /// Gönderim için İKİ koşul birden gerekir:
    /// <list type="bullet">
    /// <item><c>Status == Onay</c> — kişi bize onay verdi ve geri çekmedi.
    /// Yerel ret doğrulamayı beklemeden gönderimi keser (kural 4).</item>
    /// <item><c>LastVerifiedStatus == Onay</c> — İYS de onaylı diyor. İYS'nin
    /// RET'i yerel onayı EZMEZ (ispat olarak durur) ama gönderimi keser
    /// (kural 2). "Kayıt yok" ile "reddetti" ayırt edilemediği için
    /// fail-closed (kural 3).</item>
    /// </list>
    /// </summary>
    public static bool CanSend(IysConsent? c)
        => c is not null
           && c.Status == IysConsentStatus.Onay
           && c.LastVerifiedStatus == IysConsentStatus.Onay;
}

/// <summary>
/// Kaynak tablolardaki onay/ret olaylarını <see cref="IysConsent"/> durumuna ve
/// <see cref="IysConsentEvent"/> geçmişine çevirir.
///
/// <para><b>SaveChanges ÇAĞIRMAZ.</b> Çağıran kendi transaction'ında kaydeder;
/// onay girip olay kaydı girmemesi o kaydı görünmez yapardı.</para>
/// </summary>
public sealed class IysConsentCollector
{
    /// <summary>Yönetmelik m.7/11-12 — İYS dışı onay için kayıt süresi.</summary>
    public const int PushDeadlineBusinessDays = 3;

    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentCollector> _log;

    public IysConsentCollector(
        LicenseDbContext db, NetgsmAccountService accounts,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentCollector> log)
    {
        _db = db;
        _accounts = accounts;
        _opt = opt.Value;
        _log = log;
    }

    /// <param name="licenseId">Onayın ait olduğu yayıncı. Marka BUNDAN çözülür;
    /// çözülemezse satır açılmaz (spec §5.1).</param>
    public async Task RecordAsync(
        Guid licenseId, string? rawPhone, bool consented, DateTimeOffset occurredAt,
        string sourceTable, Guid sourceId,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var status = consented ? IysConsentStatus.Onay : IysConsentStatus.Ret;
        // Sunucu tarafının kendi normalize edicisi — OrderDeck.Core bu projeden
        // referanslı değil. İki normalize edici aynı kuralı paylaşır (Faz 1'de
        // ikisi de düzeltildi, test kümeleri eşlenik).
        var phone = Auth.PhoneNormalizer.TryNormalize(rawPhone, out var normalized)
            ? normalized
            : null;

        if (phone is null)
        {
            // Sessiz atlama, 284 kaydı fark etmeden kaybetme biçimimizdi.
            // Kayıt satırı açmıyoruz (E.164 sözleşmesi) ama olay tablosuna
            // düşüyor ve admin sayfasında görünüyor.
            _db.IysConsentEvents.Add(new IysConsentEvent
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                Recipient = Truncate(rawPhone ?? "", 20) ?? "",
                OccurredAt = occurredAt,
                EventType = consented ? IysConsentEventType.LocalConsent : IysConsentEventType.LocalRevoke,
                Status = IysConsentStatus.Unknown,
                SourceTable = sourceTable,
                SourceId = sourceId,
                ProofIp = ip,
                ProofUserAgent = Truncate(userAgent, 512),
                ErrorCode = "invalid-phone",
            });
            _log.LogWarning(
                "İYS: normalize edilemeyen numara, kayıt açılmadı ({Phone}, kaynak={Source})",
                PiiMasker.MaskPhone(rawPhone ?? ""), sourceTable);
            return;
        }

        // Marka artık global ayardan değil yayıncının hesabından geliyor.
        // Çözülemezse satır AÇMIYORUZ: BrandCode="" yazmak, kurulumu bitmemiş
        // tüm yayıncıların aynı numaraya ait onayını tekil index yüzünden TEK
        // satıra çakıştırır ve B'nin RET'i A'nın ONAY'ını sessizce ezer.
        var brandCode = await _accounts.GetBrandCodeAsync(licenseId, ct);

        var ev = new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            BrandCode = brandCode,
            Recipient = phone,
            OccurredAt = occurredAt,
            EventType = consented ? IysConsentEventType.LocalConsent : IysConsentEventType.LocalRevoke,
            Status = status,
            SourceTable = sourceTable,
            SourceId = sourceId,
            ProofIp = ip,
            ProofUserAgent = Truncate(userAgent, 512),
        };
        _db.IysConsentEvents.Add(ev);

        if (brandCode is null)
        {
            // Bozuk telefon dalının aynısı: kayıt satırı yok, ispat olayı var.
            // Yayıncı kurulumunu bitirince bu olaylar admin sayfasında görünür.
            ev.ErrorCode = "no-brand";
            _log.LogWarning(
                "İYS: lisans {LicenseId} için doğrulanmış marka yok, kayıt açılmadı (kaynak={Source})",
                licenseId, sourceTable);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var row = await _db.IysConsents.FirstOrDefaultAsync(
            c => c.BrandCode == brandCode
                 && c.ChannelType == "MESAJ"
                 && c.RecipientType == "BIREYSEL"
                 && c.Recipient == phone, ct);

        if (row is null)
        {
            row = new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = brandCode,
                ChannelType = "MESAJ",
                RecipientType = "BIREYSEL",
                Recipient = phone,
                CreatedAt = now,
            };
            _db.IysConsents.Add(row);
        }

        // Olay hangi satıra ait — denetimde ONAY/RET karışmasın diye.
        ev.IysConsentId = row.Id;

        if (row.LastLocalEventAt != default && occurredAt <= row.LastLocalEventAt)
        {
            // Kural 1: RET kendiliğinden ONAY'a yükselmez. Durum yalnızca
            // LastLocalEventAt'ten DAHA YENİ bir olayla değişir; geç işlenen
            // eski bir onay reddi ezemez. Olay yine de yazıldı (yukarıda).
            return;
        }

        row.Status = status;
        row.LastLocalEventAt = occurredAt;
        row.UpdatedAt = now;

        if (consented)
        {
            row.ConsentDate = occurredAt;
            row.SourceCode = _opt.IysSourceCode;
            row.PushDeadline = IysBusinessDays.Add(occurredAt, PushDeadlineBusinessDays);
        }

        // Yeni olay yeni push penceresi açar — Expired kalıcı yasak değildir.
        // Ret de itilir (yasal kayıt), ama gönderim bunu beklemez: kapı
        // Status'u de okuduğu için mesaj zaten kesildi.
        row.PushState = IysPushState.Pending;
        row.VerifyAttempts = 0;
        row.NextVerifyAt = null;
        row.LastError = null;
    }

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
