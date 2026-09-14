using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.IntakeForm;

/// <summary>
/// Pulls new IntakeFormSubmission rows from the license server, upserts each
/// as a Customer (platform="form"), advances the cursor in the SyncCursor
/// table. R9-D02: imleç settings.json'dan SyncCursor("intake-form-in",
/// LicenseKey) satırına taşındı — imleç, tarif ettiği Customer satırlarıyla
/// aynı SQLite dosyasında yaşar; yedek/geri yükleme ikisini birlikte taşır.
/// Eski settings alanları (LastIntakeFormSync/Id) SİLİNDİ ve tohum olarak da
/// okunmuyor. Satır yoksa baştan çekim — UpsertPersonFromIntake idempotent.
/// Idempotent: duplicate calls are no-op (server filters by SubmittedAt &gt; since).
/// </summary>
public sealed class IntakeFormSyncService
{
    private const string CursorName = "intake-form-in";

    // R9-D03: backfill işareti settings bool'undan SyncCursor satırına taşındı
    // (Seq = sürüm numarası). Sürüm 1 = eski settings bool dönemi — o dönem
    // imleci .NET Guid sırasıyla YENİDEN SIRALIYORDU; sunucu SQL
    // uniqueidentifier sırasıyla sayfaladığı için imleç takılıyor, 500 sayfa
    // tavanına çarpıp işi YARIM bırakırken bool yine de true yazılıyordu
    // (denetim: 1000 kayıttan 599'u işlenmiş). Sürüm 2 = bu onarılmış kod.
    // Satır yoksa VEYA Seq < 2 ise backfill yeniden koşar: eski kurulumların
    // yanlış "bitti" işareti böylece kendiliğinden onarılır; işaret DB'de
    // olduğu için yedekle birlikte taşınır.
    private const string BackfillMarkerName = "intake-fullname-backfill";
    private const long BackfillVersion = 2;

    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<IntakeFormSyncService> _log;

    public event EventHandler<int>? SubmissionsSynced;

    public IntakeFormSyncService(
        LicenseApiClient api,
        CustomerRepository customers,
        SyncCursorRepository cursors,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<IntakeFormSyncService> log)
    {
        _api = api;
        _customers = customers;
        _cursors = cursors;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// UI freeze fix (2026-05-13): consecutive auth-failure tracking. 25 art arda
    /// 401 her 2 dk = exception storm UI thread'i etkiliyor. Auth hatasında
    /// hosted service backoff arttırsın diye flag expose ediyor.
    /// </summary>
    public bool LastSyncWasAuthFailure { get; private set; }

    /// <summary>
    /// Tek seferlik geriye-dönük düzeltme: FullName kolonu (migration 022) öncesi
    /// kaydolan müşterilerde gerçek Ad Soyad yerelde saklanmamıştı (chat takma adı
    /// korunuyordu). Sunucudaki TÜM form kayıtlarını (cursor'dan bağımsız, baştan)
    /// gezip her birinin gerçek Ad Soyad'ını eşleşen müşteri satırlarına yazar —
    /// yalnızca boş FullName'lere, LastSeenAt/DisplayName'e dokunmadan.
    /// R9-D03: "bitti" işareti SyncCursor("intake-fullname-backfill").Seq ≥ 2;
    /// işaret YALNIZ doğal tamamlanmada (boş sayfa / kısa sayfa) yazılır —
    /// tavan çıkışı veya iptalde yazılmaz, sonraki açılış devam eder.
    /// BackfillFullNameForIdentities yalnız boş FullName doldurduğu için
    /// yeniden koşmak güvenli.
    /// </summary>
    public async Task<int> BackfillFullNamesOnceAsync(CancellationToken ct = default)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return 0;

        if ((_cursors.Get(BackfillMarkerName, licenseKey)?.Seq ?? 0) >= BackfillVersion)
            return 0;

        int totalUpdated = 0;
        DateTimeOffset? since = null;
        var sinceId = Guid.Empty;
        var completed = false;
        // Sayfalama imleci (SubmittedAt, Id); son sayfa < limit olunca dur.
        // Yalnız damgayla ilerleseydi, tam bir sayfa dolusu kayıt aynı damgayı
        // paylaştığında imleç HİÇ ilerlemez ve döngü tavana kadar aynı sayfayı
        // çekerdi. Güvenlik tavanı yine de duruyor.
        for (var page = 0; page < 500 && !ct.IsCancellationRequested; page++)
        {
            List<IntakeFormSubmissionDto> submissions;
            try
            {
                submissions = await _api.GetFormSubmissionsAsync(since, sinceId, limit: 100, ct);
            }
            catch (LicenseApiException ex)
            {
                _log.LogWarning(ex, "FullName backfill fetch failed: {Code} (will retry next launch)", ex.Code);
                return totalUpdated; // flag'i işaretleme → sonraki açılışta tekrar dener
            }

            if (submissions.Count == 0) { completed = true; break; }

            foreach (var sub in submissions)
            {
                var identities = new List<(string Platform, string Username)>();
                void Add(string platform, string? username)
                {
                    if (!string.IsNullOrWhiteSpace(username))
                        identities.Add((platform, username!));
                }
                // channelId varsa onunla, yoksa handle ile (repository ikisini de eşler).
                if (!string.IsNullOrWhiteSpace(sub.YouTubeChannelId))
                    Add("youtube", sub.YouTubeChannelId);
                else
                    Add("youtube", sub.YouTubeUsername);
                Add("instagram", sub.InstagramUsername);
                Add("facebook", sub.FacebookUsername);
                Add("tiktok", sub.TikTokUsername);

                if (identities.Count > 0 && !string.IsNullOrWhiteSpace(sub.FullName))
                    totalUpdated += _customers.BackfillFullNameForIdentities(identities, sub.FullName);
            }

            // R9-D03 / R3-01: imleç sunucunun teslim ettiği SON satırdan
            // okunur — yeniden SIRALAMA YOK. Sunucu SQL Server'ın
            // uniqueidentifier sırasıyla sayfalıyor; .NET Guid sırası farklı
            // (SQL karşılaştırmaya son 6 bayttan başlar). Eski OrderBy(...).Last()
            // imleci sunucu sayfa sınırının gerisinde bırakıyor, döngü aynı
            // satırları çekip 500 sayfa tavanına çarpıyordu.
            var last = submissions[^1];
            since = last.SubmittedAt;
            sinceId = last.Id;

            if (submissions.Count < 100) { completed = true; break; } // son sayfa
        }

        if (!completed)
        {
            // Tavan çıkışı veya iptal — iş YARIM. İşaret yazılmaz ki sonraki
            // açılış kaldığı yerden değil ama en azından yeniden denesin
            // (eski kod burada bool'u true yazıp 599/1000'de bırakıyordu).
            _log.LogWarning(
                "FullName backfill did not finish (page cap or cancel) — updated {Count} row(s), will retry next launch",
                totalUpdated);
            return totalUpdated;
        }

        _cursors.Upsert(BackfillMarkerName, licenseKey, seq: BackfillVersion);
        _log.LogInformation("FullName backfill complete: {Count} row(s) updated", totalUpdated);
        return totalUpdated;
    }

    public async Task<int> SyncOnceAsync(CancellationToken ct = default)
    {
        // R9-D02: imleç lisans anahtarına bağlı SyncCursor satırından okunur.
        // Anahtar yoksa hiç başlama — kardeş servislerle tutarlı (imleç hangi
        // lisans adına ilerleyecek bilinmeden ilerletilemez).
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            _log.LogDebug("Intake form sync skipped — no active license key");
            return 0;
        }

        // Satır yoksa baştan çekim (since=null): eski settings imleci tohum
        // OLMUYOR — settings dosyası yedeğin dışında yaşadığı için hangi veri
        // nesline/lisansa ait olduğu kanıtlanamaz; geri yüklemeden sonra ileri
        // kalmış imleç güncel adresi/telefonu sonsuza dek atlatıyordu.
        // UpsertPersonFromIntake idempotent, yeniden çekim güvenli.
        var row = _cursors.Get(CursorName, licenseKey);
        var since = row?.UpdatedAt;
        var sinceId = row?.LastId ?? Guid.Empty;

        List<IntakeFormSubmissionDto> submissions;
        try
        {
            submissions = await _api.GetFormSubmissionsAsync(since, sinceId, limit: 50, ct);
            LastSyncWasAuthFailure = false;
        }
        catch (InvalidCredentialsException ex)
        {
            // Auth token expired — bir sonraki login'e kadar denemek log spam'i
            // ve exception storm yaratıyor. Hosted service'e flag ile bildir,
            // backoff aralığını uzatsın.
            LastSyncWasAuthFailure = true;
            _log.LogWarning(ex, "Intake form sync auth failed: {Code} (backing off)", ex.Code);
            return 0;
        }
        catch (LicenseApiException ex)
        {
            LastSyncWasAuthFailure = false;
            _log.LogWarning(ex, "Intake form sync failed: {Code}", ex.Code);
            return 0;
        }

        if (submissions.Count == 0) return 0;

        var nowUnix = _clock.UnixNow();

        // İmleç (SubmittedAt, Id) çifti. Yalnız en büyük SubmittedAt alınsaydı,
        // aynı damgayı paylaşan kayıtlar sayfa sınırında kesildiğinde kalanları
        // bir daha hiç istenmezdi — ve o satır bir müşteri KAYDI olduğu için
        // eksik kendiliğinden kapanmazdı.
        // R3-01: sunucunun teslim sırası olduğu gibi kullanılır — yeniden
        // SIRALAMA YOK. Sunucu SQL uniqueidentifier sırasıyla sayfalıyor; .NET
        // Guid sırası farklı, istemci "son"u kendisi seçerse imleç sunucu sayfa
        // sınırının gerisinde kalır ve aynı satırlar tekrar iner.
        foreach (var sub in submissions)
        {
            // Bildirilen platform kimliklerini topla (çoklu-platform).
            var identities = new List<(string Platform, string Username, string? PreferredDisplayName)>();
            void Add(string platform, string? username, string? display = null)
            {
                if (!string.IsNullOrWhiteSpace(username))
                    identities.Add((platform, username!, display));
            }
            // YouTube: doğrulama channelId çektiyse Username=channelId → chat kaydıyla
            // (youtube, channelId) BİREBİR eşleşir. Ama UI'da channelId ASLA görünmesin
            // diye @handle'ı PreferredDisplayName olarak taşırız (DisplayName=@handle).
            // channelId yoksa handle'a düş (repository DisplayName ile köprüler).
            if (!string.IsNullOrWhiteSpace(sub.YouTubeChannelId))
                Add("youtube", sub.YouTubeChannelId, sub.YouTubeUsername);
            else
                Add("youtube", sub.YouTubeUsername);
            Add("instagram", sub.InstagramUsername);
            Add("facebook", sub.FacebookUsername);
            Add("tiktok", sub.TikTokUsername);

            if (identities.Count > 0)
            {
                _customers.UpsertPersonFromIntake(
                    identities,
                    sub.FullName, sub.Address, sub.Phone,
                    sub.Email, sub.Tckn, sub.WhatsAppConsent, sub.SmsConsent,
                    nowUnix, sub.City, sub.District);
            }
            else
            {
                // Eski sunucudan gelen (platform alanları olmayan) gönderim —
                // legacy tek-satır davranışına düş.
                _customers.UpsertFromIntakeForm(
                    sub.Username, sub.FullName, sub.Address, sub.Phone, nowUnix);
            }
        }

        var last = submissions[^1];
        // R6-04 emsali: imleç, işlediği Customer satırlarıyla aynı SQLite
        // dosyasına yazılır — yedek/geri yükleme ikisini birlikte taşır.
        _cursors.Upsert(CursorName, licenseKey,
            updatedAt: last.SubmittedAt, lastId: last.Id);

        _log.LogInformation("Intake form sync: {Count} submission(s) processed (cursor → {Cursor}/{CursorId})",
            submissions.Count, last.SubmittedAt, last.Id);

        SubmissionsSynced?.Invoke(this, submissions.Count);
        return submissions.Count;
    }
}
