using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class ShipmentSyncLabelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShipmentSyncLabelTests(ApiFactory f) => _factory = f;

    private sealed record Seeded(
        HttpClient Client, Guid LicenseId, Guid ConversationId, Guid LabelId, Guid WpfCustomerId);

    private async Task<Seeded> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-SHPL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId,
            LicenseId = license.Id,
            Platform = "youtube",
            Username = "ayse",
            Phone = "+905321234567",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

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
            Name = "Kargo durumu değişti",
            Color = "#3b82f6",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            EventKey = WaLabelEvent.ShipmentStatusChanged,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(client, license.Id, convo.Id, label.Id, wpfCustomerId);
    }

    private async Task<int> LabelCountAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId);
    }

    private static object Body(Guid shipmentId, Guid wpfCustomerId, string status) => new
    {
        shipments = new[]
        {
            new
            {
                id = shipmentId,
                customerId = wpfCustomerId.ToString("N"),
                status,
                cumulativeAmount = 250m,
                createdAt = DateTimeOffset.UtcNow,
                heldAt = (DateTimeOffset?)null,
                shippedAt = (DateTimeOffset?)null,
            }
        }
    };

    [Fact]
    public async Task Status_change_labels_the_conversation()
    {
        var s = await SeedAsync();
        var shipmentId = Guid.NewGuid();

        // İlk sync: Pending — henüz karar yok, etiket beklenmiyor.
        var first = await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "pending"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LabelCountAsync(s.ConversationId)).Should().Be(0);

        // İkinci sync: durum değişti → etiket.
        var second = await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "shipped"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        (await LabelCountAsync(s.ConversationId)).Should().Be(1);
    }

    /// <summary>
    /// WPF outbox aynı satırı değişmeden tekrar gönderebilir. Durum aynıysa
    /// olay yoktur — yoksa her sync turu sohbeti yeniden etiketlemeye çalışırdı.
    /// </summary>
    [Fact]
    public async Task Resending_the_same_status_is_not_an_event()
    {
        var s = await SeedAsync();
        var shipmentId = Guid.NewGuid();

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "held"));
        var countAfterFirst = await LabelCountAsync(s.ConversationId);

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/shipments/sync",
            Body(shipmentId, s.WpfCustomerId, "held"));

        countAfterFirst.Should().Be(1);
        (await LabelCountAsync(s.ConversationId)).Should().Be(1);
    }
}
