using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderDeck.Licensing.Api.Models;

// POST sales/commands (spec 2026-10-13 §4.6, S2). Sunucudaki karşılıkları Services/Sync/Commands altında:
// SalesCommandContracts.cs (SalesCommandRequest, SalesCommandExpectedDto, SalesCommandResponse, SalesCommandResult,
// SalesCommandOrderVersion, SalesCommandShipmentAmount), SalesCommandRejection (ProblemDetails ek üyeleri),
// SalesCommandService (zarf ve kapı kodları) ve on işleyicinin Payload kayıtları. Alan adları ve tipleri onların aynası.

/// <summary>Komut türleri — teldeki <c>kind</c> (sunucudaki işleyicilerin <c>KindName</c>'i).</summary>
public static class SalesCommandKinds
{
    public const string Cancel = "cancel";
    public const string Uncancel = "uncancel";
    public const string SetCodePrice = "set-code-price";
    public const string ConfirmBackup = "confirm-backup";
    public const string AddBackup = "add-backup";
    public const string MarkPrinted = "mark-printed";
    public const string Reprint = "reprint";
    public const string AttachShipment = "attach-shipment";
    public const string ShipmentDecision = "shipment-decision";
    public const string ForceEnd = "force-end";
}

/// <summary>Bir siparişin istemcinin gördüğü sürümü (<c>Orders.SyncVersion</c>, telde <c>Version</c>).</summary>
public sealed record SalesCommandExpected(Guid OrderId, int Version);

/// <summary>
/// Komut zarfı (§4.6): <c>{commandId, replicaId, protocol = 2, seenMode, kind, payload, expected}</c>. Komutlar yalnız
/// v2 konuşur (§4.0). <paramref name="CommandId"/> idempotency anahtarıdır: aynı içerikle yeniden gönderim kayıtlı
/// sonucu alır, farklı içerik 409 <c>content-conflict</c>.
/// </summary>
/// <param name="Payload">Türün yükü (<see cref="SalesCommandPayload"/>'ın teldeki hâli); yeniden denemede aynen.</param>
/// <param name="Expected">İstemcinin gördüğü satır sürümleri; kapsamı türe özgüdür (sunucunun <c>TryMatchExpected</c>'i).</param>
public sealed record SalesCommandRequest(
    Guid CommandId,
    Guid ReplicaId,
    int Protocol,
    int SeenMode,
    string Kind,
    JsonElement Payload,
    IReadOnlyList<SalesCommandExpected> Expected);

// ── yükler (sunucudaki işleyicilerin Payload kayıtları) ─────────────────────────────────────────────

/// <summary>Bir komut türünün yükü. <see cref="Kind"/> telde yazılmaz; zarfın <c>kind</c>'ıdır.</summary>
public abstract record SalesCommandPayload
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [JsonIgnore]
    public abstract string Kind { get; }

    /// <summary>Yükün teldeki JSON'u (camelCase, gerçek tipiyle). Bir niyet için bir kez üretilir ve her yeniden
    /// denemede AYNEN gönderilir: sunucu aynı <c>commandId</c>'yi içerik özetiyle tanır (§4.6 adım 2).</summary>
    public string ToWireJson() => JsonSerializer.Serialize(this, GetType(), WireOptions);
}

/// <summary><c>cancel</c>: <c>{items, reason}</c> (<c>CancelCommandHandler.Payload</c>).</summary>
public sealed record SalesCancelPayload(IReadOnlyList<Guid> Items, string Reason) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.Cancel;
}

/// <summary><c>uncancel</c>: <c>{items}</c> (<c>UncancelCommandHandler.Payload</c>).</summary>
public sealed record SalesUncancelPayload(IReadOnlyList<Guid> Items) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.Uncancel;
}

/// <summary><c>set-code-price</c>: <c>{sessionId, code, alsoSelectedIds, newPrice, paidFromBalanceIds}</c>
/// (<c>SetCodePriceCommandHandler.Payload</c>). <paramref name="Code"/> kodsuz seçimde null; <c>expected</c> planın
/// tamamıdır.</summary>
public sealed record SalesSetCodePricePayload(
    Guid SessionId,
    string? Code,
    IReadOnlyList<Guid> AlsoSelectedIds,
    decimal NewPrice,
    IReadOnlyList<Guid> PaidFromBalanceIds) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.SetCodePrice;
}

/// <summary><c>confirm-backup</c>: <c>{backupOrderId, newPrice?}</c> (<c>ConfirmBackupCommandHandler.Payload</c>).</summary>
public sealed record SalesConfirmBackupPayload(Guid BackupOrderId, decimal? NewPrice) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.ConfirmBackup;
}

/// <summary><c>add-backup</c>'ın müşterisi (<c>AddBackupCommandHandler.Customer</c>); Id ham (asıl kayda sunucu çözer).</summary>
public sealed record SalesAddBackupCustomer(string Id, string Platform, string Username, string? DisplayName);

/// <summary><c>add-backup</c>: <c>{newOrderId, parentOrderId, customer, messageText, price?, code?}</c>
/// (<c>AddBackupCommandHandler.Payload</c>). <c>expected</c> boş: komut var olan satırı yazmaz.</summary>
public sealed record SalesAddBackupPayload(
    Guid NewOrderId,
    Guid ParentOrderId,
    SalesAddBackupCustomer Customer,
    string MessageText,
    decimal? Price,
    string? Code) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.AddBackup;
}

/// <summary><c>mark-printed</c>'in bir öğesi: sipariş ve kâğıttaki sırası (1'den).</summary>
public sealed record SalesMarkPrintedItem(Guid OrderId, int Seq);

/// <summary><c>mark-printed</c>: <c>{batchId, items [{orderId, seq}]}</c> (<c>MarkPrintedCommandHandler.Payload</c>).</summary>
public sealed record SalesMarkPrintedPayload(string BatchId, IReadOnlyList<SalesMarkPrintedItem> Items) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.MarkPrinted;
}

/// <summary><c>reprint</c>: <c>{orderIds}</c>, istenen (kâğıt) sırayla (<c>ReprintCommandHandler.Payload</c>).
/// <c>expected</c> boş: yazmaz.</summary>
public sealed record SalesReprintPayload(IReadOnlyList<Guid> OrderIds) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.Reprint;
}

/// <summary><c>attach-shipment</c>: <c>{customerId, proposedShipmentId}</c> (<c>AttachShipmentCommandHandler.Payload</c>);
/// <c>expected</c> istemcinin gördüğü bağlanmamış aday kümesinin tamamıdır.</summary>
public sealed record SalesAttachShipmentPayload(string CustomerId, Guid ProposedShipmentId) : SalesCommandPayload
{
    [JsonIgnore]
    public override string Kind => SalesCommandKinds.AttachShipment;
}

/// <summary><c>shipment-decision</c>: <c>{shipmentId, decision, expectedVersion}</c>
/// (<c>ShipmentDecisionCommandHandler.Payload</c>). Dosyanın sürümü yükte; <c>expected</c> boş.</summary>
public sealed record SalesShipmentDecisionPayload(Guid ShipmentId, string Decision, int ExpectedVersion) : SalesCommandPayload
{
    public const string ShipNow = "ship-now";
    public const string Hold = "hold";
    public const string RecipientPays = "recipient-pays";

    [JsonIgnore]
    public override string Kind => SalesCommandKinds.ShipmentDecision;
}

/// <summary><c>force-end</c>: <c>{sessionId, confirm: true}</c> (<c>ForceEndCommandHandler</c>): onay her zaman
/// true — operatör onayı arayüzde alınır (§5.7).</summary>
public sealed record SalesForceEndPayload(Guid SessionId) : SalesCommandPayload
{
    public bool Confirm => true;

    [JsonIgnore]
    public override string Kind => SalesCommandKinds.ForceEnd;
}

// ── yanıt ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Sonuçta bir siparişin komuttan sonraki sürümü.</summary>
public sealed record SalesCommandOrderVersion(Guid Id, int Version);

/// <summary>Sonuçta türetilen bir dosyanın komuttan sonraki sürümü ve tutarı (K7).</summary>
public sealed record SalesCommandShipmentAmount(Guid Id, int Version, decimal Amount);

/// <summary>
/// Komutun kayıtlı sonucu (<c>SalesCommandLogs.ResultJson</c>, §3.1, §9): yalnız Id, sürüm, tutar ve sonuç Id
/// listeleri. Türe özgü listeler yalnız o türde dolu, öbürlerinde null.
/// </summary>
/// <param name="Changed">Komutun yazdığı siparişler.</param>
/// <param name="Skipped">İstenip yazacak bir şeyi olmayan siparişler (ör. zaten iptal).</param>
/// <param name="Marked"><c>mark-printed</c>: bu komutla basılmış işaretlenenler.</param>
/// <param name="AlreadyPrinted"><c>mark-printed</c>: zaten basılmış olanlar.</param>
/// <param name="Cancelled"><c>mark-printed</c>: iptal edilmiş olanlar — basılmaz, "kâğıdı imha et" (§5.6).</param>
/// <param name="ReprintIds"><c>reprint</c>: yeniden basılacaklar, istenen sırayla.</param>
/// <param name="CorrectionIds"><c>reprint</c>: bunlardan fiyatı düzeltilmiş olanlar ("DÜZELTME").</param>
/// <param name="ShipmentId"><c>attach-shipment</c>: siparişlerin bağlandığı dosya.</param>
/// <param name="Pool"><c>attach-shipment</c>: kişinin açık dosyalarının toplamı (güncel dosyalardan).</param>
/// <param name="PoolShipmentIds"><c>attach-shipment</c>: havuzun dosyaları, en yenisi başta.</param>
/// <param name="Decided"><c>shipment-decision</c>: kararın uygulandığı dosyalar.</param>
/// <param name="AlreadyReleased"><c>force-end</c>: yayın zaten devredilmişti; komut etkisiz.</param>
public sealed record SalesCommandResult(
    IReadOnlyList<Guid>? Changed,
    IReadOnlyList<Guid>? Skipped,
    IReadOnlyList<SalesCommandOrderVersion>? Orders,
    IReadOnlyList<SalesCommandShipmentAmount>? Shipments,
    IReadOnlyList<Guid>? Marked = null,
    IReadOnlyList<Guid>? AlreadyPrinted = null,
    IReadOnlyList<Guid>? Cancelled = null,
    IReadOnlyList<Guid>? ReprintIds = null,
    IReadOnlyList<Guid>? CorrectionIds = null,
    Guid? ShipmentId = null,
    decimal? Pool = null,
    IReadOnlyList<Guid>? PoolShipmentIds = null,
    IReadOnlyList<SalesCommandShipmentAmount>? Decided = null,
    bool? AlreadyReleased = null);

/// <summary>
/// Başarılı komutun (ve yeniden oynatmanın) yanıtı (§4.6 adım 7). Satırlar akış öğesi biçiminde ve GÜNCEL; istemci
/// onları akışla aynı fonksiyonla yazar (<c>SalesFeedRepository.ApplyItem</c>).
/// </summary>
/// <param name="Status"><see cref="StatusApplied"/> ya da <see cref="StatusUnchanged"/>.</param>
public sealed record SalesCommandResponse(
    string Status,
    IReadOnlyList<SalesOrderItem> Orders,
    IReadOnlyList<SalesShipmentItem> Shipments,
    IReadOnlyList<SalesSessionItem> Sessions,
    SalesCommandResult Result)
{
    public const string StatusApplied = "applied";
    public const string StatusUnchanged = "unchanged";
}

/// <summary>Komut ucunun ret kodları (ProblemDetails <c>title</c>'ı).</summary>
public static class SalesCommandProblemCodes
{
    // 409 (SalesCommandService, SalesCommandRejection)
    public const string ModeOff = "mode-off";
    public const string ClientUpdateRequired = "client-update-required";
    public const string ContentConflict = "content-conflict";
    public const string Stale = "stale";
    public const string PlanStale = "plan-stale";
    public const string SessionLive = "session-live";
    public const string ShipmentShipped = "shipment-shipped";
    public const string HandingOver = "handing-over";
    public const string OwnerActive = "owner-active";
    public const string TooManyCandidates = "too-many-candidates";

    // 400 — istemci hatası
    public const string InvalidEnvelope = "invalid-envelope";
    public const string InvalidCommand = "invalid-command";
    public const string UnknownKind = "unknown-kind";
    public const string InvalidExpected = "invalid-expected";
    public const string InvalidPayload = "invalid-payload";
    public const string UnknownOrder = "unknown-order";
    public const string UnknownSession = "unknown-session";
    public const string UnknownShipment = "unknown-shipment";
}

/// <summary>
/// Komut reddi (400/409 ProblemDetails): <c>title</c> = <see cref="Code"/>; ek üyeler sunucudaki
/// <c>SalesCommandRejection</c>'ın yazdıkları. Yalnız ilgili ret doldurur; öbürleri null.
/// </summary>
/// <param name="Current"><c>stale</c>, <c>plan-stale</c>, <c>shipment-shipped</c>: siparişlerin güncel hâli.</param>
/// <param name="CurrentShipments">Dosyaların güncel hâli (<c>shipment-decision</c>'ın <c>stale</c>'i, kargoya
/// verilmiş dosya).</param>
/// <param name="Plan"><c>plan-stale</c>: sunucunun güncel planı.</param>
/// <param name="Candidates"><c>attach-shipment</c>'ın <c>stale</c>'i: sunucunun güncel aday kümesi.</param>
/// <param name="Count"><c>too-many-candidates</c>: kümenin sayısı.</param>
/// <param name="Max"><c>too-many-candidates</c>: tek komutun tavanı.</param>
/// <param name="Machine"><c>session-live</c>, <c>owner-active</c>, <c>handing-over</c>: yayının sahibi bilgisayar.</param>
/// <param name="OwnerLastSeenAt"><c>session-live</c>, <c>handing-over</c>: sahibin son kalp atışı.</param>
/// <param name="LastSeenAt"><c>owner-active</c>: sahibin son kalp atışı.</param>
/// <param name="UnsentLabelCount"><c>handing-over</c>: sahipte gönderilmeyi bekleyen etiket sayısı.</param>
/// <param name="OrderIds"><c>unknown-order</c>: bu lisansta olmayan siparişler.</param>
public sealed record SalesCommandProblem(
    [property: JsonPropertyName("title")] string? Code,
    string? Detail,
    int? Status,
    IReadOnlyList<SalesOrderItem>? Current,
    IReadOnlyList<SalesShipmentItem>? CurrentShipments,
    IReadOnlyList<SalesCommandExpected>? Plan,
    IReadOnlyList<SalesCommandExpected>? Candidates,
    int? Count,
    int? Max,
    string? Machine,
    DateTimeOffset? OwnerLastSeenAt,
    DateTimeOffset? LastSeenAt,
    int? UnsentLabelCount,
    Guid? SessionId,
    Guid? OrderId,
    Guid? ShipmentId,
    IReadOnlyList<Guid>? OrderIds);

/// <summary><see cref="LicenseApiClient.PostSalesCommandAsync"/>'ın taşıma sonucu.</summary>
public enum SalesCommandTransport
{
    /// <summary>200: <see cref="SalesCommandHttpResult.Response"/> dolu.</summary>
    Ok,

    /// <summary>400 ya da 409 başlıklı ProblemDetails: <see cref="SalesCommandHttpResult.Problem"/> dolu.</summary>
    Problem,

    /// <summary>404: komut ucu yok (S2 öncesi imaj ya da geri alma penceresi, §8.6).</summary>
    EndpointMissing,

    /// <summary>Ağ hatası ya da zaman aşımı: sunucu komutu almış olabilir — aynı niyet aynen yeniden gönderilir.</summary>
    Offline,

    /// <summary>5xx, beklenmeyen kod ya da çözümlenemeyen gövde. Komut uygulanmış olabilir; aynı niyet aynen yeniden
    /// gönderilebilir.</summary>
    Failed,
}

/// <param name="StatusCode">HTTP kodu; ağ hatasında 0.</param>
/// <param name="Error">Günlük için kısa açıklama (kişisel veri yok).</param>
public sealed record SalesCommandHttpResult(
    SalesCommandTransport Transport,
    int StatusCode,
    SalesCommandResponse? Response = null,
    SalesCommandProblem? Problem = null,
    string? Error = null);
