using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Meta webhook gövdesinden çıkarılan, bizim ilgilendiğimiz olaylar.</summary>
public sealed record WhatsAppWebhookEvents(
    IReadOnlyList<WhatsAppInboundMessage> Messages,
    IReadOnlyList<WhatsAppStatusUpdate> Statuses,
    /// <summary>
    /// Telefon numarası taşımadığı için ayrıştırılamayan mesajlar.
    ///
    /// <para>Kullanıcı adı özelliğini açmış bir müşteri, son 30 gün içinde
    /// yazışmadıysak ve Meta'nın kişi defterinde değilse, webhook'ta
    /// <c>wa_id</c>/<c>from</c> göndermiyor — yerine yalnız BSUID geliyor.
    /// Sohbet modelimiz telefona anahtarlı olduğu için bu mesajı henüz
    /// kaydedemiyoruz. Buradaki amaç mesajı kurtarmak değil, <b>kaybın sessiz
    /// olmasını engellemek</b>: yayıncı yazan müşteriyi görmüyorken hiçbir iz
    /// kalmıyordu. Gerçek çözüm BSUID'i sohbet kimliği yapmak (ayrı iş); bu
    /// liste o işin ne kadar acil olduğunu ölçüyor.</para>
    /// </summary>
    IReadOnlyList<WhatsAppDroppedInbound> DroppedNoPhone,

    /// <summary>Pazarlama mesajı tercihi değişiklikleri (<c>user_preferences</c>).</summary>
    IReadOnlyList<WhatsAppUserPreference> UserPreferences,

    /// <summary>Coexistence rehber senkronu (<c>smb_app_state_sync</c>): yayıncının
    /// telefonundaki kişiler.</summary>
    IReadOnlyList<WhatsAppContactSync> Contacts)
{
    public static readonly WhatsAppWebhookEvents Empty =
        new(Array.Empty<WhatsAppInboundMessage>(), Array.Empty<WhatsAppStatusUpdate>(),
            Array.Empty<WhatsAppDroppedInbound>(), Array.Empty<WhatsAppUserPreference>(),
            Array.Empty<WhatsAppContactSync>());

    /// <summary>
    /// İşlenecek bir şey var mı?
    ///
    /// <para><b>Listelerin HEPSİ sayılmak ZORUNDA</b>, çünkü hepsi kalıcılaşıyor.
    /// <see cref="DroppedNoPhone"/> eskiden dışarıdaydı — o zaman yalnız
    /// loglanıyordu ve log bu kontrolden önce çalışıyordu. Artık deftere
    /// yazılıyor: dışarıda bırakılsaydı <b>yalnız numarasız mesaj içeren bir
    /// paket</b> erken döner ve ölçüm hiç çalışmazdı. Üstelik kaybın en saf
    /// hâli tam olarak o paket — tek mesaj, o da numarasız. Aynı gerekçe
    /// <see cref="Contacts"/> için de geçerli: rehber senkronu kendi paketinde
    /// gelir, mesaj taşımaz.</para>
    /// </summary>
    public bool IsEmpty =>
        Messages.Count == 0 && Statuses.Count == 0 &&
        DroppedNoPhone.Count == 0 && UserPreferences.Count == 0 &&
        Contacts.Count == 0;
}

/// <summary>
/// Coexistence rehber senkronundan (<c>smb_app_state_sync</c>) tek kişi.
///
/// <para>Bu ad, müşterinin WhatsApp profil adı DEĞİL — yayıncının kendi
/// telefonuna kaydettiği addır. İkisi farklı şeyler, bu yüzden tüketici onu
/// yalnız profil adı boşken kullanıyor.</para>
/// </summary>
public sealed record WhatsAppContactSync(
    string PhoneNumberId,
    string Phone,
    string? FullName,
    /// <summary><c>add</c> | <c>update</c> | <c>remove</c>. Tanımadığımız bir
    /// değer olduğu gibi taşınır; kararı tüketici verir.</summary>
    string Action);

/// <summary>Numarasız geldiği için panele düşemeyen bir gelen mesajın izi.</summary>
public sealed record WhatsAppDroppedInbound(
    string PhoneNumberId,
    /// <summary><c>from_user_id</c>. Hiç kimlik yoksa boş string gelir — o
    /// mesajı deftere yazamayız, kimin olduğunu bilmiyoruz.</summary>
    string UserId,
    DateTimeOffset Timestamp);

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
    /// <summary>true = mesaj yayıncı tarafından yazılmış (giden). Canlı akışta
    /// <c>smb_message_echoes</c>, geçmiş aktarımında yönü <c>from</c> belirler.
    /// Bu mesaj 24s penceresini AÇMAZ.</summary>
    bool IsEcho,

    /// <summary>
    /// true = mesaj <c>history</c> webhook'uyla gelen GEÇMİŞ kayıt, canlı bir
    /// olay değil.
    ///
    /// <para>Yön bilgisinden ayrı tutulmak zorunda: geçmiş hem gelen hem giden
    /// mesaj taşıyor, ama hiçbiri canlı bir olayın yan etkilerini
    /// tetiklememeli. Coexistence onboarding'i 180 günlük arşivi tek seferde
    /// akıtıyor; bu bayrak olmasaydı yayıncı panelde yüzlerce okunmamış
    /// rozetiyle, kapattığı sohbetlerin hepsi yeniden açılmış hâlde ve aylar
    /// önceki her belgeye "Dekont geldi" etiketi yapışmış olarak karşılaşırdı.
    /// Ayrıca arşivdeki her medya R2'ye yeniden inerdi.</para>
    /// </summary>
    bool IsHistory = false);

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
/// durum güncellemeleri), <c>smb_message_echoes</c> (yayıncının telefondan
/// attığı mesajlar), <c>user_preferences</c> (pazarlama tercihi) ve
/// coexistence'ın iki senkron alanı: <c>history</c> (geçmiş sohbetler) +
/// <c>smb_app_state_sync</c> (telefondaki rehber).</para>
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
            var droppedNoPhone = new List<WhatsAppDroppedInbound>();
            var preferences = new List<WhatsAppUserPreference>();
            var contacts = new List<WhatsAppContactSync>();

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
                    var isHistory = field == "history";
                    var isStateSync = field == "smb_app_state_sync";
                    if (field != "messages" && !isEcho && !isPreference &&
                        !isHistory && !isStateSync)
                    {
                        continue;
                    }
                    if (!change.TryGetProperty("value", out var value)) continue;

                    var phoneNumberId = value.TryGetProperty("metadata", out var meta)
                        ? Str(meta, "phone_number_id") ?? ""
                        : "";

                    if (isPreference)
                    {
                        ReadUserPreferences(value, phoneNumberId, preferences);
                        continue;
                    }

                    if (isStateSync)
                    {
                        ReadStateSync(value, phoneNumberId, contacts);
                        continue;
                    }

                    if (isHistory)
                    {
                        ReadHistory(value, phoneNumberId, messages);
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
                                droppedNoPhone.Add(new WhatsAppDroppedInbound(
                                    phoneNumberId,
                                    Str(m, "from_user_id") ?? "",
                                    ParseUnixSeconds(Str(m, "timestamp"))));
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
                   droppedNoPhone.Count == 0 && preferences.Count == 0 &&
                   contacts.Count == 0
                ? WhatsAppWebhookEvents.Empty
                : new WhatsAppWebhookEvents(
                    messages, statuses, droppedNoPhone, preferences, contacts);
        }
    }

    /// <summary>
    /// <c>history</c> paketini okur: <c>history[] → threads[] → messages[]</c>.
    ///
    /// <para><b>Yön thread'den çıkar.</b> Thread'in <c>id</c>'si karşı tarafın
    /// (müşterinin) numarası; mesajın <c>from</c>'u ona eşitse gelen, değilse
    /// giden. Canlı akıştaki gibi <c>from</c>/<c>to</c> ikilisine bakmak burada
    /// çalışmaz: geçmiş, aynı thread içinde iki yönü birden taşıyor.</para>
    ///
    /// <para><c>history_context</c> BİLEREK okunmuyor. Tek taşıdığı bilgi
    /// aylar öncesine ait teslim/okundu tiki; sohbet geçmişinde bir karara
    /// dönüşmüyor, ama taşımak durum sıralaması (<c>Rank</c>) için ikinci bir
    /// giriş yolu açardı.</para>
    ///
    /// <para>Chunk üstverisi (<c>phase</c>/<c>chunk_order</c>/<c>progress</c>)
    /// de okunmuyor: mesajlar <c>WamId</c> ile zaten idempotent yazılıyor,
    /// dolayısıyla sıra ve ilerleme yüzdesi bir şeyi değiştirmiyor.</para>
    /// </summary>
    private static void ReadHistory(
        JsonElement value, string phoneNumberId, List<WhatsAppInboundMessage> into)
    {
        if (!value.TryGetProperty("history", out var chunks) ||
            chunks.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var chunk in chunks.EnumerateArray())
        {
            if (!chunk.TryGetProperty("threads", out var threads) ||
                threads.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var thread in threads.EnumerateArray())
            {
                var counterparty = WaPhone.Canonical(Str(thread, "id"));
                // Karşı taraf numarası yoksa mesajı hangi sohbete koyacağımızı
                // bilmiyoruz; uydurmak yerine atlıyoruz.
                if (counterparty.Length == 0) continue;

                if (!thread.TryGetProperty("messages", out var msgs) ||
                    msgs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var m in msgs.EnumerateArray())
                {
                    var outgoing = WaPhone.Canonical(Str(m, "from")) != counterparty;
                    var parsed = ParseMessage(
                        m, phoneNumberId, EmptyProfileNames,
                        isEcho: outgoing, isHistory: true, knownCounterparty: counterparty);
                    if (parsed is not null) into.Add(parsed);
                }
            }
        }
    }

    /// <summary>Geçmişte <c>contacts[]</c> bloğu yok — profil adı canlı akıştan
    /// gelir. Her thread için boş sözlük ayırmamak adına tek örnek.</summary>
    private static readonly Dictionary<string, string> EmptyProfileNames =
        new(StringComparer.Ordinal);

    /// <summary>
    /// <c>smb_app_state_sync</c> paketini okur: yayıncının telefonundaki rehber.
    ///
    /// <para>Numarasız kişi atlanır — eşleştirebileceğimiz tek anahtar o.
    /// <c>type</c> bugün yalnız <c>contact</c>; başka bir tür gelirse
    /// yorumlamaya çalışmadan atlanıyor.</para>
    /// </summary>
    private static void ReadStateSync(
        JsonElement value, string phoneNumberId, List<WhatsAppContactSync> into)
    {
        if (!value.TryGetProperty("state_sync", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (Str(item, "type") != "contact") continue;
            if (!item.TryGetProperty("contact", out var contact)) continue;

            var phone = WaPhone.Canonical(Str(contact, "phone_number"));
            if (phone.Length == 0) continue;

            var fullName = Str(contact, "full_name");
            into.Add(new WhatsAppContactSync(
                phoneNumberId,
                phone,
                string.IsNullOrWhiteSpace(fullName) ? null : fullName,
                Str(item, "action") ?? "add"));
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

    /// <param name="knownCounterparty">
    /// Geçmiş aktarımında karşı taraf thread'den biliniyor; mesajın kendi
    /// <c>from</c>/<c>to</c> alanlarından çıkarmaya çalışmak orada yanlış
    /// sonuç verir (bkz. <see cref="ReadHistory"/>). Canlı akışta null.
    /// </param>
    private static WhatsAppInboundMessage? ParseMessage(
        JsonElement m, string phoneNumberId, Dictionary<string, string> profileNames,
        bool isEcho, bool isHistory = false, string? knownCounterparty = null)
    {
        var wamId = Str(m, "id");
        if (string.IsNullOrEmpty(wamId)) return null;

        // Gelen mesajda "from" = müşteri. Echo'da "from" = işletme, karşı taraf "to".
        var counterparty = knownCounterparty
            ?? WaPhone.Canonical(isEcho ? Str(m, "to") : Str(m, "from"));
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
            ParseUnixSeconds(Str(m, "timestamp")), isEcho, isHistory);
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
