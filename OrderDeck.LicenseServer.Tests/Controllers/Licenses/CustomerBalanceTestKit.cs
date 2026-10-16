using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>customer-balance</c> uçlarının kip kapısı ve kapsam kuralı testlerinin ortak kurulumu (InMemory ve gerçek SQL
/// Server) — spec 2026-10-13 §6.5-3, §6.6. Lisans ve istemci <see cref="SalesCommandTestKit"/>'ten; müşteri projeksiyonu
/// ve bakiyesi doğrudan veritabanına yazılır. Kişisel alan yoktur: Id'ler üretilir.
/// </summary>
public sealed class CustomerBalanceTestKit
{
    /// <summary>Ws (alt sürümü karşılayan) istemci.</summary>
    public const string WsUserAgent = SalesKit.WpfUserAgent;

    /// <summary>Sahadaki eski sürüm (≤0.9.10).</summary>
    public const string OldUserAgent = "OrderDeck-WPF/0.9.10";

    public const string Cumulative = "cumulative";

    private const string KindPurchaseDeduction = "purchase-deduction";

    private CustomerBalanceTestKit(SalesKit sales, Guid wpfCustomerId)
    {
        Sales = sales;
        WpfCustomerId = wpfCustomerId;
    }

    public SalesKit Sales { get; }

    public Guid LicenseId => Sales.LicenseId;

    /// <summary>Bakiyesi olan müşterinin (asıl kayıt) Id'si.</summary>
    public Guid WpfCustomerId { get; }

    private string BaseUrl => $"/api/v1/licenses/{LicenseId}/customer-balance";

    /// <summary>Verilen kipte bir lisans ve <paramref name="balance"/> bakiyeli bir müşteri (bakiye bir iade satırından,
    /// defter değişmezi gibi).</summary>
    public static async Task<CustomerBalanceTestKit> CreateAsync(ApiFactory factory, SalesSyncMode mode, decimal balance)
    {
        var sales = await SalesKit.CreateAsync(factory, mode);
        var customerId = Guid.NewGuid();
        await sales.SeedProjectionAsync(customerId);
        await sales.WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = sales.LicenseId, WpfCustomerId = customerId,
                Balance = balance, UpdatedAt = now,
            });
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = sales.LicenseId, WpfCustomerId = customerId,
                Amount = balance, Kind = "refund-full", OriginalAmount = balance,
                CreatedByCustomerId = sales.CustomerId, CreatedAt = now,
            });
            return Task.CompletedTask;
        });
        return new CustomerBalanceTestKit(sales, customerId);
    }

    /// <summary>Müşterinin bir kopyası (A5b): birleştirilmiş, asıl kayda işaret eden projeksiyon.</summary>
    public async Task<Guid> SeedAliasAsync()
    {
        var alias = Guid.NewGuid();
        await Sales.SeedProjectionAsync(alias, mergedInto: WpfCustomerId);
        return alias;
    }

    // ── İstek ─────────────────────────────────────────────────────────────────

    /// <summary><c>POST apply</c>; müşteri verilmezse <see cref="WpfCustomerId"/>.</summary>
    public Task<HttpResponseMessage> ApplyAsync(
        decimal amount, decimal productTotal, Guid? key, string? saleScope,
        string? userAgent = WsUserAgent, Guid? customerId = null) =>
        Sales.SendToAsync($"{BaseUrl}/apply",
            new
            {
                wpfCustomerId = customerId ?? WpfCustomerId,
                amount,
                productTotal,
                idempotencyKey = key,
                saleScope,
            },
            userAgent);

    /// <summary><c>GET preview</c>.</summary>
    public Task<HttpResponseMessage> PreviewAsync(string? userAgent = WsUserAgent) =>
        SendAsync(HttpMethod.Get, $"{BaseUrl}/preview?wpfCustomerId={WpfCustomerId}", userAgent);

    /// <summary><c>POST transactions/{id}/reverse</c>.</summary>
    public Task<HttpResponseMessage> ReverseAsync(Guid transactionId, string? userAgent = WsUserAgent) =>
        SendAsync(HttpMethod.Post, $"{BaseUrl}/transactions/{transactionId}/reverse", userAgent);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? userAgent)
    {
        using var request = new HttpRequestMessage(method, url);
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return await Sales.Client.SendAsync(request);
    }

    /// <summary><c>apply</c>'ın 200 yanıtı.</summary>
    public sealed record ApplyResponse(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    public static async Task<ApplyResponse> ReadApplyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApplyResponse>())!;

    // ── Okuma ─────────────────────────────────────────────────────────────────

    /// <summary>Asıl kaydın bakiyesi.</summary>
    public Task<decimal> BalanceAsync() =>
        Sales.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == LicenseId && b.WpfCustomerId == WpfCustomerId)
            .Select(b => b.Balance)
            .SingleAsync());

    /// <summary>Lisansın bütün düşüm satırları.</summary>
    public Task<List<CustomerBalanceTransaction>> DeductionsAsync() =>
        Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == LicenseId && t.Kind == KindPurchaseDeduction)
            .ToListAsync());

    /// <summary>Lisansın geri alınmamış düşümlerinin Id'leri.</summary>
    public Task<List<Guid>> UnreversedDeductionIdsAsync() =>
        Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == LicenseId
                && t.Kind == KindPurchaseDeduction
                && !db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id))
            .Select(t => t.Id)
            .ToListAsync());
}
