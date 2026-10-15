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
/// <param name="OrderIds">Toplama giren siparişler (istemcinin kümesi).</param>
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

/// <summary><c>already-requested</c>'ın bir öncül kaydı (§6.4-e).</summary>
/// <param name="Machine">Kaydı yazan bilgisayarın adı; bilinmiyorsa null.</param>
/// <param name="At">Kira için kapanış anı (yoksa başlangıç), kayıt için başlangıç.</param>
/// <param name="Unconfirmed">Mesaj gitmiş olabilir, sonucu bilinmiyor.</param>
public sealed record PaymentRequestPrior(
    Guid Id,
    string? Machine,
    decimal? NetAmount,
    DateTimeOffset At,
    bool Unconfirmed);

/// <summary>Bir ödeme isteği ucunun sonucu; uç ProblemDetails'e ya da 200'e çevirir.</summary>
public abstract record PaymentRequestReply;

/// <summary>200.</summary>
public sealed record PaymentRequestSucceeded(PaymentRequestBeginResponse Response) : PaymentRequestReply;

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

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
