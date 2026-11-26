using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // ─── Decision contract (approve + reject) ────────────────────────────────

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid paymentId, string action) =>
        action == "approve"
            ? client.PostAsync($"/api/panel/payments/{paymentId}/approve", null)
            : client.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject", new { reason = "tutar uyusmuyor" });

    private async Task<Payment> ReadPaymentAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    [Theory]
    [InlineData("approve", PaymentStatus.Approved)]
    [InlineData("approve", PaymentStatus.Rejected)]
    [InlineData("reject", PaymentStatus.Approved)]
    [InlineData("reject", PaymentStatus.Rejected)]
    public async Task Deciding_a_decided_payment_returns_409_not_pending_and_leaves_the_row(
        string action, PaymentStatus decided)
    {
        var (client, _, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, decided);
        var before = await ReadPaymentAsync(paymentId);

        var resp = await DecideAsync(client, paymentId, action);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("title").GetString().Should().Be("not-pending");
        problem.RootElement.GetProperty("detail").GetString().Should().Be("Bu ödeme zaten karara bağlanmış.");
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(409);
        (await ReadPaymentAsync(paymentId)).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Deciding_another_customers_payment_returns_404_and_leaves_it_pending(string action)
    {
        var (_, _, licenseA) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentA = await SeedPaymentAsync(licenseA, PaymentStatus.Pending);
        var before = await ReadPaymentAsync(paymentA);

        var (clientB, _, _) = await CreateAuthedCustomerWithLicenseAsync();
        var resp = await DecideAsync(clientB, paymentA, action);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadPaymentAsync(paymentA)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Approve_stamps_only_approval_fields_with_one_instant()
    {
        var (client, customerId, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        var before = await ReadPaymentAsync(paymentId);

        (await DecideAsync(client, paymentId, "approve")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await ReadPaymentAsync(paymentId);
        after.UpdatedAt.Should().BeAfter(before.UpdatedAt, "WPF'in payments/since imleci bu damgayla ilerler");
        before.Status = PaymentStatus.Approved;
        before.ApprovedAt = after.UpdatedAt;
        before.ApprovedByCustomerId = customerId;
        before.UpdatedAt = after.UpdatedAt;
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Reject_stamps_only_rejection_fields_with_one_instant()
    {
        var (client, customerId, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        var before = await ReadPaymentAsync(paymentId);

        (await DecideAsync(client, paymentId, "reject")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await ReadPaymentAsync(paymentId);
        after.UpdatedAt.Should().BeAfter(before.UpdatedAt, "WPF'in payments/since imleci bu damgayla ilerler");
        before.Status = PaymentStatus.Rejected;
        before.RejectedAt = after.UpdatedAt;
        before.RejectedByCustomerId = customerId;
        before.RejectReason = "tutar uyusmuyor";
        before.UpdatedAt = after.UpdatedAt;
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Reject_trims_the_reason_then_cuts_it_at_500_characters()
    {
        var (client, _, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);

        var resp = await client.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject",
            new { reason = "  " + new string('a', 499) + "bc  " });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadPaymentAsync(paymentId)).RejectReason.Should().Be(new string('a', 499) + "b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Reject_without_a_usable_reason_persists_null(string? reason)
    {
        var (client, _, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var paymentId = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);

        var resp = await client.PostAsJsonAsync($"/api/panel/payments/{paymentId}/reject", new { reason });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadPaymentAsync(paymentId)).RejectReason.Should().BeNull();
    }

    [Fact]
    public async Task Staff_decisions_are_stamped_with_the_tenant_customer_id()
    {
        var (owner, customerId, licenseId) = await CreateAuthedCustomerWithLicenseAsync();
        var approved = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        var rejected = await SeedPaymentAsync(licenseId, PaymentStatus.Pending);
        var staff = await PanelOperatorHelper.StaffClientAsync(_factory, owner);

        (await DecideAsync(staff, approved, "approve")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DecideAsync(staff, rejected, "reject")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ReadPaymentAsync(approved)).ApprovedByCustomerId.Should().Be(customerId);
        (await ReadPaymentAsync(rejected)).RejectedByCustomerId.Should().Be(customerId);
    }

    // NOTE: Duplicate referansNo protection (unique index on LicenseId+ReferansNo)
    // configured in LicenseDbContext.OnModelCreating but cannot be verified against
    // the EF Core InMemory provider, which ignores unique constraints. The migration
    // generates a real unique index for SQL Server in production. Integration coverage
    // would require a real DB fixture (out of scope for this PR).
}
