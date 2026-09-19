using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Lisans → Netgsm hesabı çözümlemesi ve API şifresinin şifrelenmesi.
///
/// <para><b>Anahtar kaybı:</b> <c>IDataProtection</c> anahtarları prod'da
/// <c>.\keys:/app/keys</c> volume'unda kalıcıdır (bkz. WhatsAppAccountService).
/// Klasör kaybolursa şifreler çözülemez ve yayıncıların kimliklerini yeniden
/// girmesi gerekir — bu klasör yedek kapsamında olmalı.</para>
///
/// <para><b>Fail-closed:</b> yalnız <c>Status == Verified</c> hesap iş yapar.
/// Doğrulanmamış hesap için marka çözülmez → onay satırı açılmaz (spec §5.1).</para>
/// </summary>
public sealed class NetgsmAccountService
{
    /// <summary>Şifreleme amacı — değiştirilirse mevcut şifreler çözülemez.
    ///
    /// <para><b>Değişmez kural:</b> korunan her alanın KENDİ purpose'u olur, purpose
    /// paylaşılmaz. Aynı purpose'la iki alan korunursa birinin şifreli metni öbürünün
    /// sütununa taşınıp okutulabilir (bkz. <c>WhatsAppAccountService</c>: token ve PIN
    /// ayrı purpose'larda). Bugün <c>NetgsmAccount</c>'ta korunan tek alan var; ileride
    /// ikinci bir sır eklenirse (örn. İYS API anahtarı) bu protector'a ORTAK edilmez,
    /// yeni bir purpose sabiti tanımlanır.</para></summary>
    private const string PasswordProtectorPurpose = "OrderDeck.Netgsm.Password.v1";

    private readonly LicenseDbContext _db;
    private readonly IDataProtector _protector;

    public NetgsmAccountService(LicenseDbContext db, IDataProtectionProvider protection)
    {
        _db = db;
        _protector = protection.CreateProtector(PasswordProtectorPurpose);
    }

    public string ProtectPassword(string rawPassword) => _protector.Protect(rawPassword);

    /// <summary>Şifreli metni çözer. Anahtar döndüyse/bozuksa <c>null</c> döner —
    /// çağıran o turu ATLAR ve <c>LastError</c> yazar, hesabın <c>Status</c>'üne
    /// DOKUNMAZ.
    ///
    /// <para>Gerekçe: hesabı kapatmak geri alınamaz — <c>Failed → Verified</c>
    /// dönen bir kod yolu yok ve <c>IysConsentCollector</c> markayı yalnız
    /// <c>Verified</c> hesaptan çözdüğü için kapatmak o yayıncının YENİ onay
    /// toplamasını da durdurur; bağlanmamış tek bir anahtar dizini tek koşuda
    /// bütün hesapları kilitlerdi. Anahtar geri geldiğinde sistem kendiliğinden
    /// düzelmeli.</para></summary>
    public string? TryUnprotectPassword(string protectedPassword)
    {
        try { return _protector.Unprotect(protectedPassword); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public Task<NetgsmAccount?> GetVerifiedByLicenseAsync(Guid licenseId, CancellationToken ct)
        => _db.NetgsmAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified, ct);

    /// <summary>Marka başına dönen işlerin kiracı listesi.
    ///
    /// <para><b><c>AsNoTracking</c> kasıtlı.</b> Çağıranlar (İYS push/verify
    /// işleri) bu listeyi marka marka dolaşır ve düşen bir turun catch bloğunda
    /// <c>ChangeTracker.Clear()</c> çağırır — paylaşılan scoped DbContext'te
    /// A'nın kirli kayıtlarının B'nin <c>SaveChanges</c>'ine binmemesi için.
    /// Liste tracked dönseydi o temizlik SIRADAKİ markanın hesap nesnesini de
    /// detach ederdi ve üstüne yapılan yazım hiçbir hata vermeden kaybolurdu:
    /// sıralamaya bağlı sessiz kayıp. No-tracking o hâli imkânsız kılar —
    /// buradaki nesneler ZATEN hiç izlenmez, yazmak isteyen taze okur.</para></summary>
    public async Task<IReadOnlyList<NetgsmAccount>> ListVerifiedAsync(CancellationToken ct)
        => await _db.NetgsmAccounts
            .AsNoTracking()
            .Where(a => a.Status == NetgsmAccountStatus.Verified)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Kampanya sahiplik jetonunun (<see cref="SmsCampaign.ClaimedAt"/>) bir
    /// sonraki değeri. <c>UtcNow</c> monoton olmadığı için ham atama, jetonu
    /// yerinde bırakabilir ya da geri alabilir; her iki durumda da kapatmadan
    /// ÖNCE kampanyayı okumuş bir işçi üstlenme yazımını kazanır.
    /// </summary>
    private static DateTimeOffset NextClaimedAt(DateTimeOffset? previous)
    {
        var now = DateTimeOffset.UtcNow;
        return previous.HasValue && now <= previous.Value
            ? previous.Value.AddTicks(1)
            : now;
    }

    /// <summary>
    /// Lisansın koşan/kuyruktaki kampanyalarını <c>paused</c> olarak
    /// <b>hazırlar</b> — kaydetmez. Çağıran, hesap yazımıyla aynı
    /// <c>SaveChanges</c> içinde kaydeder; böylece hesap kapanırken kampanya
    /// açık kalan bir ara durum oluşmaz.
    ///
    /// <para>Jetonu ilerletmek işin YARISI değil tamamı: durum yazımı tek
    /// başına, kampanyayı zaten okumuş işçinin <c>sending</c> yazımını
    /// engellemez.</para>
    ///
    /// <para><b>Kredi iade edilmez</b> — kalan alıcılar <c>pending</c> kalıyor
    /// ve rezervasyon tam olarak onların karşılığı. İade, kampanya gerçekten
    /// tamamlandığında (<c>failed</c> alıcı sayısına göre) yapılır.</para>
    /// </summary>
    private async Task<int> StagePauseActiveCampaignsAsync(
        Guid licenseId, CancellationToken ct)
    {
        var active = await _db.SmsCampaigns
            .Where(c => c.LicenseId == licenseId
                && (c.Status == "pending" || c.Status == "sending"))
            .ToListAsync(ct);

        foreach (var campaign in active)
        {
            campaign.Status = "paused";
            campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);
        }

        return active.Count;
    }

    /// <summary>
    /// Lisansın Netgsm kurulumunu yazar/günceller, satırı <b>her zaman</b>
    /// <see cref="NetgsmAccountStatus.Failed"/> bırakır ve lisansın koşan
    /// kampanyalarını <b>aynı işlemde</b> duraklatır.
    ///
    /// <para><b>Neden hep Failed.</b> Kimlik bilgileri değiştiyse eski doğrulama
    /// geçersizdir; <c>Verified</c>'ı korumak, yanlış kimlikle "açık" duran bir
    /// kurulum demektir. Satırı kapalı yazıp doğrulamayı ayrı çalıştırmak aynı
    /// zamanda arada süreç ölse bile fail-closed kalmayı garanti eder.</para>
    ///
    /// <para><b>Neden duraklatma BURADA, doğrulama sonucunda değil.</b> Hesap
    /// <c>Failed</c> olduğu anda marka çözülemez; ağ çağrısı sürerken (ya da o
    /// noktada süreç ölürse) kampanya açık kalırsa işçi izinsiz gönderime devam
    /// eder. Duraklatmayı ilk yazıma bağlamak bu pencereyi kapatır.</para>
    ///
    /// <para><paramref name="rawPassword"/> boş/null ise saklanan şifre KORUNUR:
    /// panel şifreyi geri göstermediği için yayıncı başlığını düzeltirken alanı
    /// boş bırakır. İlk kayıtta ise zorunludur.</para>
    ///
    /// <para><b><c>Disabled</c> kapısı BURADA.</b> Panel controller'ı da erken
    /// bir ön kontrol yapıyor, ama asıl kapı bu: ön kontrol ile yazım arasındaki
    /// pencerede kapatılan hesabı yalnız bu kontrol koruyabilir.</para>
    /// </summary>
    /// <exception cref="NetgsmAccountDisabledException">Hesap admin tarafından kapatılmış.</exception>
    /// <exception cref="ArgumentException">İlk kayıtta şifre verilmemiş.</exception>
    /// <exception cref="DbUpdateConcurrencyException">Hesap satırı yazım sürerken değişti.</exception>
    public async Task<NetgsmAccount> UpsertAsync(
        Guid licenseId, string userCode, string? rawPassword,
        string header, string brandCode, CancellationToken ct)
    {
        var account = await _db.NetgsmAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        var originalId = account?.Id;
        var originalVersion = account?.UpdatedAt;
        const int maxAttempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                account = await _db.NetgsmAccounts
                    .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
            }

            if (account?.Status == NetgsmAccountStatus.Disabled)
                throw new NetgsmAccountDisabledException();

            // Retry yalnız kampanya yarışı içindir. Hesap satırı bu arada
            // değiştiyse yeni sürümü sessizce sahiplenmeyiz — çağıran 409 alır.
            if (attempt > 1
                && (account?.Id != originalId
                    || account?.UpdatedAt != originalVersion))
            {
                _db.ChangeTracker.Clear();
                throw new DbUpdateConcurrencyException(
                    "Kurulum, kaydetme sürerken değişti.");
            }

            var now = DateTimeOffset.UtcNow;

            if (account is null)
            {
                if (string.IsNullOrWhiteSpace(rawPassword))
                {
                    throw new ArgumentException(
                        "İlk kayıtta Netgsm API şifresi zorunlu.",
                        nameof(rawPassword));
                }

                account = new NetgsmAccount
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                _db.NetgsmAccounts.Add(account);
            }

            account.UserCode = userCode.Trim();
            account.Header = header.Trim();
            account.BrandCode = brandCode.Trim();

            if (!string.IsNullOrWhiteSpace(rawPassword))
                account.PasswordProtected = ProtectPassword(rawPassword);

            account.Status = NetgsmAccountStatus.Failed;
            account.LastError = null;
            account.LastVerifiedAt = null;

            // Yalnız başlık/marka aynı kalsa bile CAS UPDATE'i üretilmeli:
            // aksi halde EF hiç UPDATE yazmaz ve jeton kontrolü hiç koşmaz.
            if (_db.Entry(account).State != EntityState.Added)
            {
                _db.Entry(account).Property(a => a.UpdatedAt)
                    .IsModified = true;
            }

            await StagePauseActiveCampaignsAsync(licenseId, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                return account;
            }
            catch (DbUpdateConcurrencyException ex) when (
                attempt < maxAttempts
                && ex.Entries.Count > 0
                && ex.Entries.All(e => e.Entity is SmsCampaign))
            {
                // Yalnız kampanya heartbeat/tamamlanma yarışını tekrar dene.
                // Hesabın özgün sürümü originalVersion olarak korunuyor.
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Onay toplama yolunun ihtiyacı: yalnız marka kodu, şifre değil.</summary>
    public async Task<string?> GetBrandCodeAsync(Guid licenseId, CancellationToken ct)
        => await _db.NetgsmAccounts
            .Where(a => a.LicenseId == licenseId && a.Status == NetgsmAccountStatus.Verified)
            .Select(a => a.BrandCode)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Doğrulanmış hesapların <b>yalnız kimlikleri</b>. Günlük doğrulama işi
    /// listeyi dolaşırken her turda satırı taze okur; nesneyi taşımak,
    /// <see cref="ListVerifiedAsync"/> doc'unda anlatılan detach tuzağını
    /// buraya da çağırırdı.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListVerifiedIdsAsync(CancellationToken ct)
        => await _db.NetgsmAccounts
            .AsNoTracking()
            .Where(a => a.Status == NetgsmAccountStatus.Verified)
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Id)
            .ToListAsync(ct);
}

/// <summary>
/// Yönetici tarafından kapatılmış (<see cref="NetgsmAccountStatus.Disabled"/>)
/// bir kuruluma yazma girişimi. Panel bunu 409'a çevirir.
/// </summary>
public sealed class NetgsmAccountDisabledException : InvalidOperationException
{
    public NetgsmAccountDisabledException()
        : base("netgsm-account-disabled")
    {
    }
}
