using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.Licensing.Api;

public sealed class LicenseApiClient : OrderDeck.Core.Chat.IFacebookOAuthBroker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;
    private readonly LicenseTokenStore _tokenStore;

    /// <summary>Optional callback to refresh the bearer token when a 401 is observed.
    /// Set by AppHost wiring after both LicenseApiClient and TokenRefresher are
    /// resolved. Must return the new access token (and have already updated the
    /// AuthStore), or null if rotation failed terminally — in which case the 401
    /// propagates to the caller as InvalidCredentialsException.</summary>
    public Func<CancellationToken, Task<string?>>? OnUnauthorized { get; set; }

    public LicenseApiClient(HttpClient http, LicenseTokenStore tokenStore)
    {
        _http = http;
        _tokenStore = tokenStore;
    }

    /// <summary>Updates the bearer token used for all subsequent requests.
    /// Thread-safe — backed by a volatile field on <see cref="LicenseTokenStore"/>
    /// rather than HttpClient.DefaultRequestHeaders (which isn't).</summary>
    public void SetAuthToken(string? token) => _tokenStore.SetToken(token);

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
    public Task<IntakeFormConfigDto?> GetIntakeFormAsync(CancellationToken ct = default)
        => GetExpectingJsonOrNullAsync<IntakeFormConfigDto>("/api/v1/me/intake-form", ct);

    public Task<IntakeFormConfigDto> UpsertIntakeFormAsync(IntakeFormUpsertRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<IntakeFormUpsertRequest, IntakeFormConfigDto>(
            "/api/v1/me/intake-form", req, ct, methodOverride: HttpMethod.Put);

    /// <summary>Artımlı kayıt akışı. İmleç <b>bileşik</b> — (<paramref name="since"/>,
    /// <paramref name="sinceId"/>). Yalnız zaman damgası gönderilseydi, aynı damgayı
    /// paylaşan kayıtlar sayfa sınırında kesildiğinde kalanları bir daha hiç
    /// dönmezdi ve o satır bir müşteri KAYDI olduğu için kendiliğinden onarılmazdı.
    /// Cursor WPF tarafında SyncCursor("intake-form-in") satırında (R9-D02).</summary>
    public async Task<List<IntakeFormSubmissionDto>> GetFormSubmissionsAsync(
        DateTimeOffset? since, Guid sinceId = default, int limit = 50, CancellationToken ct = default)
    {
        var qs = since is null
            ? $"?limit={limit}"
            : $"?since={Uri.EscapeDataString(since.Value.ToString("O"))}&sinceId={sinceId}&limit={limit}";
        return await GetExpectingJsonAsync<List<IntakeFormSubmissionDto>>(
            "/api/v1/me/form-submissions" + qs, ct) ?? new();
    }

    // ─── Payment sync (Bearer-Customer) ───────────────────────────────

    /// <summary>WPF outbox push: batch upsert by Payment.Id. Echoes server-side
    /// status back (mobile arası onay/red bilgisi de gelir). Max 200 item/batch.</summary>
    public Task<List<SyncedPaymentDto>> SyncPaymentsAsync(
        Guid licenseId, SyncPaymentsRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SyncPaymentsRequest, List<SyncedPaymentDto>>(
            $"/api/v1/licenses/{licenseId}/payments/sync", req, ct);

    /// <summary>Reverse sync: mobile onay/red sonuçlarını çeker. İmleç
    /// <b>bileşik</b> — (<paramref name="since"/>, <paramref name="sinceId"/>).
    /// Yalnız zaman damgası gönderilseydi, aynı damgayı paylaşan satırlar sayfa
    /// sınırında kesildiğinde kalanları bir daha hiç dönmezdi; damga eşitliği
    /// varsayımsal değil, tek push 200 dekontu tek damgayla yazıyor. Cursor WPF
    /// tarafında SyncCursor("payment-decision-in") satırında (R9-D02).</summary>
    public async Task<List<SyncedPaymentDto>> GetPaymentsSinceAsync(
        Guid licenseId, DateTimeOffset since, Guid sinceId,
        int take = 200, CancellationToken ct = default)
    {
        var qs = $"?since={Uri.EscapeDataString(since.ToString("O"))}&sinceId={sinceId:D}&take={take}";
        return await GetExpectingJsonAsync<List<SyncedPaymentDto>>(
            $"/api/v1/licenses/{licenseId}/payments/since{qs}", ct) ?? new();
    }

    // ─── Shipment sync (PR-D, 2026-05-13) ─────────────────────────────────

    /// <summary>WPF outbox push: Shipment batch upsert by Id. WPF authoritative
    /// (mobile mutation yapmıyor). Max 200 item/batch.
    /// <para>Yanıt (spec 2026-10-13 §7.1): v2 zarflı istekte dosya başına sunucunun güncel satırı (akış öğesi);
    /// zarfsız istekte ve eski sunucuda bugünkü <see cref="SyncedShipmentDto"/> listesi — ikisi de
    /// <see cref="SalesShipmentItem"/> olarak okunur (v1'de v2'ye özgü alanlar varsayılan değerde).</para></summary>
    public async Task<List<SalesShipmentItem>> SyncShipmentsAsync(
        Guid licenseId, SyncShipmentsRequest req, CancellationToken ct = default)
    {
        var rows = await PostJsonExpectingJsonOrUnknownAsync<SyncShipmentsRequest, List<SalesShipmentItem?>>(
            $"/api/v1/licenses/{licenseId}/shipments/sync", req, ct);
        return rows?.OfType<SalesShipmentItem>().ToList() ?? new();
    }

    /// <summary>Reverse sync (nadiren kullanılır — WPF authoritative). İmleç
    /// bileşik; gerekçe <see cref="GetPaymentsSinceAsync"/>'de.</summary>
    public async Task<List<SyncedShipmentDto>> GetShipmentsSinceAsync(
        Guid licenseId, DateTimeOffset since, Guid sinceId,
        int take = 200, CancellationToken ct = default)
    {
        var qs = $"?since={Uri.EscapeDataString(since.ToString("O"))}&sinceId={sinceId:D}&take={take}";
        return await GetExpectingJsonAsync<List<SyncedShipmentDto>>(
            $"/api/v1/licenses/{licenseId}/shipments/since{qs}", ct) ?? new();
    }

    // ─── Session + Order sync (PR siparis-sync 2026-05-13) ────────────────

    /// <summary>
    /// <c>sessions/sync</c> (spec 2026-10-13 §4.1). Yanıt öğesi sunucudaki satırdır; v2 sunucu sahipliği, devir anını
    /// ve <c>Protocol = 2</c>'yi ekler, eski sunucu eklemez (aynı tiple okunur). Çözümlenemeyen gövde
    /// <see cref="LicenseApiUnknownException"/>'a çevrilir; boş gövde boş listedir.
    /// </summary>
    public async Task<List<SyncedSessionDto>> SyncSessionsAsync(
        Guid licenseId, SyncSessionsRequest req, CancellationToken ct = default)
    {
        var rows = await PostJsonExpectingJsonOrUnknownAsync<SyncSessionsRequest, List<SyncedSessionDto?>>(
            $"/api/v1/licenses/{licenseId}/sessions/sync", req, ct);
        return rows?.OfType<SyncedSessionDto>().ToList() ?? new();
    }

    public Task<List<SyncedOrderDto>> SyncOrdersAsync(
        Guid licenseId, SyncOrdersRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SyncOrdersRequest, List<SyncedOrderDto>>(
            $"/api/v1/licenses/{licenseId}/orders/sync", req, ct);

    /// <summary>
    /// <c>orders/sync</c>, öğe başına yanıtla (spec 2026-10-13 §4.2). <c>Format = 2</c> ve v2 zarflı istekte sunucu
    /// her öğe için <see cref="SyncedOrderV2Dto"/> döner. Zarfsız istekte ya da eski sunucuda yanıt bugünkü
    /// <see cref="SyncedOrderDto"/> listesidir; aynı tiple okunur ve öğeleri <see cref="SyncedOrderV2Dto.IsLegacy"/>
    /// olur. Çözümlenemeyen gövde <see cref="LicenseApiUnknownException"/>'a çevrilir.
    /// </summary>
    public async Task<List<SyncedOrderV2Dto>> SyncOrdersV2Async(
        Guid licenseId, SyncOrdersRequest req, CancellationToken ct = default)
    {
        var rows = await PostJsonExpectingJsonOrUnknownAsync<SyncOrdersRequest, List<SyncedOrderV2Dto?>>(
            $"/api/v1/licenses/{licenseId}/orders/sync", req, ct);
        return rows?.OfType<SyncedOrderV2Dto>().ToList() ?? new();
    }

    // ─── Satış senkronu: kopya uçları (spec 2026-10-13 §4.5) ─────────────

    /// <summary>
    /// <c>POST sales/hello</c>: kopyanın kimliği, yetenek düzeyi, görülen kipi, bildirdiği kargo ayarı ve
    /// (geri yüklemeden sonra) önceki kimliği. Sunucu 204 döner; başarısız kod fırlatılır.
    /// </summary>
    public async Task PostSalesHelloAsync(Guid licenseId, SalesHelloRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, $"/api/v1/licenses/{licenseId}/sales/hello", req, ct);
        if (resp.IsSuccessStatusCode) return;
        await ThrowMappedAsync(resp);
    }

    /// <summary>
    /// <c>GET sales/state</c>: lisansın satış senkronu durumu. <paramref name="replicaId"/> ve
    /// <paramref name="seenMode"/> ile çağrı kopyanın kalp atışı sayılır (§4.0). Bozuk gövde (200 +
    /// <c>null</c>) fırlatılır: "durum yok" diye bir cevap yok.
    /// </summary>
    public async Task<SalesStateDto> GetSalesStateAsync(
        Guid licenseId, Guid replicaId, int seenMode, CancellationToken ct = default)
    {
        if (seenMode is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(seenMode), seenMode, "seenMode 0 ya da 1 olmalı.");

        var qs = $"?replicaId={replicaId:D}&seenMode={seenMode.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var state = await GetExpectingJsonAsync<SalesStateDto?>($"/api/v1/licenses/{licenseId}/sales/state{qs}", ct);
        return state ?? throw new LicenseApiUnknownException(200,
            "Satış durumu yanıtı bozuk geldi (gövde null).");
    }

    /// <summary>
    /// <c>GET sales/changes</c> (spec 2026-10-13 §4.4): satış değişiklik akışının bir sayfası. İmleç sunucunun
    /// rowversion'ı (<see cref="SalesChangesPage.NextAfterSeq"/>); istemci saati yok. <paramref name="replicaId"/> ve
    /// <paramref name="seenMode"/> ile çağrı kopyanın kalp atışı da sayılır (§4.0).
    /// <para>Müşteri akışıyla aynı gerekçe: boş sayfa turun SONLANDIRICISI — bozuk gövde (200 + <c>null</c>,
    /// <c>items</c>'sız, listede literal <c>null</c>, yan listede <c>null</c>) sessizce boş sayfaya çevrilseydi
    /// çağıran "yetiştim" sanırdı. Son öğeden küçük imleç aynı sayfayı sonsuza dek istetir, sırası bozuk sayfada öğe
    /// başına kaydedilen imleç aradaki öğeyi atlatırdı. Hepsi <see cref="LicenseApiUnknownException"/>.</para>
    /// <para><paramref name="take"/> 1..500 ya da <paramref name="seenMode"/> 0/1 dışında istek gitmeden FIRLATIR:
    /// sunucu kırpar ya da 400 döner, çağıran elindeki değeri yanlış yorumlardı.</para>
    /// </summary>
    public async Task<SalesChangesPage> GetSalesChangesAsync(
        Guid licenseId, long afterSeq, int take, Guid replicaId, int seenMode, CancellationToken ct = default)
    {
        if (take is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(take), take,
                "take 1..500 olmalı (sunucu sınırı, SalesChangesFeed.MaxTake).");
        if (seenMode is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(seenMode), seenMode, "seenMode 0 ya da 1 olmalı.");

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var qs = $"?afterSeq={afterSeq.ToString(inv)}&take={take.ToString(inv)}"
               + $"&replicaId={replicaId:D}&seenMode={seenMode.ToString(inv)}";
        var page = await GetExpectingJsonAsync<SalesChangesPage?>($"/api/v1/licenses/{licenseId}/sales/changes{qs}", ct);
        if (page?.Items is null
            || page.Items.Any(i => i is null)
            || page.SideSessions?.Any(s => s is null) == true
            || page.CustomerDrafts?.Any(d => d is null) == true
            || (page.Items.Count > 0 && page.NextAfterSeq < page.Items[^1].ChangeSeq)
            || page.Items.Zip(page.Items.Skip(1)).Any(p => p.Second.ChangeSeq <= p.First.ChangeSeq))
            throw new LicenseApiUnknownException(200,
                "Satış değişiklik sayfası bozuk geldi (gövde, items, bir öğe, yan liste, sıra ya da imleç). Bu 'değişiklik yok' demek değildir.");
        return page;
    }

    /// <summary>
    /// <c>POST sales/backfill</c> (spec 2026-10-13 §4.3): geçmişin sunucuda tamamlanması — yalnız boşu doldurur,
    /// bildirimsiz, mevcut siparişte birim ayrışmasını döner. Bütün partiyi düşüren hatalar fırlatılır: 400
    /// <c>unknown-session</c> (<see cref="ValidationException"/>, <c>Code = "unknown-session"</c>), 409
    /// <c>sync-conflict</c>. Çözümlenemeyen ya da boş gövde <see cref="LicenseApiUnknownException"/>.
    /// </summary>
    public async Task<SalesBackfillResponse> PostSalesBackfillAsync(
        Guid licenseId, SalesBackfillRequest req, CancellationToken ct = default)
    {
        var response = await PostJsonExpectingJsonOrUnknownAsync<SalesBackfillRequest, SalesBackfillResponse?>(
            $"/api/v1/licenses/{licenseId}/sales/backfill", req, ct);
        return response ?? throw new LicenseApiUnknownException(200,
            "Geri doldurma yanıtı bozuk geldi (gövde null).");
    }

    /// <summary>
    /// <c>POST sales/commands</c> (spec 2026-10-13 §4.6). FIRLATMAZ — komutun her sonucu çağıranın kararıdır:
    /// <list type="bullet">
    ///   <item>200 → <see cref="SalesCommandTransport.Ok"/>; gövde eksiksiz olmalı (liste, öğe ya da sonuç null değil),
    ///   değilse <see cref="SalesCommandTransport.Failed"/> — komut uygulanmış olabilir, aynı niyet aynen yeniden
    ///   gönderilince yeniden oynatma güncel satırları döner.</item>
    ///   <item>400 ve 409 başlıklı ProblemDetails → <see cref="SalesCommandTransport.Problem"/> (ek üyeleriyle). Başlıksız
    ///   ya da çözümlenemeyen gövde <see cref="SalesCommandTransport.Failed"/>.</item>
    ///   <item>404 → <see cref="SalesCommandTransport.EndpointMissing"/> (komut ucu yok, §8.6).</item>
    ///   <item>Ağ hatası ve zaman aşımı → <see cref="SalesCommandTransport.Offline"/>.</item>
    ///   <item>5xx ve öbür kodlar → <see cref="SalesCommandTransport.Failed"/>.</item>
    /// </list>
    /// Yalnız <paramref name="ct"/> iptali fırlatır (<see cref="OperationCanceledException"/>).
    /// </summary>
    public async Task<SalesCommandHttpResult> PostSalesCommandAsync(
        Guid licenseId, SalesCommandRequest req, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await SendJsonAsync(HttpMethod.Post, $"/api/v1/licenses/{licenseId}/sales/commands", req, ct)
                .ConfigureAwait(false);
        }
        catch (LicenseApiNetworkException ex)
        {
            return new SalesCommandHttpResult(SalesCommandTransport.Offline, 0, Error: ex.Message);
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                SalesCommandResponse? body;
                try { body = await DeserializeAsync<SalesCommandResponse?>(resp, ct).ConfigureAwait(false); }
                catch (JsonException ex)
                {
                    return new SalesCommandHttpResult(SalesCommandTransport.Failed, status,
                        Error: $"Komut yanıtı çözümlenemedi: {ex.Message}");
                }
                if (body?.Orders is null || body.Shipments is null || body.Sessions is null || body.Result is null
                    || string.IsNullOrEmpty(body.Status)
                    || body.Orders.Any(o => o is null) || body.Shipments.Any(s => s is null)
                    || body.Sessions.Any(s => s is null))
                    return new SalesCommandHttpResult(SalesCommandTransport.Failed, status,
                        Error: "Komut yanıtı bozuk geldi (gövde, liste, öğe ya da sonuç eksik).");
                return new SalesCommandHttpResult(SalesCommandTransport.Ok, status, Response: body);
            }

            if (status == (int)HttpStatusCode.NotFound)
                return new SalesCommandHttpResult(SalesCommandTransport.EndpointMissing, status);

            if (status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Conflict)
            {
                SalesCommandProblem? problem = null;
                try { problem = await resp.Content.ReadFromJsonAsync<SalesCommandProblem>(JsonOpts, ct).ConfigureAwait(false); }
                catch (JsonException) { /* başlıksız sayılır */ }
                catch (NotSupportedException) { /* problem+json değil */ }
                if (!string.IsNullOrEmpty(problem?.Code)
                    && problem.Current?.Any(o => o is null) != true
                    && problem.CurrentShipments?.Any(s => s is null) != true)
                    return new SalesCommandHttpResult(SalesCommandTransport.Problem, status, Problem: problem);
                return new SalesCommandHttpResult(SalesCommandTransport.Failed, status,
                    Error: $"HTTP {status} başlıklı bir ProblemDetails taşımıyor.");
            }

            return new SalesCommandHttpResult(SalesCommandTransport.Failed, status, Error: $"HTTP {status}");
        }
    }

    // ─── Ödeme isteği kirası ve kayıtları (spec 2026-10-13 §4.7, §6.4) ──────
    //
    // Bu altı uç FIRLATMAZ (PostSalesCommandAsync deseni): her sonuç çağıranın kararıdır ve
    // PaymentRequestHttpResult'ta gelir — Ok (eksiksiz 200), Problem (400/409 başlıklı ve ek üyeleri eksiksiz ret;
    // finish'in 404 record-not-found'u), EndpointMissing (öbür 404'ler), Offline (ağ hatası, zaman aşımı), Failed
    // (5xx, öbür kodlar, başlıksız ya da eksik ret, bozuk 200). Yalnız ct iptali fırlar. Sunucunun 400 vereceği sınır
    // dışı istek (PaymentRequestLimits) gönderilmeden fırlatılır: çağıran hatasıdır.

    /// <summary>
    /// <c>POST payment-requests/begin</c> (§6.4 istemci adımı 3): kişi×kapsamın kirası. 409'lar ek üyeleriyle
    /// <see cref="PaymentRequestTransport.Problem"/>: <c>already-requested {priors, priorsDigest}</c> (#523 öncesi sunucuda
    /// özet yok), <c>members-stale {members}</c>, <c>stale-total {serverTotal, liveElsewhere}</c>, <c>settings-stale
    /// {current}</c>, <c>in-progress-elsewhere {machine, since, recordId}</c> (kilit zaman aşımında boş). Ek üyesi eksik
    /// ret ve yanıtın kaydı başka bir Id taşıyan 200'ü <see cref="PaymentRequestTransport.Failed"/>.
    /// <para><see cref="PaymentRequestBeginRequest.ConfirmedPriorIds"/> &gt; 500 ya da üye sayısı 1–100 dışında istek
    /// gitmeden <see cref="ArgumentOutOfRangeException"/>.</para>
    /// </summary>
    public Task<PaymentRequestHttpResult<PaymentRequestBeginResponse>> PostPaymentRequestBeginAsync(
        Guid licenseId, PaymentRequestBeginRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        GuardMembers(req.MemberIds, $"{nameof(req)}.{nameof(req.MemberIds)}");
        if (req.ConfirmedPriorIds is { Count: > PaymentRequestLimits.MaxConfirmedPriorIds } confirmed)
            throw new ArgumentOutOfRangeException($"{nameof(req)}.{nameof(req.ConfirmedPriorIds)}", confirmed.Count,
                $"confirmedPriorIds en çok {PaymentRequestLimits.MaxConfirmedPriorIds} Id taşır; fazlası gönderilmez, "
                + "bütün kümeyi onay özeti onaylar (sunucu 400 invalid-confirmed-priors verirdi).");

        return PaymentRequestCallAsync<PaymentRequestBeginResponse>(
            token => SendJsonAsync(HttpMethod.Post, $"/api/v1/licenses/{licenseId}/payment-requests/begin", req, token),
            r => r.RecordId == req.RecordId && !string.IsNullOrEmpty(r.State)
                 && r.Members is not null && !r.Members.Any(m => m is null),
            IsCompleteBeginProblem,
            ct);
    }

    /// <summary>
    /// <c>POST payment-requests/{id}/finish</c> (§6.4 "Akışın sonu"): kiranın sonucu, iki kipte. Kayıt bu lisansta yoksa
    /// 404 <c>record-not-found</c> <see cref="PaymentRequestTransport.Problem"/>'dir (uç yok değil); yanıtın kaydı başka bir
    /// Id taşıyorsa <see cref="PaymentRequestTransport.Failed"/>.
    /// </summary>
    public Task<PaymentRequestHttpResult<PaymentRequestFinishResponse>> PostPaymentRequestFinishAsync(
        Guid licenseId, Guid recordId, PaymentRequestFinishRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        return PaymentRequestCallAsync<PaymentRequestFinishResponse>(
            token => SendJsonAsync(HttpMethod.Post,
                $"/api/v1/licenses/{licenseId}/payment-requests/{recordId}/finish", req, token),
            r => r.RecordId == recordId && !string.IsNullOrEmpty(r.State),
            _ => true,
            ct);
    }

    /// <summary>
    /// <c>GET payment-requests?scopeKey&amp;memberIds</c> (§6.4 "Teslim kayıtlarının okunması"): kişi×kapsamın kayıtları,
    /// salt okunur. <c>memberIds</c> yinelenen anahtarla gider (sunucu <c>List&lt;string&gt;</c> okur), değerler kaçışlı.
    /// Boş liste "kayıt yok" demektir; bu yüzden bozuk gövde (<c>records</c>'suz, literal <c>null</c> öğe, sipariş kümesi
    /// olmayan öğe) boş listeye çevrilmez, <see cref="PaymentRequestTransport.Failed"/> olur.
    /// <para>Üye sayısı 1–100 dışında istek gitmeden <see cref="ArgumentOutOfRangeException"/>.</para>
    /// </summary>
    public Task<PaymentRequestHttpResult<PaymentRequestListResponse>> GetPaymentRequestsAsync(
        Guid licenseId, string scopeKey, IReadOnlyList<string> memberIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scopeKey);
        GuardMembers(memberIds, nameof(memberIds));

        var qs = "?scopeKey=" + Uri.EscapeDataString(scopeKey)
                 + string.Concat(memberIds.Select(m => "&memberIds=" + Uri.EscapeDataString(m)));
        return PaymentRequestCallAsync<PaymentRequestListResponse>(
            token => SendGetAsync($"/api/v1/licenses/{licenseId}/payment-requests{qs}", token),
            r => r.Records is not null
                 && r.Records.All(i => i is not null && i.Id != Guid.Empty && i.OrderIds is not null
                                       && !string.IsNullOrEmpty(i.Kind) && !string.IsNullOrEmpty(i.State)),
            _ => true,
            ct);
    }

    /// <summary>
    /// <c>POST payment-requests/backfill</c> (§6.4 "Ödeme geçmişi"): kirasız kayıtlar, moddan bağımsız. Yanıt öğe başına
    /// ve istekteki sırayla gelir; sayısı ya da Id'leri istekle hizalanmayan, durumu boş sonuç
    /// <see cref="PaymentRequestTransport.Failed"/> (yineleme zararsız).
    /// <para>Parti 1–200 ya da bir öğenin üye sayısı 1–100 dışında istek gitmeden
    /// <see cref="ArgumentOutOfRangeException"/>.</para>
    /// </summary>
    public Task<PaymentRequestHttpResult<PaymentRequestBackfillResponse>> PostPaymentRequestBackfillAsync(
        Guid licenseId, PaymentRequestBackfillRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Records is not { Count: > 0 and <= PaymentRequestLimits.MaxBackfillBatch } records)
            throw new ArgumentOutOfRangeException($"{nameof(req)}.{nameof(req.Records)}", req.Records?.Count,
                $"backfill partisi 1–{PaymentRequestLimits.MaxBackfillBatch} kayıt taşır (sunucu 400 invalid-batch verirdi).");
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record, $"{nameof(req)}.{nameof(req.Records)}");
            GuardMembers(record.MemberIds, $"{nameof(req)}.{nameof(req.Records)}.{nameof(record.MemberIds)}");
        }

        return PaymentRequestCallAsync<PaymentRequestBackfillResponse>(
            token => SendJsonAsync(HttpMethod.Post, $"/api/v1/licenses/{licenseId}/payment-requests/backfill", req, token),
            r => r.Records is not null
                 && r.Records.Count == records.Count
                 && r.Records.Select((result, i) => result is not null && result.RecordId == records[i].RecordId
                                                    && !string.IsNullOrEmpty(result.Status)).All(ok => ok),
            _ => true,
            ct);
    }

    // ─── Lisans geneli kargo ayarı (spec 2026-10-13 §4.8, §6.2) ─────────────

    /// <summary><c>GET shipping-settings</c>: lisansın ayarı ve sürümü; satır yoksa sürüm 0 ve boş değerler. Fırlatmaz
    /// (ödeme isteği uçlarının sonucu).</summary>
    public Task<PaymentRequestHttpResult<ShippingSettingsSnapshot>> GetShippingSettingsAsync(
        Guid licenseId, CancellationToken ct = default)
        => PaymentRequestCallAsync<ShippingSettingsSnapshot>(
            token => SendGetAsync($"/api/v1/licenses/{licenseId}/shipping-settings", token),
            _ => true,
            _ => true,
            ct);

    /// <summary>
    /// <c>PUT shipping-settings</c> (§4.8): WPF ayar ekranı, yalnız mod On'da. Sürüm eskiyse 409 <c>stale {current}</c>
    /// <see cref="PaymentRequestTransport.Problem"/>'dir (<c>current</c>'sız <c>stale</c> başarısız); mod Off'ta 409
    /// <c>mode-off</c>. Fırlatmaz.
    /// </summary>
    public Task<PaymentRequestHttpResult<ShippingSettingsSnapshot>> PutShippingSettingsAsync(
        Guid licenseId, ShippingSettingsPutRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        return PaymentRequestCallAsync<ShippingSettingsSnapshot>(
            token => SendJsonAsync(HttpMethod.Put, $"/api/v1/licenses/{licenseId}/shipping-settings", req, token),
            _ => true,
            p => p.Code != PaymentRequestProblemCodes.Stale || p.Current is not null,
            ct);
    }

    /// <summary>Üye listesi 1–<see cref="PaymentRequestLimits.MaxMembers"/>: sunucu sınırı (<c>invalid-members</c>).</summary>
    private static void GuardMembers(IReadOnlyList<string>? memberIds, string paramName)
    {
        if (memberIds is not { Count: > 0 and <= PaymentRequestLimits.MaxMembers })
            throw new ArgumentOutOfRangeException(paramName, memberIds?.Count,
                $"memberIds 1–{PaymentRequestLimits.MaxMembers} üye taşır (sunucu 400 invalid-members verirdi).");
    }

    /// <summary><c>begin</c>'in ek üyeli retleri ancak ek üyeleri gelince kullanılabilir: soru öncülsüz, yeniden deneme
    /// sunucu grubu, toplamı ya da ayarı olmadan kurulamaz. Öncülün <c>priorsDigest</c>'i #523 öncesi sunucuda yoktur ve
    /// zorunlu değildir; <c>in-progress-elsewhere</c>'in alanları kilit zaman aşımında boştur.</summary>
    private static bool IsCompleteBeginProblem(PaymentRequestProblem p) => p.Code switch
    {
        PaymentRequestProblemCodes.AlreadyRequested => p.Priors is not null,
        PaymentRequestProblemCodes.MembersStale => p.Members is not null,
        PaymentRequestProblemCodes.StaleTotal => p.ServerTotal is not null,
        PaymentRequestProblemCodes.SettingsStale => p.Current is not null,
        _ => true,
    };

    /// <summary>
    /// Ödeme isteği ve kargo ayarı uçlarının ortak taşıma eşlemesi (yukarıdaki blok yorumu). <paramref name="isComplete"/>
    /// 200 gövdesinin, <paramref name="isCompleteProblem"/> ucun ek üyeli retlerinin eksiksizliğini denetler; listelerde
    /// literal <c>null</c> öğe her retde bozuk sayılır.
    /// </summary>
    private async Task<PaymentRequestHttpResult<TResp>> PaymentRequestCallAsync<TResp>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<TResp, bool> isComplete,
        Func<PaymentRequestProblem, bool> isCompleteProblem,
        CancellationToken ct)
        where TResp : class
    {
        HttpResponseMessage resp;
        try
        {
            resp = await send(ct).ConfigureAwait(false);
        }
        catch (LicenseApiNetworkException ex)
        {
            return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Offline, 0, Error: ex.Message);
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                TResp? body;
                try { body = await DeserializeAsync<TResp?>(resp, ct).ConfigureAwait(false); }
                catch (JsonException ex)
                {
                    return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Failed, status,
                        Error: $"Yanıt çözümlenemedi: {ex.Message}");
                }
                if (body is null || !isComplete(body))
                    return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Failed, status,
                        Error: "Yanıt bozuk geldi (gövde, liste, öğe ya da kayıt Id'si eksik/uyumsuz).");
                return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Ok, status, Response: body);
            }

            if (status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.NotFound or (int)HttpStatusCode.Conflict)
            {
                PaymentRequestProblem? problem = null;
                try { problem = await DeserializeAsync<PaymentRequestProblem?>(resp, ct).ConfigureAwait(false); }
                catch (JsonException) { /* başlıksız sayılır */ }

                if (status == (int)HttpStatusCode.NotFound)
                    return problem?.Code == PaymentRequestProblemCodes.RecordNotFound
                        ? new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Problem, status, Problem: problem)
                        : new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.EndpointMissing, status);

                if (!string.IsNullOrEmpty(problem?.Code)
                    && problem.Priors?.Any(p => p is null) != true
                    && problem.Members?.Any(m => m is null) != true
                    && problem.LiveElsewhere?.Any(s => s is null) != true
                    && isCompleteProblem(problem))
                    return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Problem, status, Problem: problem);
                return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Failed, status,
                    Error: $"HTTP {status} başlıklı ya da ek üyeleri eksiksiz bir ProblemDetails taşımıyor.");
            }

            return new PaymentRequestHttpResult<TResp>(PaymentRequestTransport.Failed, status, Error: $"HTTP {status}");
        }
    }

    // ─── WhatsApp template sync (Faz 2, 2026-05-15) ───────────────────────

    /// <summary>PaymentSettings'in WhatsApp template'lerini server'a push'lar.
    /// Upsert per License (LicenseId unique). Server fire-and-forget güvenilir;
    /// hata fırlatırsa caller log'lar, kullanıcı akışını bozmaz.</summary>
    public Task<WhatsAppTemplatesDto> PutWhatsAppTemplatesAsync(
        Guid licenseId, WhatsAppTemplatesRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<WhatsAppTemplatesRequest, WhatsAppTemplatesDto>(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates", req, ct,
            methodOverride: HttpMethod.Put);

    // ─── Shopper-code (Faz 0c-1) ──────────────────────────────────────────

    /// <summary>Returns current shopper-code settings for the authenticated panel user.
    /// Throws <see cref="HttpRequestException"/> (via 404) when no license is found.</summary>
    public Task<ShopperCodeResponse> GetShopperCodeAsync(CancellationToken ct = default)
        => GetExpectingJsonAsync<ShopperCodeResponse>("/api/panel/shopper-code", ct);

    /// <summary>Updates shopper-code. Throws <see cref="ShopperCodeValidationException"/>
    /// on 400 — <c>ErrorCode</c> is the Problem.Title from server:
    /// "empty" / "length" / "format" / "reserved" / "profanity" / "cooldown" / "taken".</summary>
    public async Task<ShopperCodeResponse> SetShopperCodeAsync(string code, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Put, "/api/panel/shopper-code",
            new SetShopperCodeRequest(code), ct);
        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            // ASP.NET Core Problem details JSON: { "title": "<errorCode>", "status": 400, ... }
            var problem = await DeserializeAsync<ProblemPayload>(resp, ct);
            throw new ShopperCodeValidationException(problem?.Title ?? "unknown");
        }
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
        return (await DeserializeAsync<ShopperCodeResponse>(resp, ct))!;
    }

    // ─── Payment account (Faz 0c-1) ───────────────────────────────────────

    /// <summary>Upserts IBAN + accountHolder for the given license on server.</summary>
    public async Task SyncPaymentAccountAsync(
        Guid licenseId, string? iban, string? accountHolder, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post,
            $"/api/v1/licenses/{licenseId}/payment-account",
            new SetPaymentAccountRequest(iban, accountHolder), ct);
        if ((int)resp.StatusCode == 204) return;
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
    }

    // ─── WPF customers bulk sync (Faz 0c-1) ───────────────────────────────

    /// <summary>Batch upsert of WPF customers (all platforms). Returns server-side
    /// synced count, retroactive shopper-code matches and copy→canonical redirects.
    /// <para>Bozuk gövde (200 + <c>null</c>) sessizce "yönlendirme yok" sayılmaz ve fırlatılır:
    /// <see cref="WpfCustomerSyncResponse.Redirects"/> yerel yeniden anahtarlamayı (C4
    /// <c>RekeyToLocal</c>) tetikler — sessizce yutulan bir null gövde kopya satırı yerelde
    /// sonsuza dek bırakırdı.</para></summary>
    public async Task<WpfCustomerSyncResponse> SyncWpfCustomersAsync(
        Guid licenseId, IReadOnlyList<WpfCustomerSyncItem> customers, CancellationToken ct = default)
    {
        var resp = await PostJsonExpectingJsonAsync<WpfCustomerSyncRequest, WpfCustomerSyncResponse>(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new WpfCustomerSyncRequest(customers), ct);
        return resp ?? throw new LicenseApiUnknownException(200,
            "Müşteri senkron yanıtı bozuk geldi (gövde null). Bu 'yönlendirme yok' demek değildir.");
    }

    // ─── WPF müşteri değişiklik akışı (çoklu bilgisayar, Bölüm C) ──────────

    /// <summary>
    /// Müşteri değişiklik akışının bir sayfası. İmleç sunucunun rowversion'ı
    /// (<see cref="WpfCustomerChangesPage.NextAfterSeq"/>); istemci saati yok.
    /// <para>Katalog uçlarıyla aynı gerekçe: boş sayfa DÖNGÜ SONLANDIRICISI — bozuk gövde
    /// (200 + <c>null</c>, items'sız) sessizce boş sayfaya çevrilseydi çağıran "yetiştim"
    /// sanıp turu başarı sayardı. Fırlatılır.</para>
    /// <para><paramref name="take"/> 1..500 dışında FIRLATIR: sunucu kırpar ve çağıran
    /// elindeki değeri yanlış yorumlardı.</para>
    /// </summary>
    public async Task<WpfCustomerChangesPage> GetWpfCustomerChangesAsync(
        Guid licenseId, long afterSeq, int take = 500, CancellationToken ct = default)
    {
        if (take is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(take), take,
                "take 1..500 olmalı (sunucu sınırı, LicensesWpfCustomersPullController.Changes).");

        var qs = $"?afterSeq={afterSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
               + $"&take={take.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var page = await GetExpectingJsonAsync<WpfCustomerChangesPage>(
            $"/api/v1/licenses/{licenseId}/wpf-customers/changes{qs}", ct);
        // Boş öğe (JSON dizisinde literal null) ya da kullanılamaz imleç — ikisi de
        // "bozuk gövde" sınıfında. Kullanılamaz imleç ÖZELLİKLE tehlikeli: sunucu
        // NextAfterSeq'i normalde son öğenin ChangeSeq'i yapar (Changes action,
        // "var next = items.Count == 0 ? afterSeq : items[^1].ChangeSeq"); ondan
        // KÜÇÜK bir değer çağıranı aynı sayfayı sonsuza dek yeniden istemeye düşürür.
        if (page?.Items is null || page.Items.Any(i => i is null)
            || (page.Items.Count > 0 && page.NextAfterSeq < page.Items[^1].ChangeSeq))
            throw new LicenseApiUnknownException(200,
                "Müşteri değişiklik sayfası bozuk geldi (gövde, items, bir öğe ya da imleç null/geçersiz). Bu 'değişiklik yok' demek değildir.");
        return page;
    }

    // ─── WPF katalog replikası (Stok Faz 1b) ──────────────────────────────

    // Bu iki metot, dosyadaki diğer liste uçlarından (örn.
    // GetPaymentsSinceAsync) BİLEREK ayrılıyor: onlarda `?? new()` ile boş
    // liste dönmek zararsız, burada boş liste DÖNGÜ SONLANDIRICISI. Bozuk bir
    // gövde (200 + literal `null`) sessizce boş listeye çevrilirse çekme döngüsü
    // "katalog boş" sanır ve işlemsel DELETE+INSERT replikayı komple siler —
    // ardından yayında hiçbir ürün kodu eşleşmez. Bu yüzden coerce etmiyoruz,
    // fırlatıyoruz. Gerçek hatalar zaten gürültülü (ThrowMappedAsync); geriye
    // yalnız bu sessiz dönüşüm kalıyordu.

    /// <summary>
    /// Kataloğun bir sayfasını çeker. <b>Tam anlık görüntü</b> —
    /// <paramref name="after"/> bir DEĞİŞİM imleci değil, birincil anahtar
    /// üstünde keyset sayfalama imleci.
    /// <para>Çağıran, boş sayfa gelene kadar son ürünün <c>Id</c>'siyle döngüye
    /// devam ETMELİ ve ancak tamamı geldiğinde replikayı baştan yazmalıdır.</para>
    /// <para><paramref name="take"/> 1..500 dışındaysa metot <b>fırlatır</b>, sessizce
    /// kırpmaz: sunucunun onurlandırmayacağı bir take'i elinde tutan çağıran, dönen
    /// sayfayı "eksik" sanıp döngüyü erken bitirirdi.</para>
    /// <para><b>Dönen satır sayısı &lt; take'i bitiş göstergesi olarak KULLANMA.</b>
    /// Tek güvenilir bitiş işareti <c>rows.Count == 0</c>'dır: son dolu sayfa da tam
    /// olarak take satır içerebilir — o zaman "eksik sayfa" hiç görünmez — ve
    /// sunucunun sayfa davranışı bir gün değişse bile boş sayfa kuralı bozulmaz.</para>
    /// <para>İmleci <c>rows[^1].Id</c>'den al, sayfayı yeniden SIRALAMA: sunucu
    /// sırası SQL Server'ın <c>uniqueidentifier</c> karşılaştırmasında üretiliyor,
    /// .NET'in <c>Guid.CompareTo</c> sırası farklı düşer ve satır atlatır.</para>
    /// <para>Hata durumunda boş liste DÖNMEZ, <see cref="LicenseApiException"/>
    /// fırlatır — 404/401/5xx/ağ <b>ve bozuk gövde</b> (JSON değil, kesik ya da
    /// şemaya uymayan yanıt) dahil hepsi bu tek aileden gelir. Çağıran döngünün
    /// tamamını tek bir <c>catch (LicenseApiException)</c> ile sarmalayıp herhangi
    /// bir hatada replikayı yazmadan çıkabilir.</para>
    /// </summary>
    public async Task<List<CatalogProductPullItem>> GetCatalogProductsAsync(
        Guid licenseId, Guid? after, int take = 200, CancellationToken ct = default)
    {
        // Sunucu take'i 1..500'e kırpıyor (LicensesWpfCatalogPullController).
        // Sessizce kırpmıyoruz: take by-value, çağıran kendi elindeki 1000'i
        // görmeye devam eder ve "500 < 1000, demek ki son sayfa" diye döngüyü
        // erken bitirir — ardından gelen tam-yenileme kataloğun kalanını siler.
        // Sınır dışı take bir ÇAĞIRAN HATASI; sessizce düzeltmek yerine fırlat.
        if (take is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(take), take,
                "take 1..500 olmalı (sunucu sınırı, LicensesWpfCatalogPullController).");

        var qs = after is null ? $"?take={take}" : $"?after={after}&take={take}";
        return await GetExpectingJsonAsync<List<CatalogProductPullItem>>(
            $"/api/v1/licenses/{licenseId}/catalog/products{qs}", ct)
            ?? throw new LicenseApiUnknownException(200,
                "Katalog ürün sayfası bozuk geldi (gövde null). Bu 'katalog boş' demek değildir.");
    }

    /// <summary>
    /// Stok hareket defterinden bileşik imleçle bir sayfa bakiye çeker.
    ///
    /// <para>Gövde <b>mutlak</b> bakiye taşır (sunucu <c>SUM</c>'ı yapıp
    /// gönderiyor); istemci toplamaz, yerine yazar.</para>
    ///
    /// <para>Katalog uçlarıyla aynı gerekçeyle burada da <c>?? new()</c> YOK:
    /// boş liste bu döngüde hem sonlandırıcı hem imleç ilerletici, bozuk gövdeyi
    /// boş sayfa saymak hareketleri sessizce kaybettirirdi.</para>
    ///
    /// <para>Sunucu <c>UtcNow - 60sn</c>'den yeni hareketleri hiç okumuyor
    /// (commit sırası ≠ zaman damgası sırası). Yani en taze hareketler bir
    /// sonraki tura kalır — bu bir hata değil, sözleşmenin parçası.</para>
    /// </summary>
    public async Task<StockBalancePullResponse> GetStockBalancesSinceAsync(
        Guid licenseId, DateTimeOffset since, Guid sinceId,
        int take = 500, CancellationToken ct = default)
    {
        if (take is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(take), take,
                "take 1..1000 olmalı (sunucu sınırı, LicensesWpfStockPullController).");

        var qs = $"?since={Uri.EscapeDataString(since.ToString("O"))}"
               + $"&sinceId={sinceId}&take={take}";

        return await GetExpectingJsonAsync<StockBalancePullResponse>(
            $"/api/v1/licenses/{licenseId}/stock/balances/since{qs}", ct)
            ?? throw new LicenseApiUnknownException(200,
                "Stok bakiye sayfası bozuk geldi (gövde null). İmleç ilerletilmemeli.");
    }

    /// <summary>Kategori ağacının tamamı; sayfalama yok (derinlik sınırlı).
    /// Sunucu <b>pasif</b> kategorileri de döndürür — bir ürün pasif kategoriye
    /// bağlı kalmış olabilir; WPF <c>IsActive == false</c> satırları beklemeli,
    /// bozuk veri saymamalıdır. Bozuk gövdede boş liste dönmez, fırlatır.</summary>
    public async Task<List<CatalogCategoryPullItem>> GetCatalogCategoriesAsync(
        Guid licenseId, CancellationToken ct = default)
        => await GetExpectingJsonAsync<List<CatalogCategoryPullItem>>(
            $"/api/v1/licenses/{licenseId}/catalog/categories", ct)
            ?? throw new LicenseApiUnknownException(200,
                "Katalog kategori ağacı bozuk geldi (gövde null). Bu 'kategori yok' demek değildir.");

    // ─── Customer balance (E1/E3) ────────────────────────────────────

    /// <summary>WPF "Ödeme iste" anlığında müşterinin bakiyesini sorgular.
    /// Hiç bakiye yoksa 0 dönen response, hata değil.</summary>
    public async Task<CustomerBalancePreview> GetBalancePreviewAsync(
        Guid licenseId, Guid wpfCustomerId, CancellationToken ct = default)
    {
        var qs = $"?wpfCustomerId={wpfCustomerId:D}";
        return await GetExpectingJsonAsync<CustomerBalancePreview>(
            $"/api/v1/licenses/{licenseId}/customer-balance/preview{qs}", ct)
            ?? new CustomerBalancePreview(wpfCustomerId, 0m, DateTimeOffset.UtcNow);
    }

    /// <summary>R4-03: bu kapsamda sunucuda geri alınmamış bir düşüm var mı?
    /// <c>null</c> = yok (204), istemci bugünkü akışta kalır.
    ///
    /// <para>Neden gerekiyor: satışın yerel kimliği (idempotency anahtarı)
    /// diskte yaşıyor. Eski bir yedek geri yüklenince o anahtar yok oluyor,
    /// uzak defter ise düşümü hatırlamaya devam ediyor — anahtarsız istemci
    /// düşümü oynatamadığı için aynı satışı ikinci kez düşerdi. Kapsam
    /// sunucuda durduğundan düşüm burada tanınıp benimsenebiliyor.</para></summary>
    public async Task<CustomerBalanceScope?> GetBalanceScopeAsync(
        Guid licenseId, Guid wpfCustomerId, string saleScope, CancellationToken ct = default)
    {
        var qs = $"?wpfCustomerId={wpfCustomerId:D}&saleScope={Uri.EscapeDataString(saleScope)}";
        return await GetExpectingJsonOrNullAsync<CustomerBalanceScope>(
            $"/api/v1/licenses/{licenseId}/customer-balance/scope{qs}", ct,
            HttpStatusCode.NoContent);
    }

    /// <summary>R6-02: kayıtlı bir düşümün sunucuda hâlâ geçerli olup olmadığı.
    /// <c>null</c> = sunucu işlemi tanımıyor (404 — ör. sunucu tarafı bir geri
    /// yükleme onu sildi); çağıran bunu "düşüm artık yok" saymalı.
    ///
    /// <para><paramref name="transactionId"/> = apply'da kullanılan idempotency
    /// anahtarı (ledger PK). Kapsam sorgusunun aksine kimlikle sorar: kapsamsız
    /// yazılmış eski satırlar (033 dönemi / legacy devri) için de doğru cevap
    /// verir — o satırlarda kapsam sorgusu 204 döner ve "iade edildi" ile
    /// "kapsamsız yazıldı" ayırt edilemezdi.</para>
    ///
    /// <para>R9-F03: <c>null</c> yalnız sunucu 404'ü <c>transaction-not-found</c>
    /// başlığıyla imzalarsa döner. Gövdesiz/başlıksız 404 (rota olmayan eski
    /// sunucu, ters proxy) hata olarak fırlatılır — çağıran onu "uç cevap
    /// veremedi" sayar ve apply anahtarını SİLMEZ. Aksi hâlde eski sunucuya
    /// karşı iki tıklama aynı satışı iki kez düşürebilirdi.</para></summary>
    public async Task<CustomerBalanceTransactionStatus?> GetBalanceTransactionStatusAsync(
        Guid licenseId, Guid transactionId, CancellationToken ct = default)
        => await GetExpectingJsonOrNullAsync<CustomerBalanceTransactionStatus>(
            $"/api/v1/licenses/{licenseId}/customer-balance/transactions/{transactionId}", ct,
            HttpStatusCode.NotFound, nullRequiresTitle: "transaction-not-found");

    /// <summary>WPF "Ödeme iste" sonrası bakiye düşüşünü commit eder.
    /// Server min(Amount, balance, productTotal) ile capped uygular.
    ///
    /// <para>Spec 2026-10-13 §6.6: mod On'da kapsamda geri alınmamış düşüm varsa 409 <c>scope-has-deduction</c> tipli
    /// <see cref="ScopeHasDeductionException"/> olur (ek üyeleri eksikse bugünkü gibi
    /// <see cref="ValidationException"/>). Öbür hatalar bugünkü gibi: kilit zaman aşımı 409 <c>stale</c> dahil 4xx
    /// <see cref="ValidationException"/> (<c>Code</c> = başlık).</para></summary>
    public async Task<CustomerBalanceApplyResponse> ApplyBalanceAsync(
        Guid licenseId, CustomerBalanceApplyRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, $"/api/v1/licenses/{licenseId}/customer-balance/apply", req, ct);
        if (resp.IsSuccessStatusCode)
            return (await DeserializeAsync<CustomerBalanceApplyResponse>(resp, ct))!;
        if (resp.StatusCode != HttpStatusCode.Conflict)
        {
            await ThrowMappedAsync(resp);
            throw new InvalidOperationException("ThrowMappedAsync her zaman fırlatır."); // ulaşılmaz
        }

        // 409 gövdesi bir kez okunur (içerik akışı ikinci okumada boş döner); eşleme ThrowMappedAsync'inkiyle aynı.
        ApplyProblemPayload? problem = null;
        try { problem = await DeserializeAsync<ApplyProblemPayload>(resp, ct); }
        catch (JsonException) { /* problem+json değil — başlıksız */ }
        if (problem is { Title: ScopeHasDeductionException.ProblemCode, TransactionId: { } transactionId,
                AppliedAmount: { } appliedAmount, ProductTotal: { } productTotal })
            throw new ScopeHasDeductionException(transactionId, appliedAmount, productTotal, problem.Detail);
        throw MapProblem((int)resp.StatusCode, problem?.Title, problem?.Detail);
    }

    /// <summary>Bir purchase-deduction satırını geri alır (revizyon akışı, K2).
    /// <paramref name="transactionId"/> = apply'da kullanılan idempotency
    /// anahtarı (ledger PK). Sunucu 409 already-reversed dönerse çağıran bunu
    /// BAŞARI saymalı — geri alma zaten yapılmış.</summary>
    public async Task ReverseBalanceTransactionAsync(
        Guid licenseId, Guid transactionId, CancellationToken ct = default)
    {
        var url = $"api/v1/licenses/{licenseId}/customer-balance/transactions/{transactionId}/reverse";
        using var resp = await SendJsonAsync(HttpMethod.Post, url, new { }, ct);
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
    }

    /// <summary>Panel endpoint'i ile customer detay + transaction listesi.
    /// wpfCustomerId = WPF lokal Customer.Id (hex N format) — sync sırasında
    /// server'daki WpfCustomerProjection.Id ile aynı tutuluyor.</summary>
    public async Task<CustomerBalanceDetailsResponse> GetCustomerBalanceAsync(
        Guid wpfCustomerId, int take = 50, CancellationToken ct = default)
    {
        return await GetExpectingJsonAsync<CustomerBalanceDetailsResponse>(
            $"/api/panel/customers/{wpfCustomerId}/balance?take={take}", ct)
            ?? new CustomerBalanceDetailsResponse(
                new CustomerBalanceDto(wpfCustomerId, Guid.Empty, 0m, DateTimeOffset.UtcNow),
                Array.Empty<CustomerBalanceTransactionDto>());
    }

    public async Task AddRefundFullAsync(
        Guid wpfCustomerId, RefundFullRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post,
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-full", req, ct);
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
    }

    public async Task AddRefundNetAsync(
        Guid wpfCustomerId, RefundNetRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post,
            $"/api/panel/customers/{wpfCustomerId}/balance/refund-net", req, ct);
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
    }

    // ─── Toplu SMS (Bearer-Customer) ──────────────────────────────────

    /// <summary>Toplu SMS önizleme — alıcı sayısı, segment ve tahmini SMS maliyeti.</summary>
    public Task<SmsPreviewResponse> PreviewSmsCampaignAsync(
        Guid licenseId, SmsPreviewRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SmsPreviewRequest, SmsPreviewResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", req, ct);

    /// <summary>Kampanya oluşturur; gönderim arka planda yapılır (ücret Netgsm tarafında).</summary>
    public Task<SmsCreateResponse> CreateSmsCampaignAsync(
        Guid licenseId, SmsCreateRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SmsCreateRequest, SmsCreateResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", req, ct);

    /// <summary>Tek kampanyanın gönderim durumu (sent/failed/skipped sayıları).</summary>
    public Task<SmsCampaignStatusResponse> GetSmsCampaignStatusAsync(
        Guid licenseId, Guid campaignId, CancellationToken ct = default)
        => GetExpectingJsonAsync<SmsCampaignStatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{campaignId}", ct);

    /// <summary>Kampanya geçmişi (en yeni önce).</summary>
    public async Task<List<SmsCampaignListItem>> ListSmsCampaignsAsync(
        Guid licenseId, int take = 20, CancellationToken ct = default)
        => await GetExpectingJsonAsync<List<SmsCampaignListItem>>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns?take={take}", ct)
            ?? new List<SmsCampaignListItem>();

    // ─── WhatsApp tek mesaj (Bearer-Customer) ─────────────────────────

    /// <summary>Yayıncının kendi lisansından tek WhatsApp metin mesajı gönderir.
    /// Gönderilemediğinde de 200 döner — sonucu <see cref="WhatsAppSendResponse.Ok"/>
    /// ve <see cref="WhatsAppSendResponse.ErrorCode"/> taşır, çağıran buna göre
    /// wa.me'ye düşer.</summary>
    public Task<WhatsAppSendResponse> SendWhatsAppTextAsync(
        Guid licenseId, WhatsAppSendRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<WhatsAppSendRequest, WhatsAppSendResponse>(
            $"/api/v1/licenses/{licenseId}/whatsapp/send", req, ct);

    /// <summary>Lisansa bağlı WhatsApp hesabının Meta'da onaylı şablonları —
    /// ayar ekranındaki şablon seçicinin veri kaynağı.
    ///
    /// <para>Lisans AÇIK geçiliyor (panelin örtük kapsamı değil): operatörün
    /// seçtiği lisans neyse şablonlar da o hesabınki olmalı, yoksa seçim ile
    /// gönderim farklı hesaplara bakar.</para>
    ///
    /// <para>Hesap bağlı değilse sunucu 503 döner ve bu metot fırlatır. Boş liste
    /// dönmek yanlış olurdu: "WhatsApp bağlı değil" ile "bağlı ama onaylı şablon
    /// yok" yayıncı için taban tabana zıt iki iş.</para></summary>
    public async Task<List<ApprovedTemplateDto>> GetApprovedWhatsAppTemplatesAsync(
        Guid licenseId, CancellationToken ct = default)
        => await GetExpectingJsonAsync<List<ApprovedTemplateDto>>(
            $"/api/v1/licenses/{licenseId}/whatsapp/approved-templates", ct)
            ?? new List<ApprovedTemplateDto>();

    /// <summary>Yayıncının bağlı shopper'larının bekleyen (opsiyonel: geçmiş dahil)
    /// destek taleplerini listeler. Bearer-Customer auth otomatik.</summary>
    public async Task<SupportRequestDto[]> GetSupportRequestsAsync(
        bool includeResolved = false, int take = 50, CancellationToken ct = default)
    {
        var path = $"/api/panel/support-requests?includeResolved={(includeResolved ? "true" : "false")}&take={take}";
        return await GetExpectingJsonAsync<SupportRequestDto[]>(path, ct)
            ?? Array.Empty<SupportRequestDto>();
    }

    /// <summary>Bir forgot-password talebi için geçici parola üretir; server
    /// shopper'ın hash'ini günceller + refresh token'larını iptal eder. Plaintext
    /// parola sadece bu response'ta döner — yayıncı WhatsApp'tan iletir.</summary>
    public async Task<IssueTempPasswordResponse> IssueTempPasswordAsync(
        Guid requestId, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post,
            $"/api/panel/support-requests/{requestId}/issue-temp-password",
            new { }, ct);
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
        return (await DeserializeAsync<IssueTempPasswordResponse>(resp, ct))!;
    }

    // ─── Facebook OAuth aracısı (Bearer-Customer) ─────────────────────

    /// <summary>Yapılandırma sunucudan gelir; böylece Meta panelinde config
    /// veya izin seti değişince yeni masaüstü sürümü yayınlamak gerekmez.</summary>
    public Task<OrderDeck.Core.Chat.FacebookOAuthConfig> GetFacebookOAuthConfigAsync(
        CancellationToken ct = default)
        => GetExpectingJsonAsync<OrderDeck.Core.Chat.FacebookOAuthConfig>(
            "/api/v1/facebook/oauth/config", ct);

    /// <summary>Takası sunucu yapar — App Secret masaüstü binary'sine hiç
    /// girmez. <c>code</c> kısa ömürlü, çağrı gecikmemeli.</summary>
    public Task<OrderDeck.Core.Chat.FacebookLongLivedToken> ExchangeFacebookCodeAsync(
        string code, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<object, OrderDeck.Core.Chat.FacebookLongLivedToken>(
            "/api/v1/facebook/oauth/exchange", new { code }, ct);

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

    /// <summary><see cref="PostJsonExpectingJsonAsync{TReq,TResp}"/> gibi; ek olarak çözümlenemeyen başarılı gövde
    /// (kesik yanıt, JSON content-type'lı HTML) ham <see cref="JsonException"/> yerine
    /// <see cref="LicenseApiUnknownException"/> olur — <see cref="GetExpectingJsonAsync{TResp}"/>'daki kural.
    /// Gönderim servisleri yalnız <see cref="LicenseApiException"/>'ı gönderim hatası sayar.</summary>
    private async Task<TResp?> PostJsonExpectingJsonOrUnknownAsync<TReq, TResp>(string path, TReq body, CancellationToken ct)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, path, body, ct);
        if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
        try { return await DeserializeAsync<TResp>(resp, ct); }
        catch (JsonException ex)
        {
            throw new LicenseApiUnknownException((int)resp.StatusCode, $"Gövde çözümlenemedi: {ex.Message}");
        }
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
                try { return (await DeserializeAsync<TResp>(resp, ct))!; }
                catch (JsonException ex)
                {
                    // Gövde JSON değil ya da şemaya uymuyor (kesik yanıt, JSON
                    // content-type'lı HTML hata sayfası...). Çağıran bu dosyadan
                    // LicenseApiException bekliyor; ham JsonException sızmasın.
                    throw new LicenseApiUnknownException((int)resp.StatusCode,
                        $"Gövde çözümlenemedi: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Refresh-aware GET where ONE status code means "kayıt yok" and
    /// becomes <c>null</c> — /me/intake-form'da 404 ("henüz yapılandırılmadı"),
    /// customer-balance/scope'ta 204. Geri kalan başarısız kodlar hata olarak
    /// fırlatılır: "yok" ile "sorulamadı" aynı şey değil ve ikincisini sessizce
    /// yutmak, çağıranı var olmayan bir cevaba göre karar vermeye iter.
    /// Mirrors <see cref="GetExpectingJsonAsync{TResp}"/> for the 401 retry path
    /// so previously-bypass endpoints now honor token rotation.
    ///
    /// <para>R9-F03: <paramref name="nullRequiresTitle"/> verilirse "kayıt yok"
    /// cevabı yalnız problem+json gövdesi o başlığı taşıdığında <c>null</c>
    /// sayılır. Başlıksız/yabancı başlıklı aynı statü kodu hataya dönüşür:
    /// rotayı hiç tanımayan eski sunucunun çıplak 404'ü ile denetimli
    /// "işlem yok" 404'ü aynı koddur ve ayrımı yalnız başlık yapar.</para></summary>
    private async Task<TResp?> GetExpectingJsonOrNullAsync<TResp>(
        string path, CancellationToken ct, HttpStatusCode nullStatus = HttpStatusCode.NotFound,
        string? nullRequiresTitle = null)
        where TResp : class
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
                if (await OnUnauthorized!(ct) is null) continue;
                continue;
            }

            using (resp)
            {
                if (resp.StatusCode == nullStatus)
                {
                    if (nullRequiresTitle is null) return null;

                    string? title = null;
                    try
                    {
                        var problem = await resp.Content.ReadFromJsonAsync<ProblemPayload>(JsonOpts, ct);
                        title = problem?.Title;
                    }
                    catch
                    {
                        // Gövde problem+json değil — başlıksız say.
                    }
                    if (title == nullRequiresTitle) return null;
                    throw new ValidationException(
                        title ?? $"http-{(int)resp.StatusCode}",
                        $"HTTP {(int)resp.StatusCode} '{nullRequiresTitle}' başlığını taşımıyor "
                        + "— 'kayıt yok' cevabı sayılmadı (R9-F03)");
                }
                if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
                return await DeserializeAsync<TResp>(resp, ct);
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

    /// <summary>Gövdesiz GET, <see cref="SendJsonAsync{TReq}"/>'in 401 yenileme yoluyla: yanıtı çağırana bırakır, ağ hatası
    /// ve zaman aşımı <see cref="LicenseApiNetworkException"/>. Fırlatmayan uçlar (ödeme isteği listesi, kargo ayarı)
    /// kullanır.</summary>
    private async Task<HttpResponseMessage> SendGetAsync(string path, CancellationToken ct)
    {
        var canRefresh = OnUnauthorized is not null && !path.StartsWith("/api/v1/auth/");
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(path, ct); }
            catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

            if (resp.StatusCode != HttpStatusCode.Unauthorized || attempt > 0 || !canRefresh)
                return resp;

            // Yenileme başarısızsa da bir kez daha sorulur: çağıran belirli bir 401 görür (SendJsonAsync gibi).
            resp.Dispose();
            await OnUnauthorized!(ct);
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

        throw MapProblem(status, title, detail);
    }

    /// <summary><see cref="ThrowMappedAsync"/>'in (durum, başlık) eşlemesi; gövdeyi kendisi okuyan çağıran (apply) aynı
    /// istisnayı buradan alır.</summary>
    private static LicenseApiException MapProblem(int status, string? title, string? detail)
    {
        // Map by (status, title)
        if (status == 401) return new InvalidCredentialsException(detail ?? "E-posta veya şifre yanlış");
        if (status == 403 && title == "email-not-confirmed") return new EmailNotConfirmedException(detail ?? "E-posta doğrulanmamış");
        if (status == 409 && title == "slot-full") return new SlotFullException(detail ?? "Slot dolu");
        if (status == 409 && title == "license-revoked") return new LicenseRevokedException(detail ?? "Lisans iptal");
        if (status == 409 && title == "license-expired") return new LicenseExpiredException(detail ?? "Lisans süresi dolmuş");
        if (status >= 400 && status < 500)
            return new ValidationException(title ?? $"http-{status}", detail ?? $"HTTP {status}");

        return new LicenseApiUnknownException(status, detail ?? $"HTTP {status}");
    }

    private sealed record ProblemPayload(string? Title, string? Detail, int? Status);

    /// <summary><c>customer-balance/apply</c>'ın 409 gövdesi: <c>scope-has-deduction</c>'ın ek üyeleriyle (§6.6).</summary>
    private sealed record ApplyProblemPayload(
        string? Title, string? Detail, Guid? TransactionId, decimal? AppliedAmount, decimal? ProductTotal);
}
