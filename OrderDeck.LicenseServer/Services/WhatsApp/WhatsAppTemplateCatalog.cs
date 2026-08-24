using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Meta'da ONAYLI bir şablonun panelin ihtiyaç duyduğu hâli.
///
/// <para><b>Gövde metni neden taşınıyor:</b> onaylı metin Meta'da duruyor ve biz
/// hiçbir yerde saklamıyoruz. Panel yayıncıya "hangi mesaj gidecek" sorusunu
/// ancak bu alanla cevaplayabiliyor — şablonu adından seçtirmek, içeriğini
/// görmeden ücretli mesaj göndertmek demekti.</para>
///
/// <para><paramref name="UnsupportedReason"/> doluysa şablon listede görünür ama
/// gönderilemez. Gizlemek yerine sebebini yazıyoruz: yayıncı Meta'da onaylattığı
/// şablonu panelde hiç göremezse eksikliği bize değil kendi hesabına yorar.</para>
/// </summary>
public sealed record ApprovedTemplate(
    string Name,
    string Language,
    string Category,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons,
    int ParameterCount,
    IReadOnlyList<string> ParameterExamples,
    string? UnsupportedReason);

/// <summary>WABA'nın onaylı şablon listesi. Yalnız HTTP yapar; DB'ye dokunmaz,
/// karar vermez — panel ucu testlerinde tek parça sahtelenebilsin diye.</summary>
public interface IWhatsAppTemplateCatalog
{
    /// <summary>Yalnız <c>APPROVED</c> şablonları döndürür. Onay bekleyen ya da
    /// reddedilen şablon gönderilemez; listede göstermek yayıncıya
    /// gönderebileceği izlenimi verirdi.</summary>
    Task<GraphResult<IReadOnlyList<ApprovedTemplate>>> ListApprovedAsync(
        string wabaId, string businessToken, CancellationToken ct);
}

/// <summary>
/// Şablon gövdesindeki yer tutucuların çözümlenmesi.
///
/// <para>HTTP'den ayrı duruyor çünkü asıl incelik burada: yanlış sayıda parametre
/// göndermek Meta'dan 132000 ile döner ve şablon <b>ücretli</b> olduğu için
/// yayıncı parasını denemelere yatırır.</para>
/// </summary>
public static class WhatsAppTemplateShape
{
    /// <summary>Yer tutucunun kendisi — içi rakam mı isim mi, ayrımı çağıran yapar.</summary>
    private static readonly Regex Placeholder =
        new(@"\{\{(.*?)\}\}", RegexOptions.CultureInvariant);

    public const string NamedParams =
        "Bu şablon isimli değişken kullanıyor; panel yalnız {{1}}, {{2}} biçimini gönderebiliyor.";

    public const string GappedParams =
        "Şablonun değişken numaraları 1'den başlayarak sırayla gitmiyor.";

    public const string HeaderMedia =
        "Şablonun başlığında görsel/belge var; panel yalnız metin başlıklı şablon gönderebiliyor.";

    public const string HeaderVariable =
        "Şablonun başlığında değişken var; panel yalnız gövde değişkenlerini doldurabiliyor.";

    public const string ButtonVariable =
        "Şablonun butonu değişken istiyor; panel bu tür şablonu gönderemiyor.";

    public const string AuthCategory =
        "Doğrulama (authentication) şablonları ayrı bir gönderim biçimi istiyor.";

    /// <summary>
    /// Gövdedeki konumsal parametre sayısı.
    /// </summary>
    /// <returns><c>Unsupported</c> doluysa gövde bizim gönderebileceğimiz
    /// biçimde değil ve <c>Count</c> anlamsızdır.</returns>
    public static (int Count, string? Unsupported) CountBodyParams(string bodyText)
    {
        var matches = Placeholder.Matches(bodyText);
        if (matches.Count == 0) return (0, null);

        var indexes = new SortedSet<int>();
        foreach (Match m in matches)
        {
            var inner = m.Groups[1].Value.Trim();
            // Meta 2024'ten beri {{musteri_adi}} gibi isimli değişkene de izin
            // veriyor. Gönderenimiz konumsal dizi yolluyor; isimli şablonda o
            // dizi sessizce yanlış yere oturmaz, Meta reddeder — ama ücretli
            // denemeye bırakmak yerine burada eliyoruz.
            if (!int.TryParse(inner, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return (0, NamedParams);
            }
            indexes.Add(n);
        }

        // 1..n bitişik olmalı. Meta bunu kendi de zorluyor ama liste bizim
        // verimiz değil: boşluklu bir dizide ({{1}}, {{3}}) yayıncının girdiği
        // değerler bir sıra kayar ve yanlış bilgi müşteriye gider.
        var expected = 1;
        foreach (var n in indexes)
        {
            if (n != expected++) return (0, GappedParams);
        }

        return (indexes.Count, null);
    }
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
    public async Task<GraphResult<IReadOnlyList<ApprovedTemplate>>> ListApprovedAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        var url =
            $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}/{wabaId}/message_templates" +
            $"?fields=name,status,category,language,components&limit={PageLimit}";

        var list = new List<ApprovedTemplate>();

        for (var page = 1; ; page++)
        {
            if (page > MaxPages)
            {
                _log.LogWarning(
                    "WhatsApp şablon sayfalaması {Max} sayfayı aştı (waba={Waba}) — imleç döngüsü olabilir",
                    MaxPages, wabaId);
                return GraphResult<IReadOnlyList<ApprovedTemplate>>.Failure(
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

        return GraphResult<IReadOnlyList<ApprovedTemplate>>.Success(list);
    }

    /// <summary>Tek sayfa: satırları <paramref name="into"/>'ya ekler.</summary>
    /// <returns><c>Next</c> doluysa devam edilecek mutlak URL;
    /// <c>Failure</c> doluysa çağrı orada biter.</returns>
    private async Task<(string? Next, GraphResult<IReadOnlyList<ApprovedTemplate>>? Failure)> ReadPageAsync(
        string url, string businessToken, string wabaId, List<ApprovedTemplate> into, CancellationToken ct)
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
            return (null, GraphResult<IReadOnlyList<ApprovedTemplate>>.Failure("network", ex.Message));
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
                return (null, GraphResult<IReadOnlyList<ApprovedTemplate>>.Failure(code, msg));
            }

            if (!resp.IsSuccessStatusCode ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning(
                    "WhatsApp şablon listesi beklenmedik yanıt (HTTP {Status}): {Body}",
                    (int)resp.StatusCode, Truncate(body));
                return (null, GraphResult<IReadOnlyList<ApprovedTemplate>>.Failure(
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
            return (null, GraphResult<IReadOnlyList<ApprovedTemplate>>.Failure(
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

    /// <summary>Tek şablon satırı → <see cref="ApprovedTemplate"/>. Onaylı
    /// olmayan ya da şekli tanınmayan satır için null (listeye girmez).</summary>
    private static ApprovedTemplate? ReadTemplate(JsonElement item)
    {
        var name = Str(item, "name");
        var language = Str(item, "language");
        var status = Str(item, "status");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language)) return null;
        if (!string.Equals(status, "APPROVED", StringComparison.OrdinalIgnoreCase)) return null;

        var category = Str(item, "category") ?? "";

        string? headerText = null;
        string? bodyText = null;
        string? footerText = null;
        string? unsupported = null;
        var buttons = new List<string>();
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

        return new ApprovedTemplate(
            name!, language!, category, headerText, bodyText!, footerText,
            buttons, count, examples, unsupported);
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

    private static void ReadButtons(JsonElement buttonsComponent, List<string> labels, ref string? unsupported)
    {
        if (!buttonsComponent.TryGetProperty("buttons", out var bs) || bs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var b in bs.EnumerateArray())
        {
            var label = Str(b, "text");
            if (!string.IsNullOrWhiteSpace(label)) labels.Add(label!);

            // Dinamik URL soneki ve kopyalanabilir kod, gövdeden AYRI bir
            // bileşen parametresi istiyor. Sabit butonlar (quick reply, düz URL,
            // telefon) parametresiz çalıştığı için sorun değil.
            var type = (Str(b, "type") ?? "").ToUpperInvariant();
            var url = Str(b, "url");
            if (type == "COPY_CODE" || (url?.Contains("{{", StringComparison.Ordinal) ?? false))
                unsupported ??= WhatsAppTemplateShape.ButtonVariable;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string body) =>
        body.Length <= LogBodyLimit ? body : string.Concat(body.AsSpan(0, LogBodyLimit), "…");
}
