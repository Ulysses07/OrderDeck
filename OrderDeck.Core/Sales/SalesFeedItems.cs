using System;
using System.Collections.Generic;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Sales;

// Satış akışının öğeleri, YEREL biçimde (spec 2026-10-13 §4.4, §5.4): kimlikler "N" biçimli metin, zamanlar unix
// saniye, oturumun platformları liste. Core, Licensing'in tel kayıtlarını (SalesChangeItem, SalesOrderItem, …) görmez;
// App tarafındaki saf eşleyici (SalesFeedMapper) teli bunlara çevirir — W1'in LabelServerState'i ile aynı desen.
// Yalnız akış uygulamasının kullandığı alanlar taşınır; birim damgaları bilgi amaçlıdır, kararı sunucu verir.

/// <summary>Akış öğesinin türü (teldeki <c>kind</c> ile aynı yazım; <c>SalesFeedFailure.Kind</c>).</summary>
public static class SalesFeedItemKind
{
    public const string Session = "session";
    public const string Order = "order";
    public const string Shipment = "shipment";
}

/// <summary>Yayın oturumu: akıştaki devredilmiş oturum ya da sayfadaki siparişlerin yan listedeki oturumu.</summary>
/// <param name="EndedAt">Sunucunun kapanış anı. Uzak oturum yerelde hiçbir zaman açık inmez (§3.2, §5.9): boşsa
/// devir anı yazılır (sunucunun onarım işi §4.9-d ile aynı kural).</param>
/// <param name="OwnerMachineName">Yayını açan bilgisayarın adı.</param>
/// <param name="ReleasedAt">Devir anı; akışta ve yan listede dolu (akış yalnız devredilmiş yayını verir).</param>
public sealed record SalesFeedSession(
    string Id,
    string? Title,
    long StartedAt,
    long? EndedAt,
    IReadOnlyList<string> Platforms,
    string? Notes,
    string? OwnerMachineName,
    long? ReleasedAt);

/// <summary>Sipariş (etiket): sunucu satırının yerelde tutulan alanları.</summary>
/// <param name="SessionId">Akış SessionId'si boş siparişi vermez (yerelde NOT NULL FK); boşsa öğe uygulanamaz.</param>
/// <param name="CustomerId">Asıl kaydın Id'si; projeksiyonu olmayan müşteride ham değer. Yalnız eklemede yazılır
/// (yönlendirmeden çözülerek); güncellemede hiç (DM4).</param>
/// <param name="Version">Sunucunun sürümü: yerelde <c>ServerVersion</c> olur (komutun beklenen sürümü, v2 push'unun
/// <c>baseVersion</c>'ı).</param>
public sealed record SalesFeedOrder(
    string Id,
    string? SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string? DisplayName,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    long? PrintedAt,
    long? CancelledAt,
    string? CancelReason,
    bool IsShippingFee,
    bool IsBackupPromoted,
    bool IsTentativeBackup,
    string? ProductId,
    string? ProductVariantId,
    int Version,
    string? ParentLabelId,
    string? ShipmentId,
    string? PrintBatchId,
    long? PrintSeq,
    long? PriceCorrectedAt,
    decimal? PaidPrice);

/// <summary>Kargo dosyası: yerel aynanın alanları (§7.1). Yalnız mod On'da ve <c>disable</c> turunda uygulanır.</summary>
/// <param name="CustomerId">Yalnız eklemede yazılır (yönlendirmeden çözülerek).</param>
/// <param name="OriginReplicaId">Mod Off'ta dosyanın tek yazıcısı (§3.1, §4.10); <c>disable</c> turu okur (§8.5).</param>
public sealed record SalesFeedShipment(
    string Id,
    string CustomerId,
    ShipmentStatus Status,
    decimal CumulativeAmount,
    long CreatedAt,
    long? HeldAt,
    long? ShippedAt,
    int Version,
    string? OriginReplicaId);

/// <summary>Yan listedeki müşteri taslağı (§4.4): siparişin müşterisi yerelde yoksa bundan açılır.</summary>
/// <param name="Id">Asıl kaydın Id'si; projeksiyonu olmayan müşteride siparişin ham <c>CustomerId</c>'si.</param>
/// <param name="PurgedAt">Asıl kayıt KVKK ile silindiyse dolu (unix saniye).</param>
public sealed record SalesFeedCustomerDraft(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    long? PurgedAt);

/// <summary>Akış sayfasının bir öğesi: türü, sırası ve satırı (tam olarak biri dolu).</summary>
/// <param name="Kind"><see cref="SalesFeedItemKind"/>.</param>
/// <param name="ChangeSeq">Satırın rowversion'ı; uygulanamayan öğenin deneme sayacı yeni seq'te sıfırlanır (U10).</param>
public sealed record SalesFeedItem(
    string Kind,
    long ChangeSeq,
    SalesFeedSession? Session,
    SalesFeedOrder? Order,
    SalesFeedShipment? Shipment)
{
    /// <summary>Satırın Id'si (<c>SalesFeedFailure.ItemId</c>).</summary>
    public string ItemId => Session?.Id ?? Order?.Id ?? Shipment?.Id ?? "";
}

/// <param name="ApplyShipments">Dosya öğeleri yalnız mod On'da ve <c>disable</c> turunda uygulanır (§5.4, §8.5);
/// değilse dosya öğesi <see cref="SalesFeedApplyStatus.Ignored"/> döner.</param>
/// <param name="CountCatchUpOverwrite">Mod On'un ilk yetişmesi (§8.4): kendi etiketinde farklı birim yazılmadan
/// önce <see cref="SalesConflictLog.CatchUpOverwrite"/> izi düşer. İz ayrıca işlem içinde okunan
/// <c>FirstCatchUpAt</c> boşken düşer; komut yanıtı ve <c>disable</c> turu bayrağı vermez.</param>
/// <param name="OwnRowsOnly"><c>disable</c> turunun son akışı (§8.5-1, W2-11): yalnız bu bilgisayarın kendi
/// (<c>Origin = 'local'</c>) oturumları ve etiketleri yazılır; yerelde olmayan oturum ve kendi oturumunda olmayan
/// etiket öğesi <see cref="SalesFeedApplyStatus.Ignored"/> döner. Uzak satırlar turun 2. adımında zaten silinir; mod On
/// dönemini hiç görmemiş kopyada (yerel kip 0) bu sayede yerel tabloya uzak etiket hiç inmez (§6.5-1). Dosya öğeleri
/// bundan etkilenmez.</param>
public sealed record SalesFeedApplyOptions(bool ApplyShipments, bool CountCatchUpOverwrite, bool OwnRowsOnly = false);

/// <summary><see cref="SalesFeedRepository.ApplyItem"/>'in sonucu.</summary>
public enum SalesFeedApplyStatus
{
    /// <summary>Öğe uygulandı; en az bir değer değişti (etkilenen kimlikler sonuçta).</summary>
    Applied,

    /// <summary>Öğe uygulandı ama hiçbir değer değişmedi (aynı öğe ikinci kez) ya da öğe yereldeki sürümden eski.</summary>
    Unchanged,

    /// <summary>Dosya öğesi ve <see cref="SalesFeedApplyOptions.ApplyShipments"/> kapalı, ya da
    /// <see cref="SalesFeedApplyOptions.OwnRowsOnly"/> açık ve öğe bu bilgisayarın satırı değil: hiçbir şey yazılmadı.</summary>
    Ignored,

    /// <summary>Yerel satırda gönderilmemiş değişiklik var (U5): hiçbir şey yazılmadı. Deneme sayılmaz; tur önce
    /// gönderir.</summary>
    Stalled,

    /// <summary>Müşteri yerelde yok ve aynı (Platform, Username) başka Id'de: yönlendirmeyi müşteri akışı getirecek.
    /// Hiçbir şey yazılmadı, deneme sayılmaz.</summary>
    PendingCustomer,

    /// <summary>Etiketin müşterisi ödeme akışında (U13): hiçbir şey yazılmadı, sonraki turda yeniden.</summary>
    Busy,
}

/// <summary><see cref="SalesFeedRepository.ApplyItem"/>'in sonucu: durum ve arayüzün tazeleyeceği kimlikler
/// (<c>SalesChanged</c>, §5.4). Kimlik listeleri yalnız değeri değişen satırları taşır.</summary>
/// <param name="SessionEndedRemotely">Öğe bu bilgisayarın hâlâ açık (aktif) yayınına dokundu ve o yayın sunucuda
/// bitirilmiş (force-end): MainShell bandı açılır (§5.7). Yayın yerel kalır, kesilmez (M18).</param>
/// <param name="CatchUpOverwrites">Yazılan <see cref="SalesConflictLog.CatchUpOverwrite"/> izi sayısı (0 ya da 1).</param>
/// <param name="Aggregates">Müşteri toplamlarının yeniden hesabı (§6.1); aynı öğeyi ikinci kez uygulamak sıfırdır.</param>
public sealed record SalesFeedApplyResult(
    SalesFeedApplyStatus Status,
    IReadOnlyList<string> LabelIds,
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> ShipmentIds,
    bool SessionEndedRemotely,
    int CatchUpOverwrites,
    AggregateRecomputeResult Aggregates)
{
    internal static SalesFeedApplyResult Of(SalesFeedApplyStatus status) => new(
        status, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, 0,
        AggregateRecomputeResult.None);
}

/// <summary><see cref="SalesFeedRepository.PurgeRemoteRows"/>'un sonucu: silinen satır sayıları ve toplamların yeniden
/// hesabı. Günlüğe yalnız bunlar yazılır.</summary>
/// <param name="Busy">Etkilenen bir müşteri ödeme akışındaydı (U13): hiçbir şey silinmedi, sonraki turda yeniden.</param>
public sealed record SalesRemotePurgeResult(
    bool Busy,
    int Sessions,
    int Labels,
    int LiveSessions,
    AggregateRecomputeResult Aggregates)
{
    internal static SalesRemotePurgeResult WasBusy { get; } = new(true, 0, 0, 0, AggregateRecomputeResult.None);
}

/// <summary><see cref="SalesFeedRepository.ApplyModeOff"/>'un sonucu (<c>disable</c> turunun 2. adımı, §8.5-2): sayılar ve
/// toplamların yeniden hesabı. Günlüğe yalnız bunlar yazılır.</summary>
/// <param name="Busy">Silinecek uzak etiketlerin bir müşterisi ödeme akışındaydı (U13): hiçbir şey yazılmadı (yerel kip
/// dahil), sonraki turda yeniden.</param>
/// <param name="SessionsToLocal">Yeniden <c>Authority = 'local'</c> olan kendi yayınları.</param>
/// <param name="Shipments">Silinen, Off'ta başka kopyanın olacak kargoya verilmemiş dosyalar.</param>
/// <param name="FeedFailures">Silinen akış hata kayıtları (U10).</param>
public sealed record SalesModeOffResult(
    bool Busy,
    int SessionsToLocal,
    int Sessions,
    int Labels,
    int LiveSessions,
    int Shipments,
    int FeedFailures,
    AggregateRecomputeResult Aggregates)
{
    internal static SalesModeOffResult WasBusy { get; } = new(true, 0, 0, 0, 0, 0, 0, AggregateRecomputeResult.None);
}

/// <summary>
/// Akış öğesi sunucu sözleşmesine uymuyor (örn. oturumsuz sipariş, yan listede olmayan oturum ya da taslak, devir anı
/// olmayan uzak oturum). Öğeye özgüdür, ortam hatası değildir: akış servisi uygulanamayan öğe sayar (U10). Mesaj
/// yalnız kimlik taşır.
/// </summary>
public sealed class SalesFeedContractException(string message) : InvalidOperationException(message);
