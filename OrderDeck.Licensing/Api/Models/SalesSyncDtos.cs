using System.Text.Json.Serialization;

namespace OrderDeck.Licensing.Api.Models;

// Satış senkronunun kopya uçları (spec 2026-10-13 §4.5). Sunucudaki karşılıkları
// LicensesSalesController.HelloRequest, ReportedShippingDto, SalesStateDto, LiveSessionDto,
// ShippingSettingsDto ve LegacyMachineDto; alan adları ve tipleri onların aynası.

/// <summary>Kopyanın yerel kargo ayarı (§6.2). Özellik kapalıyken iki alan da <c>null</c>.</summary>
public sealed record ReportedShippingDto(decimal? Threshold, decimal? Fee);

/// <summary>
/// <c>POST sales/hello</c> gövdesi. İsteğe bağlı alanlar boşken telde hiç yazılmaz: sunucu yalnız dolu
/// değeri işler ve "bekleyen önceki kimlik yok" boş alanla değil alanın yokluğuyla anlatılır.
/// </summary>
/// <param name="PreviousReplicaId">Geri yüklemeden önceki kimlik (§8.7); sunucu bir kez yazar.</param>
/// <param name="FeatureLevel">Derlemenin yetenek düzeyi; mod kapısı 3 ister (§4.10-2, §8.1-4).</param>
/// <param name="SeenMode">Görülen kip (§2): 0 ya da 1.</param>
/// <param name="BackfillCompleted">Geri doldurma ve ayrışma onarımı bitti (§8.2-5); yalnız true etkili.</param>
/// <param name="PaymentHistoryCompleted">Ödeme geçmişi geri doldurması bitti (§8.2-6); yalnız true etkili.</param>
public sealed record SalesHelloRequest(
    Guid ReplicaId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PreviousReplicaId,
    string? MachineName,
    string? HardwareFingerprint,
    int Protocol,
    int FeatureLevel,
    int SeenMode,
    ReportedShippingDto? ReportedShipping,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BackfillCompleted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PaymentHistoryCompleted = null);

/// <summary>Başka bilgisayarın canlı ya da devrediliyor yayını (<c>ReleasedAt</c> boş).</summary>
/// <param name="State"><c>live</c> ya da <c>handing-over</c>.</param>
/// <param name="OwnerLastSeenAt">Sahip kopyanın son kalp atışı.</param>
/// <param name="UnsentLabelCount">Devrediliyor yayında sahibin bildirdiği gönderilmemiş etiket sayısı.</param>
public sealed record LiveSessionDto(
    Guid Id,
    string? Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? OwnerMachineName,
    DateTimeOffset? OwnerLastSeenAt,
    string State,
    int? UnsentLabelCount);

/// <summary>Lisans geneli kargo ayarı (§6.2); ayar hiç yazılmadıysa state yanıtında <c>null</c>.</summary>
public sealed record ShippingSettingsDto(int Version, decimal? FreeShippingThreshold, decimal? Fee);

public sealed record LegacyMachineDto(string? MachineName, string? AppVersion, DateTimeOffset LastSeenAt);

/// <summary><c>GET sales/state</c> yanıtı (§4.5).</summary>
/// <param name="Mode">0 = Off, 1 = On.</param>
/// <param name="MinClientVersion">Tanımsızsa <c>null</c> (mod kapısı kapalı).</param>
/// <param name="ForceEndSilenceMinutes">Sunucunun sahip sessizliği eşiği; istemci 15'i sabit yazmaz (§5.7).</param>
public sealed record SalesStateDto(
    int Mode,
    DateTimeOffset? ModeChangedAt,
    string? MinClientVersion,
    IReadOnlyList<LiveSessionDto>? LiveSessions,
    ShippingSettingsDto? Shipping,
    bool ScrubOrderTextOnPurge,
    int ForceEndSilenceMinutes,
    IReadOnlyList<LegacyMachineDto>? LegacyMachines);

// Akış öğesi biçimi (§4.4; sunucuda Services/Sync/SalesFeedItems.cs). Gönderim uçlarının v2 yanıtındaki `current`
// (§4.2), kargo gönderiminin v2 yanıtı (§7.1) ve W2'nin akışı aynı biçimi taşır; alan adları ve tipleri sunucudaki
// kayıtların aynası.

/// <summary>Sipariş (etiket) öğesi: sunucu satırının tamamı; birim damgaları bilgi amaçlı.</summary>
/// <param name="CustomerId">Asıl kaydın hex'i; GUID olmayan değer ham gelir.</param>
/// <param name="Version">Satırın sürümü: komutların beklenen sürümü ve v2 gönderiminin <c>baseVersion</c>'ı.</param>
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
    long ChangeSeq);

/// <summary>Kargo dosyası öğesi. v2 <c>shipments/sync</c> yanıtı bunun listesidir (§7.1); eski sunucunun v1 yanıtı
/// (<see cref="SyncedShipmentDto"/>) da bu tiple okunur — v2'ye özgü alanlar o zaman varsayılan değerde kalır.</summary>
/// <param name="Status"><c>pending</c>, <c>held</c>, <c>recipientpays</c> ya da <c>shipped</c>.</param>
/// <param name="AmountDerivedAt">Doluysa tutar bağlı siparişlerden türetilir (DM7).</param>
/// <param name="CommandedAt">Dosyaya sunucu komutu dokundu (§7.1).</param>
/// <param name="OriginReplicaId">Mod Off'ta dosyanın tek yazıcısı (§3.1).</param>
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
    long ChangeSeq);
