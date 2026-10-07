using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF lokal Customer kayıtlarının LicenseServer'a periyodik delta sync'i.
/// Müşteri (shopper) app kullanıcısı bir yayıncıya bağlanırken (LicenseId,
/// Platform, Username) ile match yapılır — bu match için server-side projection
/// gerekli. Server retroactive match'i sync endpoint'inde drive-by yapar.
///
/// <para><b>Biçim 2 (çoklu bilgisayar, Bölüm C, kural 5):</b> her öğe tam alanlarıyla ve
/// BİRİM damgalarıyla gider (yerelde unix ms; sunucu birim başına son-yazan-kazanır
/// uygular). Satırlar <see cref="CustomerSyncRepository.GetForPush"/>'tan okunur. Ad ve takma
/// ad ayrı birimlerdir: R3-02'nin "FullName boşsa DisplayName'e düş" geri düşüşü yalnız eski
/// (biçim 1) sözleşmeydi — biçim 2'de takma ad gerçek ad diye gitmez.</para>
///
/// <para><b>İmleç:</b> SyncCursor(<see cref="CursorName"/> = "customer-projection-out-v2",
/// LicenseKey).Seq — Customer.SyncSeq, tablo geneli kesin artan ama ARDIŞIK OLMAYAN sayaç
/// (N03-g; göç 036/045 tetikleyicileri ve silinmeye dayanıklı sayaç). Önceki sürümün imleci
/// ("customer-projection-out") ayrı kalır (U15). R6-04: imleç veriyle aynı SQLite dosyasında,
/// lisans anahtarına bağlı. R9-D01: settings.json'daki eski alan tohum olarak da okunmuyor —
/// tek kalıcı kaynak SyncCursor satırı, satır yoksa tam tarama. Parti 500; tur, satırlar
/// tükenene kadar parti parti sürer.</para>
///
/// <para><b>Elenen satırlar</b> gönderilmez ama imleç üstlerinden geçer: yerelde silinmiş
/// (<c>PurgedAt</c>), GUID olmayan Id, sunucunun reddedeceği Platform/Username.</para>
///
/// <para><b>Yönlendirmeler:</b> sunucu gönderilen bir Id'yi bir asıl kaydın kopyası olarak
/// bağladıysa yanıt bunu söyler; yerel satır asıl kayda taşınır
/// (<see cref="CustomerSyncRepository.RekeyToLocal"/>) — ödeme akışındaki müşteri hariç (U13).</para>
///
/// <para><b>Tek tur kilidi:</b> zamanlayıcı, akış servisinin durma sonrası çağrısı (C7),
/// "gönder ve kapat" (D5) ve imleç geri sarma (C7) aynı imleci yazar.</para>
///
/// <para><b>İş parçacığı:</b> bütün <c>await</c>'ler <c>ConfigureAwait(false)</c>. Taşıma
/// <c>CustomerBusySet</c> kilidini EŞZAMANLI bekler ve o kilit bir senkron öğesi SQLite yazma
/// kilidini beklerken tutulur (yazma kilidi bütçesi kadar sürebilir); arayüz iş parçacığından
/// başlatılan bir tur (D5'in kapanış gönderimi) devamlarını orada koşsaydı pencere donardı.</para>
///
/// LicenseId resolution: GetMyLicensesAsync ile key → Guid (cached).
/// </summary>
public sealed class WpfCustomerProjectionSyncService
{
    private const int BatchSize = 500;

    /// <summary>Biçim-2 gönderim imleci (U15). Önceki sürümün imleci "customer-projection-out"
    /// ayrı kalır: geri dönüşte o sürüm kendi imleciyle sürer; yeniden yükseltmede bu imleç, eski
    /// sürümün aradaki (tetikleyicinin damgaladığı) düzenlemelerini biçim 2 ile yeniden gönderir.
    /// D1'in sayacı da bu sabiti okur.</summary>
    public const string CursorName = "customer-projection-out-v2";

    private readonly LicenseApiClient _api;
    private readonly CustomerSyncRepository _sync;
    private readonly SyncCursorRepository _cursors;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly IClock _clock;
    private readonly ILogger<WpfCustomerProjectionSyncService> _log;

    /// <summary>Tek tur kuralı: zamanlayıcı, akış servisinin durma sonrası çağrısı (C7),
    /// "gönder ve kapat" (D5) ve imleç geri sarma (C7) aynı imleci yazıyor; üst üste
    /// binselerdi aynı parti iki kez gider ya da geri sarmayı ileri yazım ezerdi.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public WpfCustomerProjectionSyncService(
        LicenseApiClient api,
        CustomerSyncRepository sync,
        SyncCursorRepository cursors,
        ICurrentLicenseProvider licenseProvider,
        IClock clock,
        ILogger<WpfCustomerProjectionSyncService> log)
    {
        _api             = api;
        _sync            = sync;
        _cursors         = cursors;
        _licenseProvider = licenseProvider;
        _clock           = clock;
        _log             = log;
    }

    // Sunucunun kabul sınırları (LicensesWpfCustomersSyncController "Validate
    // input items minimally" bloğu). Burada AYNEN tekrarlanıyor, çünkü tek amaç
    // "sunucu bunu reddeder mi?" sorusunu göndermeden önce cevaplamak. Sunucu
    // sınırı bir gün gevşerse buradaki eleme fazladan kayıt atlar (hata değil,
    // yalnız gecikme); sıkılaşırsa yine parti reddedilir ve bu sabitler
    // güncellenmeli — bu yüzden değerler kaynağıyla birlikte anılıyor.
    private const int MaxPlatformLength = 32;
    private const int MaxUsernameLength = 128;

    // [NotNullWhen(true)]: "kabul edilebilir" demek aynı zamanda "boş değil"
    // demek. Bu olmadan aşağıdaki uyarı satırındaki `?.` derleyiciyi şüpheye
    // düşürüp WpfCustomerSyncItem kurulumunda CS8604 veriyor.
    private static bool IsServerAcceptable(
        [NotNullWhen(true)] string? platform,
        [NotNullWhen(true)] string? username)
        => !string.IsNullOrWhiteSpace(platform) && platform.Length <= MaxPlatformLength
        && !string.IsNullOrWhiteSpace(username) && username.Length <= MaxUsernameLength;

    /// <summary>
    /// Tek gönderim turu: depo BatchSize'dan az satır döndürene kadar parti parti gönderir;
    /// gönderilen toplam müşteri sayısını döner. İmleç her başarılı partiden sonra SyncCursor
    /// tablosunda ilerler; bir parti başarısız olursa tur, imleci o partinin ötesine taşımadan
    /// biter. Aynı anda tek tur koşar (<see cref="_gate"/>).
    /// </summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SyncCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Gönderim imlecini sıfırlar: sonraki tur bütün müşterileri yeniden
    /// gönderir. Akış servisi sunucudan CursorReset alınca çağırır (sunucu yedekten
    /// dönmüş, son gönderilenleri kaybetmiş olabilir — kural 8).</summary>
    public async Task RewindAsync(string licenseKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { _cursors.Upsert(CursorName, licenseKey, seq: 0); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Şu anki gönderim imleci. Akış servisi "bu yerel satır sunucuya gitti mi" sorusunu
    /// bununla cevaplar (U3, U5). Satır yoksa 0 = hiçbir şey gitmedi → tam tarama.
    /// R9-D01: eski settings alanı tohum OLMUYOR — settings dosyası yedeğin dışında yaşadığı
    /// için hangi veri nesline ve hangi lisansa ait olduğu kanıtlanamıyor (037 yedeği ilk 038
    /// çalışmasından önce geri yüklenince ileri kalmış değer satırları sonsuza dek atlatıyordu).
    /// Tam tarama güvenli (sunucu gönderimi idempotent, PurgedAt kapılı): bedeli bir tur fazla
    /// trafik.
    /// </summary>
    public long Watermark(string licenseKey) => _cursors.Get(CursorName, licenseKey)?.Seq ?? 0L;

    private async Task<int> SyncCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        var licenseId  = await ResolveLicenseIdAsync(ct).ConfigureAwait(false);
        if (licenseId is null || string.IsNullOrWhiteSpace(licenseKey))
        {
            _log.LogDebug("Customer projection sync skipped — no active license resolved");
            return 0;
        }

        var watermark = Watermark(licenseKey);
        int totalSynced = 0, totalMatches = 0, rekeyed = 0, waiting = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = _sync.GetForPush(watermark, BatchSize);
            if (batch.Count == 0) break;

            var items = new List<WpfCustomerSyncItem>(batch.Count);
            foreach (var c in batch)
            {
                // Yerelde silinmiş satır gönderilmez (C4 kalite incelemesi): akıştan inen KVKK
                // silmesi, silinmiş kimliğe sohbetten açılan satır ya da önceki sürümlerden kalma
                // bayat mezar taşı. Sohbet eklemesindeki mezar taşı boşaltması "[Silindi]"yi şimdi
                // damgasıyla yazar — gitseydi sunucudaki asıl kayıt silinmemişse gerçek takma adı
                // her bilgisayarda ezerdi; dönüştürülen silinmiş miras satırı da sunucuda
                // "[Silindi]" adlı asıl kayıt açardı. Atlama BURADA, GUID olmayan satırlar gibi:
                // imleç üstünden geçer (yoksa silinmiş bir kimlik sahibi akışı U5'te sonsuza dek
                // durdururdu). Kabul edilen sonuç: hiç gönderilmemiş silinmiş satır sunucu Id'sini
                // görmeden yeniden anahtarlanabilir; o Id'yle senkronlanmış sipariş/etiket sunucuda
                // asıl kayda çözülmez — KVKK ile silinmiş kişi için kabul edilebilir.
                if (c.Fields.PurgedAt is not null)
                {
                    _log.LogDebug("Skipping purged customer Id={Id} (yerelde silinmiş — gönderilmez)", c.Id);
                    continue;
                }
                if (!Guid.TryParseExact(c.Id, "N", out var customerGuid))
                {
                    _log.LogWarning("Skipping customer with non-GUID Id={Id}", c.Id);
                    continue;
                }
                // Sunucu boş/aşırı uzun Platform veya Username gördüğünde
                // PARTİNİN TAMAMINI 400'le reddediyor
                // (LicensesWpfCustomersSyncController). Watermark hatada
                // ilerlemediği için tek bir bozuk satır boru hattını KALICI
                // kilitler: aynı parti her turda yeniden gönderilir, arkasındaki
                // herkes rehin kalır ve tek belirti dakikada bir düşen bir
                // uyarı satırı olur. 2026-08-14'te sahada tam olarak bu yaşandı
                // — Facebook App Review onayından önce açılmış, Username'i boş
                // TEK kayıt 565 müşteriyi 12 gün boyunca sunucuya ulaştırmadı.
                //
                // Bu yüzden bozuk satır burada elenir. Sunucudaki doğrulama
                // aynen kalmalı: sınır orada, bu yalnız istemcinin kendi
                // kuyruğunu zehirlememesi. Elenen kayıt zaten eşleşemezdi —
                // sunucu tarafı match'i (LicenseId, Platform, Username) üçlüsüne
                // dayanıyor, bu alanlar boşken eşleşecek bir şey yok.
                if (!IsServerAcceptable(c.Platform, c.Username))
                {
                    _log.LogWarning(
                        "Skipping customer Id={Id}: sunucunun reddedeceği Platform/Username " +
                        "(platform uzunluk {PlatformLength}, username uzunluk {UsernameLength})",
                        c.Id, c.Platform?.Length ?? 0, c.Username?.Length ?? 0);
                    continue;
                }
                items.Add(ToItem(customerGuid, c));
            }

            // If all items in the batch were skipped (purged / invalid rows), advance
            // watermark to prevent an infinite loop, then continue.
            if (items.Count == 0)
            {
                AdvanceWatermark(licenseKey, batch, ref watermark);
                if (batch.Count < BatchSize) break;
                continue;
            }

            WpfCustomerSyncResponse resp;
            try
            {
                resp = await _api.SyncWpfCustomersAsync(licenseId.Value, items, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Customer projection sync batch failed; abandoning this tick");
                return totalSynced; // don't advance watermark on failure
            }
            totalSynced  += resp.Synced;
            totalMatches += resp.RetroactiveMatches;

            // İmleçten ÖNCE: taşıma asıl kaydın SyncSeq'ini ilerletir (U2) → aynı turun
            // sonraki partisinde ya da sonraki turda asıl kaydın Id'siyle gider. Eşik,
            // gönderilen partinin en büyük SyncSeq'i (C4 incelemesi M-7) — imleç değil.
            var (r, w) = ApplyRedirects(resp.Redirects, pushedThroughSeq: batch[^1].SyncSeq);
            rekeyed += r;
            waiting += w;

            AdvanceWatermark(licenseKey, batch, ref watermark);
            if (batch.Count < BatchSize) break; // last page — no more rows
        }

        if (totalSynced > 0 || rekeyed + waiting > 0)
        {
            _log.LogInformation(
                "Customer projection sync: pushed {Synced} (retro matches {Matches}), redirects rekeyed {Rekeyed}, waiting {Waiting}, watermark→{Watermark}",
                totalSynced, totalMatches, rekeyed, waiting, watermark);
        }

        return totalSynced;
    }

    /// <summary>
    /// Biçim-2 öğesi: tam alanlar + birim damgaları, sunucunun <c>SyncItem</c> adlarıyla.
    /// <see cref="WpfCustomerSyncItem"/>'in sondaki parametreleri isteğe bağlı: buradan unutulan
    /// bir argüman DERLENİR ve sessiz hata olur — değeri olmadan giden damga sunucuda değeri
    /// siler, damgası olmadan giden değer yayılmaz (C5 incelemesi). Testi 33 özelliğin hepsini
    /// doğrular.
    /// </summary>
    private static WpfCustomerSyncItem ToItem(Guid id, CustomerSyncRow c)
    {
        var f = c.Fields;
        return new WpfCustomerSyncItem(
            Id: id,
            Platform: c.Platform,
            Username: c.Username,
            // Biçim 2'de ad ve takma ad ayrı birimler. R3-02'nin "FullName boşsa
            // DisplayName" geri düşüşü eski sözleşmeydi; takma ad gerçek ad diye gitmez.
            FullName: f.FullName,
            Phone: f.Phone,
            Address: f.Address,
            // Eski `since` okuyucuları (henüz güncellenmemiş bilgisayarlar) için iş zamanı —
            // biçim 1'deki anlamı korunur; birim kuralları damgalara bakar.
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(c.LastSeenAt),
            Format: 2,
            FullNameChangedAt: Ms(f.FullNameChangedAt),
            DisplayName: f.DisplayName,
            DisplayNameChangedAt: Ms(f.DisplayNameChangedAt),
            GroupId: f.GroupId,
            GroupIdChangedAt: Ms(f.GroupIdChangedAt),
            City: f.City,
            District: f.District,
            AddressChangedAt: Ms(f.AddressChangedAt),
            RecipientPaysActive: f.RecipientPaysActive,
            RecipientPaysChangedAt: Ms(f.RecipientPaysChangedAt),
            PhoneChangedAt: Ms(f.PhoneChangedAt),
            Email: f.Email,
            EmailChangedAt: Ms(f.EmailChangedAt),
            Tckn: f.Tckn,                                   // DÜZ — sunucu şifreler
            TcknChangedAt: Ms(f.TcknChangedAt),
            WhatsAppConsent: f.WhatsAppConsent,
            WhatsAppConsentChangedAt: Ms(f.WhatsAppConsentChangedAt),
            SmsConsent: f.SmsConsent,
            SmsConsentChangedAt: Ms(f.SmsConsentChangedAt),
            IsBlacklisted: f.IsBlacklisted,
            BlacklistReason: f.BlacklistReason,
            // Yerelde unix SANİYE (Customer tablosuyla aynı) — damgalar gibi ms değil.
            BlacklistedAt: f.BlacklistedAt is long s ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            BlacklistChangedAt: Ms(f.BlacklistChangedAt),
            Notes: f.Notes,
            NotesChangedAt: Ms(f.NotesChangedAt));
    }

    private static DateTimeOffset? Ms(long? unixMs)
        => unixMs is long v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    /// <summary>
    /// Sunucu bu Id'yi bir asıl kaydın KOPYASI olarak bağladı (S6/S7). Asıl kayıt yerelde
    /// varsa yerel satır ona taşınır; yoksa (TargetMissing) yapılacak şey yok — akış asıl
    /// kaydı indirdiğinde bu satır kimlik sahibi olarak bulunup taşınır (U4). Başarısız taşıma
    /// kalıcı değildir: sunucu yönlendirmeyi saklıyor, akıştaki kopya satırı ya da satırın
    /// bir sonraki gönderimi aynı taşımayı yeniden dener. Ödeme akışındaki müşteri taşınmaz (U13):
    /// depo kaynağı kararla aynı işlemde yeniden gönderime koyar, sonraki turun yanıtı
    /// yönlendirmeyi yeniden getirir. Günlüğe yalnız Id'ler.
    /// </summary>
    /// <param name="pushedThroughSeq">Gönderilen partinin en büyük SyncSeq'i (C4 incelemesi M-7): kopya
    /// partiden sonra değiştiyse depo yeniden anahtarlamaz (Deferred) — sonraki gönderim onu götürür.</param>
    private (int Rekeyed, int Waiting) ApplyRedirects(IReadOnlyList<WpfCustomerRedirect>? redirects, long pushedThroughSeq)
    {
        if (redirects is null || redirects.Count == 0) return (0, 0);
        var now = _clock.UnixNow();
        int rekeyed = 0, waiting = 0;
        foreach (var r in redirects)
        {
            try
            {
                switch (_sync.RekeyToLocal(r.Id.ToString("N"), r.CanonicalId.ToString("N"), pushedThroughSeq, now))
                {
                    case RekeyResult.Rekeyed: rekeyed++; break;
                    case RekeyResult.TargetMissing: waiting++; break;  // U4: asıl kayıt akışla gelince taşınır
                    case RekeyResult.Busy: waiting++; break;           // U13: ödeme akışı sürüyor; depo aynı işlemde yeniden kuyruğa koydu
                    case RekeyResult.Deferred: waiting++; break;       // kopya partiden sonra değişti; sonraki gönderim götürür
                    case RekeyResult.SourceMissing: break;             // yerelde yok (zaten taşınmış) — yapılacak şey yok
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Müşteri {From} → {To} yerel taşıma başarısız; yeniden denenecek",
                    r.Id, r.CanonicalId);
            }
        }
        return (rekeyed, waiting);
    }

    /// <summary>İmleci partinin SON satırına taşır ve kalıcılaştırır. Depo
    /// SyncSeq ASC sıralı döndürdüğü için son satır = partinin en büyük imleci.
    /// R6-04: kalıcılaştırma SyncCursor tablosuna — imleç, tarif ettiği
    /// SyncSeq değerleriyle aynı dosyada yaşamalı ki yedek/geri yükleme
    /// ikisini birlikte taşısın.</summary>
    private void AdvanceWatermark(
        string licenseKey,
        IReadOnlyList<CustomerSyncRow> batch,
        ref long watermark)
    {
        var w = batch[^1].SyncSeq;
        watermark = w;
        _cursors.Upsert(CursorName, licenseKey, seq: w);
    }

    // ─── LicenseId resolution (same caching pattern as other sync services) ──

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match    = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null) return null;

            _cachedLicenseId  = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "License resolve failed for customer projection sync");
            return null;
        }
    }
}
