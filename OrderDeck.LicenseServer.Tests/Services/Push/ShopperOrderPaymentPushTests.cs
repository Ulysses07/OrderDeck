using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Push;

/// <summary>
/// Faz 4c-3 ek: shopper'a order + payment approve/reject push fan-out.
/// Server zaten yayıncıya (Customer) push gönderiyordu; bu testler ek
/// shopper push'unun doğru tetiklendiğini ve filtre kurallarına uyduğunu
/// doğrular.
/// </summary>
public class ShopperOrderPaymentPushTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ShopperOrderPaymentPushTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Push.Clear();
    }

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            licenseId = Guid.NewGuid();
            db.Licenses.Add(new License
            {
                Id = licenseId,
                LicenseKey = "LDK-SHP-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
        }
        return (client, customerId, licenseId);
    }

    private async Task<(Guid shopperId, Guid wpfCustomerId)> SeedShopperWithLinkAsync(
        Guid licenseId,
        bool ordersEnabled = true,
        bool paymentsEnabled = true,
        bool leftLink = false,
        bool deleted = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopperId = Guid.NewGuid();
        var wpfCustomerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new Shopper
        {
            Id = shopperId,
            FullName = "Shopper " + shopperId.ToString("N")[..6],
            Phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999),
            PasswordHash = "ph",
            Address = "Addr",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deleted ? now : null,
            NotificationsEnabledBroadcast = true,
            NotificationsEnabledOrders = ordersEnabled,
            NotificationsEnabledPayments = paymentsEnabled,
        });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "u" + shopperId.ToString("N")[..6],
            WpfCustomerId = wpfCustomerId,
            JoinedAt = now,
            LeftAt = leftLink ? now : null,
        });
        await db.SaveChangesAsync();
        return (shopperId, wpfCustomerId);
    }

    // ─── Order push ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OrderSync_pushes_to_matched_shopper()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (shopperId, wpfCustomerId) = await SeedShopperWithLinkAsync(licenseId);

        var now = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[]
                {
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = wpfCustomerId.ToString("N"),
                          platform = "youtube", username = "u1", displayName = (string?)null,
                          messageText = "x #5", code = (string?)null, price = 250m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.SentToShoppers.Should().HaveCount(1);
        var n = _factory.Push.SentToShoppers[0];
        n.ShopperIds.Should().BeEquivalentTo(new[] { shopperId });
        n.Body.Should().Contain("250");
        n.Data!["type"].Should().Be("order");
        n.Data["licenseId"].Should().Be(licenseId.ToString());
    }

    [Fact]
    public async Task OrderSync_opted_out_shopper_does_not_receive_push()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (_, wpfCustomerId) = await SeedShopperWithLinkAsync(licenseId, ordersEnabled: false);

        var now = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[]
                {
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = wpfCustomerId.ToString("N"),
                          platform = "youtube", username = "u1", displayName = (string?)null,
                          messageText = "x", code = (string?)null, price = 100m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.SentToShoppers.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderSync_unmatched_customer_id_does_not_push_shopper()
    {
        var (client, _, licenseId) = await SetupAsync();
        await SeedShopperWithLinkAsync(licenseId);

        var now = DateTimeOffset.UtcNow;
        // Order CustomerId rastgele bir GUID — hiçbir shopper linkine match etmiyor.
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[]
                {
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = Guid.NewGuid().ToString("N"),
                          platform = "youtube", username = "u1", displayName = (string?)null,
                          messageText = "x", code = (string?)null, price = 100m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.SentToShoppers.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderSync_aggregates_per_shopper()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (shopperId, wpfCustomerId) = await SeedShopperWithLinkAsync(licenseId);

        var now = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[]
                {
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = wpfCustomerId.ToString("N"),
                          platform = "youtube", username = "u1", displayName = (string?)null,
                          messageText = "a", code = (string?)null, price = 100m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = wpfCustomerId.ToString("N"),
                          platform = "youtube", username = "u1", displayName = (string?)null,
                          messageText = "b", code = (string?)null, price = 200m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.SentToShoppers.Should().HaveCount(1);
        var n = _factory.Push.SentToShoppers[0];
        n.ShopperIds.Should().BeEquivalentTo(new[] { shopperId });
        n.Body.Should().Contain("2").And.Contain("300");
        n.Data!["count"].Should().Be("2");
    }

    // ─── Payment approve/reject push ─────────────────────────────────────────

    [Fact]
    public async Task PaymentApprove_pushes_shopper()
    {
        var (client, customerId, licenseId) = await SetupAsync();
        var (shopperId, _) = await SeedShopperWithLinkAsync(licenseId);
        var paymentId = await SeedPendingPaymentAsync(licenseId, shopperId, amount: 480m);
        _factory.Push.Clear();

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _factory.Push.SentToShoppers.Should().HaveCount(1);
        var n = _factory.Push.SentToShoppers[0];
        n.ShopperIds.Should().BeEquivalentTo(new[] { shopperId });
        n.Body.Should().Contain("480").And.Contain("onayland");
        n.Data!["type"].Should().Be("payment-approved");
    }

    [Fact]
    public async Task PaymentReject_pushes_shopper_with_reason()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (shopperId, _) = await SeedShopperWithLinkAsync(licenseId);
        var paymentId = await SeedPendingPaymentAsync(licenseId, shopperId, amount: 100m);
        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/payments/{paymentId}/reject",
            new { reason = "Tutar uyuşmuyor" });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _factory.Push.SentToShoppers.Should().HaveCount(1);
        var n = _factory.Push.SentToShoppers[0];
        n.ShopperIds.Should().BeEquivalentTo(new[] { shopperId });
        n.Body.Should().Contain("reddedildi").And.Contain("Tutar uyu");
        n.Data!["type"].Should().Be("payment-rejected");
    }

    [Fact]
    public async Task PaymentApprove_with_null_shopper_does_not_push()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = await SeedPendingPaymentAsync(licenseId, shopperId: null, amount: 50m);
        _factory.Push.Clear();

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _factory.Push.SentToShoppers.Should().BeEmpty();
    }

    [Fact]
    public async Task PaymentApprove_opted_out_shopper_does_not_push()
    {
        var (client, _, licenseId) = await SetupAsync();
        var (shopperId, _) = await SeedShopperWithLinkAsync(licenseId, paymentsEnabled: false);
        var paymentId = await SeedPendingPaymentAsync(licenseId, shopperId, amount: 50m);
        _factory.Push.Clear();

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _factory.Push.SentToShoppers.Should().BeEmpty();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> SeedPendingPaymentAsync(
        Guid licenseId, Guid? shopperId, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var paymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Payments.Add(new Payment
        {
            Id = paymentId,
            LicenseId = licenseId,
            ShopperId = shopperId,
            PayerName = "Test Payer",
            Amount = amount,
            PaidAt = now,
            ReferansNo = "REF-" + Guid.NewGuid().ToString("N")[..8],
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return paymentId;
    }
}
