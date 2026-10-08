using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bir çekme turunun sonucu.</summary>
public enum CustomerPullOutcome
{
    /// <summary>Lisans yok ya da çözülemedi — akış istenmedi.</summary>
    NoLicense,
    /// <summary>Sayfa/ağ hatası (429 dahil), uygulanamayan öğe (atlanana kadar), yerel ortam hatası
    /// (kilit çekişmesi, disk dolu…) ya da gönderimin yerel hatası. İmleç son uygulanan öğede.</summary>
    Failed,
    /// <summary>Gönderilmemiş yerel kimlik sahibi (U5) ve durmadan sonraki gönderim onu
    /// götüremedi. İmleç o öğede.</summary>
    Stalled,
    /// <summary>Taşınacak müşteri ödeme akışında (U13). İmleç o öğede, sonraki tur.</summary>
    Busy,
    /// <summary>Tur başına sayfa sınırına ulaşıldı (<see cref="CustomerChangesPullService.MaxPagesPerRound"/>);
    /// akış sonraki turda kaldığı yerden sürer. Yetişme sayılmaz; izleyiciye yetişme ilerlemesi
    /// olarak işlenir (<see cref="SyncStatusTracker.MarkCatchUpProgress"/>, D2).</summary>
    MorePending,
    /// <summary>Akış boş sayfaya kadar uygulandı; <see cref="SyncStatusTracker"/>'a işlendi.</summary>
    CaughtUp,
}

/// <summary>
/// Sunucunun müşteri değişiklik akışını yerelde uygular (Bölüm C, kural 8): diğer
/// bilgisayarların ve Shopper uygulamasının açtığı/değiştirdiği müşteriler, kopya
/// yönlendirmeleri ve KVKK silmeleri. Eski ShopperRegistrationIngestService'in yerine
/// geçer — o yalnız yeni satır ekliyor, güncellemeyi hiç indirmiyor, kullanıcı adını harf
/// duyarlı eşliyor ve Shopper'ın geçici beyanını sıradan müşteri olarak indiriyordu. Geçici
/// satır yerelde hiç açılmaz; eski ingest'in açtığı miras satırı dönüştürülür (kural 7, U9).
///
/// <para><b>Tur:</b> yerel bakım (kalmış kilit satırı — U15; süreç başına bir kez kimlik
/// anahtarı onarımı — U6), gönderim, sonra akış. İmleç
/// SyncCursor(<see cref="CursorName"/>, LicenseKey).Seq = sunucunun rowversion'ı; öğe uygulandıkça
/// ilerler, sayfa başına kaydedilir, BOŞ sayfaya kadar döner (dolu olmayan sayfa son sayılmaz —
/// S11) — en fazla <see cref="MaxPagesPerRound"/> sayfa, kalanı sonraki tur. Durma (U5) ve meşgul
/// müşteri (U13) imleci o öğede bırakır. Uygulanamayan öğe <see cref="MaxAttemptsBeforeSkip"/>
/// turdan sonra atlanır, kaydı ve uyarısı kalır (U10); öğeye özgü olmayan hatalar (yerel ortam,
/// sızmış kilit satırı, kilit yeniden girişi) deneme sayılmaz. Yeniden uygulama damga kurallarıyla
/// zararsız. Sayfa/ağ hatasında (429 dahil — sunucu IP başına dakikada ~100 istek kabul eder) tur
/// başarısız, sonraki tur kaydedilen sayfadan sürer. İptalde imleç son uygulanan öğede kaydedilir.</para>
///
/// <para><b>Durma (U5):</b> asıl kayıt geldiğinde yerelde aynı kimlikte, HENÜZ GÖNDERİLMEMİŞ
/// başka Id'li satır varsa taşınmaz: önce gönderim koşar (sunucu o satırı kopya olarak bağlar,
/// verisini birleştirir, siparişlerini taşır), akış aynı turda bir kez daha denenir — yalnız
/// gönderim filigranı ilerlediyse. Gönderim HTTP hatasında fırlatmadan döner; filigran
/// ilerlemediyse ikinci deneme aynı öğede yine dururdu: istek harcanmaz, tur biter.</para>
///
/// <para><b>Takılan öğe (C7 incelemesi I-1):</b> durma ya da meşgul müşteri imleci o öğede
/// tutar; arkasındaki her şey bekler. KVKK silmeleri beklemez: aynı sayfada takılan öğenin
/// ilerisindeki (geçici olmayan) silmeler imleç ilerlemeden hemen uygulanır — imleç oraya
/// varınca yeniden uygulanır (zararsız). Erken uygulanan silmeyi, daha önceki bir öğenin sonradan
/// uygulanması geri açamaz: bütün yazımlar <c>PurgedAt IS NULL</c> kapılı, eklemeler mezar taşına
/// takılır. Aynı öğe <see cref="BlockedRoundsBeforeWarning"/> tur üst üste takılırsa öğe Id'si ve
/// sebebiyle BİR uyarı yazılır ve durum <see cref="SyncStatusTracker.BlockedOn"/>'da görünür (D2:
/// "çevrimdışı" değil "bekliyor"); eşikten sonra her takılı turda tazelenir (D2 incelemesi I-2 —
/// tazelenmeyen takılmayı durum satırı üç dakika sonra yok sayar), öğe uygulanınca kalkar. Takılan
/// öğe ASLA kendiliğinden atlanmaz (U5/U13'ün koruduğu veri kaybolurdu).</para>
///
/// <para><b>Yetişme ilerlemesi (D2 incelemesi M-2):</b> akış imleci bu turda ilerlediyse ama tur
/// boş sayfaya varmadıysa (sayfa sınırı, ya da sayfalar uygulanıp sonra 429/hata/takılma) izleyiciye
/// yetişme ilerlemesi yazılır — durum satırı ilerleyen bilgisayarı çevrimdışı göstermez. Turun
/// BAŞINDAKİ, akıştan ÖNCE koşan gönderim de aynı şekilde sayılır (son inceleme M-1): ilk kurulumun
/// uzun biçim-2 resend'i akışın ilk sayfasına varmadan üç dakikayı geçerse durum satırı yine
/// "Çevrimdışı" demesin — sunucuya gerçekten ulaşılıyor.</para>
///
/// <para><b>Gönderimin hatası:</b> gönderim HTTP hatasını kendisi yutar (imleç ilerlemez) ama
/// yerel SQLite hatası çıkabilir. Çağrı korunur: tur başarısız sayılır, akış yine uygulanır (KVKK
/// silmeleri bir gönderim hatasının arkasında beklemez); yetişme kaydı yalnız akışa bakar.</para>
///
/// <para><b>Lisans değişimi:</b> imleçler lisans anahtarına bağlı (yeni lisansın akışı baştan);
/// akış hatası kayıtları bağlı değil — servis önceki turdan farklı bir anahtar görünce onları ve
/// takılma durumunu siler (M-3).</para>
///
/// <para><b>İş parçacığı:</b> bütün <c>await</c>'ler <c>ConfigureAwait(false)</c> — öğe uygulaması
/// <c>CustomerBusySet</c> kilidini eşzamanlı bekler, arayüz iş parçacığında koşmamalı.
/// Turlar ve destek eyleminin sıfırlaması (<see cref="RequestFullResyncAsync"/>, D5b) tek tur
/// kilidinden geçer; turu yalnız arka plan işi başlatır (30 sn) — sıfırlama da işi ona bırakır.</para>
/// </summary>
public sealed class CustomerChangesPullService
{
    public const string CursorName = "customer-changes-in";
    private const int PageSize = 500;

    /// <summary>U10: bu kadar başarısız turdan sonra öğe atlanır (30 sn ritimde ~2,5 dk).</summary>
    internal const int MaxAttemptsBeforeSkip = 5;

    /// <summary>I-1: aynı öğede bu kadar tur üst üste takılınca (30 sn ritimde ~2,5 dk) bir uyarı ve
    /// durum. D2'nin "çevrimdışı" eşiğinin (3 dk) ALTINDA (D2 incelemesi M-1): takılma, satır
    /// çevrimdışına düşmeden görünür.</summary>
    internal const int BlockedRoundsBeforeWarning = 5;

    /// <summary>M-7: tur başına en fazla sayfa (500'lük sayfalarla 5.000 öğe); kalanı sonraki tur.
    /// Büyük bir ilk yetişme tek turda sunucunun IP başına hız sınırını (dakikada ~100 istek)
    /// tüketmesin — turun sonundaki yankı gönderimine (U2) ve diğer senkron servislerine pay kalsın.</summary>
    internal const int MaxPagesPerRound = 10;

    /// <summary>N-1: veritabanı bozulmasında operatöre/desteğe yol gösterir.</summary>
    private const string CorruptionHint =
        "'PRAGMA integrity_check' ile veritabanını denetleyin; arama dizini (CustomerFts) tutarsızsa " +
        "INSERT INTO CustomerFts(CustomerFts) VALUES('rebuild') ile yeniden kurun";

    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly CustomerSyncRepository _sync;
    private readonly SyncCursorRepository _cursors;
    private readonly WpfCustomerProjectionSyncService _push;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly SyncStatusTracker _tracker;
    private readonly ILogger<CustomerChangesPullService> _log;

    /// <summary>Tek tur kuralı: zamanlayıcının turu ile destek eyleminin (D5b) imleç sıfırlaması üst
    /// üste binmez — turun sayfa sonundaki imleç yazımı sıfırlamayı ezerdi. Kilit sırası: önce bu kilit,
    /// sonra gönderim servisinin tur kilidi (tur da sıfırlama da bu sırayla alır; gönderim bu kilidi
    /// hiç almaz).</summary>
    private readonly SemaphoreSlim _tourGate = new(1, 1);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private string? _lastLicenseKey;
    /// <summary>Son tur lisanssızdı (çıkış): aynı lisansla dönüşte izleme yeniden başlar (D3 incelemesi).</summary>
    private bool _licenseGap;
    private bool _identityKeysHealed;
    private int _lastLegacyJobs = -1;
    private int _pagesThisRound;
    private bool _feedAdvancedThisRound;
    private bool _offlineLogged;
    private bool _environmentErrorLogged;
    private bool _reentrancyLogged;

    private BlockKey? _block;
    private int _blockRounds;
    private DateTimeOffset _blockSince;

    /// <summary>Takılan öğe: aynı öğe, aynı değişiklik, aynı sebep (I-1).</summary>
    private readonly record struct BlockKey(string ItemId, long ChangeSeq, SyncBlockReason Reason);

    private readonly record struct PassResult(CustomerPullOutcome Outcome, long PushWatermark = 0, BlockKey? Block = null);

    public CustomerChangesPullService(
        LicenseApiClient api, CustomerRepository customers, CustomerSyncRepository sync,
        SyncCursorRepository cursors, WpfCustomerProjectionSyncService push,
        ICurrentLicenseProvider licenseProvider, IClock clock, SyncStatusTracker tracker,
        ILogger<CustomerChangesPullService> log)
    {
        _api = api; _customers = customers; _sync = sync; _cursors = cursors; _push = push;
        _licenseProvider = licenseProvider; _clock = clock; _tracker = tracker; _log = log;
    }

    public async Task<CustomerPullOutcome> PullOnceAsync(CancellationToken ct)
    {
        await _tourGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await PullOnceCoreAsync(ct).ConfigureAwait(false); }
        finally { _tourGate.Release(); }
    }

    /// <summary>
    /// D5b — destek eylemi: müşteri senkronunu baştan al. Biçim-2 gönderim imleci ve akış imleci
    /// sıfırlanır; işi arka plan servisinin sonraki turu yapar (bütün müşteriler yeniden gönderilir,
    /// akış baştan uygulanır — <c>CursorReset</c> yolunun aynısı, yeniden uygulama damga kurallarıyla
    /// zararsız: eşit damga yazmaz, kilit altındaki yazım yankılanmaz). Akış hatası kayıtları silinir
    /// (C7 incelemesi; uygulama kapalıyken lisans değiştiyse kalan eski kayıtlar dahil) ve bellekteki
    /// takılma durumu temizlenir — düzelen öğe yeniden uygulanır, hâlâ uygulanamayan beş turda yeniden
    /// atlanır. Form imleci ve oynatma işareti (U14) değişmez: bilgisayar damgalı kipte kalır. Hiçbir
    /// yerel veri silinmez. Sürmekte olan tur önce biter (<see cref="_tourGate"/>).
    /// </summary>
    /// <returns>Lisans yoksa false (hiçbir şey sıfırlanmaz).</returns>
    public async Task<bool> RequestFullResyncAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return false;

        int cleared;
        await _tourGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _push.RewindAsync(licenseKey, ct).ConfigureAwait(false);
            _cursors.Upsert(CursorName, licenseKey, seq: 0);
            cleared = _sync.ClearFeedFailures();
            ClearBlocked();
        }
        finally { _tourGate.Release(); }

        _log.LogWarning(
            "Destek: müşteri senkronu baştan alınıyor — gönderim ve akış imleçleri sıfırlandı, {Count} akış hatası kaydı silindi",
            cleared);
        return true;
    }

    private async Task<CustomerPullOutcome> PullOnceCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            _licenseGap = true;
            return CustomerPullOutcome.NoLicense;
        }

        OnLicenseSeen(licenseKey);
        // Gönderimden ÖNCE: kalmış bir kilit satırında gönderimin taşımaları da (SyncApplyScope
        // ikinci kilit satırına çarpar) düşerdi.
        RunLocalMaintenance();
        _pagesThisRound = 0;
        _feedAdvancedThisRound = false;

        // Gönderim her çekmeden ÖNCE (eski Açık soru 13). İki bilgisayar aynı yayında yorum
        // okurken yeni yorumcuların satırları sunucuya önce gider; asıl kayıt geldiğinde
        // sahipleri "gönderildi" olur, durma (U5) nadirleşir.
        //
        // Son inceleme M-1: ilk kurulumun biçim-2 TAM gönderimi de burada, akışın ilk sayfasından
        // ÖNCE koşar. Binlerce yerel müşteri varsa bu resend üç dakikayı geçebilir; o süre boyunca
        // SyncStatusFormatter'ın "çevrimdışı" dayanağı (TrackingSince) bayatlar — ama sunucuya
        // GERÇEKTEN ulaşılıyor. countsAsCatchUpProgress: true, gerçek bir parti giden her başarılı
        // gönderimi yetişme ilerlemesi sayar (durum satırı "Güncelleniyor…" gösterir, "Çevrimdışı"
        // değil). Aşağıdaki durma-sonrası ve "hemen gönder" (U2) çağrıları bunu BİLEREK vermez —
        // onlar akışın kendi sonucu belirlendikten (ör. tam yetişme) SONRA koşar.
        var pushOk = await TryPushAsync(ct, countsAsCatchUpProgress: true).ConfigureAwait(false);

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null) return CustomerPullOutcome.NoLicense;

        var tally = new Dictionary<FeedApplyResult, int>();
        var pass = await PullPassAsync(licenseKey, licenseId.Value, tally, ct).ConfigureAwait(false);
        var outcome = pass.Outcome;
        if (outcome == CustomerPullOutcome.Stalled)
        {
            // U5: sahibi gönder, akışı aynı turda bir kez daha dene. Gönderim HTTP hatasında
            // fırlatmadan döner — dönüş değeri "başarısız" ile "gönderilecek yok"u ayırmaz;
            // filigran duran öğenin gördüğünden ilerlemediyse ikinci deneme aynı yerde durur.
            if (!await TryPushAsync(ct).ConfigureAwait(false))
                outcome = CustomerPullOutcome.Failed;
            else if (_push.Watermark(licenseKey) > pass.PushWatermark)
            {
                pass = await PullPassAsync(licenseKey, licenseId.Value, tally, ct).ConfigureAwait(false);
                outcome = pass.Outcome;
            }
        }
        LogTally(tally);
        if (pass.Block is { } block) NoteBlocked(block);
        // M-2: akış bu turda ilerledi ama boş sayfaya varmadı — yetişiyor (durum satırı, D2).
        if (outcome != CustomerPullOutcome.CaughtUp
            && (_feedAdvancedThisRound || outcome == CustomerPullOutcome.MorePending))
            _tracker.MarkCatchUpProgress(DateTimeOffset.UtcNow);
        if (outcome is not (CustomerPullOutcome.CaughtUp or CustomerPullOutcome.MorePending)) return outcome;

        ClearBlocked();
        _environmentErrorLogged = false;                      // N-2: ortam hatası serisi bitti
        if (outcome == CustomerPullOutcome.CaughtUp)
        {
            _tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, licenseKey);
            LogLegacyPaymentJobs();
        }

        // U2: eklenen satırın yankısı ve taşıma/dönüştürmeyle gönderime giren birimler 60 sn'lik
        // gönderim turunu beklemesin (D1/D5 "gönderilmemiş" sayısı da boşalır; dönüştürülen
        // miras satırının yeni Id'si sunucuya gidip devralmayı tetiklesin — S8).
        if (tally.GetValueOrDefault(FeedApplyResult.Inserted) + tally.GetValueOrDefault(FeedApplyResult.Rekeyed)
            + tally.GetValueOrDefault(FeedApplyResult.Converted) > 0
            && !await TryPushAsync(ct).ConfigureAwait(false))
            pushOk = false;

        return pushOk ? outcome : CustomerPullOutcome.Failed;
    }

    /// <returns>Sonuç; durma/meşgulde o sayfanın uygulandığı gönderim filigranı ve takılan öğe.</returns>
    private async Task<PassResult> PullPassAsync(
        string licenseKey, Guid licenseId, Dictionary<FeedApplyResult, int> tally, CancellationToken ct)
    {
        var after = _cursors.Get(CursorName, licenseKey)?.Seq ?? 0L;
        try
        {
            var failing = _sync.GetFeedFailureIds();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_pagesThisRound >= MaxPagesPerRound) return new(CustomerPullOutcome.MorePending);
                var page = await _api.GetWpfCustomerChangesAsync(licenseId, after, PageSize, ct).ConfigureAwait(false);
                _pagesThisRound++;
                _offlineLogged = false;                       // M-8: sunucuya ulaşıldı

                if (page.CursorReset)
                {
                    // Sunucu imleci geçersiz buldu: veritabanı yedekten dönmüş/kopyalanmış
                    // olabilir, son gönderilenleri kaybetmiş olabilir. Akış baştan (gelen
                    // sayfa zaten baştan), gönderim de baştan.
                    _log.LogWarning("Müşteri akışı imleci sunucuda geçersiz (seq {After}); akış ve gönderim baştan", after);
                    await _push.RewindAsync(licenseKey, ct).ConfigureAwait(false);
                    after = 0;
                    _cursors.Upsert(CursorName, licenseKey, seq: 0);
                }

                if (page.Items.Count == 0) break;

                var pushWatermark = _push.Watermark(licenseKey);
                var now = _clock.UnixNow();
                for (var i = 0; i < page.Items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = page.Items[i];
                    var itemId = item.Id.ToString("N");
                    FeedApplyResult? applied = null;
                    var guardRecovered = false;
                    while (applied is null)
                    {
                        try
                        {
                            applied = Apply(item, pushWatermark, now);
                        }
                        catch (Exception ex) when (IsGuardLeak(ex) && !guardRecovered)
                        {
                            // M-1: başka bir yolun sızdırdığı kilit satırı (tur başındaki temizlikten
                            // sonra). Öğeye özgü değil: deneme sayılmaz. Temizlenir, öğe bir kez daha
                            // denenir; temizlik fırlatırsa (bu catch'in dışına) tur biter.
                            guardRecovered = true;
                            var cleared = _sync.ClearStaleGuards();
                            if (cleared > 0)
                                _log.LogError(ex,
                                    "SyncApplyGuard'da tur ortasında kalmış {Count} kilit satırı silindi (öğe {ItemId}) — bir yol kilit satırını bırakmadan commit etti; öğe yeniden deneniyor",
                                    cleared, itemId);
                            else
                                _log.LogError(ex,
                                    "SyncApplyGuard kilit satırı eklenemedi ama kalmış satır bulunamadı (öğe {ItemId}) — öğe yeniden deneniyor",
                                    itemId);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && !IsNotItemSpecific(ex))
                        {
                            // U10: uygulanamayan öğe. Deneme kalıcı sayılır (yeniden başlatma sıfırlamaz).
                            // Bozulma (SQLITE_CORRUPT) da burada: çoğu zaman öğeye özgüdür (ör. FTS5 dış
                            // içerik dizini YALNIZ o satır için tutarsız → CORRUPT_VTAB); geçici sayılsaydı akışı
                            // kayıtsız ve görünmez biçimde sonsuza dek durdururdu (N-1).
                            var corrupt = IsCorruption(ex);
                            var attempts = _sync.RecordFeedFailure(itemId, item.ChangeSeq, $"{ex.GetType().Name}: {ex.Message}", now);
                            if (attempts < MaxAttemptsBeforeSkip)
                            {
                                _cursors.Upsert(CursorName, licenseKey, seq: after);
                                // M-2: yığın izi yalnız bu (Id, ChangeSeq) çiftinin ilk hatasında.
                                if (corrupt)
                                    _log.LogError(attempts == 1 ? ex : null,
                                        "Müşteri akışı öğesi {ItemId} (seq {Seq}) uygulanırken veritabanı bozulma hatası (SQLITE_CORRUPT) — deneme {Attempts}/{Max}. {Hint}",
                                        itemId, item.ChangeSeq, attempts, MaxAttemptsBeforeSkip, CorruptionHint);
                                else if (attempts == 1)
                                    _log.LogWarning(ex,
                                        "Müşteri akışı öğesi {ItemId} (seq {Seq}) uygulanamadı — deneme {Attempts}/{Max}, sonraki turda yeniden",
                                        itemId, item.ChangeSeq, attempts, MaxAttemptsBeforeSkip);
                                else
                                    _log.LogWarning(
                                        "Müşteri akışı öğesi {ItemId} (seq {Seq}) yine uygulanamadı ({Error}) — deneme {Attempts}/{Max}",
                                        itemId, item.ChangeSeq, ex.GetType().Name, attempts, MaxAttemptsBeforeSkip);
                                return new(CustomerPullOutcome.Failed);
                            }
                            _sync.MarkFeedItemSkipped(itemId, now);
                            _log.LogError(
                                "Müşteri akışı öğesi {ItemId} (seq {Seq}) {Max} turda uygulanamadı ({Error}) — ATLANDI; durum satırı uyarı gösterir{Hint}",
                                itemId, item.ChangeSeq, MaxAttemptsBeforeSkip, ex.GetType().Name,
                                corrupt ? ". " + CorruptionHint : "");
                            applied = FeedApplyResult.Skipped;
                        }
                    }
                    var result = applied.Value;

                    if (result is FeedApplyResult.Stalled or FeedApplyResult.Busy)
                    {
                        _cursors.Upsert(CursorName, licenseKey, seq: after);
                        ApplyPurgesAhead(page.Items, i + 1, tally, ct);
                        var reason = result == FeedApplyResult.Stalled ? SyncBlockReason.Stalled : SyncBlockReason.Busy;
                        return new(result == FeedApplyResult.Stalled ? CustomerPullOutcome.Stalled : CustomerPullOutcome.Busy,
                            pushWatermark, new BlockKey(itemId, item.ChangeSeq, reason));
                    }
                    // Aynı Id'nin daha yeni bir değişikliği uygulandı: eski hata kaydı ve uyarı kalkar.
                    if (result != FeedApplyResult.Skipped && failing.Contains(itemId))
                        _sync.ClearFeedFailure(itemId);
                    if (_block?.ItemId == itemId) ClearBlocked();
                    tally[result] = tally.GetValueOrDefault(result) + 1;
                    after = item.ChangeSeq;
                    _feedAdvancedThisRound = true;
                }
                after = page.NextAfterSeq;
                _cursors.Upsert(CursorName, licenseKey, seq: after);
            }
        }
        catch (OperationCanceledException)
        {
            // M-6: uygulanan öğeler yeniden uygulanmasın diye imleç kaydedilir (yine de zararsız olurdu).
            try { _cursors.Upsert(CursorName, licenseKey, seq: after); }
            catch (Exception saveEx) when (saveEx is not OperationCanceledException)
            {
                _log.LogDebug(saveEx, "İptalde müşteri akışı imleci kaydedilemedi");
            }
            throw;
        }
        catch (Exception ex)
        {
            _cursors.Upsert(CursorName, licenseKey, seq: after);
            LogRoundFailure(ex, after, tally);
            return new(CustomerPullOutcome.Failed);
        }
        return new(CustomerPullOutcome.CaughtUp);
    }

    private FeedApplyResult Apply(WpfCustomerChangeItem item, long pushWatermark, long now)
    {
        var id = item.Id.ToString("N");

        // 1) Kopya satırı YALNIZ yönlendirmedir (S12): PurgedAt'e de alanlara da bakılmaz.
        if (item.MergedIntoId is { } target)
            return _sync.ApplyFeedRedirect(id, target.ToString("N"), pushWatermark, now);

        // 2) Geçici satır (kural 7, U9) — silinmiş olsun olmasın: yerelde ASLA açılmaz, hiçbir
        //    satır ona taşınmaz; yalnız aynı Id'li miras satırı dönüştürülür. Silme kimliğe
        //    YAYILMAZ (sahiplenenin silme talebi gerçek müşteriyi silmesin); silinmişse beyan
        //    sunucuda boşaltıldı → bilinmiyor (null).
        if (item.CreatedByShopper)
            return _sync.ApplyProvisional(id, item.PurgedAt is null ? ToServerCustomer(item).Fields : null, now);

        // 3) Silinmiş asıl kayıt: kimlik geneli karar, mezar taşı. Yerelde satır yoksa AÇILMAZ
        //    (eski ingest'in davranışı); karar mezar taşında kalır.
        if (item.PurgedAt is { } purgedAt)
            return ApplyPurge(item, purgedAt);

        // 4) Asıl kayıt.
        return _sync.ApplyServerCustomer(ToServerCustomer(item), pushWatermark, now);
    }

    /// <summary>Yalnız yeni bir şey yapan silme <see cref="FeedApplyResult.Purged"/> sayılır (M-4):
    /// CursorReset tekrarında zaten uygulanmış silme "KVKK silme" günlüğünü yinelemez.</summary>
    private FeedApplyResult ApplyPurge(WpfCustomerChangeItem item, DateTimeOffset purgedAt)
    {
        _customers.RecordPurge(item.Platform, item.Username, purgedAt.ToUnixTimeSeconds(), out var changed);
        return changed ? FeedApplyResult.Purged : FeedApplyResult.Unchanged;
    }

    /// <summary>
    /// I-1: takılan öğenin ilerisindeki KVKK silmeleri (yalnız geçici olmayan asıl kayıt silmeleri —
    /// kopya satırı ve geçici satır sıradaki işlemlerini bekler) imleç İLERLEMEDEN uygulanır. İmleç
    /// oraya varınca yeniden uygulanır (zararsız, günlük yinelenmez). Başarısızlık turu değiştirmez:
    /// öğe sırası gelince normal yolundan (U10 dahil) uygulanır.
    /// </summary>
    private void ApplyPurgesAhead(IReadOnlyList<WpfCustomerChangeItem> items, int from,
        Dictionary<FeedApplyResult, int> tally, CancellationToken ct)
    {
        for (var i = from; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested) return;
            var item = items[i];
            if (item.MergedIntoId is not null || item.CreatedByShopper || item.PurgedAt is not { } purgedAt) continue;
            try
            {
                var result = ApplyPurge(item, purgedAt);
                tally[result] = tally.GetValueOrDefault(result) + 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(
                    "Takılan akışın ilerisindeki silme öğesi {ItemId} şimdi uygulanamadı ({Error}); imleç oraya varınca yeniden denenir",
                    item.Id.ToString("N"), ex.GetType().Name);
                return;
            }
        }
    }

    /// <summary>Gönderim turu. HTTP hatası gönderimin içinde kalır (imleç ilerlemez); buraya
    /// çıkan yalnız yerel hata (ör. <c>GetForPush</c>) — tur başarısız sayılır, servis düşmez.</summary>
    /// <param name="countsAsCatchUpProgress">Son inceleme M-1: bkz. <see cref="WpfCustomerProjectionSyncService.SyncOnceAsync"/>.</param>
    private async Task<bool> TryPushAsync(CancellationToken ct, bool countsAsCatchUpProgress = false)
    {
        try
        {
            await _push.SyncOnceAsync(ct, countsAsCatchUpProgress).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Müşteri gönderimi yerel hatayla düştü; tur başarısız sayılır, akış sürer");
            return false;
        }
    }

    /// <summary>M-3: akış hatası kayıtları ve takılma durumu lisansa bağlı değil — önceki turdan
    /// farklı bir lisans anahtarı görülünce silinir (imleçler zaten anahtara bağlı). Yetişme durumu
    /// da sıfırlanır (C10): yeni lisansın form oynatması kendi akışını bekler. Süreçte ilk görülen
    /// lisansta (N-3) ve lisanssız bir turdan sonra AYNI lisansla dönüşte (çıkış → giriş) yalnız
    /// izleme yeniden başlar; yetişme bilgisi aynı lisansın olduğu için kalır.</summary>
    private void OnLicenseSeen(string licenseKey)
    {
        var afterGap = _licenseGap;
        _licenseGap = false;
        if (_lastLicenseKey is null)
        {
            // N-3: lisans bu süreçte İLK KEZ görüldü — açılışın ilk turu ya da çalışırken deneme →
            // lisanslı. İzleme şimdi başlar: lisanssız geçen süre durum satırında "Çevrimdışı" /
            // "Gönderilemiyor" sayılmasın. Hata kayıtları silinmez (süreçler arası kalıcı uyarı).
            _tracker.RestartTracking();
            _lastLicenseKey = licenseKey;
            return;
        }
        if (string.Equals(_lastLicenseKey, licenseKey, StringComparison.Ordinal))
        {
            if (afterGap) _tracker.RestartTracking();
            return;
        }
        // Bellekte, düşemez — aşağıdaki silme başarısız olup sonraki turda yinelense de zararsız.
        _tracker.ResetForLicenseChange();
        try
        {
            var cleared = _sync.ClearFeedFailures();
            ClearBlocked();
            _lastLegacyJobs = -1;
            _offlineLogged = false;
            _lastLicenseKey = licenseKey;
            if (cleared > 0)
                _log.LogInformation("Lisans değişti — önceki lisansın {Count} müşteri akışı hata kaydı silindi", cleared);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Lisans değişiminde müşteri akışı hata kayıtları silinemedi; sonraki turda yeniden");
        }
    }

    /// <summary>U6 (süreç başına bir kez) ve U15 (her tur). Hataları turu düşürmez.</summary>
    private void RunLocalMaintenance()
    {
        if (!_identityKeysHealed)
        {
            try
            {
                // Eski sürüme dönüşte açılmış satırların ve mezar taşlarının kimlik anahtarı (U6, U16).
                var healed = _sync.HealIdentityKeys();
                _identityKeysHealed = true;
                if (healed > 0) _log.LogWarning("Kimlik anahtarı eksik {Count} müşteri/mezar taşı satırı onarıldı", healed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Kimlik anahtarı onarımı başarısız; sonraki turda yeniden");
            }
        }

        try
        {
            // U15: kalmış kilit satırı bütün damgalamayı ve gönderimi sessizce kapatır.
            var staleGuards = _sync.ClearStaleGuards();
            if (staleGuards > 0)
                _log.LogError(
                    "SyncApplyGuard'da kalmış {Count} kilit satırı silindi — bu süre boyunca müşteri düzenlemeleri damgalanmadı ve gönderilmedi",
                    staleGuards);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Kalmış kilit satırı temizliği başarısız; sonraki turda yeniden");
        }
    }

    // ── takılan öğe (I-1) ────────────────────────────────────────────────

    private void NoteBlocked(BlockKey key)
    {
        var now = DateTimeOffset.UtcNow;
        if (_block == key)
            _blockRounds++;
        else
        {
            // Başka bir takılma (başka öğe, değişiklik ya da sebep): sayım baştan; eski durum artık
            // bu takılmayı anlatmıyor.
            if (_block is not null) _tracker.SetBlockedOn(null);
            _block = key;
            _blockRounds = 1;
            _blockSince = now;
        }

        if (_blockRounds == BlockedRoundsBeforeWarning)
            _log.LogWarning(
                "Müşteri akışı {Rounds} turdur öğe {ItemId} (seq {Seq}) için bekliyor: {Reason} — arkasındaki değişiklikler inmiyor (KVKK silmeleri aynı sayfada uygulanıyor)",
                _blockRounds, key.ItemId, key.ChangeSeq,
                key.Reason == SyncBlockReason.Stalled ? "gönderilemeyen yerel kopya (durma)" : "ödeme akışındaki müşteri");
        else if (_blockRounds < BlockedRoundsBeforeWarning)
            _log.LogDebug("Müşteri akışı öğe {ItemId} (seq {Seq}) için bekliyor ({Reason}), tur {Rounds}",
                key.ItemId, key.ChangeSeq, key.Reason, _blockRounds);

        // I-2: eşikten sonra HER takılı turda tazelenir — son görülme anı durum satırının tazelik ölçüsü.
        if (_blockRounds >= BlockedRoundsBeforeWarning)
            _tracker.SetBlockedOn(new SyncBlock(key.ItemId, key.Reason, _blockSince, now));
    }

    private void ClearBlocked()
    {
        if (_block is null) return;
        _block = null;
        _blockRounds = 0;
        _tracker.SetBlockedOn(null);
    }

    // ── hata sınıfları ──────────────────────────────────────────────────

    /// <summary>Öğeye özgü OLMAYAN hata (M-1): deneme sayılmaz, öğe atlanmaz, tur başarısız biter.
    /// Yerel ortam (kilit çekişmesi, disk dolu, G/Ç, salt okunur, açılamayan dosya), sızmış
    /// kilit satırı ve kilit yeniden girişi (programlama hatası).</summary>
    private static bool IsNotItemSpecific(Exception ex)
        => IsEnvironmentError(ex) || IsGuardLeak(ex) || ex is CustomerBusySetReentrancyException;

    /// <summary>SQLite birincil sonuç kodları: BUSY 5, LOCKED 6 (kilit çekişmesi — başka bir yazım
    /// yazma kilidini bütçeden uzun tuttu; gönderimin sınıflandırmasıyla aynı), NOMEM 7, READONLY 8,
    /// IOERR 10, FULL 13, CANTOPEN 14, PROTOCOL 15. CORRUPT (11) ve NOTADB (26) BURADA DEĞİL (N-1):
    /// bozulma çoğu zaman öğeye özgüdür ve U10 ile sayılır — bkz. <see cref="IsCorruption"/>.</summary>
    private static bool IsEnvironmentError(Exception ex)
        => ex is SqliteException s && (s.SqliteErrorCode & 0xFF) is 5 or 6 or 7 or 8 or 10 or 13 or 14 or 15;

    /// <summary>SQLITE_CORRUPT ve genişletilmiş kodları (ör. CORRUPT_VTAB 267).</summary>
    private static bool IsCorruption(Exception ex)
        => ex is SqliteException s && (s.SqliteErrorCode & 0xFF) == 11;

    /// <summary>Kilit satırı zaten var: <c>SyncApplyScope.Begin</c>'in eklemesi birincil anahtara çarptı
    /// (SQLITE_CONSTRAINT 19, genişletilmiş kodlar dahil).</summary>
    private static bool IsGuardLeak(Exception ex)
        => ex is SqliteException s && (s.SqliteErrorCode & 0xFF) == 19
           && s.Message.Contains("SyncApplyGuard", StringComparison.Ordinal);

    /// <summary>Sunucunun genel hız sınırı gövdesiz 429 döner → <c>http-429</c>.</summary>
    private static bool IsRateLimited(Exception ex)
        => ex is ValidationException { Code: "http-429" };

    private void LogRoundFailure(Exception ex, long after, Dictionary<FeedApplyResult, int> tally)
    {
        if (IsRateLimited(ex))
            // Beklenen yük durumu (C5 incelemesi): yalnız sayılar, yığın izi yok.
            _log.LogWarning(
                "Müşteri akışı sunucu hız sınırına takıldı (429) — bu tur {Applied} öğe uygulandı, kalan sonraki turda",
                tally.Values.Sum());
        else if (ex is LicenseApiNetworkException)
        {
            // M-8: çevrimdışıyken her 30 sn'de bir yığın izi yazılmaz; sunucuya ulaşılınca sıfırlanır.
            if (!_offlineLogged)
            {
                _offlineLogged = true;
                _log.LogWarning(ex, "Customer changes pull failed at seq {After} (ağ); will retry", after);
            }
            else
                _log.LogWarning("Müşteri akışı hâlâ sunucuya ulaşamıyor (seq {After}); sonraki turda yeniden", after);
        }
        else if (IsEnvironmentError(ex))
        {
            // N-2: ortam hatası serisinde yığın izi bir kez; başarılı bir turda sıfırlanır.
            if (!_environmentErrorLogged)
            {
                _environmentErrorLogged = true;
                _log.LogWarning(ex, "Customer changes pull failed at seq {After} (yerel ortam hatası); will retry", after);
            }
            else
                _log.LogWarning(
                    "Müşteri akışı yine yerel ortam hatasına takıldı (SQLite {Code}, seq {After}); sonraki turda yeniden",
                    ((SqliteException)ex).SqliteErrorCode, after);
        }
        else if (ex is CustomerBusySetReentrancyException)
        {
            // M-1: programlama hatası işareti — bir kez yığın iziyle.
            if (!_reentrancyLogged)
            {
                _reentrancyLogged = true;
                _log.LogError(ex, "Müşteri akışında CustomerBusySet kilidine yeniden girildi (programlama hatası); tur bitti, öğe deneme sayılmadı");
            }
            else
                _log.LogDebug("Müşteri akışında CustomerBusySet kilidine yine yeniden girildi (seq {After})", after);
        }
        else
            _log.LogWarning(ex, "Customer changes pull failed at seq {After}; will retry", after);
    }

    private static ServerCustomer ToServerCustomer(WpfCustomerChangeItem i) => new(
        i.Id.ToString("N"), i.Platform, i.Username, i.CreatedByShopper,
        new CustomerSyncState
        {
            Username = i.Username,
            FullName = i.FullName, FullNameChangedAt = Ms(i.FullNameChangedAt),
            DisplayName = i.DisplayName, DisplayNameChangedAt = Ms(i.DisplayNameChangedAt),
            GroupId = i.GroupId, GroupIdChangedAt = Ms(i.GroupIdChangedAt),
            Address = i.Address, City = i.City, District = i.District, AddressChangedAt = Ms(i.AddressChangedAt),
            RecipientPaysActive = i.RecipientPaysActive, RecipientPaysChangedAt = Ms(i.RecipientPaysChangedAt),
            Phone = i.Phone, PhoneChangedAt = Ms(i.PhoneChangedAt),
            Email = i.Email, EmailChangedAt = Ms(i.EmailChangedAt),
            Tckn = i.Tckn, TcknChangedAt = Ms(i.TcknChangedAt),
            WhatsAppConsent = i.WhatsAppConsent, WhatsAppConsentChangedAt = Ms(i.WhatsAppConsentChangedAt),
            SmsConsent = i.SmsConsent, SmsConsentChangedAt = Ms(i.SmsConsentChangedAt),
            IsBlacklisted = i.IsBlacklisted, BlacklistReason = i.BlacklistReason,
            // Yerelde unix SANİYE (Customer tablosuyla aynı) — damgalar gibi ms değil.
            BlacklistedAt = i.BlacklistedAt?.ToUnixTimeSeconds(), BlacklistChangedAt = Ms(i.BlacklistChangedAt),
            Notes = i.Notes, NotesChangedAt = Ms(i.NotesChangedAt),
        });

    private static long? Ms(DateTimeOffset? d) => d?.ToUnixTimeMilliseconds();

    /// <summary>Tek satır, yalnız sayılar — kişisel veri yok. Silme satırı ayrı: silmenin
    /// sahaya indiğinin tek kanıtı bu günlük (eski ingest'le aynı). Takılma (durma/meşgul) burada
    /// değil: eşiğe kadar Debug, eşikte bir uyarı (I-1).</summary>
    private void LogTally(Dictionary<FeedApplyResult, int> t)
    {
        int N(FeedApplyResult r) => t.GetValueOrDefault(r);
        if (N(FeedApplyResult.Inserted) + N(FeedApplyResult.Updated) + N(FeedApplyResult.Rekeyed)
            + N(FeedApplyResult.Converted) + N(FeedApplyResult.SkippedProvisional) + N(FeedApplyResult.Deferred)
            + N(FeedApplyResult.Skipped) > 0)
            _log.LogInformation(
                "Customer changes: +{Inserted} ~{Updated} ⇄{Rekeyed} (miras dönüştürüldü {Converted}, geçici atlandı {Provisional}, ertelendi {Deferred}, uygulanamayıp atlandı {Poison})",
                N(FeedApplyResult.Inserted), N(FeedApplyResult.Updated), N(FeedApplyResult.Rekeyed),
                N(FeedApplyResult.Converted), N(FeedApplyResult.SkippedProvisional), N(FeedApplyResult.Deferred),
                N(FeedApplyResult.Skipped));
        if (N(FeedApplyResult.Purged) > 0)
            _log.LogInformation("KVKK silme: {Count} silme kararı uygulandı", N(FeedApplyResult.Purged));
    }

    /// <summary>U8: taşıma, ödeme işi kapsam çakışmasında kopyanın anahtarlı işini miras kapsamına
    /// alır; o müşterinin bir sonraki "Ödeme iste"si uzlaştırana kadar açık kalır. Sayı
    /// değiştikçe günlüğe (kalıcı görünürlük durum satırında, D2). Yalnız tanı — hatası turu
    /// düşürmez.</summary>
    private void LogLegacyPaymentJobs()
    {
        try
        {
            var count = _sync.CountOpenKeyedLegacyJobs();
            if (count == _lastLegacyJobs) return;
            if (count > 0)
                _log.LogWarning(
                    "{Count} ödeme işi uzlaştırma bekliyor (miras kapsamında, anahtarlı) — ilgili müşterinin 'Ödeme iste'si uzlaştırır",
                    count);
            _lastLegacyJobs = count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Açık miras ödeme işi sayımı başarısız");
        }
    }

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey) return _cachedLicenseId;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "License resolve failed for customer changes pull");
            return null;
        }
    }
}
