using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.Licensing.Api;

public sealed class LicenseApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly LicenseAuthHandler _authHandler;

    /// <summary>Optional callback to refresh the bearer token when a 401 is observed.
    /// Set by AppHost wiring after both LicenseApiClient and TokenRefresher are
    /// resolved. Must return the new access token (and have already updated the
    /// AuthStore), or null if rotation failed terminally — in which case the 401
    /// propagates to the caller as InvalidCredentialsException.</summary>
    public Func<CancellationToken, Task<string?>>? OnUnauthorized { get; set; }

    public LicenseApiClient(HttpClient http, LicenseAuthHandler authHandler)
    {
        _http = http;
        _authHandler = authHandler;
    }

    /// <summary>Updates the bearer token used for all subsequent requests.
    /// Thread-safe — backed by a volatile field on <see cref="LicenseAuthHandler"/>
    /// rather than HttpClient.DefaultRequestHeaders (which isn't).</summary>
    public void SetAuthToken(string? token) => _authHandler.SetToken(token);

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

    /// <summary>Anonymous endpoint — exchanges a valid refresh token for a fresh
    /// access+refresh pair. The old refresh is revoked atomically server-side.
    /// 401 → InvalidCredentialsException (caller should clear local auth + relogin).</summary>
    public Task<LoginResponse> RefreshAsync(RefreshRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<RefreshRequest, LoginResponse>("/api/v1/auth/refresh", req, ct);

    /// <summary>Authenticated — revokes the supplied refresh token. Idempotent;
    /// safe to call from a background "best-effort" path on logout.</summary>
    public async Task LogoutAsync(LogoutRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/auth/logout", req, ct);
        if ((int)resp.StatusCode is 204 or 200) return;
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

    // ─── Intake Form (Phase 4f) ───────────────────────────────────────

    /// <summary>Returns null when no config is set yet (404 from server).</summary>
    public async Task<IntakeFormConfigDto?> GetIntakeFormAsync(CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync("/api/v1/me/intake-form", ct); }
        catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
            return await DeserializeAsync<IntakeFormConfigDto>(resp, ct);
        }
    }

    public Task<IntakeFormConfigDto> UpsertIntakeFormAsync(IntakeFormUpsertRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<IntakeFormUpsertRequest, IntakeFormConfigDto>(
            "/api/v1/me/intake-form", req, ct, methodOverride: HttpMethod.Put);

    public async Task<List<IntakeFormSubmissionDto>> GetFormSubmissionsAsync(
        DateTimeOffset? since, int limit = 50, CancellationToken ct = default)
    {
        var qs = since is null
            ? $"?limit={limit}"
            : $"?since={Uri.EscapeDataString(since.Value.ToString("O"))}&limit={limit}";

        HttpResponseMessage resp;
        try { resp = await _http.GetAsync("/api/v1/me/form-submissions" + qs, ct); }
        catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
            return (await DeserializeAsync<List<IntakeFormSubmissionDto>>(resp, ct)) ?? new();
        }
    }

    // ─── HTTP helpers ────────────────────────────────────────────────

    private async Task<TResp> PostJsonExpectingJsonAsync<TReq, TResp>(
        string path, TReq body, CancellationToken ct, int[]? successCodes = null,
        HttpMethod? methodOverride = null)
    {
        var method = methodOverride ?? HttpMethod.Post;
        using var resp = await SendJsonAsync(method, path, body, ct);
        var ok = successCodes is null
            ? resp.IsSuccessStatusCode
            : Array.IndexOf(successCodes, (int)resp.StatusCode) >= 0;
        if (!ok) await ThrowMappedAsync(resp);
        return (await DeserializeAsync<TResp>(resp, ct))!;
    }

    private async Task<TResp> GetExpectingJsonAsync<TResp>(string path, CancellationToken ct)
    {
        var canRefresh = OnUnauthorized is not null && !path.StartsWith("/api/v1/auth/");
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(path, ct); }
            catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && canRefresh)
            {
                resp.Dispose();
                if (await OnUnauthorized!(ct) is null) continue;  // refresh failed → next attempt 401s deterministically
                continue;
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
                return (await DeserializeAsync<TResp>(resp, ct))!;
            }
        }
    }

    private async Task<HttpResponseMessage> SendJsonAsync<TReq>(
        HttpMethod method, string path, TReq body, CancellationToken ct)
    {
        // Two attempts max: original send → on 401, ask the refresh callback for
        // a fresh token, rebuild the request (HttpRequestMessage is single-use)
        // and try once more. Skip the retry path for the auth endpoints
        // themselves — they SHOULD legitimately return 401 to the caller.
        var canRefresh = OnUnauthorized is not null && !path.StartsWith("/api/v1/auth/");
        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(body, options: JsonOpts)
            };
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

            if (resp.StatusCode != HttpStatusCode.Unauthorized || attempt > 0 || !canRefresh)
                return resp;

            resp.Dispose();
            var refreshed = await OnUnauthorized!(ct);
            if (refreshed is null)
            {
                // Rotation gave up. Re-issue the request once unauthenticated so
                // the caller observes a deterministic 401 (not a stale resp).
                req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOpts) };
                try { return await _http.SendAsync(req, ct); }
                catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }
            }
            // SetAuthToken already updated header inside OnUnauthorized; loop to retry.
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
