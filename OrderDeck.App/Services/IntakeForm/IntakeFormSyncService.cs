using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
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
///
/// <para>Damga = formun <c>SubmittedAt</c>'i (Bölüm C kural 3): her bilgisayar
/// formları kendi imleciyle uygular; işleme anı damgası geç açılan bilgisayarın eski
/// formları en yeni damgayla oynatıp sonradan yapılan düzeltmeleri ezmesi demekti.</para>
///
/// <para>U14: imleçsiz başlayan bilgisayarın ilk tam oynatmasında oynatmanın başlangıcından (T0)
/// önce gönderilmiş formlar doldurma kipinde (<see cref="IntakeApplyMode.FillOnly"/> — yalnız boş ve
/// damgasız birim, damga yazılmaz), sonrakiler damgalı; form işleme iki kipte de bu süreçte bu
/// lisansın ilk tam müşteri akışını bekler.</para>
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
    // olduğu için yedekle birlikte taşınır. Seq < 2 iken UpdatedAt/LastId =
    // kaldığı yer (C10 incelemesi: tur başına sınırlı sayfa, sonraki tur sürer).
    private const string BackfillMarkerName = "intake-fullname-backfill";
    private const long BackfillVersion = 2;

    // U14: taze bilgisayarın (intake-form-in imleci olmadan başlayan) ilk tam form oynatması
    // doldurma kipinde. İşaret SyncCursor satırında (yedekle birlikte taşınır): Seq 1 = oynatma
    // sürüyor, 2 = bitti (ya da hiç gerekmedi). UpdatedAt = oynatmanın başladığı an (T0, yerel
    // saat): oynatma sürerken T0'dan SONRA gönderilen form damgalı uygulanır (ReplayState).
    private const string ReplayMarkerName = "intake-form-replay";
    private const long ReplayRunning = 1;
    private const long ReplayDone = 2;

    // Tur başına sayfa sınırları (C10 incelemesi): sunucunun genel sınırı IP başına dakikada 100
    // istek; diğer senkron servisleri de aynı bütçeyi kullanır (aynı NAT arkasındaki ikinci
    // bilgisayar dahil). Oynatma sürerken form turu birkaç sayfa çeker (pencere kısalır), backfill
    // her turda en çok bu kadar sayfa çekip kaldığı yeri kaydeder.
    private const int ReplayPagesPerTick = 5;
    private const int BackfillPagesPerTick = 5;
    private const int FormPageSize = 50;
    private const int BackfillPageSize = 100;

    /// <summary>Akışı bundan uzun bekleyen form işleme bir kez bilgi günlüğüne yazılır (sn).</summary>
    private const long FeedWaitLogAfterSeconds = 10 * 60;

    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<IntakeFormSyncService> _log;
    private readonly SyncStatusTracker? _tracker;

    // Akış beklemesinin başladığı an (unix s) ve bu bekleme için uzun bekleme günlüğü yazıldı mı.
    // Form turu ve backfill aynı arka plan işinden sırayla çağrılır.
    private long? _feedWaitSince;
    private bool _feedWaitLogged;

    /// <summary>"Bu oturumda yeni" form sayısı (rozet). Yalnız damgalı uygulanan (yeni) formlar
    /// sayılır — taze bilgisayar oynatmasının eski formları yeni değildir.</summary>
    public event EventHandler<int>? SubmissionsSynced;

    /// <param name="tracker">Müşteri akışının durum izleyicisi (DI'da tekil): form işleme bu süreçteki
    /// ilk tam akıştan sonra başlar. Boşsa (yalnız testler) beklenmez.</param>
    public IntakeFormSyncService(
        LicenseApiClient api,
        CustomerRepository customers,
        SyncCursorRepository cursors,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<IntakeFormSyncService> log,
        SyncStatusTracker? tracker = null)
    {
        _api = api;
        _customers = customers;
        _cursors = cursors;
        _licenseProvider = licenseProvider;
        _clock = clock;
        _log = log;
        _tracker = tracker;
    }

    /// <summary>
    /// Taze bilgisayar oynatmasının durumu: sürüyorsa başladığı an (T0). Kip FORM BAŞINA seçilir:
    /// oynatma sürerken T0'dan ÖNCE gönderilmiş form doldurma kipinde (U14 — göç öncesi damgasız
    /// değerleri ezmesin), T0'dan sonraki form damgalı. Sunucu formdan müşteri kaydı türetmez; tek
    /// bilgisayarlı lisansta oynatma sürerken gelen yeni form (geri dönen müşterinin yeni adresi)
    /// yalnız doldurulsaydı yeni değer kalıcı kaybolurdu — T0'dan sonraki form ise oynatma öncesindeki
    /// her değerden yenidir, U14'ün koruduğu hiçbir şeyi ezmez. T0 yerel saatten: saat kayması sınırı
    /// o kadar kaydırır (kabul).
    /// </summary>
    private readonly record struct ReplayState(DateTimeOffset? RunningSince)
    {
        public bool Running => RunningSince is not null;

        public IntakeApplyMode ModeOf(DateTimeOffset submittedAt)
            => RunningSince is { } t0 && submittedAt < t0 ? IntakeApplyMode.FillOnly : IntakeApplyMode.Stamped;
    }

    /// <summary>U14: imleç yoksa (yeni kurulum, yedeksiz açılış) ilk tam oynatma doldurma kipinde;
    /// imleç varsa (güncellenen kurulum, yedekten dönüş) oynatma yok. İşaret ilk karar anında T0 ile
    /// yazılır ve İMLEÇTEN ÖNCE denetlenir — oynatmanın kendisi imleci ilerletir, yarıda kalan oynatma
    /// yeniden başlatmada da aynı T0 ile sürer.</summary>
    private ReplayState ReplayFor(string licenseKey)
    {
        var marker = _cursors.Get(ReplayMarkerName, licenseKey);
        if (marker?.Seq == ReplayDone) return new ReplayState(null);
        if (marker?.Seq == ReplayRunning && marker.UpdatedAt is { } t0) return new ReplayState(t0);
        if (marker?.Seq != ReplayRunning && _cursors.Get(CursorName, licenseKey) is not null)
        {
            _cursors.Upsert(ReplayMarkerName, licenseKey, seq: ReplayDone);
            return new ReplayState(null);
        }
        var start = DateTimeOffset.FromUnixTimeSeconds(_clock.UnixNow());
        _cursors.Upsert(ReplayMarkerName, licenseKey, seq: ReplayRunning, updatedAt: start);
        return new ReplayState(start);
    }

    /// <summary>Form işleme (İKİ kipte de) yalnız bu süreçte BU LİSANSIN ilk tam müşteri akışından
    /// SONRA: önce sunucu gerçeği iner. Doldurma kipinde form yalnız onun bıraktığı boşluğu doldurur
    /// (U14); damgalı kipte sırayla kullanılan bilgisayar, başka bilgisayarın aynı form için gönderdiği
    /// (eşit damgalı) sonucu indirmeden formu kendi yerel durumuyla işleyip türetilen değerde (grup)
    /// ayrışmaz (C2 kalite incelemesi). İzleyici yalnız akış boş sayfaya ulaşınca kurulur — büyük
    /// lisansta ilk yetişme birkaç tur sürebilir; bekleme <see cref="FeedWaitLogAfterSeconds"/>'ı aşarsa
    /// bir kez bilgi günlüğü. İzleyicisiz kurulum (testler) beklemez.</summary>
    private bool MustWaitForFeed(string licenseKey)
    {
        if (_tracker is null || _tracker.IsInitialCatchUpDoneFor(licenseKey))
        {
            _feedWaitSince = null;
            _feedWaitLogged = false;
            return false;
        }

        var now = _clock.UnixNow();
        _feedWaitSince ??= now;
        if (!_feedWaitLogged && now - _feedWaitSince.Value > FeedWaitLogAfterSeconds)
        {
            _feedWaitLogged = true;
            _log.LogInformation(
                "Form senkronu {Minutes} dakikadır ilk tam müşteri akışını bekliyor — formlar akış yetişince uygulanır",
                (now - _feedWaitSince.Value) / 60);
        }
        return true;
    }

    /// <summary>Sunucunun genel hız sınırı gövdesiz 429 döner → <c>http-429</c>.</summary>
    private static bool IsRateLimited(LicenseApiException ex) => ex is ValidationException { Code: "http-429" };

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
    /// tur sınırı, 429 ya da iptalde yazılmaz.
    /// BackfillFullNameForIdentities yalnız boş FullName doldurduğu için
    /// yeniden koşmak güvenli.
    ///
    /// <para>C10 incelemesi: arka plan işi backfill'i bitene dek HER TURDA dener (U14: taze
    /// bilgisayarda akışı bekler). Bu yüzden tur başına en çok <see cref="BackfillPagesPerTick"/>
    /// sayfa ve konum (son satırın SubmittedAt/Id'si) her sayfadan sonra işaret satırına yazılır
    /// (Seq &lt; 2 iken UpdatedAt/LastId); sonraki tur kaldığı yerden sürer. Eskisi gibi her turda
    /// baştan 500 sayfa, büyük lisansta IP başına dakikada 100 isteklik sınırı her turda tüketirdi
    /// (429) — ne backfill ne oynatma ilerler, diğer senkronlar da 429 alırdı.</para>
    /// </summary>
    public async Task<int> BackfillFullNamesOnceAsync(CancellationToken ct = default)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return 0;

        var marker = _cursors.Get(BackfillMarkerName, licenseKey);
        if ((marker?.Seq ?? 0) >= BackfillVersion)
            return 0;

        // U14: taze bilgisayarda oynatma sürerken T0'dan önceki form doldurma kipinde (boş + damgasız
        // ad, damga yazılmaz); iki kipte de ilk tam müşteri akışından sonra. Beklerken işaret yazılmaz →
        // arka plan işi sonraki turda yeniden dener.
        var replay = ReplayFor(licenseKey);
        if (MustWaitForFeed(licenseKey)) return 0;

        int totalUpdated = 0;
        // Kaldığı yer (Seq < 2 iken işaret satırında); yoksa baştan.
        var since = marker?.UpdatedAt;
        var sinceId = marker?.LastId ?? Guid.Empty;
        var completed = false;
        // Sayfalama imleci (SubmittedAt, Id); son sayfa < limit olunca dur.
        // Yalnız damgayla ilerleseydi, tam bir sayfa dolusu kayıt aynı damgayı
        // paylaştığında imleç HİÇ ilerlemezdi.
        for (var page = 0; page < BackfillPagesPerTick && !ct.IsCancellationRequested; page++)
        {
            List<IntakeFormSubmissionDto> submissions;
            try
            {
                submissions = await _api.GetFormSubmissionsAsync(since, sinceId, limit: BackfillPageSize, ct);
            }
            catch (LicenseApiException ex)
            {
                // Tur biter, konum işaret satırında kalır → sonraki tur kaldığı yerden.
                if (IsRateLimited(ex))
                    _log.LogInformation("FullName backfill: hız sınırı (429) — {Count} satır güncellendi, sonraki turda sürer",
                        totalUpdated);
                else
                    _log.LogWarning(ex, "FullName backfill fetch failed: {Code} (will retry next interval)", ex.Code);
                return totalUpdated;
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
                    totalUpdated += _customers.BackfillFullNameForIdentities(
                        identities, sub.FullName, submittedAtMs: sub.SubmittedAt.ToUnixTimeMilliseconds(),
                        mode: replay.ModeOf(sub.SubmittedAt));
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
            // Konum kalıcı (Seq < 2 korunur): sınır, 429 ya da iptal kaldığı yeri kaybettirmez.
            _cursors.Upsert(BackfillMarkerName, licenseKey, seq: marker?.Seq, updatedAt: since, lastId: sinceId);

            if (submissions.Count < BackfillPageSize) { completed = true; break; } // son sayfa
        }

        if (!completed)
        {
            // Tur sınırı ya da iptal — iş YARIM. "Bitti" işareti yazılmaz; sonraki tur kaldığı
            // yerden sürer (eski kod burada bool'u true yazıp 599/1000'de bırakıyordu).
            _log.LogDebug("FullName backfill sürüyor — bu turda {Count} satır güncellendi", totalUpdated);
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

        var replay = ReplayFor(licenseKey);
        if (MustWaitForFeed(licenseKey))
        {
            _log.LogDebug("Form senkronu ilk tam müşteri akışını bekliyor (oynatma: {Replay})", replay.Running);
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
        var nowUnix = _clock.UnixNow();

        // Oynatma sürerken tur birkaç sayfa çeker (doldurma penceresi kısalır; hız sınırına uyar),
        // sonra tur başına tek sayfa (bugünkü ritim).
        var maxPages = replay.Running ? ReplayPagesPerTick : 1;
        var processed = 0;
        var stamped = 0;
        for (var page = 0; page < maxPages; page++)
        {
            List<IntakeFormSubmissionDto> submissions;
            try
            {
                submissions = await _api.GetFormSubmissionsAsync(since, sinceId, limit: FormPageSize, ct);
                LastSyncWasAuthFailure = false;
            }
            catch (InvalidCredentialsException ex)
            {
                // Auth token expired — bir sonraki login'e kadar denemek log spam'i
                // ve exception storm yaratıyor. Hosted service'e flag ile bildir,
                // backoff aralığını uzatsın.
                LastSyncWasAuthFailure = true;
                _log.LogWarning(ex, "Intake form sync auth failed: {Code} (backing off)", ex.Code);
                break;
            }
            catch (LicenseApiException ex)
            {
                // Tur biter; işlenen sayfaların imleci kaydedildi, sonraki tur kaldığı yerden.
                LastSyncWasAuthFailure = false;
                if (IsRateLimited(ex))
                    _log.LogInformation("Intake form sync: hız sınırı (429) — {Count} form işlendi, sonraki turda sürer",
                        processed);
                else
                    _log.LogWarning(ex, "Intake form sync failed: {Code}", ex.Code);
                break;
            }

            if (submissions.Count == 0)
            {
                // U14: tam oynatma boş sayfaya ulaştı — bundan sonraki formlar damgalı (kural 3).
                if (replay.Running)
                {
                    _cursors.Upsert(ReplayMarkerName, licenseKey, seq: ReplayDone, updatedAt: replay.RunningSince);
                    _log.LogInformation("Taze bilgisayar form oynatması tamamlandı — yeni formlar damgalı uygulanır (U14)");
                }
                break;
            }

            var filled = 0;
            foreach (var sub in submissions)
            {
                var mode = replay.ModeOf(sub.SubmittedAt);
                ApplySubmission(sub, mode, nowUnix);
                if (mode == IntakeApplyMode.Stamped) stamped++;
                else filled++;
            }

            // İmleç (SubmittedAt, Id) çifti. Yalnız en büyük SubmittedAt alınsaydı,
            // aynı damgayı paylaşan kayıtlar sayfa sınırında kesildiğinde kalanları
            // bir daha hiç istenmezdi — ve o satır bir müşteri KAYDI olduğu için
            // eksik kendiliğinden kapanmazdı.
            // R3-01: sunucunun teslim sırası olduğu gibi kullanılır — yeniden
            // SIRALAMA YOK. Sunucu SQL uniqueidentifier sırasıyla sayfalıyor; .NET
            // Guid sırası farklı, istemci "son"u kendisi seçerse imleç sunucu sayfa
            // sınırının gerisinde kalır ve aynı satırlar tekrar iner.
            var last = submissions[^1];
            since = last.SubmittedAt;
            sinceId = last.Id;
            // R6-04 emsali: imleç, işlediği Customer satırlarıyla aynı SQLite
            // dosyasına yazılır — yedek/geri yükleme ikisini birlikte taşır.
            _cursors.Upsert(CursorName, licenseKey, updatedAt: since, lastId: sinceId);
            processed += submissions.Count;

            _log.LogInformation(
                "Intake form sync: {Count} submission(s) processed, {Filled} fill-only (cursor → {Cursor}/{CursorId})",
                submissions.Count, filled, last.SubmittedAt, last.Id);
        }

        // "Bu oturumda yeni" rozeti: oynatmanın eski formları (doldurma) yeni değildir.
        if (stamped > 0) SubmissionsSynced?.Invoke(this, stamped);
        return processed;
    }

    /// <summary>Tek formu kipine göre uygular (bkz. <see cref="ReplayState"/>).</summary>
    private void ApplySubmission(IntakeFormSubmissionDto sub, IntakeApplyMode mode, long nowUnix)
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
                nowUnix,
                formId: FormIdOf(sub, identities),
                submittedAtMs: sub.SubmittedAt.ToUnixTimeMilliseconds(),
                city: sub.City, district: sub.District, mode: mode);
        }
        else
        {
            // Eski sunucudan gelen (platform alanları olmayan) gönderim —
            // legacy tek-satır davranışına düş.
            _customers.UpsertFromIntakeForm(
                sub.Username, sub.FullName, sub.Address, sub.Phone, nowUnix,
                submittedAtMs: sub.SubmittedAt.ToUnixTimeMilliseconds(), mode: mode);
        }
    }

    /// <summary>
    /// Formun kimliği; sunucu boş göndermişse (JSON'da "id" yok → <see cref="Guid.Empty"/>)
    /// her bilgisayarda AYNI çıkan türetilmiş kimlik. Depo boş kimliği reddeder (yeni grup
    /// ondan türer) ve imleç sayfanın SONUNDA ilerlediği için, boş kimlik depoya geçseydi tek
    /// bozuk gönderim sonraki BÜTÜN formları her bilgisayarda kilitler, sayfanın önceki
    /// formlarını da her turda yeniden uygulatıp gönderime koyardı.
    /// </summary>
    private Guid FormIdOf(
        IntakeFormSubmissionDto sub,
        IReadOnlyList<(string Platform, string Username, string? PreferredDisplayName)> identities)
    {
        if (sub.Id != Guid.Empty) return sub.Id;

        // Kişisel veri yazılmaz: kullanıcı adı / ad yok, yalnız gönderim anı.
        _log.LogWarning("Intake form: boş form kimliği, türetilmiş kimlik kullanıldı (gönderim {SubmittedAt})",
            sub.SubmittedAt);
        return DerivedFormId(sub.SubmittedAt.ToUnixTimeMilliseconds(), identities);
    }

    /// <summary>
    /// Boş kimlikli formun belirleyici yedeği: SHA-256("{gönderim ms}|{sıralı
    /// "platform:kimlik anahtarı" çiftleri '|' ile}") özetinin ilk 16 baytı. Aynı formu işleyen
    /// her bilgisayar aynı kimliği — dolayısıyla aynı yeni grubu — türetir. Sıralama ordinal, sayı
    /// biçimi kültürden bağımsız: sonuç makinenin diline bağlı olmamalı.
    /// </summary>
    private static Guid DerivedFormId(
        long submittedAtMs,
        IEnumerable<(string Platform, string Username, string? PreferredDisplayName)> identities)
    {
        var pairs = identities
            .Select(i => $"{i.Platform}:{CustomerIdentity.KeyOrNull(i.Username)}")
            .OrderBy(pair => pair, StringComparer.Ordinal);
        var text = submittedAtMs.ToString(CultureInfo.InvariantCulture) + "|" + string.Join('|', pairs);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }
}
