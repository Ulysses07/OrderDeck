using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

// POST sales/commands'ın teldeki biçimi — spec 2026-10-13 §4.6. Zarf satış uçlarının v2 zarfıdır (§4.0); komutlar
// yalnız v2 konuşur.

/// <summary>Bir siparişin istemcinin gördüğü sürümü (<c>Orders.SyncVersion</c>, telde <c>Version</c>).</summary>
/// <param name="OrderId">Sipariş (etiket) Id'si.</param>
/// <param name="Version">Akıştan ya da önceki yanıttan bilinen sürüm.</param>
public sealed record SalesCommandExpectedDto(Guid? OrderId, int? Version);

/// <summary>
/// Komut zarfı (§4.6): <c>{commandId, replicaId, protocol = 2, seenMode, kind, payload, expected}</c>.
/// </summary>
/// <param name="CommandId">İstemcinin ürettiği kimlik; idempotency anahtarı. Aynı içerikle yeniden gönderim kayıtlı
/// sonucu döner, farklı içerik 409 <c>content-conflict</c>.</param>
/// <param name="Kind">Komut türü (<c>cancel</c>, <c>uncancel</c>, …); bilinmeyen tür 400 <c>unknown-kind</c>.</param>
/// <param name="Payload">Türe özgü yük; türün işleyicisi tipli okur.</param>
/// <param name="Expected">İstemcinin gördüğü satır sürümleri (iyimser eşzamanlılık). Kapsamı türe özgüdür.</param>
public sealed record SalesCommandRequest(
    Guid? CommandId,
    string? Kind,
    JsonElement? Payload,
    List<SalesCommandExpectedDto>? Expected,
    Guid? ReplicaId = null,
    int? Protocol = null,
    int? SeenMode = null) : ISalesEnvelopeFields;

/// <summary>
/// Başarılı komutun (ve yeniden oynatmanın) yanıtı (§4.6 adım 7). Satırlar akış öğesi biçimindedir
/// (<see cref="SalesOrderItem"/>, <see cref="SalesShipmentItem"/>, <see cref="SalesSessionItem"/>): istemci yanıtı akışla
/// aynı uygulama fonksiyonuyla yazar. Siparişlerde KVKK okuma maskesi akıştakiyle aynıdır.
/// </summary>
/// <param name="Status"><c>applied</c> (en az bir satır yazıldı) ya da <c>unchanged</c>.</param>
/// <param name="Orders">Komutun hedef siparişleri, GÜNCEL hâlleriyle (yeniden oynatmada da).</param>
/// <param name="Shipments">Bu siparişlerin bağlı olduğu ve komutun türettiği dosyalar, güncel hâlleriyle.</param>
/// <param name="Sessions">Siparişlerin oturumları, güncel hâlleriyle (akışın yan listesi gibi).</param>
/// <param name="Result">Komutun kayıtlı sonucu (<c>SalesCommandLogs.ResultJson</c>); yeniden oynatmada ilk
/// uygulamanınkiyle aynı.</param>
public sealed record SalesCommandResponse(
    string Status,
    IReadOnlyList<SalesOrderItem> Orders,
    IReadOnlyList<SalesShipmentItem> Shipments,
    IReadOnlyList<SalesSessionItem> Sessions,
    JsonElement Result);

/// <summary>Sonuçta bir siparişin komuttan sonraki sürümü.</summary>
public sealed record SalesCommandOrderVersion(Guid Id, int Version);

/// <summary>Sonuçta türetilen bir dosyanın komuttan sonraki sürümü ve tutarı (K7).</summary>
public sealed record SalesCommandShipmentAmount(Guid Id, int Version, decimal Amount);

/// <summary>
/// <c>SalesCommandLogs.ResultJson</c>'un biçimi (§3.1, §9): yalnız Id, sürüm, tutar ve sonuç Id listeleri — ad, yorum
/// ve iptal nedeni yok. Türe özgü listeler yalnız o türde yazılır (öbür türlerde alan yok).
/// </summary>
/// <param name="Changed">Komutun yazdığı siparişler.</param>
/// <param name="Skipped">İstenip yazacak bir şeyi olmayan siparişler (ör. zaten iptal); sürümleri ilerlemedi.</param>
/// <param name="Orders">Yazılan siparişlerin yeni sürümleri.</param>
/// <param name="Shipments">Türetilen dosyaların yeni sürüm ve tutarları.</param>
public sealed record SalesCommandResult(
    IReadOnlyList<Guid> Changed,
    IReadOnlyList<Guid> Skipped,
    IReadOnlyList<SalesCommandOrderVersion> Orders,
    IReadOnlyList<SalesCommandShipmentAmount> Shipments)
{
    /// <summary><c>mark-printed</c>: bu komutla basılmış işaretlenenler.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? Marked { get; init; }

    /// <summary><c>mark-printed</c>: zaten basılmış olanlar (ilk yazan kazandı, değişmedi).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? AlreadyPrinted { get; init; }

    /// <summary><c>mark-printed</c>: iptal edilmiş olanlar — basılmaz; istemci "kâğıdı imha et" der (§5.6).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? Cancelled { get; init; }

    /// <summary><c>reprint</c>: yeniden basılacaklar, istenen (kâğıt) sırayla — basılmış ve iptal edilmemiş olanlar
    /// (yerel <c>PrepareReprint</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? ReprintIds { get; init; }

    /// <summary><c>reprint</c>: yeniden basılacaklardan fiyatı düzeltilmiş olanlar (kâğıtta "DÜZELTME" işareti).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? CorrectionIds { get; init; }

    /// <summary><c>attach-shipment</c>: siparişlerin bağlandığı dosya (seçilen ya da açılan); bağlanacak aday yoksa
    /// yazılmaz (dosya açılmadı).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ShipmentId { get; init; }

    /// <summary>
    /// <c>attach-shipment</c>: müşterinin havuzu — bütün Pending/Held dosyalarının tutar toplamı, komuttan sonra
    /// (türetilmiş dosyalar türetilmiş tutarla, kabul edilmiş dosya saklanan tutarıyla, §4.10-6). İstemci eşiği bununla
    /// değerlendirir (§7.1, <c>EvaluateAfterPayment</c>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Pool { get; init; }

    /// <summary><c>attach-shipment</c>: havuzun dosyaları, en yenisi başta; yanıtın <c>shipments</c>'ında güncel
    /// hâlleriyle döner.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? PoolShipmentIds { get; init; }

    /// <summary><c>shipment-decision</c>: kararın uygulandığı dosyaların komuttan sonraki sürüm ve tutarları (kargoya
    /// verilen dosyada donmuş tutar).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SalesCommandShipmentAmount>? Decided { get; init; }

    /// <summary>Yanıtta güncel hâliyle dönen dosyalar: türetilenler, havuz ve kararın dosyaları.</summary>
    public IReadOnlyList<Guid> ResponseShipmentIds() =>
        Shipments.Select(s => s.Id)
            .Concat(PoolShipmentIds ?? [])
            .Concat(Decided?.Select(s => s.Id) ?? [])
            .Distinct()
            .ToList();
}

/// <summary>Komut ucunun sonucu: başarı ya da ProblemDetails'e çevrilen ret.</summary>
public abstract record SalesCommandReply;

/// <summary>200: komut uygulandı ya da yeniden oynatıldı.</summary>
public sealed record SalesCommandSucceeded(SalesCommandResponse Response) : SalesCommandReply;

/// <summary>Ret: <c>title</c> = <paramref name="Code"/>, ek üyeler <paramref name="Extensions"/>.</summary>
public sealed record SalesCommandFailed(
    int StatusCode, string Code, string Detail, IReadOnlyDictionary<string, object?> Extensions) : SalesCommandReply;
