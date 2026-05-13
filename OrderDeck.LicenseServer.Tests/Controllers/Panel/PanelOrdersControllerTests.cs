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

/// <summary>
/// Mobile Panel "Siparişler" ekranı integration tests.
/// </summary>
public class PanelOrdersControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelOrdersControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record SessionSummaryDto(Guid Id, string? Title,
        DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Platforms,
        int OrderCount, decimal TotalAmount);

    private sealed record OrderDto(Guid Id, Guid? SessionId, string CustomerId,
        string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price,
        DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason,
        bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup);

    private async Task<(HttpClient client, Guid licenseId, Guid sessionId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        Guid sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PANEL-ORD-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = now,
                ExpiresAt = now.AddDays(30)
            };
            db.Licenses.Add(license);

            db.StreamSessions.Add(new StreamSession
            {
                Id = sessionId, LicenseId = license.Id,
                Title = "Test Yayın", StartedAt = now.AddHours(-3),
                EndedAt = now.AddHours(-1), Platforms = "instagram",
                UpdatedAt = now
            });

            // 3 order, biri cancelled, biri shipping fee
            db.Orders.AddRange(
                new Order { Id = Guid.NewGuid(), LicenseId = license.Id,
                    SessionId = sessionId, CustomerId = "c1",
                    Platform = "instagram", Username = "@a",
                    MessageText = "ürün 1", Price = 100m,
                    AddedAt = now.AddHours(-2), UpdatedAt = now },
                new Order { Id = Guid.NewGuid(), LicenseId = license.Id,
                    SessionId = sessionId, CustomerId = "c2",
                    Platform = "instagram", Username = "@b",
                    MessageText = "ürün 2", Price = 200m,
                    AddedAt = now.AddHours(-2).AddMinutes(1), UpdatedAt = now },
                new Order { Id = Guid.NewGuid(), LicenseId = license.Id,
                    SessionId = sessionId, CustomerId = "c3",
                    Platform = "instagram", Username = "@c",
                    MessageText = "ürün 3 iptal", Price = 50m,
                    AddedAt = now.AddHours(-2).AddMinutes(2),
                    CancelledAt = now.AddHours(-1), CancelReason = "iade",
                    UpdatedAt = now },
                new Order { Id = Guid.NewGuid(), LicenseId = license.Id,
                    SessionId = sessionId, CustomerId = "c1",
                    Platform = "instagram", Username = "@a",
                    MessageText = "kargo", Price = 150m,
                    AddedAt = now.AddHours(-2).AddMinutes(3),
                    IsShippingFee = true, UpdatedAt = now });

            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, licenseId, sessionId);
    }

    [Fact]
    public async Task ListSessions_returns_summary_excluding_cancelled_and_shipping_fee()
    {
        var (client, _, sessionId) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/sessions");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SessionSummaryDto>>();
        body.Should().HaveCount(1);
        var s = body![0];
        s.Id.Should().Be(sessionId);
        // 4 order: 1 cancelled (50), 1 shipping fee (150), 2 valid (100, 200)
        s.OrderCount.Should().Be(2);
        s.TotalAmount.Should().Be(300m);
    }

    [Fact]
    public async Task ListOrdersBySession_returns_all_including_cancelled_and_shipping()
    {
        // Client tarafı badge ile filtreleyebilir; server hepsini döner audit/transparency için.
        var (client, _, sessionId) = await SeedAsync();
        var resp = await client.GetAsync($"/api/panel/sessions/{sessionId}/orders");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<OrderDto>>();
        body.Should().HaveCount(4);
        body!.Should().Contain(o => o.CancelledAt != null);
        body.Should().Contain(o => o.IsShippingFee);
    }

    [Fact]
    public async Task ListOrdersBySession_returns_404_for_unknown_session()
    {
        var (client, _, _) = await SeedAsync();
        var resp = await client.GetAsync($"/api/panel/sessions/{Guid.NewGuid()}/orders");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListSessions_tenant_isolation_strangers_hidden()
    {
        var (client, _, _) = await SeedAsync();

        // Başka tenant için session + order ekle
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var stranger = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"stranger-{Guid.NewGuid():N}@example.com",
                EmailConfirmedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var foreignLicense = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "FOREIGN-" + Guid.NewGuid().ToString("N"),
                CustomerId = stranger.Id, SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Customers.Add(stranger);
            db.Licenses.Add(foreignLicense);
            db.StreamSessions.Add(new StreamSession
            {
                Id = Guid.NewGuid(), LicenseId = foreignLicense.Id,
                Title = "Stranger yayını", StartedAt = DateTimeOffset.UtcNow,
                Platforms = "tiktok", UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/sessions");
        var body = await resp.Content.ReadFromJsonAsync<List<SessionSummaryDto>>();
        body.Should().HaveCount(1); // sadece kendi yayını
        body![0].Title.Should().Be("Test Yayın");
    }
}
