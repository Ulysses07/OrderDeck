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
