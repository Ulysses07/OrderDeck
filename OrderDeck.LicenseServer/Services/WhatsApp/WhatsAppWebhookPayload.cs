using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Meta webhook gövdesinden çıkarılan, bizim ilgilendiğimiz olaylar.</summary>
public sealed record WhatsAppWebhookEvents(
    IReadOnlyList<WhatsAppInboundMessage> Messages,
    IReadOnlyList<WhatsAppStatusUpdate> Statuses)
{
    public static readonly WhatsAppWebhookEvents Empty =
        new(Array.Empty<WhatsAppInboundMessage>(), Array.Empty<WhatsAppStatusUpdate>());

    public bool IsEmpty => Messages.Count == 0 && Statuses.Count == 0;
}

/// <summary>Gelen (ya da echo ile geri düşen) tek bir mesaj.</summary>
public sealed record WhatsAppInboundMessage(
    string PhoneNumberId,
    string WamId,
    string FromPhone,
    string? ProfileName,
    string Type,
    string? Body,
    string? MediaId,
    string? MediaMimeType,
    DateTimeOffset Timestamp,
    /// <summary>true = yayıncının Business App'ten attığı mesajın echo'su
    /// (<c>smb_message_echoes</c>). Bu mesaj 24s penceresini AÇMAZ.</summary>
    bool IsEcho);

/// <summary>Giden mesajın teslim durumu güncellemesi.</summary>
public sealed record WhatsAppStatusUpdate(
    string WamId,
    string Status,
    DateTimeOffset Timestamp,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Meta webhook JSON'unu ayrıştırır. <see cref="JsonDocument"/> ile toleranslı
/// okur: tanımadığımız alan/tip webhook'u düşürmez — Meta yeni alan eklediğinde
/// ayrıştırma patlamamalı, aksi halde Hangfire retry döngüsüne gireriz.
///
/// <para>İlgilendiğimiz <c>field</c> değerleri: <c>messages</c> (gelen mesaj +
/// durum güncellemeleri) ve <c>smb_message_echoes</c> (yayıncının telefondan
/// attığı mesajlar).</para>
/// </summary>
public static class WhatsAppWebhookParser
{
    public static WhatsAppWebhookEvents Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return WhatsAppWebhookEvents.Empty;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException) { return WhatsAppWebhookEvents.Empty; }

        using (doc)
        {
            var messages = new List<WhatsAppInboundMessage>();
            var statuses = new List<WhatsAppStatusUpdate>();

            if (!doc.RootElement.TryGetProperty("entry", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return WhatsAppWebhookEvents.Empty;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) ||
                    changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    var field = Str(change, "field") ?? "";
                    var isEcho = field == "smb_message_echoes";
                    if (field != "messages" && !isEcho) continue;
                    if (!change.TryGetProperty("value", out var value)) continue;

                    var phoneNumberId = value.TryGetProperty("metadata", out var meta)
                        ? Str(meta, "phone_number_id") ?? ""
                        : "";

                    var profileNames = ReadContactNames(value);

                    // Gelen mesajlar: "messages"; echo'lar: "message_echoes".
                    var listName = isEcho ? "message_echoes" : "messages";
                    if (value.TryGetProperty(listName, out var msgs) &&
                        msgs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in msgs.EnumerateArray())
                        {
                            var parsed = ParseMessage(m, phoneNumberId, profileNames, isEcho);
                            if (parsed is not null) messages.Add(parsed);
                        }
                    }

                    if (value.TryGetProperty("statuses", out var sts) &&
                        sts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sts.EnumerateArray())
                        {
                            var parsed = ParseStatus(s);
                            if (parsed is not null) statuses.Add(parsed);
                        }
                    }
                }
            }

            return messages.Count == 0 && statuses.Count == 0
                ? WhatsAppWebhookEvents.Empty
                : new WhatsAppWebhookEvents(messages, statuses);
        }
    }

    private static Dictionary<string, string> ReadContactNames(JsonElement value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!value.TryGetProperty("contacts", out var contacts) ||
            contacts.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var c in contacts.EnumerateArray())
        {
            var waId = Str(c, "wa_id");
            if (string.IsNullOrEmpty(waId)) continue;
            if (c.TryGetProperty("profile", out var profile))
            {
                var name = Str(profile, "name");
                if (!string.IsNullOrWhiteSpace(name)) map[waId] = name!;
            }
        }
        return map;
    }

    private static WhatsAppInboundMessage? ParseMessage(
        JsonElement m, string phoneNumberId, Dictionary<string, string> profileNames, bool isEcho)
    {
        var wamId = Str(m, "id");
        if (string.IsNullOrEmpty(wamId)) return null;

        // Gelen mesajda "from" = müşteri. Echo'da "from" = işletme, karşı taraf "to".
        var counterparty = WaPhone.Canonical(isEcho ? Str(m, "to") : Str(m, "from"));
        if (counterparty.Length == 0) return null;

        var type = Str(m, "type") ?? "unknown";
        string? body = null;
        string? mediaId = null;
        string? mime = null;

        switch (type)
        {
            case "text":
                if (m.TryGetProperty("text", out var t)) body = Str(t, "body");
                break;

            case "image" or "video" or "audio" or "document" or "sticker":
                if (m.TryGetProperty(type, out var media))
                {
                    mediaId = Str(media, "id");
                    mime = Str(media, "mime_type");
                    body = Str(media, "caption");
                }
                break;

            case "button":
                if (m.TryGetProperty("button", out var btn)) body = Str(btn, "text");
                break;

            case "interactive":
                if (m.TryGetProperty("interactive", out var ia))
                {
                    if (ia.TryGetProperty("button_reply", out var br)) body = Str(br, "title");
                    else if (ia.TryGetProperty("list_reply", out var lr)) body = Str(lr, "title");
                }
                break;

            case "location":
                if (m.TryGetProperty("location", out var loc))
                {
                    var lat = Num(loc, "latitude");
                    var lng = Num(loc, "longitude");
                    body = Str(loc, "name") ?? (lat is null || lng is null ? null
                        : string.Create(CultureInfo.InvariantCulture, $"{lat},{lng}"));
                }
                break;
        }

        profileNames.TryGetValue(counterparty, out var profileName);

        return new WhatsAppInboundMessage(
            phoneNumberId, wamId!, counterparty, profileName, type, body, mediaId, mime,
            ParseUnixSeconds(Str(m, "timestamp")), isEcho);
    }

    private static WhatsAppStatusUpdate? ParseStatus(JsonElement s)
    {
        var wamId = Str(s, "id");
        if (string.IsNullOrEmpty(wamId)) return null;

        string? errCode = null;
        string? errMsg = null;
        if (s.TryGetProperty("errors", out var errs) &&
            errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var e = errs[0];
            errCode = e.TryGetProperty("code", out var c) ? c.ToString() : null;
            errMsg = Str(e, "title") ?? Str(e, "message");
        }

        return new WhatsAppStatusUpdate(
            wamId!, Str(s, "status") ?? "unknown", ParseUnixSeconds(Str(s, "timestamp")), errCode, errMsg);
    }

    /// <summary>Meta timestamp'i saniye cinsinden <b>string</b> gönderir.
    /// Bozuk/eksikse şimdiki zamana düşülür (mesajı kaybetmektense sırası bozulsun).</summary>
    private static DateTimeOffset ParseUnixSeconds(string? raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : DateTimeOffset.UtcNow;

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
