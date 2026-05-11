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

public class LicensesPaymentsSyncControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesPaymentsSyncControllerTests(ApiFactory factory) => _factory = factory;

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
                LicenseKey = "LDK-SYNC-" + Guid.NewGuid().ToString("N"),
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

    private sealed record SyncedPaymentDto(
        Guid Id, string Status, DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt, string? RejectReason, DateTimeOffset UpdatedAt);

    [Fact]
    public async Task Sync_inserts_new_payments_and_returns_echoed_status()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new
                    {
                        id = paymentId,
                        payerName = "Ahmet Yıldız",
                        amount = 250.75m,
                        paidAt = DateTimeOffset.UtcNow.AddHours(-1),
                        referansNo = "REF-001",
                        pdfHash = (string?)null
                    }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body.Should().HaveCount(1);
        body![0].Id.Should().Be(paymentId);
        body[0].Status.Should().Be("pending");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Payments.FirstAsync(p => p.Id == paymentId);
        stored.PayerName.Should().Be("Ahmet Yıldız");
        stored.Amount.Should().Be(250.75m);
        stored.LicenseId.Should().Be(licenseId);
    }

    [Fact]
    public async Task Sync_is_idempotent_by_payment_id()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();

        async Task<HttpResponseMessage> Push() => await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "Foo", amount = 100m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-IDEM", pdfHash = (string?)null }
                }
            });

        (await Push()).EnsureSuccessStatusCode();
        (await Push()).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.Payments.CountAsync(p => p.LicenseId == licenseId);
        count.Should().Be(1, "same Id should upsert, not insert duplicate");
    }

    [Fact]
    public async Task Sync_updates_mutable_fields_on_existing_payment()
    {
        var (client, _, licenseId) = await SetupAsync();
        var paymentId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "ilk isim", amount = 100m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-MUT", pdfHash = (string?)null }
                }
            });

        // Re-push with refined name (e.g. better PDF parse)
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new
            {
                payments = new[]
                {
                    new { id = paymentId, payerName = "DOGRU ISIM", amount = 150m,
                          paidAt = DateTimeOffset.UtcNow, referansNo = "REF-MUT", pdfHash = "abc123" }
                }
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Payments.FirstAsync(p => p.Id == paymentId);
        stored.PayerName.Should().Be("DOGRU ISIM");
        stored.Amount.Should().Be(150m);
        stored.PdfHash.Should().Be("abc123");
    }

    [Fact]
    public async Task Sync_other_customers_license_returns_404()
    {
        var (clientA, _, _) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();

        var resp = await clientA.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseB}/payments/sync",
            new { payments = Array.Empty<object>() });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sync_empty_batch_returns_200_with_empty_array()
    {
        var (client, _, licenseId) = await SetupAsync();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_rejects_batch_over_200()
    {
        var (client, _, licenseId) = await SetupAsync();
        var huge = Enumerable.Range(0, 201).Select(i => new
        {
            id = Guid.NewGuid(),
            payerName = $"Payer {i}",
            amount = 10m,
            paidAt = DateTimeOffset.UtcNow,
            referansNo = $"R-{i}",
            pdfHash = (string?)null
        }).ToArray();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = huge });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sync_without_auth_returns_401()
    {
        var (_, _, licenseId) = await SetupAsync();
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payments/sync",
            new { payments = Array.Empty<object>() });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Since_returns_updated_payments_after_cursor()
    {
        var (client, _, licenseId) = await SetupAsync();
        var pid1 = Guid.NewGuid();
        var pid2 = Guid.NewGuid();
        var cursor = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Two payments with UpdatedAt in the future, then one in the past
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Payments.AddRange(
                new Payment
                {
                    Id = pid1, LicenseId = licenseId, PayerName = "A", Amount = 1,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "S1",
                    Status = PaymentStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                },
                new Payment
                {
                    Id = pid2, LicenseId = licenseId, PayerName = "B", Amount = 2,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "S2",
                    Status = PaymentStatus.Approved,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    ApprovedAt = DateTimeOffset.UtcNow
                },
                new Payment
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, PayerName = "C", Amount = 3,
                    PaidAt = DateTimeOffset.UtcNow, ReferansNo = "S3",
                    Status = PaymentStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
                });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync(
            $"/api/v1/licenses/{licenseId}/payments/since?since={Uri.EscapeDataString(cursor.ToString("O"))}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body.Should().HaveCount(2, "only two updated after cursor");
        body!.Should().Contain(d => d.Id == pid1);
        body.Should().Contain(d => d.Id == pid2);
    }

    [Fact]
    public async Task Since_isolates_by_license()
    {
        var (clientA, _, licenseA) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();
        var cursor = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Payments.AddRange(
                new Payment
                {
                    Id = Guid.NewGuid(), LicenseId = licenseA, PayerName = "A1",
                    Amount = 1, PaidAt = DateTimeOffset.UtcNow, ReferansNo = "AL1",
                    Status = PaymentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                new Payment
                {
                    Id = Guid.NewGuid(), LicenseId = licenseB, PayerName = "B1",
                    Amount = 1, PaidAt = DateTimeOffset.UtcNow, ReferansNo = "BL1",
                    Status = PaymentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var resp = await clientA.GetAsync(
            $"/api/v1/licenses/{licenseA}/payments/since?since={Uri.EscapeDataString(cursor.ToString("O"))}");
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedPaymentDto>>();
        body!.Should().HaveCount(1, "client A sees only A's license payments");
    }
}
