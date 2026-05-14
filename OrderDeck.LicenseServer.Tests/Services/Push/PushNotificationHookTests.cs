using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Push;

/// <summary>
/// Sync controller'larında push hook'larının doğru tetiklendiğini doğrular.
/// Sender olarak <see cref="RecordingNotificationSender"/> kullanılır
/// (ApiFactory tarafından register edilir).
/// </summary>
public class PushNotificationHookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PushNotificationHookTests(ApiFactory factory)
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
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PUSH-" + Guid.NewGuid().ToString("N"),
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
        return (client, customerId, licenseId);
    }

    // ─── Payment sync ─────────────────────────────────────────────────

    [Fact]
    public async Task PaymentSync_new_pending_payment_triggers_single_notification()
    {
        var (client, customerId, licenseId) = await SetupAsync();
        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = Guid.NewGuid(), payerName = "Ali Veli", amount = 250m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-PUSH-1",
                          pdfHash = (string?)null }
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.Sent.Should().HaveCount(1);
        var n = _factory.Push.Sent[0];
        n.CustomerId.Should().Be(customerId);
        n.Title.Should().Be("Yeni dekont");
        n.Body.Should().Contain("Ali Veli").And.Contain("250");
        n.Data!["type"].Should().Be("payment");
        n.Data["count"].Should().Be("1");
    }

    [Fact]
    public async Task PaymentSync_multiple_new_payments_send_one_aggregated_notification()
    {
        var (client, _, licenseId) = await SetupAsync();
        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = Guid.NewGuid(), payerName = "A", amount = 100m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-AGG-1", pdfHash = (string?)null },
                    new { id = Guid.NewGuid(), payerName = "B", amount = 200m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-AGG-2", pdfHash = (string?)null },
                    new { id = Guid.NewGuid(), payerName = "C", amount = 300m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-AGG-3", pdfHash = (string?)null }
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.Sent.Should().HaveCount(1, "batch is aggregated to a single push");
        _factory.Push.Sent[0].Body.Should().Contain("3").And.Contain("600");
        _factory.Push.Sent[0].Data!["count"].Should().Be("3");
    }

    [Fact]
    public async Task PaymentSync_repeat_push_does_not_re_notify()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();
        var payload = new
        {
            payments = new[]
            {
                new { id = paymentId, payerName = "Idempot", amount = 50m,
                      paidAt = DateTimeOffset.UtcNow, referansNo = "REF-IDEM", pdfHash = (string?)null }
            }
        };

        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/payments/sync", payload);
        _factory.Push.Clear();
        await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/payments/sync", payload);

        _factory.Push.Sent.Should().BeEmpty("upsert of existing payment is not a new dekont");
    }

    // ─── Session sync ─────────────────────────────────────────────────

    [Fact]
    public async Task SessionSync_new_live_session_triggers_notification()
    {
        var (client, customerId, licenseId) = await SetupAsync();
        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sessions/sync",
            new
            {
                sessions = new[]
                {
                    new { id = Guid.NewGuid(), title = "Akşam yayını",
                          startedAt = DateTimeOffset.UtcNow,
                          endedAt = (DateTimeOffset?)null,
                          platforms = "youtube",
                          notes = (string?)null }
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.Sent.Should().HaveCount(1);
        var n = _factory.Push.Sent[0];
        n.CustomerId.Should().Be(customerId);
        n.Title.Should().Be("Yayın başladı");
        n.Body.Should().Be("Akşam yayını");
        n.Data!["type"].Should().Be("session-started");
    }

    [Fact]
    public async Task SessionSync_already_ended_session_does_not_notify()
    {
        var (client, _, licenseId) = await SetupAsync();
        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sessions/sync",
            new
            {
                sessions = new[]
                {
                    new { id = Guid.NewGuid(), title = "Geçmiş yayın",
                          startedAt = DateTimeOffset.UtcNow.AddHours(-3),
                          endedAt = (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-1),
                          platforms = "youtube",
                          notes = (string?)null }
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.Sent.Should().BeEmpty("ended sessions are historical sync, not live");
    }

    // ─── Order sync ───────────────────────────────────────────────────

    [Fact]
    public async Task OrderSync_new_printed_orders_trigger_single_aggregated_notification()
    {
        var (client, customerId, licenseId) = await SetupAsync();
        _factory.Push.Clear();

        var now = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[]
                {
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = "cust1", platform = "youtube",
                          username = "u1", displayName = (string?)null,
                          messageText = "x #5", code = (string?)null, price = 100m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = "cust2", platform = "youtube",
                          username = "u2", displayName = (string?)null,
                          messageText = "y #5", code = (string?)null, price = 200m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false }
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.Sent.Should().HaveCount(1);
        var n = _factory.Push.Sent[0];
        n.CustomerId.Should().Be(customerId);
        n.Title.Should().Be("Yeni sipariş");
        n.Body.Should().Contain("2").And.Contain("300");
        n.Data!["count"].Should().Be("2");
    }

    [Fact]
    public async Task OrderSync_unprinted_or_cancelled_orders_do_not_notify()
    {
        var (client, _, licenseId) = await SetupAsync();
        _factory.Push.Clear();

        var now = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[]
                {
                    // PrintedAt null → henüz basılmadı
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = "cust1", platform = "youtube",
                          username = "u1", displayName = (string?)null,
                          messageText = "x #5", code = (string?)null, price = 100m,
                          addedAt = now, printedAt = (DateTimeOffset?)null,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                    // Cancelled
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = "cust2", platform = "youtube",
                          username = "u2", displayName = (string?)null,
                          messageText = "y #5", code = (string?)null, price = 200m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)now, cancelReason = "iade",
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = false },
                    // Shipping fee
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = "cust3", platform = "youtube",
                          username = "u3", displayName = (string?)null,
                          messageText = "kargo", code = (string?)null, price = 30m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = true, isBackupPromoted = false,
                          isTentativeBackup = false },
                    // Tentative backup
                    new { id = Guid.NewGuid(), sessionId = (Guid?)null,
                          customerId = "cust4", platform = "youtube",
                          username = "u4", displayName = (string?)null,
                          messageText = "y", code = (string?)null, price = 50m,
                          addedAt = now, printedAt = (DateTimeOffset?)now,
                          cancelledAt = (DateTimeOffset?)null, cancelReason = (string?)null,
                          isShippingFee = false, isBackupPromoted = false,
                          isTentativeBackup = true }
                }
            });
        resp.EnsureSuccessStatusCode();

        _factory.Push.Sent.Should().BeEmpty(
            "unprinted, cancelled, shipping-fee, tentative orders shouldn't notify");
    }
}
