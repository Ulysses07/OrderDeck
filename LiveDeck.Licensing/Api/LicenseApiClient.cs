using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LiveDeck.Licensing.Api.Models;

namespace LiveDeck.Licensing.Api;

public sealed class LicenseApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;

    public LicenseApiClient(HttpClient http) => _http = http;

    public void SetAuthToken(string? token)
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    // ─── Auth (anonymous) ─────────────────────────────────────────────

    public Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<LoginRequest, LoginResponse>("/api/v1/auth/login", req, ct);

    public async Task RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/auth/register", req, ct);
        if ((int)resp.StatusCode is 201 or 202) return;
        await ThrowMappedAsync(resp);
    }

    public async Task ResendConfirmationAsync(ResendRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/auth/resend-confirmation", req, ct);
        if ((int)resp.StatusCode is 202 or 200) return;
        await ThrowMappedAsync(resp);
    }

    // ─── Me (Bearer-Customer) ─────────────────────────────────────────

    public Task<MeResponse> GetMeAsync(CancellationToken ct = default)
        => GetExpectingJsonAsync<MeResponse>("/api/v1/me", ct);

    public async Task ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/me/password", req, ct);
        if ((int)resp.StatusCode == 204) return;
        await ThrowMappedAsync(resp);
    }

    public Task<List<LicenseSummary>> GetMyLicensesAsync(CancellationToken ct = default)
        => GetExpectingJsonAsync<List<LicenseSummary>>("/api/v1/me/licenses", ct);

    // ─── Licenses (Bearer-Customer) ───────────────────────────────────

    /// <summary>Returns null when license/customer not found (404). All other errors throw.</summary>
    public async Task<ValidateResponse?> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/licenses/validate", req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.IsSuccessStatusCode)
            return await DeserializeAsync<ValidateResponse>(resp, ct);
        await ThrowMappedAsync(resp);
        return null; // unreachable
    }

    public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<ActivateRequest, ActivateResponse>("/api/v1/licenses/activate", req, ct, successCodes: new[] { 201, 200 });

    public async Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/licenses/deactivate", req, ct);
        if ((int)resp.StatusCode is 204 or 200 or 404) return;
        await ThrowMappedAsync(resp);
    }

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<HeartbeatRequest, HeartbeatResponse>("/api/v1/licenses/heartbeat", req, ct);

    // ─── HTTP helpers ────────────────────────────────────────────────

    private async Task<TResp> PostJsonExpectingJsonAsync<TReq, TResp>(
        string path, TReq body, CancellationToken ct, int[]? successCodes = null)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, path, body, ct);
        var ok = successCodes is null
            ? resp.IsSuccessStatusCode
            : Array.IndexOf(successCodes, (int)resp.StatusCode) >= 0;
        if (!ok) await ThrowMappedAsync(resp);
        return (await DeserializeAsync<TResp>(resp, ct))!;
    }

    private async Task<TResp> GetExpectingJsonAsync<TResp>(string path, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(path, ct); }
        catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
            return (await DeserializeAsync<TResp>(resp, ct))!;
        }
    }

    private async Task<HttpResponseMessage> SendJsonAsync<TReq>(
        HttpMethod method, string path, TReq body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOpts)
        };
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LicenseApiNetworkException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LicenseApiNetworkException("timeout", ex);
        }
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
    }

    private static async Task ThrowMappedAsync(HttpResponseMessage resp)
    {
        var status = (int)resp.StatusCode;
        string? title = null;
        string? detail = null;

        try
        {
            var problem = await resp.Content.ReadFromJsonAsync<ProblemPayload>(JsonOpts);
            title = problem?.Title;
            detail = problem?.Detail;
        }
        catch
        {
            // Body wasn't problem+json — fall through with title=null
        }

        // Map by (status, title)
        if (status == 401) throw new InvalidCredentialsException(detail ?? "E-posta veya şifre yanlış");
        if (status == 403 && title == "email-not-confirmed") throw new EmailNotConfirmedException(detail ?? "E-posta doğrulanmamış");
        if (status == 409 && title == "slot-full") throw new SlotFullException(detail ?? "Slot dolu");
        if (status == 409 && title == "license-revoked") throw new LicenseRevokedException(detail ?? "Lisans iptal");
        if (status == 409 && title == "license-expired") throw new LicenseExpiredException(detail ?? "Lisans süresi dolmuş");
        if (status >= 400 && status < 500)
            throw new ValidationException(title ?? $"http-{status}", detail ?? $"HTTP {status}");

        throw new LicenseApiUnknownException(status, detail ?? $"HTTP {status}");
    }

    private sealed record ProblemPayload(string? Title, string? Detail, int? Status);
}
