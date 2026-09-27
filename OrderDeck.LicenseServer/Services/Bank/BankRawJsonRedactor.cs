using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Ham hareket JSON'unu saklamadan önce kimlik alanlarını siler (spec §7): adı <c>IBAN</c>, <c>VKN</c> ya da
/// <c>TCKN</c> içeren (büyük/küçük harf duyarsız; ör. KarsiHesapIBAN, BorcluVKN, AmirVKN, LehdarTCKN) her
/// özelliğin değeri <see cref="Placeholder"/> olur — iç içe nesne ve dizilerde de. Adlar (GonderenAdi vb.) kalır;
/// ad zaten <c>CounterpartyName</c> sütununda. Hash + maske DTO'nun ham değerinden, redaksiyondan ÖNCE üretilir.
/// </summary>
public static class BankRawJsonRedactor
{
    public const string Placeholder = "[redakte]";

    private static readonly string[] IdentityMarkers = ["IBAN", "VKN", "TCKN"];

    // Türkçe karakterler \uXXXX diye kaçışlanmaz: saklanan kopya admin tanısı için insan gözüyle okunur; HTML'e
    // değil DB'ye gider.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>İstemci satırı zaten ayrıştırdı; JSON olmayan metin sözleşme ihlalidir →
    /// <see cref="ObifinProtocolException"/> (ham hâliyle sessizce saklanmaz).</summary>
    public static string Redact(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ObifinProtocolException("Ham hareket JSON'u ayrıştırılamadı."); }
        using (doc)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
                Write(doc.RootElement, writer);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    public static bool IsIdentityProperty(string name)
        => IdentityMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsIdentityProperty(property.Name)) writer.WriteStringValue(Placeholder);
                    else Write(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
