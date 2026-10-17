using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// WPF App ↔ LicenseServer StreamSession + Order senkronizasyonu
/// (PR siparis-sync 2026-05-13). Yayıncı ve ekibi mobile Panel
/// "Siparişler" ekranında geçmiş yayınların sipariş listesini görmesi için.
///
/// WPF authoritative — yayın başlatma/bitirme + label oluşturma WPF'te
/// olur, server pasif replika. Pattern: <see cref="PaymentSyncService"/>
/// ile paralel. Push-only (reverse sync gerekmiyor).
///
/// <para><b>v2 etiket gönderimi</b> (spec 2026-10-13 §4.2, §5.2; W1-06): istek v2 zarfını (kopya kimliği,
/// <c>Protocol = 2</c>, görülen kip) ve <c>Format = 2</c>'yi taşır; her etiket 048 alanlarını, yedek ebeveynini,
/// kargo bağını ve <c>baseVersion = ServerVersion</c>'ı. Yanıt öğe başınadır: <c>applied</c> satırda
/// <c>SyncedAt</c>, <c>StockSyncedAt</c> ve <c>ServerVersion</c> tek kısa işlemde, F05 karşılaştır-ve-yaz kuralıyla
/// yazılır. Durumsuz öğe ya da v1 listesi eski sunucudur — bugünkü gibi onaylanır, sürüm bilinmez. Geçiş dönemi:
/// <c>ignored</c> ve <c>rejected</c> satırlar da bugünkü gibi onaylanır, sayıları günlüğe düşer (sunucunun
/// değerini uygulamak W1-08'in işi). Kopya kimliği eksik ya da bozuksa gönderim durmaz, zarfsız (v1) gövde gider
/// (<see cref="SalesSyncEnvelope.ForPush"/>); geri yüklemeden sonra hello gidene kadar tur atlanır (§8.7).</para>
///
/// <para><b>Oturumlar</b> W1-07'ye kadar bugünkü (v1) gövdeyle gider: sunucunun eski devir yolu ("EndedAt dolu
/// gelmesi yeter") sürer.</para>
///
/// <para><b>Gönderim kapısı</b> (§5.2): <see cref="SyncOnceAsync"/> birden çok yerden eşzamanlı çağrılır —
/// zamanlayıcı (<see cref="SessionOrderSyncHostedService"/>), kapanıştaki "gönder ve kapat"
/// (<see cref="SyncFlushService.ForClose"/>) ve sonraki görevlerde geri doldurma. Bütün tur tek bir
/// <see cref="SemaphoreSlim"/> altındadır: aynı parti iki kez gitmez, onay sırası karışmaz.</para>
///
/// <para><b>Ucuz tur</b> (sahip kararı Y9): yayında tur ≈5 sn'de bir koşar
/// (<see cref="SessionOrderSyncHostedService"/>). Gönderilecek oturum ya da etiket yoksa hiçbir istek atılmaz —
/// lisans çözümü dahil; kuyruk iki kısmi dizinden birer satırla okunur. Gönderim hatası uyarısı en çok
/// <see cref="FailureWarningInterval"/>'da bir yazılır, arası Debug.</para>
///
/// <para><b>Canlı yol beklemez:</b> tur arka plan iş parçacığında koşar ve her <c>await</c>
/// <c>ConfigureAwait(false)</c>'tır. Kuyruk okuması kendi bağlantısını HTTP'den ÖNCE kapatır, onay HTTP'den SONRA
/// kısa bir işlemde yazılır: hiçbir SQLite bağlantısı ya da yazma kilidi ağ isteğini beklemez — yavaş ya da düşmüş
/// sunucu etiket yazmayı, baskıyı ve iptali durdurmaz.</para>
/// </summary>
public sealed class SessionOrderSyncService
{
    private const int SessionBatchSize = 50;

    /// <summary>Turdaki en çok etiket (sunucunun tavanı 200).</summary>
    internal const int OrderBatchSize = 100;

    /// <summary>Sunucudaki <c>Orders.PrintBatchId</c> kolonunun uzunluğu. Uzun değer partinin TAMAMINI 400'e
    /// düşürürdü; bozuk tek satır bütün kuyruğu (ve devri) tutmasın diye boş gönderilir.</summary>
    internal const int PrintBatchIdMaxLength = 40;

    /// <summary>Durum satırındaki gönderim ilerlemesinin adı (D2 incelemesi I-3).</summary>
    public const string PushStatusName = "oturum-siparis";

    private readonly LicenseApiClient _api;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly SalesSyncEnvelope _envelope;
    private readonly IClock _clock;
    private readonly ILogger<SessionOrderSyncService> _log;
    private readonly SyncStatusTracker? _tracker;

    /// <summary>Gönderim kapısı: bütün tur bunun altında (§5.2).</summary>
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    /// <summary>İki gönderim hatası uyarısı arasındaki en kısa süre (Y9: yayında tur ≈5 sn'de bir).</summary>
    internal static readonly System.TimeSpan FailureWarningInterval = System.TimeSpan.FromMinutes(1);

    private System.Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private long? _lastFailureWarnAt;

    public SessionOrderSyncService(
        LicenseApiClient api,
        SessionRepository sessions,
        LabelRepository labels,
        ICurrentLicenseProvider licenseProvider,
        SalesSyncEnvelope envelope,
        IClock clock,
        ILogger<SessionOrderSyncService> log,
        SyncStatusTracker? statusTracker = null)
    {
        _api = api;
        _sessions = sessions;
        _labels = labels;
        _licenseProvider = licenseProvider;
        _envelope = envelope;
        _clock = clock;
        _log = log;
        _tracker = statusTracker;
        _tracker?.RegisterPush(PushStatusName);
    }

    public readonly record struct SyncResult(int SessionsPushed, int OrdersPushed);

    /// <summary>Bir gönderim turu; gönderim kapısından geçer (eşzamanlı çağrılar sıraya girer). Belirteç kapıyı
    /// beklerken de dinlenir: kapanışın payı dolunca beklemeden çıkar.</summary>
    public async Task<SyncResult> SyncOnceAsync(System.Threading.CancellationToken ct = default)
    {
        await _pushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    private async Task<SyncResult> SyncCoreAsync(System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_licenseProvider.CurrentLicenseKey))
        {
            _log.LogDebug("Session/Order sync skipped — no license key");
            return default;
        }

        // Y9: yayında tur ≈5 sn'de bir koşar; gönderilecek bir şey yoksa HİÇBİR istek atılmaz — lisans çözümü dahil.
        // İki kısmi dizinden birer satırlık okuma.
        if (!_sessions.HasUnsynced() && !_labels.HasUnsynced())
        {
            _tracker?.MarkPushOk(PushStatusName, System.DateTimeOffset.UtcNow);   // I-3: gönderecek bir şey yok
            return default;
        }

        // null: geri yüklemeden sonra hello bekleniyor — hiçbir gönderim yapılmaz (§8.7).
        if (_envelope.ForPush() is not { } envelope)
        {
            _log.LogDebug("Session/Order sync skipped — waiting for sales/hello after restore");
            return default;
        }

        var licenseId = await ResolveLicenseIdAsync(ct).ConfigureAwait(false);
        if (licenseId is null)
        {
            _log.LogDebug("Session/Order sync skipped — no active license resolved");
            return default;
        }

        // Önce session'lar (Order.SessionId FK olduğu için)
        var (sessionsPushed, sessionsOk) = await PushSessionsAsync(licenseId.Value, ct).ConfigureAwait(false);
        var (ordersPushed, ordersOk) = await PushOrdersAsync(licenseId.Value, envelope, ct).ConfigureAwait(false);

        // I-3: iki gönderim de başardı ya da gönderecek bir şey yoktu — durum satırı sağlıklı sayar.
        if (sessionsOk && ordersOk) _tracker?.MarkPushOk(PushStatusName, System.DateTimeOffset.UtcNow);

        if (sessionsPushed > 0 || ordersPushed > 0)
            _log.LogInformation(
                "Session/Order sync: sessions={Sessions} orders={Orders}",
                sessionsPushed, ordersPushed);

        return new SyncResult(sessionsPushed, ordersPushed);
    }

    /// <returns>Gönderilen sayı; <c>Ok</c> = gönderim başarılı ya da gönderecek bir şey yoktu.</returns>
    private async Task<(int Pushed, bool Ok)> PushSessionsAsync(System.Guid licenseId, System.Threading.CancellationToken ct)
    {
        var batch = _sessions.GetUnsynced(SessionBatchSize);
        if (batch.Count == 0) return (0, true);

        var items = batch.Select(s => new SyncSessionItem(
            Id: System.Guid.Parse(s.Id),
            Title: s.Title,
            StartedAt: System.DateTimeOffset.FromUnixTimeSeconds(s.StartedAt),
            EndedAt: s.EndedAt.HasValue ? System.DateTimeOffset.FromUnixTimeSeconds(s.EndedAt.Value) : null,
            Platforms: string.Join(",", s.Platforms),
            Notes: s.Notes
        )).ToList();

        try
        {
            _ = await _api.SyncSessionsAsync(licenseId, new SyncSessionsRequest(items), ct).ConfigureAwait(false);
        }
        catch (LicenseApiException ex)
        {
            LogFailure(ex, "Session outbox push failed: {Code}", ex.Code);
            return (0, false);
        }

        var now = _clock.UnixNow();
        // F05: push'a giden Revision ile onayla — uçuş sırasında satır
        // değiştiyse MarkSynced 0 satır etkiler, sonraki tick tekrar gönderir.
        foreach (var s in batch) _sessions.MarkSynced(s.Id, now, s.Revision);
        return (batch.Count, true);
    }

    /// <returns>Gönderilen sayı; <c>Ok</c> = gönderim başarılı ya da gönderecek bir şey yoktu.</returns>
    private async Task<(int Pushed, bool Ok)> PushOrdersAsync(
        System.Guid licenseId, SalesPushEnvelope envelope, System.Threading.CancellationToken ct)
    {
        var batch = _labels.GetUnsyncedForPush(OrderBatchSize);
        if (batch.Count == 0) return (0, true);

        var items = batch.Select(ToSyncOrderItem).ToList();
        // Bu istemci katalog kimliği gönderebiliyor; sunucu stok hareketini yalnız bu bayrak açıkken üretiyor.
        var request = envelope.IsV2
            ? new SyncOrdersRequest(items, CatalogAware: true,
                ReplicaId: envelope.ReplicaId, Protocol: envelope.Protocol, SeenMode: envelope.SeenModeField,
                Format: OrderFormatV2)
            : new SyncOrdersRequest(items, CatalogAware: true);

        List<SyncedOrderV2Dto> response;
        try
        {
            response = await _api.SyncOrdersV2Async(licenseId, request, ct).ConfigureAwait(false);
        }
        catch (LicenseApiException ex)
        {
            LogFailure(ex, "Order outbox push failed: {Code}", ex.Code);
            return (0, false);
        }

        var outcome = Acknowledge(batch, items, response);
        if (outcome.Ignored > 0 || outcome.Rejected > 0 || outcome.Unknown > 0)
            _log.LogWarning(
                "orders/sync: {Ignored} etiket yok sayıldı, {Rejected} etiket reddedildi, {Unknown} etiketin durumu "
              + "tanınmadı; sunucunun değeri henüz uygulanmıyor, satırlar bugünkü gibi onaylandı",
                outcome.Ignored, outcome.Rejected, outcome.Unknown);
        return (batch.Count, true);
    }

    /// <summary>v2 öğe biçimi (§4.2).</summary>
    private const int OrderFormatV2 = 2;

    private readonly record struct AckOutcome(int Ignored, int Rejected, int Unknown);

    /// <summary>
    /// Yanıtı satırlarla eşleyip onayı tek işlemde yazar. <c>applied</c>: sürümüyle (<c>ServerVersion</c>). Eski
    /// sunucu (durumsuz öğe, v1 listesi, yanıtta olmayan satır): bugünkü gibi, sürümsüz. Geçiş dönemi —
    /// <c>ignored</c>, <c>rejected</c> ve tanınmayan durum: sürümsüz onay (sunucunun değeri uygulanmadan sürümü
    /// yazmak, sonraki gönderimi "güncel sürümü gördüm" diye damgalı birimi ezebilir hâle getirirdi, K2).
    /// </summary>
    private AckOutcome Acknowledge(
        IReadOnlyList<LabelPushRow> batch, IReadOnlyList<SyncOrderItem> items, IReadOnlyList<SyncedOrderV2Dto> response)
    {
        var byId = new Dictionary<System.Guid, SyncedOrderV2Dto>(response.Count);
        foreach (var r in response) byId[r.Id] = r;

        int ignored = 0, rejected = 0, unknown = 0;
        var acks = new List<LabelPushAck>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            int? version = null;
            if (byId.TryGetValue(items[i].Id, out var r) && !r.IsLegacy)
            {
                switch (r.Status)
                {
                    case SyncedOrderV2Dto.StatusApplied: version = r.Version; break;
                    case SyncedOrderV2Dto.StatusIgnored: ignored++; break;
                    case SyncedOrderV2Dto.StatusRejected: rejected++; break;
                    default: unknown++; break;
                }
            }
            acks.Add(new LabelPushAck(batch[i].Id, batch[i].Revision, version));
        }

        // F05: Revision karşılaştırmalı; uçuş sırasında değişen satıra yazılmaz, sonraki tur güncel hâliyle gönderir.
        _labels.MarkSyncedBatch(acks, _clock.UnixNow());
        return new AckOutcome(ignored, rejected, unknown);
    }

    /// <summary>Yerel satırdan tel öğesi (§4.2): TEXT "N" kimlikler Guid'e, unix saniyeler DateTimeOffset'e,
    /// <c>baseVersion = ServerVersion</c>. Ayrıştırılamayan kimlik boş gider: bozuk tek satır bütün partiyi
    /// düşürmemeli.</summary>
    internal static SyncOrderItem ToSyncOrderItem(LabelPushRow l) => new(
        Id: System.Guid.Parse(l.Id),
        SessionId: ParseGuid(l.SessionId),
        CustomerId: l.CustomerId,
        Platform: l.Platform,
        Username: l.Username,
        DisplayName: l.DisplayName,
        MessageText: l.MessageText,
        Code: l.Code,
        Price: l.Price,
        AddedAt: System.DateTimeOffset.FromUnixTimeSeconds(l.AddedAt),
        PrintedAt: FromUnix(l.PrintedAt),
        CancelledAt: FromUnix(l.CancelledAt),
        CancelReason: l.CancelReason,
        IsShippingFee: l.IsShippingFee,
        IsBackupPromoted: l.IsBackupPromoted,
        IsTentativeBackup: l.IsTentativeBackup,
        ProductId: ParseGuid(l.ProductId),
        ProductVariantId: ParseGuid(l.ProductVariantId),
        BaseVersion: l.ServerVersion is { } v ? (int)v : null,
        ParentOrderId: ParseGuid(l.ParentLabelId),
        PrintBatchId: l.PrintBatchId is { Length: <= PrintBatchIdMaxLength } batchId ? batchId : null,
        PrintSeq: l.PrintSeq is { } seq ? (int)seq : null,
        PriceCorrectedAt: FromUnix(l.PriceCorrectedAt),
        PaidPrice: l.PaidPrice,
        ShipmentId: ParseGuid(l.ShipmentId));

    private static System.Guid? ParseGuid(string? value) => System.Guid.TryParse(value, out var g) ? g : null;

    private static System.DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } s ? System.DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private async Task<System.Guid?> ResolveLicenseIdAsync(System.Threading.CancellationToken ct)
    {
        var key = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null)
            {
                LogFailure(null, "LicenseId not found for current license key");
                return null;
            }
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (LicenseApiException ex)
        {
            LogFailure(ex, "Failed to resolve LicenseId: {Code}", ex.Code);
            return null;
        }
    }

    /// <summary>
    /// Gönderim hatası günlüğü (Y9): yayında tur ≈5 sn'de bir koştuğu için çevrimdışı bir bilgisayar her turda uyarı
    /// yazardı. Uyarı en çok <see cref="FailureWarningInterval"/>'da bir; arası Debug. Tur kapı altında koştuğu için
    /// durum tek iş parçacığından okunur ve yazılır.
    /// </summary>
    private void LogFailure(System.Exception? ex, string message, params object?[] args)
    {
        var now = _clock.UnixNow();
        if (_lastFailureWarnAt is { } last && now - last < (long)FailureWarningInterval.TotalSeconds)
        {
            _log.LogDebug(ex, message, args);
            return;
        }
        _lastFailureWarnAt = now;
        _log.LogWarning(ex, message, args);
    }
}
