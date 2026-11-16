using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.App.Services.Sync;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Bakiye uçlarını taklit eden sade sunucu — ödeme akışını ViewModel düzeyinde süren testler için
/// (kart, rapor, ikisi aynı servis örneğinde). Gerçek sunucu paritesi: işlem kimliği = istemcinin
/// idempotency anahtarı; uygulanan tutar istenenle bakiyenin küçüğü; kapsam ucu iz yoksa 204.
/// Cloud API gönderimi taklit edilmez (testler wa.me yolunu kullanır).
/// </summary>
public sealed class BalanceApiStub : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly Guid _licenseId = Guid.NewGuid();

    /// <summary>Lisans anahtarı her örnekte üretilir — testte sabit kimlik bilgisi dizgisi yok.</summary>
    public string LicenseKey { get; } = $"LDK-{Guid.NewGuid():N}";

    /// <summary>Önizlemenin döneceği bakiye.</summary>
    public decimal PreviewBalance { get; set; }

    /// <summary>Düşüm uçlarına giden apply'lar: (anahtar, WpfCustomerId, istenen tutar).</summary>
    public List<(Guid Key, Guid WpfCustomerId, decimal Amount)> Applies { get; } = new();

    /// <summary>Geri alınan işlem kimlikleri.</summary>
    public List<Guid> ReverseCalls { get; } = new();

    /// <summary>Panel bakiye okumasının (<c>GET /api/panel/customers/{id}/balance</c>) <c>members[]</c>'i, verilen
    /// sırayla: (WpfCustomerId, satır bakiyesi, silinmiş mi) — Ayır onayı (ortak bakiye spec 2026-10-11 §6.1-e) ve
    /// müşteri kartı (§6.1-f). Sunucu paritesi: <c>PanelCustomerBalanceController.MemberDto</c>.</summary>
    public List<(Guid WpfCustomerId, decimal Balance, bool Purged)> PanelMembers { get; } = new();

    /// <summary>Panel bakiye okumasının <c>balance.balance</c>'ı.</summary>
    public decimal PanelBalance { get; set; }

    /// <summary>true → panel bakiye okuması 404 problem döner (sahibin projeksiyonu sunucuda yok —
    /// <c>PanelCustomerBalanceController.cs:87</c>).</summary>
    public bool PanelFails { get; set; }

    /// <summary>Panel bakiye okumasında istenen Id'ler (yolun segmenti), sırayla.</summary>
    public List<Guid> PanelBalanceRequests { get; } = new();

    public LicenseApiClient Client() =>
        new(new HttpClient(this) { BaseAddress = new Uri("https://stub") }, new LicenseTokenStore());

    public ICurrentLicenseProvider License() => new Provider(LicenseKey);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/me/licenses")
            return Json($$"""
                [{"licenseKey":"{{LicenseKey}}","skuCode":"STD",
                  "expiresAt":"2030-01-01T00:00:00+00:00","revokedAt":null,"id":"{{_licenseId}}"}]
                """);

        if (path.EndsWith("/customer-balance/scope", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        if (path.EndsWith("/customer-balance/preview", StringComparison.Ordinal))
            return Json($$"""
                {"wpfCustomerId":"{{Guid.Empty}}","balance":{{Dec(PreviewBalance)}},
                 "updatedAt":"2030-01-01T00:00:00+00:00"}
                """);

        if (path.EndsWith("/customer-balance/apply", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var key = doc.RootElement.GetProperty("idempotencyKey").GetGuid();
            var wpf = doc.RootElement.GetProperty("wpfCustomerId").GetGuid();
            var amount = doc.RootElement.GetProperty("amount").GetDecimal();
            lock (_sync) Applies.Add((key, wpf, amount));
            return Json($$"""
                {"transactionId":"{{key}}","appliedAmount":{{Dec(Math.Min(PreviewBalance, amount))}},"remainingBalance":0}
                """);
        }

        if (path.Contains("/customer-balance/transactions/", StringComparison.Ordinal))
        {
            var segments = path.Split('/');
            if (path.EndsWith("/reverse", StringComparison.Ordinal))
            {
                lock (_sync) ReverseCalls.Add(Guid.Parse(segments[^2]));
                return Json("{}");
            }
            var tx = Guid.Parse(segments[^1]);
            lock (_sync)
            {
                var applied = Applies.Find(a => a.Key == tx);
                return Json($$"""
                    {"transactionId":"{{tx}}","appliedAmount":{{Dec(Math.Min(PreviewBalance, applied.Amount))}},
                     "reversed":{{(ReverseCalls.Contains(tx) ? "true" : "false")}}}
                    """);
            }
        }

        if (request.Method == HttpMethod.Get
            && path.StartsWith("/api/panel/customers/", StringComparison.Ordinal)
            && path.EndsWith("/balance", StringComparison.Ordinal))
        {
            var id = Guid.Parse(path.Split('/')[^2]);
            List<(Guid WpfCustomerId, decimal Balance, bool Purged)> members;
            lock (_sync)
            {
                PanelBalanceRequests.Add(id);
                members = new(PanelMembers);
            }
            if (PanelFails)
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"title":"Not Found","status":404}""", Encoding.UTF8,
                        "application/problem+json"),
                };
            var items = string.Join(",", members.Select(m =>
                $$"""{"wpfCustomerId":"{{m.WpfCustomerId}}","balance":{{Dec(m.Balance)}},"purged":{{(m.Purged ? "true" : "false")}}}"""));
            return Json($$"""
                {"balance":{"wpfCustomerId":"{{id}}","licenseId":"{{_licenseId}}","balance":{{Dec(PanelBalance)}},
                  "updatedAt":"2030-01-01T00:00:00+00:00"},
                 "transactions":[],"members":[{{items}}]}
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Provider(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => key;
    }
}
