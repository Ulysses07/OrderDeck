using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using DomainShopper = OrderDeck.LicenseServer.Domain.Shopper;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelPaymentsLabelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelPaymentsLabelTests(ApiFactory f) => _factory = f;

    private sealed record Seeded(HttpClient Client, Guid LicenseId, Guid PaymentId, Guid ConversationId, Guid LabelId);

    private async Task<Seeded> SeedAsync(WaLabelEvent eventKey)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-WAL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var shopper = new DomainShopper
        {
            Id = Guid.NewGuid(),
            FullName = "Ayşe K.",
            Phone = "+905321234567",
            PasswordHash = "x",
            Address = "adres",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ShopperId = shopper.Id,
            PayerName = "Ayşe K.",
            Amount = 1450m,
            PaidAt = DateTimeOffset.UtcNow,
            ReferansNo = "4471X",
            Status = PaymentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Payments.Add(payment);

        var convo = new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CustomerPhone = "905321234567",
            PhoneNumberId = "PNID_1",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(convo);

        var label = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Name = eventKey == WaLabelEvent.PaymentApproved ? "Ödeme onaylandı" : "Ödeme reddedildi",
            Color = "#22c55e",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            EventKey = eventKey,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(client, license.Id, payment.Id, convo.Id, label.Id);
    }

    private async Task<List<WaConversationLabel>> LabelsAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync();
    }

    [Fact]
    public async Task Approving_a_payment_labels_the_customers_conversation()
    {
        var s = await SeedAsync(WaLabelEvent.PaymentApproved);

        var resp = await s.Client.PostAsync($"/api/panel/payments/{s.PaymentId}/approve", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var labels = await LabelsAsync(s.ConversationId);
        labels.Should().ContainSingle().Which.WaLabelId.Should().Be(s.LabelId);
    }

    [Fact]
    public async Task Rejecting_a_payment_labels_the_customers_conversation()
    {
        var s = await SeedAsync(WaLabelEvent.PaymentRejected);

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/panel/payments/{s.PaymentId}/reject", new { reason = "Tutar eksik" });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var labels = await LabelsAsync(s.ConversationId);
        labels.Should().ContainSingle().Which.WaLabelId.Should().Be(s.LabelId);
    }

    /// <summary>
    /// Etiketleme onayı GERİ ALMAZ. Kural tanımlı değilken de onay geçmeli;
    /// bu test, etiket yolunun iş akışına bağlanmadığının kanıtı.
    /// </summary>
    [Fact]
    public async Task Approval_still_succeeds_when_no_rule_is_defined()
    {
        var s = await SeedAsync(WaLabelEvent.PaymentRejected);   // kural RET için, onay için değil

        var resp = await s.Client.PostAsync($"/api/panel/payments/{s.PaymentId}/approve", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LabelsAsync(s.ConversationId)).Should().BeEmpty();
    }
}
