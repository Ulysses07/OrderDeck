using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Sync;

// Satış senkronunun akış öğesi biçimi — spec 2026-10-13 §4.4. Değişiklik akışı (`sales/changes`) bunları taşır;
// gönderim uçlarının v2 yanıtındaki `current` (§4.2) ve komut yanıtları (§4.6) aynı biçimi kullanır, böylece istemci
// hepsini tek uygulama fonksiyonuyla yazar (§5.4). Alanlar sunucu satırının bütün hâlidir; istemci hangi alanı
// yazacağına kendi kuralıyla karar verir (oluşum alanları yalnız eklemede, DM4).

/// <summary>Yayın oturumu öğesi.</summary>
/// <param name="ReleasedAt">Devir anı; akıştaki oturumda hep dolu, yan listede de (devredilmiş yayının siparişi).</param>
/// <param name="ChangeSeq">Satırın rowversion'ı. Yan listede de satırın kendisininkidir; imleci etkilemez.</param>
public sealed record SalesSessionItem(
    Guid Id,
    string? Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Platforms,
    string? Notes,
    DateTimeOffset UpdatedAt,
    Guid? OwnerReplicaId,
    string? OwnerMachineName,
    string? ForceEndedBy,
    DateTimeOffset? ReleasedAt,
    int? UnsentLabelCount,
    long ChangeSeq)
{
    public static SalesSessionItem From(StreamSession s) => new(
        s.Id, s.Title, s.StartedAt, s.EndedAt, s.Platforms, s.Notes, s.UpdatedAt,
        s.OwnerReplicaId, s.OwnerMachineName, s.ForceEndedBy, s.ReleasedAt, s.UnsentLabelCount, s.ChangeSeq);
}

/// <summary>Sipariş (etiket) öğesi: satırın tamamı, birim damgaları bilgi amaçlı.</summary>
/// <param name="CustomerId">Asıl kaydın hex'i (<see cref="SalesCustomerResolution.Id"/>); yan listedeki müşteri
/// taslağının Id'siyle aynıdır. GUID olmayan değer ham gider.</param>
/// <param name="Version">Satırın sürümü (<see cref="Order.SyncVersion"/>): komutların beklenen sürümü ve v2
/// gönderiminin <c>baseVersion</c>'ı (§3.1, K2).</param>
/// <param name="PriceSetAt">Birim damgaları (§2): "bu birime komut dokundu". Kararı sunucu verir; istemci yalnız
/// gösterir.</param>
public sealed record SalesOrderItem(
    Guid Id,
    Guid? SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string? DisplayName,
    string MessageText,
    string? Code,
    decimal Price,
    DateTimeOffset AddedAt,
    DateTimeOffset? PrintedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    bool IsShippingFee,
    bool IsBackupPromoted,
    bool IsTentativeBackup,
    Guid? ProductId,
    Guid? ProductVariantId,
    DateTimeOffset UpdatedAt,
    int Version,
    Guid? ParentOrderId,
    Guid? ShipmentId,
    Guid? OriginReplicaId,
    string? PrintBatchId,
    int? PrintSeq,
    DateTimeOffset? PriceCorrectedAt,
    decimal? PaidPrice,
    DateTimeOffset? SessionReleasedAt,
    DateTimeOffset? PriceSetAt,
    DateTimeOffset? CancelSetAt,
    DateTimeOffset? BackupSetAt,
    DateTimeOffset? ShipmentSetAt,
    DateTimeOffset? TextScrubbedAt,
    long ChangeSeq)
{
    /// <summary>
    /// Sunucu satırını akış biçimine çevirir. Müşteri çözümü ZORUNLU parametre: sipariş, asıl kaydın Id'siyle gider
    /// ve KVKK okuma maskesi her zaman uygulanır — asıl kaydı silinmiş müşterinin siparişi <c>DisplayName = null</c>,
    /// <c>MessageText = ""</c> ile gider (§9, <see cref="OrderTextScrub.MasksOnRead"/>). Maske bir yazım değildir:
    /// sunucu satırı ve <c>TextScrubbedAt</c> değişmez.
    /// </summary>
    public static SalesOrderItem From(Order o, SalesCustomerResolution customer)
    {
        var masked = OrderTextScrub.MasksOnRead(customer.Canonical?.PurgedAt);
        return new(
            o.Id, o.SessionId, customer.Id, o.Platform, o.Username,
            masked ? null : o.DisplayName, masked ? "" : o.MessageText,
            o.Code, o.Price, o.AddedAt, o.PrintedAt, o.CancelledAt, o.CancelReason,
            o.IsShippingFee, o.IsBackupPromoted, o.IsTentativeBackup, o.ProductId, o.ProductVariantId,
            o.UpdatedAt, o.SyncVersion,
            o.ParentOrderId, o.ShipmentId, o.OriginReplicaId, o.PrintBatchId, o.PrintSeq,
            o.PriceCorrectedAt, o.PaidPrice, o.SessionReleasedAt,
            o.PriceSetAt, o.CancelSetAt, o.BackupSetAt, o.ShipmentSetAt, o.TextScrubbedAt,
            o.ChangeSeq);
    }
}

/// <summary>Kargo dosyası öğesi. Akış dosyaları iki kipte verir; istemci yalnız mod On'da ve <c>disable</c> turunda
/// uygular (§4.4, §8.5).</summary>
/// <param name="Status"><c>shipments/sync</c> ile aynı yazım: <c>pending</c>, <c>held</c>, <c>recipientpays</c>,
/// <c>shipped</c>.</param>
/// <param name="Version">Komutların beklenen sürümü (§3.1).</param>
/// <param name="AmountDerivedAt">Doluysa tutar bağlı siparişlerden türetilir (DM7).</param>
/// <param name="CommandedAt">Dosyaya sunucu komutu dokundu (§7.1).</param>
/// <param name="OriginReplicaId">Mod Off'ta dosyanın tek yazıcısı (§3.1, §4.10).</param>
public sealed record SalesShipmentItem(
    Guid Id,
    string CustomerId,
    string Status,
    decimal CumulativeAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HeldAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    DateTimeOffset? AmountDerivedAt,
    DateTimeOffset? CommandedAt,
    Guid? OriginReplicaId,
    long ChangeSeq)
{
    public static SalesShipmentItem From(Shipment s) => new(
        s.Id, s.CustomerId, s.Status.ToString().ToLowerInvariant(), s.CumulativeAmount,
        s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt,
        s.Version, s.AmountDerivedAt, s.CommandedAt, s.OriginReplicaId, s.ChangeSeq);
}

/// <summary>
/// Yan listedeki müşteri taslağı (§4.4, §5.4): siparişin müşterisi yerelde yoksa istemci onu müşteri akışının
/// sunucudan ekleme yoluyla bu taslaktan açar (0/0 toplam, mezar taşı boşaltması). Kişisel alan taşımaz; yalnız
/// kimlik ve takma ad.
/// </summary>
/// <param name="Id">Asıl kaydın hex'i; projeksiyonu olmayan müşteride siparişin ham <c>CustomerId</c>'si.</param>
/// <param name="PurgedAt">Asıl kayıt KVKK ile silindiyse dolu; taslak o zaman takma adsız gelir.</param>
public sealed record SalesCustomerDraft(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    DateTimeOffset? PurgedAt);

/// <summary>Akış sayfasının bir öğesi: türü ve satırı. Tam olarak biri dolu.</summary>
/// <param name="Kind"><c>session</c>, <c>order</c> ya da <c>shipment</c>.</param>
public sealed record SalesChangeItem(
    string Kind,
    long ChangeSeq,
    SalesSessionItem? Session,
    SalesOrderItem? Order,
    SalesShipmentItem? Shipment)
{
    public const string KindSession = "session";
    public const string KindOrder = "order";
    public const string KindShipment = "shipment";
}

/// <summary>Değişiklik akışının bir sayfası (§4.4).</summary>
/// <param name="Items">Üç kaynağın ChangeSeq sırasıyla birleşimi.</param>
/// <param name="NextAfterSeq">Son öğenin seq'i; boş sayfada istemcinin verdiği imleç (sıfırlandıysa 0).</param>
/// <param name="CursorReset">İmleç geçersizdi (eksi ya da ufkun üstü) ve sayfa baştan verildi.</param>
/// <param name="SideSessions">Sayfadaki siparişlerin oturumları, güncel hâliyle; imleci etkilemez. rowversion
/// satırın yalnız son hâlini gösterdiği için oturum siparişlerinden sonra gelebilir: yan liste FK sırasını sayfa
/// içinde sağlar.</param>
/// <param name="CustomerDrafts">Sayfadaki siparişlerin müşterileri; imleci etkilemez. Eksik müşteri öğeyi
/// zehirlemez: projeksiyonu olmayan müşterinin taslağı siparişin kendi alanlarından kurulur.</param>
public sealed record SalesChangesPage(
    IReadOnlyList<SalesChangeItem> Items,
    long NextAfterSeq,
    bool CursorReset,
    IReadOnlyList<SalesSessionItem> SideSessions,
    IReadOnlyList<SalesCustomerDraft> CustomerDrafts);
