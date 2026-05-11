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

public class PanelPaymentsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PanelPaymentsControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> CreateAuthedCustomerWithLicenseAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PMT-" + Guid.NewGuid().ToString("N"),
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

    private async Task<Guid> SeedPaymentAsync(Guid licenseId, PaymentStatus status, string? refNo = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Payments.Add(new Payment
        {
            Id = id,
            LicenseId = licenseId,
            PayerName = "Test Payer",
            Amount = 250.50m,
            PaidAt = now.AddHours(-1),
            ReferansNo = refNo ?? Guid.NewGuid().ToString("N"),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed record PaymentDto(
        Guid Id, Guid LicenseId, string PayerName, decimal Amount,
        DateTimeOffset PaidAt, string ReferansNo, string Status,
        DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt, string? RejectReason);

    [Fact]
    public async Task List_returns_only_own_pending_payments_by_default()
    {
        var (client, _, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        await SeedPaymentAsync(licenseId, PaymentStatus.Approved);

        var resp = await client.GetAsync("/api/panel/payments?status=pending");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<List<PaymentDto>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(2);
        body.Should().OnlyContain(p => p.Status == "pending");
    }

    [Fact]
    public async Task List_isolates_by_customer_license()
    {
        // Customer A
        var (clientA, _, licenseA) = await CreateAuthedCustomerWithLicenseAsync();
        await SeedPaymentAsync(licenseA, PaymentStatus.Pending);

        // Customer B with different license
        var (clientB, _, licenseB) = await CreateAuthedCustomerWithLicenseAsync();
        await SeedPaymentAsync(licenseB, PaymentStatus.Pending);
        await SeedPaymentAsync(licenseB, PaymentStatus.Pending);

        var respA = await clientA.GetAsync("/api/panel/payments?status=pending");
        var bodyA = await respA.Content.ReadFromJsonAsync<List<PaymentDto>>();
        bodyA!.Should().HaveCount(1, "A only sees own pending");

        var respB = await clientB.GetAsync("/api/panel/payments?status=pending");
        var bodyB = await respB.Content.ReadFromJsonAsync<List<PaymentDto>>();
        bodyB!.Should().HaveCount(2, "B only sees own pending");
    }

    [Fact]
    public async Task List_returns_400_for_invalid_status()
    {
        var (client, _, _) = await CreateAuthedCustomerWithLicenseAsync();
        var resp = await client.GetAsync("/api/panel/payments?status=garbage");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_without_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/panel/payments");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Approve_transitions_pending_to_approved()
    {
        var (client, customerId, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var payment = await db.Payments.FirstAsync(p => p.Id == paymentId);
        payment.Status.Should().Be(PaymentStatus.Approved);
        payment.ApprovedAt.Should().NotBeNull();
        payment.ApprovedByCustomerId.Should().Be(customerId);
    }

    [Fact]
    public async Task Approve_returns_409_for_already_decided_payment()
    {
        var (client, _, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Approved);

        var resp = await client.PostAsync($"/api/panel/payments/{paymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_other_customer_payment_returns_404()
    {
        var (_, _, licenseA) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentA = await SeedPaymentAsync(licenseA, PaymentStatus.Pending);

        // Customer B tries to approve A's payment
        var (clientB, _, _) = await CreateAuthedCustomerWithLicenseAsync();
        var resp = await clientB.PostAsync($"/api/panel/payments/{paymentA}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reject_with_reason_persists_status_and_reason()
    {
        var (client, customerId, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);

        var resp = await client.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject",
            new { reason = "tutar uyusmuyor" });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var payment = await db.Payments.FirstAsync(p => p.Id == paymentId);
        payment.Status.Should().Be(PaymentStatus.Rejected);
        payment.RejectedAt.Should().NotBeNull();
        payment.RejectedByCustomerId.Should().Be(customerId);
        payment.RejectReason.Should().Be("tutar uyusmuyor");
    }

    [Fact]
    public async Task Reject_with_empty_reason_persists_null()
    {
        var (client, _, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);

        var resp = await client.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject",
            new { reason = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var payment = await db.Payments.FirstAsync(p => p.Id == paymentId);
        payment.RejectReason.Should().BeNull();
    }

    // NOTE: Duplicate referansNo protection (unique index on LicenseId+ReferansNo)
    // configured in LicenseDbContext.OnModelCreating but cannot be verified against
    // the EF Core InMemory provider, which ignores unique constraints. The migration
    // generates a real unique index for SQL Server in production. Integration coverage
    // would require a real DB fixture (out of scope for this PR).
}
