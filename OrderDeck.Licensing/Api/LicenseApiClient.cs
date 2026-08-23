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
        => GetExpectingJsonOrNullOn404Async<IntakeFormConfigDto>("/api/v1/me/intake-form", ct);

    public Task<IntakeFormConfigDto> UpsertIntakeFormAsync(IntakeFormUpsertRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<IntakeFormUpsertRequest, IntakeFormConfigDto>(
            "/api/v1/me/intake-form", req, ct, methodOverride: HttpMethod.Put);

    public async Task<List<IntakeFormSubmissionDto>> GetFormSubmissionsAsync(
        DateTimeOffset? since, int limit = 50, CancellationToken ct = default)
    {
        var qs = since is null
            ? $"?limit={limit}"
            : $"?since={Uri.EscapeDataString(since.Value.ToString("O"))}&limit={limit}";
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
    /// tarafında AppSettings.LastPaymentReverseSync(+Id)'de.</summary>
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
    /// (mobile mutation yapmıyor). Max 200 item/batch.</summary>
    public Task<List<SyncedShipmentDto>> SyncShipmentsAsync(
        Guid licenseId, SyncShipmentsRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SyncShipmentsRequest, List<SyncedShipmentDto>>(
            $"/api/v1/licenses/{licenseId}/shipments/sync", req, ct);

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

    public Task<List<SyncedSessionDto>> SyncSessionsAsync(
        Guid licenseId, SyncSessionsRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SyncSessionsRequest, List<SyncedSessionDto>>(
            $"/api/v1/licenses/{licenseId}/sessions/sync", req, ct);

    public Task<List<SyncedOrderDto>> SyncOrdersAsync(
        Guid licenseId, SyncOrdersRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SyncOrdersRequest, List<SyncedOrderDto>>(
            $"/api/v1/licenses/{licenseId}/orders/sync", req, ct);

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
    /// synced count and retroactive shopper-code matches.</summary>
    public Task<WpfCustomerSyncResponse> SyncWpfCustomersAsync(
        Guid licenseId, IReadOnlyList<WpfCustomerSyncItem> customers, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<WpfCustomerSyncRequest, WpfCustomerSyncResponse>(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new WpfCustomerSyncRequest(customers), ct);

    // ─── WPF customers pull (Faz 0c-3) ────────────────────────────────────

    /// <summary>Pulls server-created WpfCustomerProjection rows (auto-created on
    /// shopper register/join). İmleç bileşik — (<paramref name="since"/>,
    /// <paramref name="sinceId"/>); gerekçe <see cref="GetPaymentsSinceAsync"/>'de.
    /// WPF sayfanın son satırından imleci ilerletir.</summary>
    public async Task<List<WpfCustomerPullItem>> GetWpfCustomersSinceAsync(
        Guid licenseId, DateTimeOffset since, Guid sinceId,
        int take = 100, CancellationToken ct = default)
    {
        var qs = $"?since={Uri.EscapeDataString(since.ToString("O"))}&sinceId={sinceId:D}&take={take}";
        return await GetExpectingJsonAsync<List<WpfCustomerPullItem>>(
            $"/api/v1/licenses/{licenseId}/wpf-customers/since{qs}", ct) ?? new();
    }

    // ─── WPF katalog replikası (Stok Faz 1b) ──────────────────────────────

    // Bu iki metot, dosyadaki diğer liste uçlarından (örn.
    // GetWpfCustomersSinceAsync) BİLEREK ayrılıyor: onlarda `?? new()` ile boş
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

    /// <summary>WPF "Ödeme iste" sonrası bakiye düşüşünü commit eder.
    /// Server min(Amount, balance, productTotal) ile capped uygular.</summary>
    public async Task<CustomerBalanceApplyResponse> ApplyBalanceAsync(
        Guid licenseId, CustomerBalanceApplyRequest req, CancellationToken ct = default)
    {
        return await PostJsonExpectingJsonAsync<CustomerBalanceApplyRequest, CustomerBalanceApplyResponse>(
            $"/api/v1/licenses/{licenseId}/customer-balance/apply", req, ct);
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

    /// <summary>Yayıncının SMS kredi bakiyesi (salt-okunur; yükleme admin tarafında).</summary>
    public Task<SmsBalanceResponse> GetSmsBalanceAsync(Guid licenseId, CancellationToken ct = default)
        => GetExpectingJsonAsync<SmsBalanceResponse>($"/api/v1/licenses/{licenseId}/sms/balance", ct);

    /// <summary>Toplu SMS önizleme — alıcı sayısı, segment ve gerekli kredi.</summary>
    public Task<SmsPreviewResponse> PreviewSmsCampaignAsync(
        Guid licenseId, SmsPreviewRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<SmsPreviewRequest, SmsPreviewResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", req, ct);

    /// <summary>Kampanya oluşturur: kredi rezerve edilir, gönderim arka planda yapılır.</summary>
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

    /// <summary>Refresh-aware GET that maps 404 → null. Used by endpoints like
    /// /me/intake-form where "not configured yet" is a legitimate state, not
    /// an error. Mirrors <see cref="GetExpectingJsonAsync{TResp}"/> for the
    /// 401 retry path so previously-bypass endpoints now honor token rotation.</summary>
    private async Task<TResp?> GetExpectingJsonOrNullOn404Async<TResp>(string path, CancellationToken ct)
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
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
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
