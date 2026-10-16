using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

/// <summary>
/// <c>POST payment-requests/begin</c> gövdesi — spec 2026-10-13 §6.4 istemci adımı 3. v2 zarfı (<see cref="ISalesEnvelopeFields"/>)
/// ve isteğin kendisi. Alanların hepsi telde isteğe bağlıdır; eksik zorunlu alan 400'dür.
/// </summary>
/// <param name="RecordId">İstemcinin ürettiği kayıt Id'si; aynı Id ve aynı içerik yeniden oynatmadır (§6.4-a).</param>
/// <param name="OwnerId">Sahip satırın Id'si ("N" hex ya da GUID'siz ham Id); <paramref name="MemberIds"/>'te olmalı.</param>
/// <param name="MemberIds">Kişinin BÜTÜN üyeleri, GUID'siz olanlar dahil (ham Id).</param>
/// <param name="ScopeKey"><c>cumulative</c> ya da <c>session:{"N" hex}</c>.</param>
/// <param name="OrderIds">Toplama giren siparişler (istemcinin kümesi); sunucunun kümesiyle eşit olmalı (§6.4-f).</param>
/// <param name="ProductTotal">Siparişlerin toplamı; sunucu toplamıyla kuruşu kuruşuna eşit olmalı (§6.4-f).</param>
/// <param name="TotalBeforeBalance"><paramref name="ProductTotal"/> + lisans ayarıyla kargo (§6.2, §6.4-g).</param>
/// <param name="ShippingSettingsVersion">Tutarın hesaplandığı lisans kargo ayarı sürümü (satır yoksa 0).</param>
/// <param name="ConfirmedPriorIds">Kullanıcının "tekrar gönderilsin mi?" sorusunda onayladığı önceki kayıtlar; saklanmaz,
/// özete girmez.</param>
public sealed record PaymentRequestBeginRequest(
    Guid? RecordId,
    string? OwnerId,
    List<string?>? MemberIds,
    string? ScopeKey,
    List<Guid>? OrderIds,
    decimal? ProductTotal,
    decimal? TotalBeforeBalance,
    bool? RecipientPays,
    int? ShippingSettingsVersion,
    List<Guid>? ConfirmedPriorIds,
    Guid? ReplicaId = null,
    int? Protocol = null,
    int? SeenMode = null) : ISalesEnvelopeFields;

/// <summary>
/// <c>begin</c>'in 200 yanıtı (ilk kayıt ve yeniden oynatma): kaydın GÜNCEL hâli.
/// </summary>
/// <param name="State"><c>sending</c>, <c>delivered</c>, <c>unconfirmed</c> ya da <c>abandoned</c>
/// (<see cref="PaymentRequestWire.State"/>).</param>
/// <param name="Members">Kaydın üyeleri: istemcinin kümesi asıl kayda çözülmüş, sunucunun grubuyla birleşmiş; ordinal
/// sıralı. İstemci kapsam uzlaştırmasında bunları kullanır (§6.4 "Kapsam uzlaştırması").</param>
public sealed record PaymentRequestBeginResponse(
    Guid RecordId,
    string State,
    DateTimeOffset? LeaseUntil,
    IReadOnlyList<string> Members);

/// <summary>
/// <c>already-requested</c>'ın bir öncül kaydı (§6.4-e). Öncüller <paramref name="FinishedAt"/>'e (sunucu saati), eşitlikte
/// <paramref name="At"/>'e, sonra Id'ye göre eskiden yeniye sıralıdır: liste ucunun sırası, iki bilgisayarın saati
/// karşılaştırılmaz.
/// </summary>
/// <param name="Machine">Kaydı yazan bilgisayarın adı; bilinmiyorsa null.</param>
/// <param name="NetAmount">İstenen net tutar (<c>finish</c> ya da geri doldurma); teslim kaydı olmadan dolan kirada
/// null.</param>
/// <param name="TotalBeforeBalance">Kiranın istediği bakiyeden önceki tutar (<c>begin</c>'in); kirasız kayıtta null.
/// <paramref name="NetAmount"/> null iken sorunun gösterebileceği tutar budur ("550 ₺ istenmiş olabilir").</param>
/// <param name="At">İsteğin anı, gösterim için: kirada sunucunun başlangıç anı, kirasız kayıtta istemcinin anı.</param>
/// <param name="FinishedAt">Kaydın kapanış anı, sunucu saati: <c>finish</c>, süresi dolmuş kirada kira sonu, kirasız
/// kayıtta sunucunun alış anı.</param>
/// <param name="Unconfirmed">Mesaj gitmiş olabilir, sonucu bilinmiyor.</param>
public sealed record PaymentRequestPrior(
    Guid Id,
    string? Machine,
    decimal? NetAmount,
    decimal? TotalBeforeBalance,
    DateTimeOffset At,
    DateTimeOffset? FinishedAt,
    bool Unconfirmed);

/// <summary>
/// <c>stale-total</c>'ın <c>liveElsewhere</c> öğesi (§6.4-f): sunucu toplamıyla istemcininki arasındaki farkı açıklayan,
/// başka kopyanın devredilmemiş (canlı ya da devrediliyor) yayını.
/// </summary>
/// <param name="Machine">Yayının sahibi bilgisayar (<c>OwnerMachineName</c>); bilinmiyorsa null.</param>
public sealed record PaymentRequestLiveSession(Guid SessionId, string? Machine);

/// <summary>
/// <c>POST payment-requests/{id}/finish</c> gövdesi — spec 2026-10-13 §6.4 "Akışın sonu". v2 zarfı ve kiranın sonucu.
/// </summary>
/// <param name="Outcome"><c>delivered</c>, <c>unconfirmed</c> ya da <c>abandoned</c> (ordinal).</param>
/// <param name="NetAmount">İstenen net tutar.</param>
/// <param name="AppliedAmount">Bakiyeden düşülen tutar; bilinmiyorsa null.</param>
/// <param name="FullyFromBalance">Tutarın tamamı bakiyeden karşılandı.</param>
public sealed record PaymentRequestFinishRequest(
    string? Outcome,
    decimal? NetAmount,
    decimal? AppliedAmount,
    bool? FullyFromBalance,
    Guid? ReplicaId = null,
    int? Protocol = null,
    int? SeenMode = null) : ISalesEnvelopeFields;

/// <summary><c>finish</c>'in 200 yanıtı (ilk yazım ve yeniden oynatma): kaydın GÜNCEL hâli.</summary>
public sealed record PaymentRequestFinishResponse(Guid RecordId, string State, DateTimeOffset? FinishedAt);

/// <summary>
/// <c>GET payment-requests</c>'in bir öğesi — spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması". Süresi dolmuş canlı
/// kira <c>unconfirmed</c> olarak, <paramref name="FinishedAt"/> = kiranın dolduğu an ile bildirilir (satır yazılmaz).
/// </summary>
/// <param name="Kind"><c>lease</c> ya da <c>record</c> (<see cref="PaymentRequestWire.Kind"/>).</param>
/// <param name="OrderIds">Kaydın kapsadığı siparişler, sıralı: uzak kayıtta "bu istek bu etiketi içeriyor mu" sorusu
/// (<c>Covers</c>) bununla cevaplanır, iki bilgisayarın saati karşılaştırılmaz.</param>
public sealed record PaymentRequestListItem(
    Guid Id,
    Guid ReplicaId,
    string? Machine,
    string Kind,
    string State,
    decimal? NetAmount,
    decimal? AppliedAmount,
    bool? FullyFromBalance,
    decimal? ProductTotal,
    decimal? TotalBeforeBalance,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<Guid> OrderIds);

/// <summary><c>GET payment-requests</c>'in 200 yanıtı: kapanış anına, eşitlikte başlangıca göre eskiden yeniye.</summary>
public sealed record PaymentRequestListResponse(IReadOnlyList<PaymentRequestListItem> Records);

/// <summary>
/// <c>POST payment-requests/backfill</c>'in bir öğesi — spec 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)": yerel
/// <c>PaymentJob</c>'un kirasız kaydı. Alanların hepsi telde isteğe bağlıdır; eksik zorunlu alan partinin 400'üdür.
/// </summary>
/// <param name="RecordId">İş Id'si ile teslim sayısından türetilmiş kayıt Id'si; yineleme zararsız.</param>
/// <param name="OwnerId">Sahip satır ("N" hex ya da GUID'siz ham Id); <paramref name="MemberIds"/>'te olmalı.</param>
/// <param name="ScopeKey"><c>cumulative</c>, <c>session:{"N" hex}</c>, <c>legacy</c> ya da <c>legacy:{anahtar}</c>.</param>
/// <param name="OrderIds">Yerelde <c>Covers</c> kuralıyla hesaplanmış, kaydın kapsadığı siparişler.</param>
/// <param name="State"><c>delivered</c> ya da <c>unconfirmed</c> (<c>HasUnconfirmedSend</c>).</param>
/// <param name="At">Kaydın yerel anı (istemci saati); bilgi ve gösterim içindir, sıralama sunucu saatiyle.</param>
public sealed record PaymentRequestBackfillItem(
    Guid? RecordId,
    string? OwnerId,
    List<string?>? MemberIds,
    string? ScopeKey,
    List<Guid>? OrderIds,
    string? State,
    decimal? NetAmount,
    decimal? AppliedAmount,
    bool? FullyFromBalance,
    DateTimeOffset? At);

/// <summary><c>POST payment-requests/backfill</c> gövdesi: v2 zarfı ve en çok 200 kayıt.</summary>
public sealed record PaymentRequestBackfillRequest(
    List<PaymentRequestBackfillItem?>? Records,
    Guid? ReplicaId = null,
    int? Protocol = null,
    int? SeenMode = null) : ISalesEnvelopeFields;

/// <summary>Geri doldurmanın öğe sonucu.</summary>
/// <param name="Status"><c>applied</c> (yazıldı), <c>unchanged</c> (aynı içerik zaten kayıtlı; yeni üyeler eklenir) ya da
/// <c>content-conflict</c> (aynı Id'de başka içerik, başka lisansın ya da kiranın Id'si).</param>
public sealed record PaymentRequestBackfillResult(Guid RecordId, string Status);

/// <summary><c>backfill</c>'in 200 yanıtı: istekteki sırayla, öğe başına bir sonuç.</summary>
public sealed record PaymentRequestBackfillResponse(IReadOnlyList<PaymentRequestBackfillResult> Records);

/// <summary>Bir ödeme isteği ucunun sonucu; uç ProblemDetails'e ya da 200'e çevirir.</summary>
public abstract record PaymentRequestReply;

/// <summary>200; <paramref name="Response"/> ucun yanıt gövdesi.</summary>
public sealed record PaymentRequestSucceeded(object Response) : PaymentRequestReply;

/// <summary>Ret: <c>title</c> = <paramref name="Code"/>, ek üyeler <paramref name="Extensions"/>.</summary>
public sealed record PaymentRequestFailed(
    int StatusCode, string Code, string Detail, IReadOnlyDictionary<string, object?> Extensions) : PaymentRequestReply;

/// <summary>Telde kullanılan adlar.</summary>
public static class PaymentRequestWire
{
    public static string State(PaymentRequestState state) => state switch
    {
        PaymentRequestState.Sending => "sending",
        PaymentRequestState.Delivered => "delivered",
        PaymentRequestState.Unconfirmed => "unconfirmed",
        PaymentRequestState.Abandoned => "abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static string Kind(PaymentRequestKind kind) => kind switch
    {
        PaymentRequestKind.Lease => "lease",
        PaymentRequestKind.Record => "record",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Bir kapanış durumunun teldeki adı (ordinal): <c>delivered</c>, <c>unconfirmed</c>, <c>abandoned</c>.
    /// <c>sending</c> kapanış değildir.</summary>
    public static bool TryParseOutcome(string? value, out PaymentRequestState state)
    {
        state = value switch
        {
            "delivered" => PaymentRequestState.Delivered,
            "unconfirmed" => PaymentRequestState.Unconfirmed,
            "abandoned" => PaymentRequestState.Abandoned,
            _ => PaymentRequestState.Sending,
        };
        return state != PaymentRequestState.Sending;
    }
}

/// <summary>
/// <c>begin</c> isteğinin özeti (<c>PaymentRequestRecords.RequestHash</c>, §6.4-a): aynı <c>recordId</c>'nin yeniden
/// gönderimi mi, yoksa aynı Id'ye başka içerik mi (409 <c>content-conflict</c>)? <c>SalesCommandHash</c> deseni: ham gövde
/// değil, doğrulanmış ve normalleştirilmiş istek özetlenir — üyeler ve siparişler tekilleştirilip ordinal sıralanır,
/// tutarlar iki ondalığa sabitlenir (<c>100</c> ile <c>100.00</c> aynı istektir). Alan sırası ya da JSON yazımı farklı
/// iki gönderim aynı özeti verir.
///
/// <para>Özete girmeyenler: kalp atışı alanları (<c>protocol</c>, <c>seenMode</c>) ve <c>confirmedPriorIds</c> (saklanmaz;
/// yanıtı kaybolan isteğin yeniden gönderimi kullanıcının sonraki onayıyla gelebilir). Kopya özete girer: aynı Id'yi
/// başka kopyanın göndermesi de çakışmadır. Sahip ve üyeler istemcinin ham yazımıyla girer: aradaki bir birleştirme
/// yeniden gönderimi çakışmaya çevirmesin.</para>
/// </summary>
public static class PaymentRequestHash
{
    public static byte[] Compute(
        Guid replicaId,
        string ownerId,
        IEnumerable<string> memberIds,
        string scopeKey,
        IEnumerable<Guid> orderIds,
        decimal productTotal,
        decimal totalBeforeBalance,
        bool recipientPays,
        int shippingSettingsVersion)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("replicaId", replicaId.ToString("N"));
            writer.WriteString("ownerId", ownerId);
            writer.WriteStartArray("memberIds");
            foreach (var member in memberIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                writer.WriteStringValue(member);
            writer.WriteEndArray();
            writer.WriteString("scopeKey", scopeKey);
            writer.WriteStartArray("orderIds");
            foreach (var order in orderIds.Distinct().Order())
                writer.WriteStringValue(order.ToString("N"));
            writer.WriteEndArray();
            writer.WriteString("productTotal", Money(productTotal));
            writer.WriteString("totalBeforeBalance", Money(totalBeforeBalance));
            writer.WriteBoolean("recipientPays", recipientPays);
            writer.WriteNumber("shippingSettingsVersion", shippingSettingsVersion);
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>
    /// Geri doldurulan kirasız kaydın özeti (<c>payment-requests/backfill</c>, §6.4 "Ödeme geçmişi"): asıl kayda
    /// çözülmüş sahip, kapsam, durum, tutarlar ve sıralı siparişler. Aynı normalleştirme (tekil, ordinal sıralı, iki
    /// ondalık).
    ///
    /// <para>Özete girmeyenler: üyeler (grup senkronu iki deneme arasında onları değiştirebilir; yeniden oynatmada yeni
    /// üyeler birleştirilir), kopya (geri yüklenmiş kopya aynı geçmişi yeniden gönderebilir; Id iş Id'sinden türer) ve
    /// yerel an (<c>at</c>: bilgi ve gösterim içindir). Sahip asıl kayda çözülmüş girer: iki veritabanı kopyasından biri
    /// aradaki yerel birleştirmeyle aynı işi başka bir kopya Id'siyle gönderse de aynı kayıttır.</para>
    /// </summary>
    public static byte[] ComputeRecord(
        string canonicalOwnerId,
        string scopeKey,
        PaymentRequestState state,
        decimal netAmount,
        decimal? appliedAmount,
        bool fullyFromBalance,
        IEnumerable<Guid> orderIds)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", PaymentRequestWire.Kind(PaymentRequestKind.Record));
            writer.WriteString("ownerId", canonicalOwnerId);
            writer.WriteString("scopeKey", scopeKey);
            writer.WriteString("state", PaymentRequestWire.State(state));
            writer.WriteString("netAmount", Money(netAmount));
            if (appliedAmount is { } applied) writer.WriteString("appliedAmount", Money(applied));
            else writer.WriteNull("appliedAmount");
            writer.WriteBoolean("fullyFromBalance", fullyFromBalance);
            writer.WriteStartArray("orderIds");
            foreach (var order in orderIds.Distinct().Order())
                writer.WriteStringValue(order.ToString("N"));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.ToArray());
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
