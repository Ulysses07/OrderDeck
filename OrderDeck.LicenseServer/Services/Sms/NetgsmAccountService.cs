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

    /// <summary>Şifre çözülemediğinde panelde gösterilen metin. Hesabın
    /// <c>Status</c>'üne DOKUNULMAZ — gerekçe
    /// <see cref="TryUnprotectPassword"/> doc'unda.</summary>
    public const string UndecryptableMessage =
        "Saklı Netgsm şifresi çözülemedi. Kimlik bilgilerini panelden tekrar girin.";

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
    /// <para>Kalan alıcılar <c>pending</c> kalır — kurulum düzelip kampanya
    /// devam ettirildiğinde kitle kaldığı yerden sürer.</para>
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
    ///
    /// <para><b>Bu metot <c>ChangeTracker</c>'ı TEMİZLEYEBİLİR.</b> Hem retry
    /// turları hem de fırlatma yolları paylaşılan scoped bağlamda
    /// <c>ChangeTracker.Clear()</c> çağırıyor; bu da çağıranın ÖNCEDEN stage
    /// ettiği kaydedilmemiş değişiklikleri iz bırakmadan yutar. Dolayısıyla
    /// <c>UpsertAsync</c>, bekleyen yazımların üstüne çağrılmaz: önce onları
    /// kaydet, sonra buraya gel.</para>
    /// </summary>
    /// <exception cref="NetgsmAccountDisabledException">Hesap admin tarafından kapatılmış.</exception>
    /// <exception cref="ArgumentException">İlk kayıtta şifre verilmemiş.</exception>
    /// <exception cref="DbUpdateConcurrencyException">Hesap satırı yazım sürerken
    /// değişti — ya da kampanya üstlenme yarışı dört turda da kaybedildi.</exception>
    public async Task<NetgsmAccount> UpsertAsync(
        Guid licenseId, string userCode, string? rawPassword,
        string header, string brandCode, CancellationToken ct)
    {
        var account = await _db.NetgsmAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        var originalId = account?.Id;
        var originalVersion = account?.UpdatedAt;
        // Retry YALNIZ kampanya kalp atışı/tamamlanma yarışı için; hesap satırı
        // değişmişse ilk turda zaten 409'a düşüyoruz. 4, o yarışın pratik üst
        // sınırı: işçi bir kampanyayı en çok bir kez üstlenir, aynı lisansta
        // üst üste dört kaybetmek gerçekçi değil.
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

            if (account is null)
            {
                if (string.IsNullOrWhiteSpace(rawPassword))
                {
                    throw new ArgumentException(
                        "İlk kayıtta Netgsm API şifresi zorunlu.",
                        nameof(rawPassword));
                }

                // Yalnız YENİ satırın seed damgası. Güncelleme yolunda buraya
                // hiçbir şey yazılmıyor: jetonu DbContext damgalıyor.
                var now = DateTimeOffset.UtcNow;

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
            catch (DbUpdateException)
            {
                // Buraya tükenen CAS, marka kodu tekil indeks ihlali ve diğer
                // yazım hataları düşer. Fırlatmadan ÖNCE temizle: aksi hâlde
                // çağıranın scope'unda yarı-yazılmış hesap + `paused` damgalı
                // kampanyalar izleniyor kalır ve o scope'ta atılacak SONRAKİ
                // herhangi bir `SaveChanges` onları kimsenin karar vermediği bir
                // anda diske basar. Yukarıdaki dalın `Clear()`'ı bir sonraki tur
                // için; bu çıkış yolunda bir sonraki tur yok.
                _db.ChangeTracker.Clear();
                throw;
            }
        }
    }

    /// <summary>
    /// Kurulumu kapatır (<paramref name="status"/>) ve o lisansın HENÜZ
    /// BİTMEMİŞ kampanyalarını <c>paused</c> yapar — <b>tek</b>
    /// <c>SaveChanges</c>'te, yani ya ikisi de olur ya hiçbiri. Duraklatılan
    /// kampanya sayısını döndürür.
    ///
    /// <para><b><c>pending</c> de duraklatılmak ZORUNDA.</b> Yalnız
    /// <c>sending</c> duraklatılsaydı <see cref="SmsCampaignRecoveryJob"/>
    /// iki dakika içinde bekleyeni kuyruğa alır ve kararı sessizce geri
    /// alırdı (<c>SmsCampaignRecoveryJob.cs:48-53</c>).</para>
    ///
    /// <para>Kalan alıcılar <c>pending</c> kalır — kampanya devam
    /// ettirildiğinde kitle kaldığı yerden sürer.</para>
    ///
    /// <para><b>Neden yeniden deneme var.</b> <c>SmsCampaign.ClaimedAt</c> bir
    /// concurrency token (<c>LicenseDbContext.cs:816</c>) ve gönderim işi onu
    /// ALICI BAŞINA tazeliyor (<c>SmsCampaignSendJob.cs:172</c>). Okumamızla
    /// yazmamız arasına bir kalp atışı girerse
    /// <see cref="DbUpdateConcurrencyException"/> gelir. Yakalamazsak kapatma
    /// isteği 500 ile düşer — hem de tam kampanya akarken, yani anahtarın en
    /// çok gerektiği anda. Aynı token, kampanyanın tam o anda tamamlanmasıyla
    /// olan yarışı da yakalıyor: bayat okumayla tamamlanmış kampanyayı
    /// <c>paused</c>'a geri çevirip yeniden gönderime açamayız.</para>
    ///
    /// <para><b><paramref name="expectedUpdatedAt"/> — bayat ret koruması.</b>
    /// Günlük iş (<c>Failed</c>) hesabı ağ çağrısından ÖNCE okuyor; çağrı
    /// sürerken admin hesabı <c>Disabled</c> yapmış ya da yayıncı yeni bir
    /// şifreyle kaydetmiş olabilir. O sürümü doğrulamadık, o sürüme ret
    /// yazamayız: <c>Disabled</c>'ı <c>Failed</c>'a çevirmek admin kilidini
    /// kaldırır, değişmiş şifreye ret yazmak da doğrulanmamış bir kimliği
    /// yanlışlıkla mahkûm eder. Bu yüzden <c>Failed</c> çağrısı sürümü
    /// TAŞIMAK ZORUNDA ve eşleşmezse <c>0</c> dönüp sessizce çekilir —
    /// sonraki tur güncel sürümü baştan doğrular.</para>
    ///
    /// <para><c>Disabled</c> (admin kill switch) sürüm İSTEMEZ: yönetici
    /// kararı en güncel karardır ve her hâlükârda kazanmalıdır.</para>
    ///
    /// <para><paramref name="departure"/>=<c>true</c> ile <c>Disabled</c>
    /// geçişinde <see cref="NetgsmAccount.DisabledAt"/> damgalanır (§6 saklama
    /// saati) — yalnız GEÇİŞTE. Sistem kaynaklı kapanışlar (ör. anahtar
    /// halkası kaybı, §2.4) <c>departure=false</c> bırakır ve saati
    /// BAŞLATMAZ: kimse ayrılmadı, veri silinmemeli.</para>
    /// </summary>
    public async Task<int> CloseAccountAndPauseCampaignsAsync(
        Guid accountId,
        NetgsmAccountStatus status,
        string? lastError,
        CancellationToken ct = default,
        DateTimeOffset? expectedUpdatedAt = null,
        bool departure = false)
    {
        if (status is not (NetgsmAccountStatus.Disabled or NetgsmAccountStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status));

        if (status == NetgsmAccountStatus.Failed && expectedUpdatedAt is null)
            throw new ArgumentException(
                "Günlük ret doğrulanan hesap sürümünü taşımalıdır.", nameof(expectedUpdatedAt));

        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            // Her turda TEMİZ oku: çağıranın izlediği bayat nesne bu kararın
            // içine sızmamalı (günlük iş `acc`'i hâlâ izliyor).
            _db.ChangeTracker.Clear();

            var account = await _db.NetgsmAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null) return 0;

            if (status == NetgsmAccountStatus.Failed
                && (account.Status != NetgsmAccountStatus.Verified
                    || account.UpdatedAt != expectedUpdatedAt!.Value))
                return 0;   // araya giren karar var — bayat ret düşer

            if (departure
                && status == NetgsmAccountStatus.Disabled
                && account.Status != NetgsmAccountStatus.Disabled)
            {
                // Saklama saati ayrılış ANINDAN sayılır (§6: 30 gün). Yalnız GEÇİŞTE
                // damgala: zaten Disabled hesabı tekrar kapatmak saati ilerletirdi.
                // `departure` açık bayrak: anahtar halkası kaybı (§2.4, SmsCampaignSendJob)
                // da hesabı Disabled yapar ama o bir ayrılış DEĞİLDİR — silme saati
                // orada başlamamalı. Varsayılan false = güvenli yön (unutmak veri SİLMEZ).
                account.DisabledAt = DateTimeOffset.UtcNow;
            }

            account.Status = status;
            account.LastError = lastError;
            _db.Entry(account).Property(a => a.UpdatedAt).IsModified = true;

            var paused = await StagePauseActiveCampaignsAsync(account.LicenseId, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                return paused;
            }
            catch (DbUpdateConcurrencyException) when (attempt >= maxAttempts)
            {
                // Tükendik. Fırlatmadan ÖNCE temizle: aksi hâlde çağıranın
                // scope'unda (admin kapatma yolu, günlük iş) yarı-yazılmış
                // hesap + "paused" damgalı kampanyalar izleniyor kalır ve o
                // scope'ta atılacak SONRAKİ herhangi bir `SaveChanges` onları
                // kimsenin karar vermediği bir anda diske basar. Döngünün
                // başındaki `Clear()` bir sonraki tur için; bu çıkış yolunda
                // bir sonraki tur yok.
                _db.ChangeTracker.Clear();
                throw;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Kalp atışı araya girdi, kampanya tam o anda tamamlandı ya da
                // hesap satırı başkası tarafından yazıldı. Döngü başındaki
                // `Clear()` + taze okuma kararı GÜNCEL duruma göre yeniden
                // verir; tamamlanmış kampanya ikinci turda filtreye girmez
                // (resurrection yok), değişmiş hesap da sürüm kontrolüne
                // takılıp `0` döner.
            }
            catch (DbUpdateException)
            {
                // Eşzamanlılık DIŞI yazım hatası (ör. `LastError` sütun taşması,
                // FK ihlali). Retry anlamsız — aynı veriyle tekrar denemek aynı
                // hatayı verir. Fırlatmadan ÖNCE temizle: bu metodu günlük iş bir
                // DÖNGÜ içinden çağırıyor, kirli tracker sıradaki hesabın
                // `SaveChanges`'ine biner.
                //
                // Üç `catch`'in SIRASI zorunlu: `DbUpdateConcurrencyException`,
                // `DbUpdateException`'dan TÜRÜYOR; iki türemiş cümle ÜSTTE, taban
                // ALTTA kalmalı (derleyici tersini zaten kabul etmez). Taban dal
                // olmadan eşzamanlılık dışı bir yazım hatası buradan `Clear()`
                // yapılmadan çıkardı — bu metot tam olarak `lastError` yazıyor ve
                // `LastError` sütunu 500 karakterle sınırlı.
                _db.ChangeTracker.Clear();
                throw;
            }
        }
    }

    /// <summary>
    /// Kurulum yeniden doğrulandığında duraklatılmış kampanyaları devam
    /// ettirmeye HAZIRLAR: bellekteki nesneleri "pending" yapar ve devam
    /// ettirilecek kampanya sayısını döndürür. Geriye kaç kampanyanın
    /// hazırlandığını döndürür.
    ///
    /// <para><b>KAYDETMEZ.</b> Çağıran, hesabı <c>Verified</c> yazan
    /// <c>SaveChanges</c>'in içine alır — kapatma yolunun
    /// (<see cref="CloseAccountAndPauseCampaignsAsync"/>) atomikliğinin
    /// aynası. Ayrı kaydedilseydi aradaki çökme hesabı Verified, kampanyaları
    /// "paused" bırakırdı; <see cref="SmsCampaignRecoveryJob"/> "paused"a
    /// bakmadığı için o kampanyalar sonsuza dek asılı kalırdı.</para>
    ///
    /// <para><b>Kuyruğa da ATMAZ.</b> Yalnız "pending" yazılır;
    /// <see cref="SmsCampaignRecoveryJob"/> 5 dakikada bir süpürüp kuyruğa
    /// alır. Gerekçe İYS push işiyle aynı: kaçırılan bir Enqueue kaydı sessizce
    /// kaybeder, süpürme kaybetmez. Kapatma/açma nadir bir olay, 5 dakikalık
    /// gecikmenin ölçülebilir bir bedeli yok.</para>
    ///
    /// <para><b><c>ClaimedAt</c> TEMİZLENMEZ, ilerletilir.</b> Jetonun tek işi
    /// monoton artmak: "bu satırı en son kim yazdı" sorusunun cevabı o.
    /// <c>null</c>'a çekmek zinciri koparır — sonraki üstlenme
    /// <c>NextClaimedAt(null) = UtcNow</c> üretir ve bu değer, duraklatma
    /// sırasında bir tick ileri itilmiş eski damganın GERİSİNDE kalabilir.
    /// O an <c>sending/L → paused/L+1 → pending/null → sending/L</c> dizisi
    /// mümkün olur ve duraklatmadan önce okumuş bir işçi kendi jetonunu
    /// yeniden görüp hem sahiplik yoklamasından hem CAS'tan geçer.</para>
    ///
    /// <para><c>null</c> gerekmiyor da: <see cref="SmsCampaignRecoveryJob"/>'ın
    /// <c>pending</c> dalı <c>CreatedAt</c>'e bakıyor
    /// (<c>SmsCampaignRecoveryJob.cs:51</c>), <c>SmsCampaignSendJob</c>'ın
    /// üstlenme kapısı da <c>pending</c> için <c>ClaimedAt</c>'e hiç bakmıyor
    /// (<c>SmsCampaignSendJob.cs:66-74</c>). "Devralınan koşu" uyarısı da
    /// tetiklenmez: <c>resumed</c> yalnız <c>Status == "sending"</c> iken
    /// doğru olur.</para>
    /// </summary>
    public async Task<int> StageResumePausedCampaignsAsync(
        Guid licenseId, CancellationToken ct = default)
    {
        var paused = await _db.SmsCampaigns
            .Where(c => c.LicenseId == licenseId && c.Status == "paused")
            .ToListAsync(ct);

        foreach (var c in paused)
        {
            c.Status = "pending";
            c.ClaimedAt = NextClaimedAt(c.ClaimedAt);
        }
        return paused.Count;
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
