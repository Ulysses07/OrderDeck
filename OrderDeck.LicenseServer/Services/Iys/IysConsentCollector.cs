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

    /// <summary>
    /// Oynatmada <c>no-brand</c> ONAY olaylarının SQL'de kabaca daraltıldığı
    /// takvim penceresi. Kesin karar bellekte, <see cref="IysBusinessDays"/>
    /// ile (iş günü hesabı SQL'e çevrilemez). 3 iş günü + hafta sonu en fazla
    /// 5 takvim günü eder; 14 gün rahat pay bırakır ve ekle-only olay
    /// tablosunun tamamını taramaz. Resmî tatiller <see cref="IysBusinessDays"/>'e
    /// eklenirse bu sayı yeniden türetilmeli (9 günlük bayram köprüsü 3 iş
    /// gününü ~12 takvim gününe taşır).
    /// </summary>
    public const int ConsentReplayLookbackDays = 14;

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
        // Sütun nullable ve "bilinmiyor"un tek temsili null. Çağıranların bir
        // kısmı lisansı çözemediğinde Guid.Empty geçiyor (bkz. IntakeFormService:
        // çağrıyı atlamak ispat olayını da yazmazdı); iki ayrı "bilinmiyor"
        // değeri admin sorgusunu ve ileriki analizi ikiye bölerdi.
        Guid? eventLicenseId = licenseId == Guid.Empty ? null : licenseId;
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
                LicenseId = eventLicenseId,
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
            LicenseId = eventLicenseId,
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

        var row = await ApplyToRowAsync(brandCode, phone, status, occurredAt, ct);

        // Olay hangi satıra ait — denetimde ONAY/RET karışmasın diye.
        // Tekrar oynatma bunu YAPMAZ: olay tablosu ekle-only.
        ev.IysConsentId = row.Id;
    }

    /// <summary>
    /// Marka çözülemediği için düşmüş ONAY/RET olaylarını, marka artık
    /// doğrulanmışken uygular. <b>KAYDETMEZ</b> — çağıran, hesabın <c>Verified</c> yazımıyla
    /// AYNI <c>SaveChanges</c>'te indirir (kalıp:
    /// <see cref="Sms.NetgsmAccountService.StageResumePausedCampaignsAsync"/>).
    /// Ayrılsalardı aradaki çökme RET'leri bir daha kimsenin bulamayacağı
    /// şekilde düşürürdü.
    ///
    /// <para><b>RET her zaman, ONAY yalnız penceresi açıkken.</b> Onay zamana
    /// bağlı: İYS dışında alınan onay üç iş günü içinde kaydedilmezse hukuken
    /// geçersiz (<see cref="PushDeadlineBusinessDays"/>). Haftalarca
    /// <c>no-brand</c> beklemiş bir onayı canlandırıp İYS'ye push etmek,
    /// geçersiz bir onayı kayda geçirmek olurdu — o onaylar ATLANIR. Penceresi
    /// henüz kapanmamış onay ise ORİJİNAL tarihiyle (<c>ConsentDate</c> ve
    /// <c>PushDeadline</c> onay anından) kuyruğa girer: kurulumunu bitirmeden
    /// izleyici toplayan yayıncı o onayları kaybetmez (2026-09-21'de 4 onay
    /// elle İYS'ye yüklenmek zorunda kalmıştı). Düşen (süresi dolmuş) onayın
    /// bedeli "o kişiye pazarlama yapılamaz"; düşen reddin bedeli, onayını geri
    /// çekmiş kişiye ticari SMS. Asimetri bilinçli ve fail-closed doktrininin
    /// aynısı: numaranın en yeni olayı süresi dolmuş bir onaysa, ondan eski RET
    /// yine uygulanır — geçerli onay yoksa satır Ret.</para>
    ///
    /// <para><b>Yeni olay YAZILMAZ, eski olay GÜNCELLENMEZ.</b> İspat geçmişini
    /// çoğaltmak denetimde "bu kişi kaç kez reddetti" sorusunu bozardı; tablo
    /// zaten ekle-only. Bu yüzden olaylar <c>AsNoTracking</c> okunuyor —
    /// yanlışlıkla bile UPDATE üretilemesin.</para>
    ///
    /// <para><b>İdempotentlik bedava.</b> Olayı "oynatıldı" diye
    /// işaretleyemediğimiz için ikinci çağrı aynı olayları yine dolaşır, ama
    /// <see cref="IysConsent.LastLocalEventAt"/> sıra damgası hepsini atlatır.
    /// Aynı kural, oynatmanın DAHA YENİ gerçek bir olayı ezmesini de engeller.</para>
    ///
    /// <para><b>Marka parametreyle geliyor, burada çözülmüyor.</b> Çağıran onu
    /// az önce doğrulanmış hesaptan okur. Burada <c>Verified</c> filtresi
    /// olmadan çözmek — "ret her zaman güvenli yön" diye cazip gelse de —
    /// kiracı sızıntısı olurdu: marka tekilliği yalnız doğrulanmış satırlar
    /// arasında garantili, doğrulanmamış bir hesap başkasının kodunu taşıyabilir
    /// ve B'nin müşterisinin reddi A'nın onayını düşürürdü.</para>
    /// </summary>
    /// <returns>Oynatma uygulanan NUMARA sayısı — olay sayısı değil (numara
    /// başına yalnız en yeni olay uygulanıyor, aşağıdaki gerekçeye bakın).
    /// Kaç satırın gerçekten DEĞİŞTİĞİ de değil: sıra damgasına takılanlar da
    /// sayılır.</returns>
    public async Task<int> StageReplayNoBrandEventsAsync(
        Guid licenseId, string brandCode, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var consentCutoff = now.AddDays(-ConsentReplayLookbackDays);
        var dropped = await _db.IysConsentEvents
            .AsNoTracking()
            .Where(e => e.LicenseId == licenseId
                        && e.ErrorCode == "no-brand"
                        && (e.EventType == IysConsentEventType.LocalRevoke
                            || (e.EventType == IysConsentEventType.LocalConsent
                                && e.OccurredAt >= consentCutoff)))
            .Select(e => new { e.Recipient, e.EventType, e.OccurredAt })
            .ToListAsync(ct);

        // Kesin pencere kontrolü bellekte: süresi dolmuş onay beyan edilemez.
        var candidates = dropped
            .Where(e => e.EventType == IysConsentEventType.LocalRevoke
                        || IysBusinessDays.Add(e.OccurredAt, PushDeadlineBusinessDays) > now)
            .ToList();
        var expiredConsents = dropped.Count - candidates.Count;

        // Numara başına YALNIZ en yeni ADAY olay uygulanır. Eskileri de uygulamak
        // sonucu DEĞİŞTİRMEZ (mutlak atama: en yeni aday olay son durumu tek
        // başına belirler) ama iş sınırsız büyür: profil kaydı onay kutusunun
        // mevcut değerini HER kaydetmede yeniden yazıyor (ShopperMeController —
        // bilinçli, bkz. oradaki gerekçe), yani tek bir müşteri tek başına bu
        // tabloya yüzlerce satır bırakabilir.
        //
        // Kesilen maliyet DB turu ya da SaveChanges DEĞİL: ikisi de zaten
        // numara sayısıyla ölçekleniyordu, çünkü bir numaranın ilk olayı satırı
        // `Local`'a sokuyor ve sonrakiler diske hiç gitmiyor (aşağıda,
        // ApplyToRowAsync). Kesilen şey o `Local` taramasının olay×numara
        // büyümesi ve N kez dönen async döngü. Sonuç aynı yere çıkıyor:
        // oynatma tek HTTP PUT'un ve tek SaveChanges'in içinde koştuğu için N
        // büyüdüğünde istek zaman aşımına uğrar, HİÇBİR ŞEY commit edilmez —
        // hesabın `Verified` yazımı da dahil. Olay tablosu EKLE-ONLY olduğu
        // için sonraki deneme aynı yükü çeker: kurulum KALICI olarak
        // doğrulanamaz hâle gelir. Hâlâ açık olan pay, aşağıdaki
        // `ToListAsync`'in N satırı belleğe çekmesi; gerekirse gruplama SQL'e
        // indirilebilir.
        //
        // <b>Tuzak — İKİ varsayım:</b> (1) iki olay tipi var (ONAY/RET) ve her
        // ikisi de MUTLAK atama: numaranın en yeni ADAY olayı son durumu tek
        // başına belirler, ara olaylar sonucu değiştirmez. (Süresi dolmuş
        // onaylar adaylar arasında DEĞİL — bu yüzden "en yeni olay" değil "en
        // yeni aday" uygulanır; bkz. yukarıdaki pencere filtresi.) Üçüncü bir
        // olay tipi ya da birikimli bir yan etki girerse bu varsayım bozulur.
        // (2) `ApplyToRowAsync` çağrı başına BİRİKEN bir yan etki üretmiyor —
        // bugün yalnız mutlak atama yapıyor. Oraya bir sayaç, giden bir push
        // satırı ya da denetim kaydı eklenirse atlanan olaylar görünür olur.
        // İkisinden biri bozulursa de-duplikasyon ÖNCE kalkmalıdır.
        var replay = candidates
            .GroupBy(e => e.Recipient)
            // Aynı ana düşen ONAY ve RET: fail-closed, RET kazanır — MaxBy'ın
            // DB sırasına bağlı belirsizliği burada bilerek kapatıldı.
            .Select(g => g
                .OrderByDescending(e => e.OccurredAt)
                .ThenByDescending(e => e.EventType == IysConsentEventType.LocalRevoke)
                .First())
            // Deterministik sıra: aynı numaraya ait olaylar zaten tekil, ama
            // farklı numaraların satır açma sırası `Local` taramasında ve
            // günlükte öngörülebilir kalsın.
            .OrderBy(e => e.OccurredAt)
            .ToList();

        var replayedConsents = 0;
        foreach (var e in replay)
        {
            var status = e.EventType == IysConsentEventType.LocalRevoke
                ? IysConsentStatus.Ret
                : IysConsentStatus.Onay;
            if (status == IysConsentStatus.Onay) replayedConsents++;
            await ApplyToRowAsync(brandCode, e.Recipient, status, e.OccurredAt, ct);
        }

        if (replay.Count > 0 || expiredConsents > 0)
        {
            _log.LogInformation(
                // `{Count}` ADI BİLEREK KULLANILMADI: bu satır eskiden onu OLAY
                // sayısı için kullanıyordu. Aynı adı numara sayısıyla yeniden
                // doldurmak, geçmişe bakan bir günlük sorgusunda iki farklı
                // büyüklüğü tek seriye karıştırırdı — kimse fark etmeden.
                "İYS: lisans {LicenseId} doğrulandı, no-brand oynatma: {Recipients} numara "
                + "({Consents} ONAY, {Revokes} RET), son {LookbackDays} gündeki {ExpiredConsents} onay olayının penceresi kapalı — atlandı",
                licenseId, replay.Count, replayedConsents, replay.Count - replayedConsents,
                ConsentReplayLookbackDays, expiredConsents);
        }

        return replay.Count;
    }

    /// <summary>
    /// Durum satırını bulur/açar ve bir olayı ona uygular. <b>Tek yer</b> —
    /// "RET kendiliğinden ONAY'a yükselmez" kuralı hem canlı toplama hem
    /// tekrar oynatma yolunda geçerli; iki kopya ileride sessizce ayrışır ve
    /// ayrışan kopya yasa dışı gönderime izin verir.
    /// </summary>
    private async Task<IysConsent> ApplyToRowAsync(
        string brandCode, string phone, IysConsentStatus status,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        var consented = status == IysConsentStatus.Onay;
        var now = DateTimeOffset.UtcNow;

        // ÖNCE yerel görünüm, SONRA disk. Tekrar oynatma tek SaveChanges'e
        // yazıyor (hesabın Verified yazımıyla atomik olmak zorunda), yani aynı
        // numaranın ikinci olayı geldiğinde birinci olayın açtığı satır HENÜZ
        // DİSKTE YOK — sorgu onu bulamaz, ikinci bir satır açılır ve
        // (BrandCode, ChannelType, RecipientType, Recipient) tekil indeksi
        // patlar. O noktada yayıncının PUT'u 500 döner ve kurulumu elle
        // müdahale edilene dek bir daha doğrulanamaz.
        var row = _db.IysConsents.Local.FirstOrDefault(
                      c => c.BrandCode == brandCode
                           && c.ChannelType == "MESAJ"
                           && c.RecipientType == "BIREYSEL"
                           && c.Recipient == phone)
                  ?? await _db.IysConsents.FirstOrDefaultAsync(
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

        if (row.LastLocalEventAt != default && occurredAt <= row.LastLocalEventAt)
        {
            // Kural 1: RET kendiliğinden ONAY'a yükselmez. Durum yalnızca
            // LastLocalEventAt'ten DAHA YENİ bir olayla değişir; geç işlenen
            // eski bir onay reddi ezemez. Olay yine de yazıldı (yukarıda).
            // Tekrar oynatmanın idempotentliği de BURADAN geliyor.
            return row;
        }

        var durumDegisti = row.Status != status;

        row.Status = status;
        row.LastLocalEventAt = occurredAt;
        // Ayna satırı (IYS_MIRROR) artık YEREL bir beyan taşıyor: İYS'ye giden
        // `source` tanımlı bir izin kaynağı olmalı — ayna işareti olaylarda kalır.
        if (row.SourceCode == IysMirrorImportJob.SourceCodeMirror)
            row.SourceCode = _opt.IysSourceCode;
        row.UpdatedAt = now;

        if (consented)
        {
            row.ConsentDate = occurredAt;
            row.SourceCode = _opt.IysSourceCode;
            row.PushDeadline = IysBusinessDays.Add(occurredAt, PushDeadlineBusinessDays);
        }
        else
        {
            // RET de kendi push penceresini açar. Ticari İletişim Yönetmeliği:
            // ret 3 iş günü içinde İYS'de işlenir. Eski onayın penceresi
            // devralınsaydı — dolmuşsa — push işinin süpürmesi RET'i hiç itmeden
            // Expired yazar, kapı kapalı kalır (Status≠Onay) ama yasal bildirim
            // sessizce kaybolurdu. Pencere İŞLENME anından sayılır, olay anından
            // değil: no-brand oynatması haftalık RET'i geç getirir; onu hiç
            // denememek yerine sınırlı süre denenir. Doğrulama işi RET beyanına
            // gelen RET cevabını kabul sayar (Confirmed); İYS hâlâ ONAY diyorsa
            // (ret işlenmedi ya da düştü) satır pencere dolunca Expired'a düşer —
            // sınır o döngüyü keser, sonsuz döngü yok. Tekrarlanan
            // RET (profil kaydı kutuyu her seferinde gönderir) pencereyi ve
            // LastLocalEventAt'i ileri alır; ilk reddin ispatı olay tablosunda
            // durur, mesaj ilk RET'te zaten kesilmişti. ConsentDate'e DOKUNULMAZ
            // (onay tarihi olarak kalır); RET'in beyan tarihi LastLocalEventAt.
            row.PushDeadline = IysBusinessDays.Add(now, PushDeadlineBusinessDays);
        }

        if (!durumDegisti && row.PushState is IysPushState.Pushed or IysPushState.Confirmed)
        {
            // Aynı beyanın tekrarı İYS'ye YENİ bir şey söylemez. Profil kaydı
            // kutunun mevcut değerini her seferinde gönderdiği için aynı RET
            // her kaydetmede yeniden itilirdi; bu hem gereksiz, hem de
            // VerifyAttempts/LastError'ı sıfırlayarak kalıcı bir gönderim
            // hatasını görünmez yapardı. Olay yine yazıldı — ispat bozulmadı.
            return row;
        }

        // Yeni olay yeni push penceresi açar — Expired kalıcı yasak değildir.
        // Pending/Failed/Expired hâlleri kasten dışarıda: beyan İYS'ye henüz
        // ULAŞMAMIŞ demektir, tekrar da olsa pencerenin açılması doğrudur.
        // Ret de itilir (yasal kayıt), ama gönderim bunu beklemez: kapı
        // Status'u de okuduğu için mesaj zaten kesildi.
        row.PushState = IysPushState.Pending;
        row.VerifyAttempts = 0;
        row.NextVerifyAt = null;
        row.LastError = null;
        return row;
    }

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
