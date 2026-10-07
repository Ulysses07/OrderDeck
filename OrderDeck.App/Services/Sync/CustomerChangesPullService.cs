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
    /// <summary>Sayfa/ağ hatası (429 dahil), uygulanamayan öğe (atlanana kadar), kilit çekişmesi
    /// ya da gönderimin yerel hatası. İmleç son uygulanan öğede.</summary>
    Failed,
    /// <summary>Gönderilmemiş yerel kimlik sahibi (U5) ve durmadan sonraki gönderim onu
    /// götüremedi. İmleç o öğede.</summary>
    Stalled,
    /// <summary>Taşınacak müşteri ödeme akışında (U13). İmleç o öğede, sonraki tur.</summary>
    Busy,
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
/// S11). Durma (U5) ve meşgul müşteri (U13) imleci o öğede bırakır. Uygulanamayan öğe
/// <see cref="MaxAttemptsBeforeSkip"/> turdan sonra atlanır, kaydı ve uyarısı kalır (U10). Yeniden
/// uygulama damga kurallarıyla zararsız. Sayfa/ağ hatasında (429 dahil — sunucu IP başına dakikada
/// ~100 istek kabul eder) tur başarısız, sonraki tur kaydedilen sayfadan sürer.</para>
///
/// <para><b>Durma (U5):</b> asıl kayıt geldiğinde yerelde aynı kimlikte, HENÜZ GÖNDERİLMEMİŞ
/// başka Id'li satır varsa taşınmaz: önce gönderim koşar (sunucu o satırı kopya olarak bağlar,
/// verisini birleştirir, siparişlerini taşır), akış aynı turda bir kez daha denenir — yalnız
/// gönderim filigranı ilerlediyse. Gönderim HTTP hatasında fırlatmadan döner; filigran
/// ilerlemediyse ikinci deneme aynı öğede yine dururdu: istek harcanmaz, tur biter.</para>
///
/// <para><b>Gönderimin hatası:</b> gönderim HTTP hatasını kendisi yutar (imleç ilerlemez) ama
/// yerel SQLite hatası çıkabilir. Çağrı korunur: tur başarısız sayılır, akış yine uygulanır (KVKK
/// silmeleri bir gönderim hatasının arkasında beklemez); yetişme kaydı yalnız akışa bakar.</para>
///
/// <para><b>İş parçacığı:</b> bütün <c>await</c>'ler <c>ConfigureAwait(false)</c> — öğe uygulaması
/// <c>CustomerBusySet</c> kilidini eşzamanlı bekler, arayüz iş parçacığında koşmamalı.
/// Aynı anda tek çağıran varsayılır (arka plan işi, 30 sn).</para>
/// </summary>
public sealed class CustomerChangesPullService
{
    public const string CursorName = "customer-changes-in";
    private const int PageSize = 500;

    /// <summary>U10: bu kadar başarısız turdan sonra öğe atlanır (30 sn ritimde ~2,5 dk).</summary>
    internal const int MaxAttemptsBeforeSkip = 5;

    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly CustomerSyncRepository _sync;
    private readonly SyncCursorRepository _cursors;
    private readonly WpfCustomerProjectionSyncService _push;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly SyncStatusTracker _tracker;
    private readonly ILogger<CustomerChangesPullService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private bool _identityKeysHealed;
    private int _lastLegacyJobs = -1;

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
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return CustomerPullOutcome.NoLicense;

        // Gönderimden ÖNCE: kalmış bir kilit satırında gönderimin taşımaları da (SyncApplyScope
        // ikinci kilit satırına çarpar) düşerdi.
        RunLocalMaintenance();

        // Gönderim her çekmeden ÖNCE (eski Açık soru 13). İki bilgisayar aynı yayında yorum
        // okurken yeni yorumcuların satırları sunucuya önce gider; asıl kayıt geldiğinde
        // sahipleri "gönderildi" olur, durma (U5) nadirleşir.
        var pushOk = await TryPushAsync(ct).ConfigureAwait(false);

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null) return CustomerPullOutcome.NoLicense;

        var tally = new Dictionary<FeedApplyResult, int>();
        var (outcome, stalledAtWatermark) =
            await PullPassAsync(licenseKey, licenseId.Value, tally, ct).ConfigureAwait(false);
        if (outcome == CustomerPullOutcome.Stalled)
        {
            // U5: sahibi gönder, akışı aynı turda bir kez daha dene. Gönderim HTTP hatasında
            // fırlatmadan döner — dönüş değeri "başarısız" ile "gönderilecek yok"u ayırmaz;
            // filigran duran öğenin gördüğünden ilerlemediyse ikinci deneme aynı yerde durur.
            if (!await TryPushAsync(ct).ConfigureAwait(false))
                outcome = CustomerPullOutcome.Failed;
            else if (_push.Watermark(licenseKey) > stalledAtWatermark)
                (outcome, _) = await PullPassAsync(licenseKey, licenseId.Value, tally, ct).ConfigureAwait(false);
        }
        LogTally(tally, stalled: outcome == CustomerPullOutcome.Stalled);
        if (outcome != CustomerPullOutcome.CaughtUp) return outcome;

        _tracker.MarkPullSucceeded(DateTimeOffset.UtcNow);
        LogLegacyPaymentJobs();

        // U2: eklenen satırın yankısı ve taşıma/dönüştürmeyle gönderime giren birimler 60 sn'lik
        // gönderim turunu beklemesin (D1/D5 "gönderilmemiş" sayısı da boşalır; dönüştürülen
        // miras satırının yeni Id'si sunucuya gidip devralmayı tetiklesin — S8).
        if (tally.GetValueOrDefault(FeedApplyResult.Inserted) + tally.GetValueOrDefault(FeedApplyResult.Rekeyed)
            + tally.GetValueOrDefault(FeedApplyResult.Converted) > 0
            && !await TryPushAsync(ct).ConfigureAwait(false))
            pushOk = false;

        return pushOk ? CustomerPullOutcome.CaughtUp : CustomerPullOutcome.Failed;
    }

    /// <returns>Sonuç ve — durma/meşgulde — o sayfanın uygulandığı gönderim filigranı.</returns>
    private async Task<(CustomerPullOutcome Outcome, long PushWatermark)> PullPassAsync(
        string licenseKey, Guid licenseId, Dictionary<FeedApplyResult, int> tally, CancellationToken ct)
    {
        var after = _cursors.Get(CursorName, licenseKey)?.Seq ?? 0L;
        try
        {
            var failing = _sync.GetFeedFailureIds();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _api.GetWpfCustomerChangesAsync(licenseId, after, PageSize, ct).ConfigureAwait(false);

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
                foreach (var item in page.Items)
                {
                    var itemId = item.Id.ToString("N");
                    FeedApplyResult result;
                    try
                    {
                        result = Apply(item, pushWatermark, now);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && !IsLockContention(ex))
                    {
                        // U10: uygulanamayan öğe. Deneme kalıcı sayılır (yeniden başlatma sıfırlamaz).
                        var attempts = _sync.RecordFeedFailure(itemId, item.ChangeSeq, $"{ex.GetType().Name}: {ex.Message}", now);
                        if (attempts < MaxAttemptsBeforeSkip)
                        {
                            _cursors.Upsert(CursorName, licenseKey, seq: after);
                            _log.LogWarning(ex,
                                "Müşteri akışı öğesi {ItemId} (seq {Seq}) uygulanamadı — deneme {Attempts}/{Max}, sonraki turda yeniden",
                                itemId, item.ChangeSeq, attempts, MaxAttemptsBeforeSkip);
                            return (CustomerPullOutcome.Failed, pushWatermark);
                        }
                        _sync.MarkFeedItemSkipped(itemId, now);
                        _log.LogError(ex,
                            "Müşteri akışı öğesi {ItemId} (seq {Seq}) {Max} turda uygulanamadı — ATLANDI; durum satırı uyarı gösterir",
                            itemId, item.ChangeSeq, MaxAttemptsBeforeSkip);
                        result = FeedApplyResult.Skipped;
                    }

                    if (result is FeedApplyResult.Stalled or FeedApplyResult.Busy)
                    {
                        _cursors.Upsert(CursorName, licenseKey, seq: after);
                        return (result == FeedApplyResult.Stalled ? CustomerPullOutcome.Stalled : CustomerPullOutcome.Busy,
                            pushWatermark);
                    }
                    // Aynı Id'nin daha yeni bir değişikliği uygulandı: eski hata kaydı ve uyarı kalkar.
                    if (result != FeedApplyResult.Skipped && failing.Contains(itemId))
                        _sync.ClearFeedFailure(itemId);
                    tally[result] = tally.GetValueOrDefault(result) + 1;
                    after = item.ChangeSeq;
                }
                after = page.NextAfterSeq;
                _cursors.Upsert(CursorName, licenseKey, seq: after);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _cursors.Upsert(CursorName, licenseKey, seq: after);
            if (IsRateLimited(ex))
                // Beklenen yük durumu (C5 incelemesi): yalnız sayılar, yığın izi yok.
                _log.LogWarning(
                    "Müşteri akışı sunucu hız sınırına takıldı (429) — bu tur {Applied} öğe uygulandı, kalan sonraki turda",
                    tally.Values.Sum());
            else
                _log.LogWarning(ex, "Customer changes pull failed at seq {After}; will retry", after);
            return (CustomerPullOutcome.Failed, 0L);
        }
        return (CustomerPullOutcome.CaughtUp, 0L);
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
        {
            _customers.RecordPurge(item.Platform, item.Username, purgedAt.ToUnixTimeSeconds());
            return FeedApplyResult.Purged;
        }

        // 4) Asıl kayıt.
        return _sync.ApplyServerCustomer(ToServerCustomer(item), pushWatermark, now);
    }

    /// <summary>Gönderim turu. HTTP hatası gönderimin içinde kalır (imleç ilerlemez); buraya
    /// çıkan yalnız yerel hata (ör. <c>GetForPush</c>) — tur başarısız sayılır, servis düşmez.</summary>
    private async Task<bool> TryPushAsync(CancellationToken ct)
    {
        try
        {
            await _push.SyncOnceAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Müşteri gönderimi yerel hatayla düştü; tur başarısız sayılır, akış sürer");
            return false;
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

    /// <summary>Kilit çekişmesi (başka bir yazım yazma kilidini bütçeden uzun tuttu) geçicidir:
    /// deneme sayılmaz, tur başarısız olur, sonraki tur aynı öğeden sürer (U10). Gönderimin
    /// sınıflandırmasıyla aynı.</summary>
    private static bool IsLockContention(Exception ex)
        => ex is SqliteException { SqliteErrorCode: 5 or 6 };   // SQLITE_BUSY, SQLITE_LOCKED

    /// <summary>Sunucunun genel hız sınırı gövdesiz 429 döner → <c>http-429</c>.</summary>
    private static bool IsRateLimited(Exception ex)
        => ex is ValidationException { Code: "http-429" };

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
    /// sahaya indiğinin tek kanıtı bu günlük (eski ingest'le aynı).</summary>
    private void LogTally(Dictionary<FeedApplyResult, int> t, bool stalled)
    {
        int N(FeedApplyResult r) => t.GetValueOrDefault(r);
        if (N(FeedApplyResult.Inserted) + N(FeedApplyResult.Updated) + N(FeedApplyResult.Rekeyed)
            + N(FeedApplyResult.Converted) + N(FeedApplyResult.SkippedProvisional) + N(FeedApplyResult.Deferred)
            + N(FeedApplyResult.Skipped) > 0 || stalled)
            _log.LogInformation(
                "Customer changes: +{Inserted} ~{Updated} ⇄{Rekeyed} (miras dönüştürüldü {Converted}, geçici atlandı {Provisional}, ertelendi {Deferred}, uygulanamayıp atlandı {Poison}){Stalled}",
                N(FeedApplyResult.Inserted), N(FeedApplyResult.Updated), N(FeedApplyResult.Rekeyed),
                N(FeedApplyResult.Converted), N(FeedApplyResult.SkippedProvisional), N(FeedApplyResult.Deferred),
                N(FeedApplyResult.Skipped),
                stalled ? " — gönderilmemiş yerel kopya için durdu" : "");
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
