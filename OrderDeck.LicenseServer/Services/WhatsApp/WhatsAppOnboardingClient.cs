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

/// <summary>Numaranın Meta'daki görünen hâli — yalnız UI için.</summary>
public sealed record WhatsAppPhoneNumberInfo(string DisplayPhoneNumber, string? VerifiedName);

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

    /// <summary>Numaranın görünen hâli (UI için).</summary>
    Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
        string phoneNumberId, string businessToken, CancellationToken ct);

    /// <summary>Numarayı Cloud API'ye kaydeder ve iki adımlı PIN'i belirler.
    /// Numara zaten kayıtlıysa Meta hata döner — çağıran bunu ölümcül saymamalı.</summary>
    Task<GraphResult<bool>> RegisterPhoneNumberAsync(
        string phoneNumberId, string pin, string businessToken, CancellationToken ct);
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
        return await SendAsync(req, "code-exchange", root =>
            root.TryGetProperty("access_token", out var t) ? t.GetString() : null, ct);
    }

    public async Task<GraphResult<bool>> SubscribeAppAsync(
        string wabaId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{wabaId}/subscribed_apps");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);
        return await SendSuccessAsync(req, "subscribe-app", ct);
    }

    public async Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
        string phoneNumberId, string businessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"{Root}/{phoneNumberId}?fields=display_phone_number,verified_name");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", businessToken);

        return await SendAsync(req, "read-phone-number", root =>
        {
            if (!root.TryGetProperty("display_phone_number", out var d)) return null;
            var display = d.GetString();
            if (string.IsNullOrWhiteSpace(display)) return null;
            var name = root.TryGetProperty("verified_name", out var v) ? v.GetString() : null;
            return new WhatsAppPhoneNumberInfo(display, string.IsNullOrWhiteSpace(name) ? null : name);
        }, ct);
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
    /// çevir. <paramref name="step"/> yalnız log içindir — token/code ASLA loglanmaz.
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
                return GraphResult<T>.Failure(((int)resp.StatusCode).ToString(), body);

            var value = read(root);
            return value is null
                ? GraphResult<T>.Failure("unexpected-shape", body)
                : GraphResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return GraphResult<T>.Failure(((int)resp.StatusCode).ToString(), body);
        }
    }
}
