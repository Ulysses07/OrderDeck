using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// §6 dönüş yolu — İYS'den AYNA içe aktarım. Geri dönen yayıncının WPF
/// müşteri telefonlarını /iys/search ile sorgular; İYS'de ONAY'ı OLANLARI
/// terminal ayna satırı olarak yazar. Yerel satırı olan numaraya DOKUNMAZ
/// (yerel beyan her zaman kazanır).
///
/// <para><b>Yalnız ONAY aynalanır.</b> RET ve Unknown ikisi de ATLANIR: İYS
/// "kayıt yok" ile "reddetti"yi ayırt EDEMİYOR, ikisine de RET diyor (bkz.
/// <see cref="NetgsmIysClient"/>, 2026-09-17 ölçümü) — tek güvenilir sinyal
/// ONAY. Satır hiç açılmazsa gönderim kapısı zaten kapalıdır (fail-closed) —
/// kayıp yok.</para>
///
/// <para>Yerel bir beyan (onay ya da ret) SONRADAN gelirse
/// <see cref="IysConsentCollector"/> SourceCode'u yerel kaynağa çevirir —
/// ayna yalnız bir başlangıç noktasıdır. <c>IYS_MIRROR</c> izi
/// <see cref="IysConsentEvent"/> tablosunda (ekle-only) kalıcı kalır.</para>
///
/// <para><b>Lisans başına eşzamanlılık kilidi.</b> Panelden çift tıklama ya da
/// iki sekme aynı lisans için işi iki kez kuyruğa atabilir. Kilit OLMASA,
/// ikisi de aynı anda <c>known</c> kümesini okur, aynı <c>missing</c>
/// listesini görür ve İKİSİ de /iys/search'e gider — Netgsm kotası (~10
/// istek/dk) gereksiz yere iki katına çıkar, üstelik kaybeden koşu tekil
/// indeks yarışında satırı yazamadan ÖNCE o çağrıyı zaten tüketmiş olur.
/// <c>[DisableConcurrentExecution("iys-mirror:{0}", ...)]</c> ikinci kopyayı
/// BEKLETİR; sıra ona geldiğinde <c>known</c> kümesi birincinin yazdıklarıyla
/// güncellenmiş olduğu için <c>missing</c> boş çıkar ve iş sıfır Netgsm
/// çağrısıyla çıkar — gerçek no-op. Kilit anahtarı lisans bazlı: farklı
/// lisansların ayna işleri birbirini BEKLEMEZ.</para>
/// </summary>
public sealed class IysMirrorImportJob
{
    public const string SourceCodeMirror = "IYS_MIRROR";

    public const int BatchSize = 20;
    // Netgsm ~10 istek/dk — partiler arası 6 sn (ilk partide bekleme yok).
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly IIysClient _client;
    private readonly ILogger<IysMirrorImportJob> _log;

    /// <summary>Enjekte edilebilir bekleme — test 6 sn beklemesin diye.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; }
        = static (d, c) => Task.Delay(d, c);

    public IysMirrorImportJob(LicenseDbContext db, NetgsmAccountService accounts,
        IIysClient client, ILogger<IysMirrorImportJob> log)
    {
        _db = db;
        _accounts = accounts;
        _client = client;
        _log = log;
    }

    // Kardeşleri (IysConsentPushJob vb.) recurring olduğu için Attempts=0 —
    // süpürme bir dahaki turda zaten yeniden dener. Bu iş TEK SEFERLİK
    // (panelden kuyruklanır, zamanlanmış bir sonraki koşusu yok): geçici bir
    // hata burada sonsuza dek beklemez, Hangfire üç kez üstel gecikmeyle
    // yeniden dener. İş idempotent — kaldığı yerden devam eder, `known`
    // kümesi zaten yazılmış numaraları atlar.
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    // Lisans başına kilit: `{0}` Hangfire 1.8'de ilk iş argümanına (licenseId) bağlanır.
    // İkinci kopya bekler, sonra `known` kümesini yeniden hesaplar ve `missing`
    // boş olduğu için sıfır Netgsm çağrısıyla çıkar — gerçek no-op. Zaman aşımı
    // uzun (30 dk): en uzun makul koşu (1000 numara ≈ 5 dk) sığar; kısa tutulsaydı
    // DistributedLockTimeoutException → sahte Failed üretirdi.
    [DisableConcurrentExecution("iys-mirror:{0}", 1800)]
    public async Task RunAsync(Guid licenseId, CancellationToken ct)
    {
        var account = await _accounts.GetVerifiedByLicenseAsync(licenseId, ct);
        if (account is null)
        {
            _log.LogWarning("Ayna: doğrulanmış Netgsm hesabı yok (lisans {LicenseId})",
                licenseId);
            return;
        }

        var password = _accounts.TryUnprotectPassword(account.PasswordProtected);
        if (password is null)
        {
            _log.LogError("Ayna: parola çözülemedi (lisans {LicenseId})", licenseId);
            return;
        }

        var ctx = new IysAccountContext(
            licenseId, account.UserCode, password, account.BrandCode);

        var rawPhones = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.PurgedAt == null
                        && p.Phone != null && p.Phone != "")
            .Select(p => p.Phone!)
            .ToListAsync(ct);

        var phones = new List<string>();
        foreach (var raw in rawPhones)
            if (Auth.PhoneNormalizer.TryNormalize(raw, out var e164))
                phones.Add(e164!);
        phones = phones.Distinct().ToList();

        var known = (await _db.IysConsents.AsNoTracking()
                .Where(c => c.BrandCode == account.BrandCode
                            && c.ChannelType == "MESAJ"
                            && c.RecipientType == "BIREYSEL")
                .Select(c => c.Recipient)
                .ToListAsync(ct))
            .ToHashSet();

        var missing = phones.Where(p => !known.Contains(p)).ToList();
        if (missing.Count == 0)
        {
            _log.LogInformation(
                "Ayna: lisans {LicenseId} için eksik numara yok (marka {Brand})",
                licenseId, account.BrandCode);
            return;
        }

        var mirroredTotal = 0;
        var first = true;
        foreach (var chunk in missing.Chunk(BatchSize))
        {
            if (!first) await DelayAsync(BatchDelay, ct);
            first = false;

            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(ctx, chunk, ct);
            }
            catch (IysConfigurationException ex)
            {
                _log.LogError(ex, "Ayna: kalıcı yapılandırma hatası (marka {Brand})",
                    account.BrandCode);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // sunucu kapanıyor — Hangfire yeniden kuyruğa alır
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning(ex,
                    "Ayna: geçici ağ hatası — kalan partiler sonraki koşuya "
                    + "(marka {Brand})", account.BrandCode);
                throw; // tek seferlik iş: [AutomaticRetry] Hangfire'a yeniden denetir
            }

            if (result.Code != "0")
            {
                // Kota/oran sınırı gibi geçici durumlar da buradan geçer — parti
                // GÜVENİLİR değil, işlenmeden atlanır ve koşu yeniden dener.
                _log.LogWarning(
                    "Ayna: /iys/search kod {Code} döndü (marka {Brand}) — parti işlenmedi",
                    result.Code, account.BrandCode);
                throw new InvalidOperationException($"İYS arama kodu {result.Code}");
            }

            var now = DateTimeOffset.UtcNow;
            var mirroredInChunk = 0;
            foreach (var phone in chunk)
            {
                // İYS "kayıt yok" ile RET'i ayırt edemez (NetgsmIysClient,
                // 2026-09-17 ölçümü) — tek kullanılabilir sinyal ONAY; RET/Unknown
                // satır yazmaz. Kapı satırsızken zaten kapalı (fail-closed): kayıp yok.
                if (!result.Statuses.TryGetValue(phone, out var status)
                    || status != IysConsentStatus.Onay)
                    continue;

                mirroredInChunk++;
                var consent = new IysConsent
                {
                    Id = Guid.NewGuid(),
                    BrandCode = account.BrandCode,
                    ChannelType = "MESAJ",
                    RecipientType = "BIREYSEL",
                    Recipient = phone,
                    Status = status,
                    ConsentDate = null,          // /iys/search consentDate DÖNMÜYOR (2026-09-17 ölçümü)
                    SourceCode = SourceCodeMirror,
                    PushState = IysPushState.Confirmed, // TERMİNAL: push Pending'i, verify Pushed'ı tarar
                    LastVerifiedStatus = status,
                    LastVerifiedAt = now,
                    // Ayna yerel bir olay DEĞİL — `default` "henüz yerel olay yok"
                    // sinyalidir (bkz. IysConsentCollector.ApplyToRowAsync:
                    // `row.LastLocalEventAt != default` koruması). `now` yazsaydık,
                    // sonradan gelen gerçek (ama daha ESKİ zaman damgalı, ör. yeniden
                    // oynatılan) bir yerel onay/ret'i bayat SAYDIRIRDI.
                    LastLocalEventAt = default,
                    NextVerifyAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.IysConsents.Add(consent);
                _db.IysConsentEvents.Add(new IysConsentEvent
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    BrandCode = account.BrandCode,
                    IysConsentId = consent.Id,
                    Recipient = phone,
                    OccurredAt = now,
                    EventType = IysConsentEventType.SearchResult,
                    Status = status,
                    ApiResponseCode = result.Code,
                });
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                mirroredTotal += mirroredInChunk;
            }
            catch (DbUpdateException ex) when (!ct.IsCancellationRequested)
            {
                _db.ChangeTracker.Clear();
                // İstisna nesnesi TAMAMEN LOGLANMAZ: SQL Server'ın tekil anahtar
                // ihlali mesajı telefonu taşır (KVKK) — yalnız tür adı + hata
                // numarası yeterli sinyal (2601/2627 = tekil indeks yarışı, başka
                // bir numara kalıcı bir yazım hatasını ayırt eder). InMemory
                // sağlayıcısında SqlException yok — SqlError alanı null kalır.
                // `when (!ct.IsCancellationRequested)`: sunucu kapanışı TAM
                // SaveChanges sürerken bir DbUpdateException'a dönüşürse bunu
                // zararsız bir yarış SANIP yutmamak için — kapanış dışarı sızmalı.
                var sqlError = (ex.InnerException as Microsoft.Data.SqlClient.SqlException)?.Number;
                _log.LogWarning(
                    "Ayna: yazım çakışması ({ExceptionType}, SqlError={SqlError}) — "
                    + "idempotent, sonraki koşu tamamlar (marka {Brand})",
                    ex.GetType().Name, sqlError, account.BrandCode);

                // Yalnız tekil indeks yarışı (2601/2627) yutulur — idempotent,
                // sonraki koşu tamamlar. Başka bir yazım hatası (FK/kesme/
                // sağlayıcı arızası) DIŞARI ÇIKAR ki [AutomaticRetry] devreye
                // girsin ve iş sessizce "başarılı" bitmesin. InMemory'de
                // SqlException yok → sqlError null → bu dal her zaman fırlatır
                // (testlerde bu yol zaten tetiklenmiyor — bkz. sınıf yorumu).
                if (sqlError is not (2601 or 2627)) throw;
            }
        }

        _log.LogInformation(
            "Ayna: marka {Brand} — {Asked} numara soruldu, {Mirrored} ONAY aynalandı, "
            + "{Skipped} kayıt yok/RET",
            account.BrandCode, missing.Count, mirroredTotal, missing.Count - mirroredTotal);
    }
}
