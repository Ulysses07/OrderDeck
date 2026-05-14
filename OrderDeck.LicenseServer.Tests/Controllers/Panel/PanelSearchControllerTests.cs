using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelSearchControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PanelSearchControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record SearchResultDto(
        List<CustomerHitDto> Customers,
        List<OrderHitDto> Orders,
        List<PaymentHitDto> Payments);

    private sealed record CustomerHitDto(
        string CustomerId, string Username, string? DisplayName, string Platform);

    private sealed record OrderHitDto(
        Guid Id, Guid? SessionId, string? Code, string MessageText,
        string Username, string? DisplayName, decimal Price, DateTimeOffset AddedAt);

    private sealed record PaymentHitDto(
        Guid Id, string ReferansNo, string PayerName, decimal Amount,
        string Status, DateTimeOffset CreatedAt);

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        var now = DateTimeOffset.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-SEARCH-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30)
        };
        db.Licenses.Add(license);
        licenseId = license.Id;

        db.Orders.AddRange(
            new Order
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = "cust_alpha",
                Platform = "youtube", Username = "alibaba", DisplayName = "Ali Baba",
                MessageText = "Yeşil kazak", Code = "AB-001", Price = 100m,
                AddedAt = now.AddMinutes(-5), UpdatedAt = now
            },
            new Order
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = "cust_beta",
                Platform = "instagram", Username = "beyza", DisplayName = "Beyza",
                MessageText = "Mavi kazak", Code = "BZ-002", Price = 150m,
                AddedAt = now.AddMinutes(-3), UpdatedAt = now
            });

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            PayerName = "Ali Baba", Amount = 100m, PaidAt = now.AddMinutes(-10),
            ReferansNo = "TXN-12345-ABC", Status = PaymentStatus.Pending,
            CreatedAt = now, UpdatedAt = now
        });

        await db.SaveChangesAsync();
        return (client, licenseId);
    }

    [Fact]
    public async Task Search_finds_customer_by_username()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/search?q=ali");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResultDto>();
        body!.Customers.Should().ContainSingle(c => c.Username == "alibaba");
    }

    [Fact]
    public async Task Search_finds_order_by_code()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/search?q=BZ-002");
        var body = await resp.Content.ReadFromJsonAsync<SearchResultDto>();
        body!.Orders.Should().ContainSingle(o => o.Code == "BZ-002");
    }

    [Fact]
    public async Task Search_finds_payment_by_referansNo()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/search?q=12345");
        var body = await resp.Content.ReadFromJsonAsync<SearchResultDto>();
        body!.Payments.Should().ContainSingle(p => p.ReferansNo.Contains("12345"));
    }

    [Fact]
    public async Task Short_query_returns_empty()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/search?q=a");
        var body = await resp.Content.ReadFromJsonAsync<SearchResultDto>();
        body!.Customers.Should().BeEmpty();
        body.Orders.Should().BeEmpty();
        body.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_query_returns_empty()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/search?q=");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SearchResultDto>();
        body!.Customers.Should().BeEmpty();
    }
}
