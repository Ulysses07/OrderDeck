using System.Security.Cryptography;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// Komut isteğinin özeti (<c>SalesCommandLogs.RequestHash</c>, spec 2026-10-13 §4.6 adım 2): aynı <c>CommandId</c>'nin
/// yeniden gönderimi mi, yoksa aynı Id'ye başka bir içerik mi (istemci hatası, 409 <c>content-conflict</c>)?
///
/// <para><b>Kanonik biçim.</b> Ham gövde değil, doğrulanmış istek özetlenir: tür, çağıran kopya, işleyicinin tipli
/// ve normalleştirdiği yük (ör. tekilleştirilmiş, sıralı öğeler) ve sipariş Id'sine göre sıralı beklenen sürümler.
/// Alan sırası, boşluk ya da JSON yazımı farklı ama anlamı aynı iki gönderim aynı özeti verir. Kopya özete girer:
/// aynı Id'yi başka bir kopyanın göndermesi de çakışmadır.</para>
///
/// <para>Özet kişisel metni (iptal nedeni) geri döndürülebilir biçimde saklamaz (§9).</para>
/// </summary>
public static class SalesCommandHash
{
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Compute(
        string kind, Guid replicaId, object canonicalPayload, IReadOnlyList<SalesCommandExpected> expected)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("replicaId", replicaId.ToString("N"));
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, canonicalPayload, canonicalPayload.GetType(), CanonicalOptions);
            writer.WriteStartArray("expected");
            foreach (var e in expected.OrderBy(e => e.OrderId).ThenBy(e => e.Version))
            {
                writer.WriteStartObject();
                writer.WriteString("orderId", e.OrderId.ToString("N"));
                writer.WriteNumber("version", e.Version);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.ToArray());
    }
}
