using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Ham hareket kaydını saklamadan önce BEYAZ LİSTEYE indirir (KVKK Ö-2, not karar 5): yalnız <see cref="AllowedFields"/>
/// kalır; bakiye, müşteri açıklaması, ek detaylar, alıcı adı, gönderen şubesi, IBAN/VKN/TCKN ve diğer her alan atılır. Açıklama
/// alanları <see cref="BankDescriptionMasker"/>'dan geçer (sayı olarak gelse de: istemci açıklamayı sayıdan da okur). İç içe
/// nesne ve dizi alınmaz (içeriği bilinmiyor). Kayıt yalnız teknik arıza incelemesi içindir; saklama süresi
/// <see cref="BankOptions.RawJsonRetentionDays"/>.
/// <para><b>Liste değişirse</b> gizlilik politikasındaki "bankanın gönderdiği kaydın kopyası" alan listesi de (iki kopya:
/// <c>Pages/Public/_GizlilikPolitikasi.cshtml</c>, <c>web/app/(tr)/gizlilik-politikasi/page.tsx</c>) değişmeli.</para>
/// <para>Özet + maske DTO'nun ham değerinden, bu işlemden ÖNCE üretilir. Saf ve idempotent (bank-data-cleanup eski kayıtları
/// bununla yeniden yazar).</para>
/// </summary>
public static class BankRawJsonRedactor
{
    /// <summary>Saklanan alanlar: açıklama, işlem açıklaması (açıklama yoksa açıklama sütunu ondan okunur), gönderen banka,
    /// işlem kodu, referans ve makbuz no. Gönderen adı (<c>GonderenAdi</c>) BİLEREK yok: sütuna da yazılmaz
    /// (<see cref="ObifinPollJob"/>, KVKK veri küçültme).</summary>
    public static readonly IReadOnlyList<string> AllowedFields =
        ["Aciklama", "IslemAciklama", "GonderenBanka", "IslemKodu", "ReferansNo", "MakbuzNo"];

    private static readonly HashSet<string> Allowed = new(AllowedFields, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DescriptionFields = new(["Aciklama", "IslemAciklama"], StringComparer.OrdinalIgnoreCase);

    // Türkçe karakterler \uXXXX diye kaçışlanmaz: saklanan kopya admin tanısı için insan gözüyle okunur; HTML'e değil DB'ye gider.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>İstemci satırı zaten ayrıştırdı; JSON nesnesi olmayan metin sözleşme ihlalidir →
    /// <see cref="ObifinProtocolException"/> (ham hâliyle sessizce saklanmaz).</summary>
    public static string Redact(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ObifinProtocolException("Ham hareket JSON'u ayrıştırılamadı."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ObifinProtocolException("Ham hareket JSON'u nesne değil.");
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!Allowed.Contains(property.Name)) continue;
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                    writer.WritePropertyName(property.Name);
                    if (DescriptionFields.Contains(property.Name)) WriteDescription(property.Value, writer);
                    else property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    private static void WriteDescription(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(BankDescriptionMasker.Mask(value.GetString()));
                break;
            case JsonValueKind.Number:
                // Sayı olarak gelen 11 haneli açıklama (TCKN) düz kalmasın; maskelenecek bir şey yoksa sayı aynen yazılır.
                var raw = value.GetRawText();
                var masked = BankDescriptionMasker.Mask(raw)!;
                if (masked == raw) value.WriteTo(writer);
                else writer.WriteStringValue(masked);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
