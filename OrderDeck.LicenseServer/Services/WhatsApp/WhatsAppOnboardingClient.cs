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
        var url = $"{Root}/oauth/access_token" +
                  $"?client_id={Uri.EscapeDataString(_opt.AppId)}" +
                  $"&client_secret={Uri.EscapeDataString(_opt.AppSecret)}" +
                  $"&code={Uri.EscapeDataString(code)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(req, "code-exchange", root =>
            root.TryGetProperty("access_token", out var t) ? t.GetString() : null, ct);
    }

    /// <summary>Ortak gövde: gönder, JSON'u ayrıştır, Meta hatasını yapısal sonuca
    /// çevir. <paramref name="step"/> yalnız log içindir — token/code ASLA loglanmaz.
    ///
    /// <para><c>where T : class</c> şart: "okuyucu null döndüyse şekil beklenmedik"
    /// kuralı değer tiplerinde işlemez (<c>false</c> asla null olmaz). Başarı
    /// bayrağı dönen uçlar için <c>SendSuccessAsync</c> var.</para></summary>
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
