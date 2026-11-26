using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Payments;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Payments;

/// <summary>Karar çekirdeğinin HTTP'siz sözleşmesi: DI'dan çözülür, istek bağlamı istemez, sonucu
/// <see cref="PaymentDecisionOutcome"/> ile bildirir. Yan etkilerin sırası, jeton ve arıza yalıtımı uçtan sınanır
/// (<c>PanelPaymentDecisionSideEffectTests</c>).</summary>
public sealed class PaymentApprovalServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PaymentApprovalServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<(Guid CustomerId, Guid PaymentId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"pay-{Guid.NewGuid():N}@example.test", Name = "Yayıncı",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now, EmailConfirmedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = "LDK-PAS-" + Guid.NewGuid().ToString("N"), CustomerId = customer.Id, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, PayerName = "Test Payer", Amount = 250.50m, PaidAt = now.AddHours(-1),
            ReferansNo = Guid.NewGuid().ToString("N"), Status = PaymentStatus.Pending, CreatedAt = now, UpdatedAt = now,
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return (customer.Id, payment.Id);
    }

    /// <summary>Her karar kendi kapsamında: istek başına bir bağlam.</summary>
    private async Task<PaymentDecisionOutcome> DecideAsync(
        Func<PaymentApprovalService, Task<PaymentDecisionOutcome>> decide)
    {
        using var scope = _factory.Services.CreateScope();
        return await decide(scope.ServiceProvider.GetRequiredService<PaymentApprovalService>());
    }

    private Task<PaymentDecisionOutcome> ApproveAsync(Guid paymentId, Guid customerId) =>
        DecideAsync(s => s.ApproveAsync(paymentId, customerId, CancellationToken.None));

    private Task<PaymentDecisionOutcome> RejectAsync(Guid paymentId, Guid customerId, string? reason) =>
        DecideAsync(s => s.RejectAsync(paymentId, customerId, reason, CancellationToken.None));

    private async Task<Payment> ReadAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().Payments.AsNoTracking()
            .SingleAsync(p => p.Id == paymentId);
    }

    [Fact]
    public async Task Onay_karari_yazar_sonraki_kararlar_NotPending_alir()
    {
        var (customerId, paymentId) = await SeedAsync();

        (await ApproveAsync(paymentId, customerId)).Should().Be(PaymentDecisionOutcome.Decided);
        (await ApproveAsync(paymentId, customerId)).Should().Be(PaymentDecisionOutcome.NotPending);
        (await RejectAsync(paymentId, customerId, "geç kaldı")).Should().Be(PaymentDecisionOutcome.NotPending);

        var payment = await ReadAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Approved);
        payment.ApprovedByCustomerId.Should().Be(customerId);
        payment.RejectReason.Should().BeNull();
    }

    [Fact]
    public async Task Ret_karari_gerekcesiyle_yazar_sonraki_kararlar_NotPending_alir()
    {
        var (customerId, paymentId) = await SeedAsync();

        (await RejectAsync(paymentId, customerId, " okunmuyor ")).Should().Be(PaymentDecisionOutcome.Decided);
        (await RejectAsync(paymentId, customerId, "başka gerekçe")).Should().Be(PaymentDecisionOutcome.NotPending);
        (await ApproveAsync(paymentId, customerId)).Should().Be(PaymentDecisionOutcome.NotPending);

        var payment = await ReadAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Rejected);
        payment.RejectedByCustomerId.Should().Be(customerId);
        payment.RejectReason.Should().Be("okunmuyor");
        payment.ApprovedAt.Should().BeNull();
    }

    [Fact]
    public async Task Bilinmeyen_ve_baska_yayincinin_odemesi_NotFound_alir_satira_dokunulmaz()
    {
        var (customerId, paymentId) = await SeedAsync();
        var (strangerId, _) = await SeedAsync();

        (await ApproveAsync(Guid.NewGuid(), customerId)).Should().Be(PaymentDecisionOutcome.NotFound);
        (await RejectAsync(Guid.NewGuid(), customerId, null)).Should().Be(PaymentDecisionOutcome.NotFound);
        (await ApproveAsync(paymentId, strangerId)).Should().Be(PaymentDecisionOutcome.NotFound);
        (await RejectAsync(paymentId, strangerId, null)).Should().Be(PaymentDecisionOutcome.NotFound);

        (await ReadAsync(paymentId)).Status.Should().Be(PaymentStatus.Pending);
    }
}
