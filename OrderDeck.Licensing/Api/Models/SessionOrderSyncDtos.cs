using System.Text.Json.Serialization;

namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF → LicenseServer push: tek StreamSession item (PR siparis-sync 2026-05-13).</summary>
/// <param name="Release">v2 (spec 2026-10-13 §4.1, §5.2, M2): yayın bitti ve etiket kuyruğu boş — yayını sunucuya
/// devret. Aktif oturumda <c>false</c>. Boşken (v1) telde yazılmaz.</param>
/// <param name="MachineName">v2: gönderen bilgisayarın adı (sunucuda <c>OwnerMachineName</c>). Boşken yazılmaz.</param>
/// <param name="UnsentLabelCount">v2, bitmiş oturum, <c>Release = false</c>: yayının gönderilmemiş etiket sayısı
/// (devrediliyor çipi ve CLI gösterir). Boşken yazılmaz.</param>
public sealed record SyncSessionItem(
    System.Guid Id,
    string? Title,
    System.DateTimeOffset StartedAt,
    System.DateTimeOffset? EndedAt,
    string Platforms,         // comma-separated: "instagram,youtube"
    string? Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Release = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MachineName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnsentLabelCount = null);

/// <param name="ReplicaId">v2 zarfı (spec 2026-10-13 §4.0): kopya kimliği, <c>Protocol = 2</c> ve görülen kip.
/// Boşken telde hiç yazılmaz — zarfsız gövde bugünkü (v1) gövdeyle birebir aynıdır.</param>
public sealed record SyncSessionsRequest(
    System.Collections.Generic.IReadOnlyList<SyncSessionItem> Sessions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] System.Guid? ReplicaId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Protocol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeenMode = null);

/// <summary>
/// <c>sessions/sync</c> yanıtının öğesi (sunucuda <c>LicensesSessionsSyncController.SyncedSessionDto</c>). Eski sunucu
/// yeni alanları göndermez: <see cref="Protocol"/> boşsa sunucu eski sürümdür (spec 2026-10-13 §5.2).
/// </summary>
/// <param name="ReleasedAt">Sunucunun devir anı; devredilmemiş oturumda boş.</param>
/// <param name="Status"><c>applied</c> ya da <c>ignored</c>.</param>
/// <param name="Reason"><c>ignored</c>'ın nedeni: <c>session-owned-elsewhere</c> ya da <c>session-released</c>.</param>
public sealed record SyncedSessionDto(
    System.Guid Id, string? Title,
    System.DateTimeOffset StartedAt, System.DateTimeOffset? EndedAt,
    string Platforms, string? Notes,
    System.DateTimeOffset UpdatedAt,
    System.Guid? OwnerReplicaId = null,
    System.DateTimeOffset? ReleasedAt = null,
    int? Protocol = null,
    string? Status = null,
    string? Reason = null);

/// <summary>WPF → LicenseServer push: tek Order (Label) item.</summary>
/// <param name="BaseVersion">v2 (spec 2026-10-13 §4.2, K2): satırın istemcideki <c>ServerVersion</c>'ı — son
/// <c>applied</c> onayında sunucunun bildirdiği sürüm. Hiç onaylanmamış satırda boş.</param>
/// <param name="ParentOrderId">v2: yedeğin ebeveyni (yerel <c>ParentLabelId</c>); sunucu yalnız boşsa doldurur (K1).</param>
/// <param name="PrintBatchId">v2: basış kimliği (048); sunucu en çok 40 karakter kabul eder.</param>
/// <param name="ShipmentId">v2: kargo bağı. Mod Off'ta boş değer de bir değerdir (bağ yok, K4): her zaman yerel
/// değer gönderilir.</param>
public sealed record SyncOrderItem(
    System.Guid Id,
    System.Guid? SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string? DisplayName,
    string MessageText,
    string? Code,
    decimal Price,
    System.DateTimeOffset AddedAt,
    System.DateTimeOffset? PrintedAt,
    System.DateTimeOffset? CancelledAt,
    string? CancelReason,
    bool IsShippingFee,
    bool IsBackupPromoted,
    bool IsTentativeBackup,
    System.Guid? ProductId = null,
    System.Guid? ProductVariantId = null,
    int? BaseVersion = null,
    System.Guid? ParentOrderId = null,
    string? PrintBatchId = null,
    int? PrintSeq = null,
    System.DateTimeOffset? PriceCorrectedAt = null,
    decimal? PaidPrice = null,
    System.Guid? ShipmentId = null);

/// <param name="CatalogAware">
/// Bu istemcinin <c>SyncOrderItem.ProductId</c>/<c>ProductVariantId</c>
/// alanlarını doldurabildiğini bildirir. Sunucu bayrağı yoksa o paket için
/// stok mutabakatını hiç çalıştırmaz. WPF katalog replikasını kurana kadar
/// varsayılan false kalıyor — Faz 1b WPF planında true'ya çevrilecek.
/// </param>
/// <param name="ReplicaId">v2 zarfı (spec 2026-10-13 §4.0): kopya kimliği, <c>Protocol = 2</c> ve görülen kip.
/// Boşken telde hiç yazılmaz — zarfsız gövde bugünkü (v1) gövdeyle birebir aynıdır.</param>
/// <param name="Format">Öğe biçimi (§4.2): 2 = v2 alanları ve öğe başına yanıt (<see cref="SyncedOrderV2Dto"/>);
/// v2 zarfı ister. Boşken yazılmaz (v1).</param>
public sealed record SyncOrdersRequest(
    System.Collections.Generic.IReadOnlyList<SyncOrderItem> Orders,
    bool CatalogAware = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] System.Guid? ReplicaId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Protocol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeenMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Format = null);

public sealed record SyncedOrderDto(
    System.Guid Id, System.Guid? SessionId, string CustomerId,
    string Platform, string Username, string? DisplayName,
    string MessageText, string? Code, decimal Price,
    System.DateTimeOffset AddedAt, System.DateTimeOffset? PrintedAt,
    System.DateTimeOffset? CancelledAt, string? CancelReason,
    bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup,
    System.DateTimeOffset UpdatedAt);

/// <summary>
/// <c>orders/sync</c> yanıtının öğesi (spec 2026-10-13 §4.2 Yanıt; sunucuda
/// <c>LicensesSessionsSyncController.SyncedOrderV2Dto</c>). Aynı tip eski sunucunun yanıtını da okur: v1 listesinin
/// (<see cref="SyncedOrderDto"/>) öğesinde <see cref="Status"/> ve <see cref="Version"/> yoktur — o öğe
/// <see cref="IsLegacy"/>'dir ve bugünkü gibi onaylanır.
/// </summary>
/// <param name="Version">Satırın yazımdan sonraki sürümü: istemcinin sonraki <c>baseVersion</c>'ı. Reddedilen
/// eklemede (sunucuda satır yok) ve eski sunucuda boş.</param>
/// <param name="Status"><see cref="StatusApplied"/>, <see cref="StatusIgnored"/> ya da <see cref="StatusRejected"/>;
/// eski sunucuda boş.</param>
/// <param name="IgnoredUnits">Yazılmayan birimler: <c>price</c>, <c>cancel</c>, <c>backup</c>, <c>shipment</c>,
/// <c>print</c>.</param>
/// <param name="Current">Sunucunun güncel satırı (akış öğesi biçiminde); <c>ignored</c> ve <c>rejected</c>'da dolu.</param>
public sealed record SyncedOrderV2Dto(
    System.Guid Id,
    int? Version,
    System.Guid? ShipmentId,
    System.DateTimeOffset? SessionReleasedAt,
    string? Status,
    System.Collections.Generic.IReadOnlyList<string>? IgnoredUnits,
    string? Reason,
    SalesOrderItem? Current)
{
    public const string StatusApplied = "applied";
    public const string StatusIgnored = "ignored";
    public const string StatusRejected = "rejected";

    /// <summary>Eski sunucunun (v1 listesi) öğesi: durum yok — bugünkü gibi uygulanmış sayılır, sürüm bilinmez.</summary>
    [JsonIgnore]
    public bool IsLegacy => Status is null;
}
