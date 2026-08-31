using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Graph çağrısının yapısal sonucu. Meta hatası fırlatılmaz — çağıran
/// (panel ucu) hangi adımda takıldığını kullanıcıya söylemek zorunda.</summary>
public sealed record GraphResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
{
    public bool Ok => ErrorCode is null;
    public static GraphResult<T> Success(T value) => new(value, null, null);
    public static GraphResult<T> Failure(string? code, string? message) =>
        new(default, string.IsNullOrWhiteSpace(code) ? "unknown" : code, message);
}

/// <summary>Numaranın Meta'daki görünen hâli.</summary>
/// <param name="PlatformType">
/// <c>CLOUD_API</c> | <c>SMB_APP</c> | <c>ON_PREMISE</c> | <c>NOT_APPLICABLE</c>.
/// <c>SMB_APP</c> = numara yayıncının telefonundaki WhatsApp Business
/// uygulamasında yaşıyor (coexistence) ve <b>yeniden kaydedilmemeli</b> —
/// bkz. <see cref="IWhatsAppOnboardingClient.RegisterPhoneNumberAsync"/>.
/// Meta alanı göndermezse null; o durumda coexistence VARSAYILMAZ.
/// </param>
public sealed record WhatsAppPhoneNumberInfo(
    string DisplayPhoneNumber, string? VerifiedName, string? PlatformType = null)
{
    /// <summary>Numara Business App'te mi yaşıyor?</summary>
    public bool IsBusinessApp =>
        string.Equals(PlatformType, "SMB_APP", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Embedded Signup'ın Graph ayağı. Yalnız HTTP yapar; DB'ye dokunmaz,
/// karar vermez — böylece panel ucu testlerinde tek parça sahtelenebilir.</summary>
public interface IWhatsAppOnboardingClient
{
    /// <summary>Embedded Signup'tan dönen <c>code</c>'u tenant'ın kalıcı iş
    /// token'ına çevirir. Kod 30 sn yaşıyor — çağrı gecikmeden yapılmalı.</summary>
    Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct);

    /// <summary>Uygulamamızı müşterinin WABA'sına abone eder — bu yapılmazsa
    /// o numaraya gelen mesajlar webhook'umuza HİÇ düşmez.</summary>
    Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string businessToken, CancellationToken ct);

    /// <summary>Aboneliği kaldırır — numara koparıldıktan sonra o hatta gelen
    /// mesajların webhook'umuza düşmeye devam etmemesi için.</summary>
    Task<GraphResult<bool>> UnsubscribeAppAsync(string wabaId, string businessToken, CancellationToken ct);

    /// <summary>Numaranın görünen hâli (UI için) — WABA'nın kendi numara
    /// listesinden okunur, böylece numaranın O WABA'ya ait olduğu da kanıtlanır.
    /// Eşleşmiyorsa <c>phone-number-not-in-waba</c> koduyla başarısız olur.</summary>
    Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
        string wabaId, string phoneNumberId, string businessToken, CancellationToken ct);

    /// <summary>Numarayı Cloud API'ye kaydeder ve iki adımlı PIN'i belirler.
    /// Numara zaten kayıtlıysa Meta hata döner — çağıran bunu ölümcül saymamalı.
    ///
    /// <para><b>Coexistence numarasında ÇAĞRILMAZ.</b> Meta'nın Business App
    /// onboarding rehberi kayıt adımını açıkça atlatıyor ("the number is already
    /// registered"); üstelik bu çağrı yayıncının telefonunda kullandığı numaraya
    /// yeni bir iki adımlı PIN yazmaya kalkar.</para></summary>
    Task<GraphResult<bool>> RegisterPhoneNumberAsync(
        string phoneNumberId, string pin, string businessToken, CancellationToken ct);

    /// <summary>
    /// Coexistence senkronunu başlatır: yayıncının telefonundaki sohbet geçmişi
    /// (<c>history</c>) ya da rehberi (<c>smb_app_state_sync</c>). Veri bu
    /// çağrının yanıtında DEĞİL, webhook'la parça parça gelir; dönen değer
    /// Meta desteğine verilecek <c>request_id</c>'dir.
    ///
    /// <para><b>Süre sınırlı:</b> onboarding tamamlandıktan sonra 24 saat içinde
    /// çağrılmazsa Meta müşterinin offboard edilmesini şart koşuyor.</para>
    /// </summary>
    Task<GraphResult<string>> SyncSmbAppDataAsync(
        string phoneNumberId, string syncType, string businessToken, CancellationToken ct);
}

/// <summary><see cref="IWhatsAppOnboardingClient.SyncSmbAppDataAsync"/>'in
/// kabul ettiği iki senkron türü. İkisi de çağrılmak zorunda: biri geçmiş
/// sohbetleri, diğeri kişi adlarını getiriyor.</summary>
public static class WhatsAppSmbSyncTypes
{
    public const string History = "history";
    public const string Contacts = "smb_app_state_sync";
}

public sealed class WhatsAppOnboardingClient : IWhatsAppOnboardingClient
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppOnboardingClient> _log;

    public WhatsAppOnboardingClient(
        HttpClient http, IOptions<WhatsAppOptions> opt, ILogger<WhatsAppOnboardingClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    private string Root => $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}";

    public async Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        // App secret ve tek kullanımlık code GÖVDEDE gider, sorgu dizesinde
        // DEĞİL: bu istemci AddHttpClient ile kayıtlı, yani LoggingHttpMessageHandler
        // istek URI'sini Information seviyesinde yazıyor ve AddHttpClientInstrumentation
        // aynı URI'yi ikinci kez tüketiyor. Query redaction ikisinde de ayrı ayrı
        // kapatılabilen bir çalışma zamanı varsayılanı — sırrı ona emanet etme.
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _opt.AppId,
                ["client_secret"] = _opt.AppSecret,
                ["code"] = code,
            }),
        };
        return await SendAsync(req, ExchangeStep, root =>
            root.TryGetProperty("access_token", out var t) ? t.GetString() : null, ct);
    }

    public async Task<GraphResult<bool>> SubscribeAppAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{wabaId}/subscribed_apps");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        return await SendSuccessAsync(req, "subscribe-app", ct);
    }

    public async Task<GraphResult<bool>> UnsubscribeAppAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Root}/{wabaId}/subscribed_apps");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        return await SendSuccessAsync(req, "unsubscribe-app", ct);
    }

    /// <summary>Bir WABA'nın numara listesinden tek satır.</summary>
    private sealed record ListedNumber(
        string Id, string DisplayPhoneNumber, string? VerifiedName, string? PlatformType);

    /// <summary>Meta bir WABA'ya varsayılan olarak 25 numaraya izin veriyor;
    /// 100 bunun çok üstünde, yani sayfalama takibine gerek kalmıyor.</summary>
    private const int PhoneNumberPageLimit = 100;

    /// <summary>
    /// Numarayı doğrudan <c>GET /{phoneNumberId}</c> ile okumak da görünen numarayı
    /// verirdi ama numaranın istekte gelen WABA'ya ait olduğunu KANITLAMAZDI:
    /// Meta'nın numara düğümünde üst WABA'ya geri işaret eden bir alan yok. Liste
    /// ucundan okumak ikisini tek çağrıda çözüyor — eşleşme doğrulanmazsa satıra
    /// numaranın sahibi olmayan bir WABA yazılır, abonelik yanlış hesaba gider ve
    /// o numaraya gelen mesajlar webhook'umuza hiç düşmez.
    /// </summary>
    public async Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
        string wabaId, string phoneNumberId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Root}/{wabaId}/phone_numbers" +
            $"?fields=id,display_phone_number,verified_name,platform_type" +
            $"&limit={PhoneNumberPageLimit}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);

        var listed = await SendAsync(req, "read-phone-number", ReadNumbers, ct);
        if (!listed.Ok)
            return GraphResult<WhatsAppPhoneNumberInfo>.Failure(listed.ErrorCode, listed.ErrorMessage);

        var match = listed.Value!.FirstOrDefault(n => n.Id == phoneNumberId);
        return match is null
            ? GraphResult<WhatsAppPhoneNumberInfo>.Failure(
                "phone-number-not-in-waba",
                "Numara bu WhatsApp Business hesabına ait değil.")
            : GraphResult<WhatsAppPhoneNumberInfo>.Success(
                new WhatsAppPhoneNumberInfo(
                    match.DisplayPhoneNumber, match.VerifiedName, match.PlatformType));
    }

    /// <summary>Boş liste geçerli bir yanıt (WABA'da numara yok) — <c>null</c>
    /// yalnız <c>data</c> dizisi hiç yokken, yani şekil beklenmedikken döner.</summary>
    private static List<ListedNumber>? ReadNumbers(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        var numbers = new List<ListedNumber>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
            var display = item.TryGetProperty("display_phone_number", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(display)) continue;
            var name = item.TryGetProperty("verified_name", out var v) ? v.GetString() : null;
            var platform = item.TryGetProperty("platform_type", out var p) ? p.GetString() : null;
            numbers.Add(new ListedNumber(
                id, display,
                string.IsNullOrWhiteSpace(name) ? null : name,
                string.IsNullOrWhiteSpace(platform) ? null : platform));
        }
        return numbers;
    }

    public async Task<GraphResult<bool>> RegisterPhoneNumberAsync(
        string phoneNumberId, string pin, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{phoneNumberId}/register")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", pin }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        return await SendSuccessAsync(req, "register-number", ct);
    }

    public async Task<GraphResult<string>> SyncSmbAppDataAsync(
        string phoneNumberId, string syncType, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{phoneNumberId}/smb_app_data")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", sync_type = syncType }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);

        // request_id yoksa "ok" dönüyoruz, null DEĞİL: bu noktada HTTP 2xx ve
        // Meta'nın error bloğu yok, yani senkron başlamış demektir. null dönmek
        // SendAsync'in "beklenmedik şekil" dalına düşer ve başarılı bir çağrıyı
        // başarısız gösterirdi — bedeli 24 saatlik pencerenin boşa harcanması.
        return await SendAsync(req, $"smb-app-data-{syncType}", root =>
            root.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : "ok", ct);
    }

    /// <summary><c>{ "success": true }</c> dönen uçlar için ortak yol. İçeride
    /// <c>string</c> ile çalışıyor çünkü <see cref="SendAsync{T}"/> "okuyucu null
    /// döndü = beklenmedik şekil" kuralına dayanıyor ve <c>bool</c> null olamaz.</summary>
    private async Task<GraphResult<bool>> SendSuccessAsync(
        HttpRequestMessage req, string step, CancellationToken ct)
    {
        var result = await SendAsync(req, step, root =>
            root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
                ? "ok" : null, ct);

        return result.Ok
            ? GraphResult<bool>.Success(true)
            : GraphResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>Ortak gövde: gönder, JSON'u ayrıştır, Meta hatasını yapısal sonuca
    /// çevir. <paramref name="step"/> yalnız log içindir; elimizdeki token ve code
    /// ASLA loglanmaz — beklenmedik yanıtlarda yalnız Meta'nın gövdesi kırpılarak
    /// sunucu log'una düşer, çağırana hiçbir şekilde dönmez.
    ///
    /// <para><c>where T : class</c> şart: "okuyucu null döndüyse şekil beklenmedik"
    /// kuralı değer tiplerinde işlemez (<c>false</c> asla null olmaz). Başarı
    /// bayrağı dönen uçlar için <see cref="SendSuccessAsync"/> var.</para></summary>
    private async Task<GraphResult<T>> SendAsync<T>(
        HttpRequestMessage req, string step, Func<JsonElement, T?> read, CancellationToken ct)
        where T : class
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WhatsApp onboarding ağ hatası ({Step})", step);
            return GraphResult<T>.Failure("network", ex.Message);
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
                _log.LogWarning("WhatsApp onboarding hatası ({Step}, {Code}): {Msg}", step, code, msg);
                return GraphResult<T>.Failure(code, msg);
            }

            if (!resp.IsSuccessStatusCode)
                return Opaque<T>(step, resp, body, ((int)resp.StatusCode).ToString());

            var value = read(root);
            return value is null
                ? Opaque<T>(step, resp, body, "unexpected-shape")
                : GraphResult<T>.Success(value);
        }
        catch (JsonException)
        {
            // Meta'nın eski OAuth ucu form-encoded dönüyordu ve GraphApiVersion
            // operatör ayarı — bu dal gerçekten erişilebilir ve o gövdede
            // access_token var. Ham hâli asla dışarı çıkmamalı.
            return Opaque<T>(step, resp, body, ((int)resp.StatusCode).ToString());
        }
    }

    /// <summary>Meta'nın kendi <c>error.message</c>'ı OLMAYAN başarısızlıklar.
    /// Gövde çağırana DÖNMEZ: çağıran panel ucu <c>ErrorMessage</c>'ı tarayıcıya
    /// gösteriyor ve code-exchange yanıtı akıştaki tek iş token'ını taşıyan gövde.
    /// Teşhis için gövdenin kırpılmış hâli yalnız sunucu log'una yazılır.</summary>
    private GraphResult<T> Opaque<T>(
        string step, HttpResponseMessage resp, string body, string code) where T : class
    {
        // Code takası yanıtı log'a da GİRMEZ. Form-encoded düşen bir gövdede
        // access_token açıkta duruyor; sunucu log'u tarayıcıdan güvenli ama
        // bu repoda sır hijyeni pazarlık konusu değil. Diğer adımların
        // gövdesinde token yok — teşhis için onlar kırpılarak yazılır.
        _log.LogWarning(
            "WhatsApp onboarding beklenmedik yanıt ({Step}, HTTP {Status}, {Code}): {Body}",
            step, (int)resp.StatusCode, code,
            step == ExchangeStep ? "[gövde gizlendi]" : Truncate(body));
        return GraphResult<T>.Failure(code, OpaqueMessage);
    }

    /// <summary>Gövdesi token taşıyabilen tek adım — <see cref="Opaque{T}"/> bunu
    /// log'da da gizler.</summary>
    private const string ExchangeStep = "code-exchange";

    /// <summary>Dışarı dönen sabit metin — gövdeden hiçbir şey taşımaz.</summary>
    private const string OpaqueMessage = "beklenmedik yanıt";

    private const int LogBodyLimit = 500;

    private static string Truncate(string body) =>
        body.Length <= LogBodyLimit ? body : string.Concat(body.AsSpan(0, LogBodyLimit), "…");
}
