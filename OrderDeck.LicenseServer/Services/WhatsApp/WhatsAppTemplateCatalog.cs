using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Meta'daki bir şablonun panelin ihtiyaç duyduğu hâli — <b>her durumdan</b>
/// (APPROVED, PENDING, REJECTED, PAUSED…).
///
/// <para><b>Gövde metni neden taşınıyor:</b> onaylı metin Meta'da duruyor ve biz
/// hiçbir yerde saklamıyoruz. Panel yayıncıya "hangi mesaj gidecek" sorusunu
/// ancak bu alanla cevaplayabiliyor — şablonu adından seçtirmek, içeriğini
/// görmeden ücretli mesaj göndertmek demekti.</para>
///
/// <para><paramref name="UnsupportedReason"/> doluysa şablon listede görünür ama
/// gönderilemez. Gizlemek yerine sebebini yazıyoruz: yayıncı Meta'da onaylattığı
/// şablonu panelde hiç göremezse eksikliği bize değil kendi hesabına yorar.</para>
///
/// <para><paramref name="RejectedReason"/> Meta'nın ham kodu (örn.
/// <c>INVALID_FORMAT</c>). Çevirmiyoruz: ret sebebini aramaya çıkan yayıncı ancak
/// bu dizgeyle Meta belgelerinde karşılık bulabiliyor.</para>
/// </summary>
public sealed record WabaTemplate(
    string Id,
    string Name,
    string Language,
    string Category,
    string Status,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<WhatsAppTemplateButton> Buttons,
    int ParameterCount,
    IReadOnlyList<string> ParameterExamples,
    string? UnsupportedReason,
    string? RejectedReason);

/// <summary>Oluşturma yanıtı. <paramref name="Status"/> neredeyse her zaman
/// <c>PENDING</c>; Meta bazen anında onaylıyor, o yüzden sabitlemiyoruz.</summary>
public sealed record WhatsAppTemplateCreated(string Id, string Status);

/// <summary>WABA'nın onaylı şablon listesi. Yalnız HTTP yapar; DB'ye dokunmaz,
/// karar vermez — panel ucu testlerinde tek parça sahtelenebilsin diye.</summary>
public interface IWhatsAppTemplateCatalog
{
    /// <summary>Yalnız <c>APPROVED</c> şablonlar — gönderim listesi. Süzme
    /// <see cref="ListAllAsync"/> çıktısı üzerinde yapılır, ayrıştırıcıda değil.</summary>
    Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListApprovedAsync(
        string wabaId, string businessToken, CancellationToken ct);

    /// <summary>Durumdan bağımsız tüm şablonlar. Panelin yönetim ekranı onay
    /// bekleyeni ve reddedileni de göstermek zorunda; ayrıca yazma uçları
    /// sahipliği bu listeyle doğruluyor (Meta'nın düzenle/sil uçları WABA
    /// kapsamlı değil).</summary>
    Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
        string wabaId, string businessToken, CancellationToken ct);

    /// <summary>Yeni şablon oluşturur. Ad/kategori/dil ayrı parametre: Meta'nın
    /// düzenleme ucu bunları değiştiremediği için taslağın parçası değiller.</summary>
    Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
        string wabaId, string businessToken, string name, string category, string language,
        WhatsAppTemplateDraft draft, CancellationToken ct);

    /// <summary>Şablonun bileşenlerini günceller. Ad/kategori/dil gönderilmiyor:
    /// onaylı şablonda Meta zaten kabul etmiyor, panel de üçünü her durumda
    /// kilitliyor.</summary>
    Task<GraphResult<bool>> UpdateAsync(
        string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct);

    /// <summary>Şablonu siler. <paramref name="name"/> da isteniyor: Meta'nın
    /// ucu ada göre çalışıyor, <c>hsm_id</c> tek bir dil sürümüne daraltıyor.
    /// Yalnız ad gönderseydik aynı adın bütün dilleri silinirdi.</summary>
    Task<GraphResult<bool>> DeleteAsync(
        string wabaId, string businessToken, string templateId, string name, CancellationToken ct);
}

public sealed class WhatsAppTemplateCatalog : IWhatsAppTemplateCatalog
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppTemplateCatalog> _log;

    public WhatsAppTemplateCatalog(
        HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<WhatsAppTemplateCatalog> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    private const int PageLimit = 250;

    /// <summary>
    /// Sayfa sayısı tavanı. <see cref="PageLimit"/> ile çarpımı (5000) gerçek bir
    /// WABA'da görülecek şablon sayısının çok üstünde; buraya düşmek neredeyse
    /// kesinlikle imleç döngüsü demek. Tavana çarpınca elde olanı döndürmüyoruz —
    /// düzeltilen kusur zaten <b>sessiz eksik liste</b>ydi.
    /// </summary>
    private const int MaxPages = 20;

    private const int LogBodyLimit = 500;

    /// <summary>
    /// <para><b>Sayfalama neden var.</b> Eskiden tek sayfa isteniyordu, gerekçe
    /// "Meta zaten 250 şablona izin veriyor"du. O tavan hesap katmanına göre
    /// değişiyor ve yükseltilebiliyor; tavanı aşan yayıncı, panelde eksik bir
    /// liste görüyordu — üstelik <b>uyarısız</b>, yani Meta'da onaylattığı şablonu
    /// bulamayınca eksikliği kendi hesabına yoruyordu. Artık
    /// <c>paging.cursors.after</c> zinciri sonuna kadar izleniyor.</para>
    ///
    /// <para>Ara sayfalardan biri düşerse <b>tüm çağrı</b> hata döner. Kısmi liste
    /// döndürmek, düzeltilen kusurun tam kendisi olurdu.</para>
    /// </summary>
    public async Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        var url =
            $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}/{wabaId}/message_templates" +
            $"?fields=id,name,status,category,language,components,rejected_reason&limit={PageLimit}";

        var list = new List<WabaTemplate>();

        for (var page = 1; ; page++)
        {
            if (page > MaxPages)
            {
                _log.LogWarning(
                    "WhatsApp şablon sayfalaması {Max} sayfayı aştı (waba={Waba}) — imleç döngüsü olabilir",
                    MaxPages, wabaId);
                return GraphResult<IReadOnlyList<WabaTemplate>>.Failure(
                    "paging-limit", "şablon listesi sayfalaması bitmedi");
            }

            var (next, failure) = await ReadPageAsync(url, businessToken, wabaId, list, ct);
            if (failure is not null) return failure;
            if (string.IsNullOrEmpty(next)) break;
            url = next;
        }

        // Aynı şablonun birden çok dili olabiliyor; ada göre sıralamak
        // panelde dil varyantlarını yan yana getiriyor. Sıralama sayfa
        // sınırlarını da siliyor — panel sayfalamayı hiç görmüyor.
        list.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(a.Language, b.Language, StringComparison.OrdinalIgnoreCase);
        });

        return GraphResult<IReadOnlyList<WabaTemplate>>.Success(list);
    }

    /// <summary>Yalnız <c>APPROVED</c> şablonlar — gönderim listesi.
    /// Süzme burada, ayrıştırıcıda değil: aynı ayrıştırıcı yönetim listesine de
    /// hizmet ediyor.</summary>
    public async Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListApprovedAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        var all = await ListAllAsync(wabaId, businessToken, ct);
        if (!all.Ok) return all;

        var approved = all.Value!
            .Where(t => string.Equals(t.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return GraphResult<IReadOnlyList<WabaTemplate>>.Success(approved);
    }

    /// <summary>Tek sayfa: satırları <paramref name="into"/>'ya ekler.</summary>
    /// <returns><c>Next</c> doluysa devam edilecek mutlak URL;
    /// <c>Failure</c> doluysa çağrı orada biter.</returns>
    private async Task<(string? Next, GraphResult<IReadOnlyList<WabaTemplate>>? Failure)> ReadPageAsync(
        string url, string businessToken, string wabaId, List<WabaTemplate> into, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp şablon listesi ağ hatası (waba={Waba})", wabaId);
            return (null, GraphResult<IReadOnlyList<WabaTemplate>>.Failure("network", ex.Message));
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                _log.LogWarning("WhatsApp şablon listesi hatası ({Code}): {Msg}", code, msg);
                return (null, GraphResult<IReadOnlyList<WabaTemplate>>.Failure(code, msg));
            }

            if (!resp.IsSuccessStatusCode ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning(
                    "WhatsApp şablon listesi beklenmedik yanıt (HTTP {Status}): {Body}",
                    (int)resp.StatusCode, Truncate(body));
                return (null, GraphResult<IReadOnlyList<WabaTemplate>>.Failure(
                    ((int)resp.StatusCode).ToString(), "beklenmedik yanıt"));
            }

            foreach (var item in data.EnumerateArray())
            {
                var parsed = ReadTemplate(item);
                if (parsed is not null) into.Add(parsed);
            }

            return (ReadNextPageUrl(root, data), null);
        }
        catch (JsonException)
        {
            _log.LogWarning(
                "WhatsApp şablon listesi JSON değil (HTTP {Status}): {Body}",
                (int)resp.StatusCode, Truncate(body));
            return (null, GraphResult<IReadOnlyList<WabaTemplate>>.Failure(
                ((int)resp.StatusCode).ToString(), "beklenmedik yanıt"));
        }
    }

    /// <summary>
    /// Bir sonraki sayfanın mutlak URL'i, yoksa null.
    ///
    /// <para>Meta son sayfada da <c>paging.cursors.after</c> döndürebiliyor; tek
    /// güvenilir "bitti" işareti <c>paging.next</c>'in <b>yokluğu</b>. Yine de boş
    /// <c>data</c> geldiğinde duruyoruz: <c>next</c> sonsuza kadar dolu kalırsa
    /// tavana çarpıp koca listeyi hata sayardık.</para>
    /// </summary>
    private static string? ReadNextPageUrl(JsonElement root, JsonElement data)
    {
        if (data.GetArrayLength() == 0) return null;
        if (!root.TryGetProperty("paging", out var paging)) return null;
        return Str(paging, "next");
    }

    /// <summary>Tek şablon satırı → <see cref="WabaTemplate"/>. Şekli tanınmayan
    /// satır için null (listeye girmez). Durum süzmesi burada DEĞİL: aynı
    /// ayrıştırıcı hem onaylı listeye hem yönetim listesine hizmet ediyor.</summary>
    private static WabaTemplate? ReadTemplate(JsonElement item)
    {
        var id = Str(item, "id");
        var name = Str(item, "name");
        var language = Str(item, "language");
        var status = Str(item, "status") ?? "";
        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(language)) return null;

        var category = Str(item, "category") ?? "";

        string? headerText = null;
        string? bodyText = null;
        string? footerText = null;
        string? unsupported = null;
        var buttons = new List<WhatsAppTemplateButton>();
        var examples = new List<string>();

        if (item.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in comps.EnumerateArray())
            {
                switch ((Str(c, "type") ?? "").ToUpperInvariant())
                {
                    case "HEADER":
                        // Gönderenimiz yalnız "body" bileşeni yolluyor. Medya
                        // başlıklı şablon başlık parametresi ister; göndermek
                        // Meta'dan hata alır, yani listede gönderilebilir
                        // göstermek yanlış olurdu.
                        if (!string.Equals(Str(c, "format") ?? "TEXT", "TEXT", StringComparison.OrdinalIgnoreCase))
                        {
                            unsupported ??= WhatsAppTemplateShape.HeaderMedia;
                            break;
                        }
                        headerText = Str(c, "text");
                        if (headerText is not null && headerText.Contains("{{", StringComparison.Ordinal))
                            unsupported ??= WhatsAppTemplateShape.HeaderVariable;
                        break;

                    case "BODY":
                        bodyText = Str(c, "text");
                        ReadBodyExamples(c, examples);
                        break;

                    case "FOOTER":
                        footerText = Str(c, "text");
                        break;

                    case "BUTTONS":
                        ReadButtons(c, buttons, ref unsupported);
                        break;
                }
            }
        }

        // Gövdesi olmayan şablon Meta'da oluşturulamıyor; şekil tanınmıyor demektir.
        if (string.IsNullOrWhiteSpace(bodyText)) return null;

        var (count, bodyUnsupported) = WhatsAppTemplateShape.CountBodyParams(bodyText);
        unsupported ??= bodyUnsupported;

        // AUTHENTICATION şablonları gövde parametresini değil OTP buton
        // parametresini istiyor — bizim gönderim biçimimize hiç uymuyor.
        if (category.Equals("AUTHENTICATION", StringComparison.OrdinalIgnoreCase))
            unsupported ??= WhatsAppTemplateShape.AuthCategory;

        return new WabaTemplate(
            id!, name!, language!, category, status, headerText, bodyText!, footerText,
            buttons, count, examples, unsupported, Str(item, "rejected_reason"));
    }

    /// <summary>Meta'nın örnek değerleri: <c>example.body_text = [[ "Ayşe", "250" ]]</c>.
    /// Panelde alanların yer tutucusu olarak gösteriliyor — yayıncı hangi
    /// değişkenin ne olduğunu ancak böyle anlıyor.</summary>
    private static void ReadBodyExamples(JsonElement bodyComponent, List<string> into)
    {
        if (!bodyComponent.TryGetProperty("example", out var ex) ||
            !ex.TryGetProperty("body_text", out var bt) ||
            bt.ValueKind != JsonValueKind.Array || bt.GetArrayLength() == 0 ||
            bt[0].ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var v in bt[0].EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.String) into.Add(v.GetString() ?? "");
        }
    }

    /// <summary>Butonları tipiyle birlikte okur — düzenleme formu adresi ve
    /// numarayı geri doldurmak zorunda; yalnız etiketi taşısaydık kaydeden
    /// yayıncı butonun adresini sessizce silerdi.</summary>
    private static void ReadButtons(
        JsonElement buttonsComponent, List<WhatsAppTemplateButton> into, ref string? unsupported)
    {
        if (!buttonsComponent.TryGetProperty("buttons", out var bs) || bs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var b in bs.EnumerateArray())
        {
            var type = (Str(b, "type") ?? "").ToUpperInvariant();
            var url = Str(b, "url");
            into.Add(new WhatsAppTemplateButton(type, Str(b, "text") ?? "", url, Str(b, "phone_number")));

            // Dinamik URL soneki ve kopyalanabilir kod, gövdeden AYRI bir
            // bileşen parametresi istiyor. Sabit butonlar (quick reply, düz URL,
            // telefon) parametresiz çalıştığı için sorun değil. İki sebep ayrı
            // yazılıyor: "değişken var" ile "bu türü göndermiyoruz" farklı
            // şeyler ve yayıncı yanlış olanı aramaya çıkıyor.
            if (type == "COPY_CODE")
                unsupported ??= WhatsAppTemplateShape.ButtonTypeUnsupported;
            else if (url?.Contains("{{", StringComparison.Ordinal) ?? false)
                unsupported ??= WhatsAppTemplateShape.ButtonVariable;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string body) =>
        body.Length <= LogBodyLimit ? body : string.Concat(body.AsSpan(0, LogBodyLimit), "…");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Base() => $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}";

    public async Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
        string wabaId, string businessToken, string name, string category, string language,
        WhatsAppTemplateDraft draft, CancellationToken ct)
    {
        // parameter_format BİLEREK gönderilmiyor: Meta'nın belgelenmiş varsayılanı
        // konumsal ({{1}}) ve gönderenimiz de konumsal dizi yolluyor. Alanı yazmak
        // yalnız Graph sürümüne bağımlılık eklerdi.
        var payload = new
        {
            name,
            category,
            language,
            components = BuildComponents(draft),
        };

        var sent = await SendAsync(
            HttpMethod.Post, $"{Base()}/{wabaId}/message_templates", businessToken, payload, wabaId, ct);
        if (!sent.Ok) return GraphResult<WhatsAppTemplateCreated>.Failure(sent.ErrorCode, sent.ErrorMessage);

        using var doc = sent.Value!;
        var id = Str(doc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return GraphResult<WhatsAppTemplateCreated>.Failure(
                "unexpected", "Meta şablon kimliği döndürmedi");
        }

        return GraphResult<WhatsAppTemplateCreated>.Success(
            new WhatsAppTemplateCreated(id!, Str(doc.RootElement, "status") ?? "PENDING"));
    }

    public async Task<GraphResult<bool>> UpdateAsync(
        string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct)
    {
        var sent = await SendAsync(
            HttpMethod.Post, $"{Base()}/{templateId}", businessToken,
            new { components = BuildComponents(draft) }, templateId, ct);

        return ReadSuccess(sent);
    }

    public async Task<GraphResult<bool>> DeleteAsync(
        string wabaId, string businessToken, string templateId, string name, CancellationToken ct)
    {
        var url = $"{Base()}/{wabaId}/message_templates" +
                  $"?name={Uri.EscapeDataString(name)}&hsm_id={Uri.EscapeDataString(templateId)}";

        var sent = await SendAsync(HttpMethod.Delete, url, businessToken, null, wabaId, ct);
        return ReadSuccess(sent);
    }

    /// <summary>Meta yazma yanıtı <c>{"success":true}</c>. HTTP 200 tek başına
    /// yetmiyor: <c>success:false</c> gövdesi de 200 ile geliyor.</summary>
    private static GraphResult<bool> ReadSuccess(GraphResult<JsonDocument> sent)
    {
        if (!sent.Ok) return GraphResult<bool>.Failure(sent.ErrorCode, sent.ErrorMessage);

        using var doc = sent.Value!;
        var ok = doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

        return ok
            ? GraphResult<bool>.Success(true)
            : GraphResult<bool>.Failure("unexpected", "Meta işlemi onaylamadı");
    }

    /// <summary>Taslak → Graph bileşen dizisi. Boş başlık/alt bilgi/buton hiç
    /// yazılmıyor: Meta boş bileşeni ret sebebi sayıyor.</summary>
    private static List<object> BuildComponents(WhatsAppTemplateDraft draft)
    {
        var comps = new List<object>();

        if (!string.IsNullOrWhiteSpace(draft.HeaderText))
            comps.Add(new { type = "HEADER", format = "TEXT", text = draft.HeaderText });

        comps.Add(draft.BodyExamples.Count == 0
            ? new { type = "BODY", text = draft.BodyText }
            : (object)new
            {
                type = "BODY",
                text = draft.BodyText,
                example = new { body_text = new[] { draft.BodyExamples.ToArray() } },
            });

        if (!string.IsNullOrWhiteSpace(draft.FooterText))
            comps.Add(new { type = "FOOTER", text = draft.FooterText });

        if (draft.Buttons.Count > 0)
        {
            var buttons = draft.Buttons.Select(b => b.Type switch
            {
                "URL" => (object)new { type = "URL", text = b.Text, url = b.Url },
                "PHONE_NUMBER" => new { type = "PHONE_NUMBER", text = b.Text, phone_number = b.PhoneNumber },
                _ => new { type = "QUICK_REPLY", text = b.Text },
            }).ToArray();

            comps.Add(new { type = "BUTTONS", buttons });
        }

        return comps;
    }

    /// <summary>Yazma çağrıları için ortak gönderim. Okuma yolundaki
    /// <see cref="ReadPageAsync"/> ile aynı hata sözleşmesi: Graph hatası
    /// istisna değil <see cref="GraphResult{T}"/> verisi.</summary>
    private async Task<GraphResult<JsonDocument>> SendAsync(
        HttpMethod method, string url, string businessToken, object? payload, string context,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        if (payload is not null)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, WriteOptions), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp şablon yazma ağ hatası ({Context})", context);
            return GraphResult<JsonDocument>.Failure("network", ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            _log.LogWarning(
                "WhatsApp şablon yazma yanıtı JSON değil (HTTP {Status}): {Body}",
                (int)resp.StatusCode, Truncate(body));
            return GraphResult<JsonDocument>.Failure(
                ((int)resp.StatusCode).ToString(), "beklenmedik yanıt");
        }

        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            doc.Dispose();
            _log.LogWarning("WhatsApp şablon yazma hatası ({Code}): {Msg}", code, msg);
            return GraphResult<JsonDocument>.Failure(code, msg);
        }

        if (!resp.IsSuccessStatusCode)
        {
            doc.Dispose();
            _log.LogWarning(
                "WhatsApp şablon yazma beklenmedik yanıt (HTTP {Status}): {Body}",
                (int)resp.StatusCode, Truncate(body));
            return GraphResult<JsonDocument>.Failure(
                ((int)resp.StatusCode).ToString(), "beklenmedik yanıt");
        }

        return GraphResult<JsonDocument>.Success(doc);
    }
}
