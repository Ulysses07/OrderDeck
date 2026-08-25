using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Meta webhook gövdesinden çıkarılan, bizim ilgilendiğimiz olaylar.</summary>
public sealed record WhatsAppWebhookEvents(
    IReadOnlyList<WhatsAppInboundMessage> Messages,
    IReadOnlyList<WhatsAppStatusUpdate> Statuses,
    /// <summary>
    /// Telefon numarası taşımadığı için ayrıştırılamayan mesajların BSUID'leri
    /// (<c>from_user_id</c>; yoksa boş string).
    ///
    /// <para>Kullanıcı adı özelliğini açmış bir müşteri, son 30 gün içinde
    /// yazışmadıysak ve Meta'nın kişi defterinde değilse, webhook'ta
    /// <c>wa_id</c>/<c>from</c> göndermiyor — yerine yalnız BSUID geliyor.
    /// Sohbet modelimiz telefona anahtarlı olduğu için bu mesajı henüz
    /// kaydedemiyoruz. Buradaki amaç kaydetmek değil, <b>kaybın sessiz
    /// olmasını engellemek</b>: yayıncı yazan müşteriyi görmüyorken logda hiçbir
    /// iz bulunmuyordu. Gerçek çözüm BSUID'i sohbet kimliği yapmak
    /// (ayrı iş); bu liste o işin ne kadar acil olduğunu ölçüyor.</para>
    /// </summary>
    IReadOnlyList<string> DroppedNoPhoneUserIds,

    /// <summary>Pazarlama mesajı tercihi değişiklikleri (<c>user_preferences</c>).</summary>
    IReadOnlyList<WhatsAppUserPreference> UserPreferences)
{
    public static readonly WhatsAppWebhookEvents Empty =
        new(Array.Empty<WhatsAppInboundMessage>(), Array.Empty<WhatsAppStatusUpdate>(),
            Array.Empty<string>(), Array.Empty<WhatsAppUserPreference>());

    /// <summary>
    /// İşlenecek bir şey var mı? <see cref="DroppedNoPhoneUserIds"/> BİLEREK
    /// dışarıda: o liste kaydedilmiyor, yalnız loglanıyor ve log bu kontrolden
    /// önce çalışıyor. <see cref="UserPreferences"/> ise kaydediliyor, dolayısıyla
    /// burada sayılmak ZORUNDA — sayılmasaydı yalnız tercih içeren bir paket
    /// erken dönüp sessizce düşerdi.
    /// </summary>
    public bool IsEmpty =>
        Messages.Count == 0 && Statuses.Count == 0 && UserPreferences.Count == 0;
}

/// <summary>
/// Müşterinin pazarlama mesajı tercihini değiştirdiği olay.
///
/// <para>Kimlik iki alandan gelebilir ve <b>ikisi de eksik olabilir</b>:
/// <c>wa_id</c> kullanıcı adı özelliğinde düşer, <c>user_id</c> (BSUID) her
/// payload'da bulunmayabilir. Ayrıştırıcı hiçbirini uydurmaz; kimliksiz olayı
/// tüketici eler.</para>
/// </summary>
public sealed record WhatsAppUserPreference(
    string PhoneNumberId,
    string? WaId,
    string? UserId,
    /// <summary>Bugün tek belgelenmiş değer: <c>marketing_messages</c>.</summary>
    string Category,
    /// <summary><c>stop</c> ya da <c>resume</c>. Tanımadığımız bir değer
    /// geldiğinde olduğu gibi taşınır — yorumlamak yerine saklamak, sessizce
    /// elemekten iyidir.</summary>
    string Value,
    DateTimeOffset Timestamp);

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
            var droppedNoPhone = new List<string>();
            var preferences = new List<WhatsAppUserPreference>();

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
                    var isPreference = field == "user_preferences";
                    if (field != "messages" && !isEcho && !isPreference) continue;
                    if (!change.TryGetProperty("value", out var value)) continue;

                    var phoneNumberId = value.TryGetProperty("metadata", out var meta)
                        ? Str(meta, "phone_number_id") ?? ""
                        : "";

                    if (isPreference)
                    {
                        ReadUserPreferences(value, phoneNumberId, preferences);
                        continue;
                    }

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
                            else if (HasNoPhone(m, isEcho))
                                droppedNoPhone.Add(Str(m, "from_user_id") ?? "");
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

            return messages.Count == 0 && statuses.Count == 0 &&
                   droppedNoPhone.Count == 0 && preferences.Count == 0
                ? WhatsAppWebhookEvents.Empty
                : new WhatsAppWebhookEvents(messages, statuses, droppedNoPhone, preferences);
        }
    }

    /// <summary>
    /// Mesaj yalnızca telefon numarası taşımadığı için mi elendi?
    ///
    /// <para><c>ParseMessage</c> birden çok sebeple null döner (kimliksiz mesaj,
    /// numarasız mesaj). Yalnız numarasızları saymak için ayrı bakıyoruz;
    /// aksi hâlde bozuk bir payload da BSUID sayacını şişirir ve ölçüm yalan
    /// söylerdi.</para>
    /// </summary>
    private static bool HasNoPhone(JsonElement m, bool isEcho)
    {
        if (string.IsNullOrEmpty(Str(m, "id"))) return false;
        var phone = WaPhone.Canonical(isEcho ? Str(m, "to") : Str(m, "from"));
        return phone.Length == 0;
    }

    /// <summary>
    /// <c>value.user_preferences[]</c> dizisini okur.
    ///
    /// <para><c>detail</c> alanı BİLEREK okunmuyor: Meta onu insan okusun diye
    /// yazıyor ("User requested to resume marketing messages") ve metni
    /// habersiz değiştirebilir. Karar <c>value</c> + <c>category</c>
    /// alanlarında; serbest metne dayanan bir mantık ilk yerelleştirmede
    /// sessizce bozulurdu.</para>
    ///
    /// <para><c>value</c>/<c>category</c> boşsa satır atlanır — kararı
    /// olmayan bir tercih kaydı, defteri kirletmekten başka işe yaramaz.
    /// Kimlik alanları ise burada elenmiyor: hangi kimliğin yeterli olduğuna
    /// tüketici karar verir.</para>
    /// </summary>
    private static void ReadUserPreferences(
        JsonElement value, string phoneNumberId, List<WhatsAppUserPreference> into)
    {
        if (!value.TryGetProperty("user_preferences", out var prefs) ||
            prefs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var p in prefs.EnumerateArray())
        {
            var decision = Str(p, "value");
            var category = Str(p, "category");
            if (string.IsNullOrWhiteSpace(decision) || string.IsNullOrWhiteSpace(category))
                continue;

            var waId = WaPhone.Canonical(Str(p, "wa_id"));
            var userId = Str(p, "user_id");

            into.Add(new WhatsAppUserPreference(
                phoneNumberId,
                waId.Length == 0 ? null : waId,
                string.IsNullOrWhiteSpace(userId) ? null : userId,
                category!,
                decision!,
                ParseUnixSeconds(Str(p, "timestamp") ?? NumAsString(p, "timestamp"))));
        }
    }

    /// <summary>Meta zaman damgasını mesajlarda <b>string</b>, tercih
    /// olaylarında <b>sayı</b> olarak gönderiyor. İkisini de kabul ediyoruz;
    /// tek biçime güvenmek, damgayı sessizce "şimdi"ye düşürürdü — ve bu
    /// alan sıralama otoritesi olduğu için yanlış tercihi kalıcı yapardı.</summary>
    private static string? NumAsString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetRawText()
            : null;

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
