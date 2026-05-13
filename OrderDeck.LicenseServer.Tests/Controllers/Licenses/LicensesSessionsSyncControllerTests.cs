using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Sipariş sync (2026-05-13): WPF StreamSession + Order outbox sync endpoint
/// integration tests. Pattern: <see cref="LicensesPaymentsSyncControllerTests"/>.
/// </summary>
public class LicensesSessionsSyncControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesSessionsSyncControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-ORD-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, licenseId);
    }

    private sealed record SyncedSessionDto(Guid Id, string? Title,
        DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
        string Platforms, string? Notes, DateTimeOffset UpdatedAt);

    private sealed record SyncedOrderDto(Guid Id, Guid? SessionId, string CustomerId,
        string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price,
        DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason,
        bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup,
        DateTimeOffset UpdatedAt);

    [Fact]
    public async Task SyncSessions_inserts_new_session()
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sessions/sync",
            new { sessions = new[] {
                new { id = sessionId, title = "Test yayın",
                      startedAt = DateTimeOffset.UtcNow.AddHours(-2),
                      endedAt = (DateTimeOffset?)null,
                      platforms = "instagram,youtube", notes = (string?)null }
            }});

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedSessionDto>>();
        body.Should().HaveCount(1);
        body![0].Id.Should().Be(sessionId);
        body[0].Title.Should().Be("Test yayın");
        body[0].Platforms.Should().Be("instagram,youtube");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.StreamSessions.FirstAsync(s => s.Id == sessionId);
        stored.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task SyncSessions_updates_endedAt_on_second_push()
    {
        var (client, licenseId) = await SetupAsync();
        var sid = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-3);

        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync",
            new { sessions = new[] { new { id = sid, title = "Yayın 1", startedAt,
                endedAt = (DateTimeOffset?)null, platforms = "instagram", notes = (string?)null } }});

        var endedAt = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync",
            new { sessions = new[] { new { id = sid, title = "Yayın 1", startedAt,
                endedAt = (DateTimeOffset?)endedAt, platforms = "instagram", notes = (string?)null } }});

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.StreamSessions.FirstAsync(s => s.Id == sid);
        stored.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncOrders_inserts_order_with_session_reference()
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Önce session push
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync",
            new { sessions = new[] { new { id = sessionId, title = "Test",
                startedAt = DateTimeOffset.UtcNow.AddHours(-1),
                endedAt = (DateTimeOffset?)null,
                platforms = "instagram", notes = (string?)null } }});

        // Sonra order push
        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { new {
                id = orderId, sessionId = (Guid?)sessionId,
                customerId = "c1hex", platform = "instagram",
                username = "@alice", displayName = "Alice",
                messageText = "ürün aldım", code = "ABC123",
                price = 250.50m,
                addedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                printedAt = (DateTimeOffset?)null,
                cancelledAt = (DateTimeOffset?)null,
                cancelReason = (string?)null,
                isShippingFee = false,
                isBackupPromoted = false,
                isTentativeBackup = false } }});

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedOrderDto>>();
        body.Should().HaveCount(1);
        body![0].Price.Should().Be(250.50m);
        body[0].SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task SyncOrders_rejects_when_license_not_owned()
    {
        var (client, _) = await SetupAsync();
        var foreignLicense = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{foreignLicense}/orders/sync",
            new { orders = new[] { new {
                id = Guid.NewGuid(), sessionId = (Guid?)null,
                customerId = "x", platform = "instagram",
                username = "@x", displayName = (string?)null,
                messageText = "msg", code = (string?)null, price = 100m,
                addedAt = DateTimeOffset.UtcNow,
                printedAt = (DateTimeOffset?)null,
                cancelledAt = (DateTimeOffset?)null,
                cancelReason = (string?)null,
                isShippingFee = false, isBackupPromoted = false,
                isTentativeBackup = false } }});

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
