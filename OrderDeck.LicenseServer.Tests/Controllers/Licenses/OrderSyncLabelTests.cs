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

public class OrderSyncLabelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OrderSyncLabelTests(ApiFactory f) => _factory = f;

    private sealed record Seeded(HttpClient Client, Guid LicenseId, Guid ConversationId, Guid WpfCustomerId);

    private async Task<Seeded> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-ORDL-" + Guid.NewGuid().ToString("N"),
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
            Name = "Yeni sipariş",
            Color = "#eab308",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            EventKey = WaLabelEvent.OrderReceived,
            WaLabelId = label.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return new Seeded(client, license.Id, convo.Id, wpfCustomerId);
    }

    private async Task<int> LabelCountAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId);
    }

    private static object Body(Guid wpfCustomerId, DateTimeOffset? printedAt, bool isShippingFee = false) => new
    {
        catalogAware = false,
        orders = new[]
        {
            new
            {
                id = Guid.NewGuid(),
                sessionId = (Guid?)null,
                customerId = wpfCustomerId.ToString("N"),
                platform = "youtube",
                username = "ayse",
                displayName = "Ayşe K.",
                messageText = "A12",
                code = "A12",
                price = 250m,
                addedAt = DateTimeOffset.UtcNow,
                printedAt,
                cancelledAt = (DateTimeOffset?)null,
                cancelReason = (string?)null,
                isShippingFee,
                isBackupPromoted = false,
                isTentativeBackup = false,
                productId = (Guid?)null,
                productVariantId = (Guid?)null,
            }
        }
    };

    [Fact]
    public async Task A_new_printed_order_labels_the_conversation()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            Body(s.WpfCustomerId, printedAt: DateTimeOffset.UtcNow));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await LabelCountAsync(s.ConversationId)).Should().Be(1);
    }

    /// <summary>
    /// Kargo ücreti satırı sipariş değildir — push bildirimi de onu saymıyor.
    /// Etiket ikinci bir "gerçek satış" tanımı üretmemeli.
    /// </summary>
    [Fact]
    public async Task A_shipping_fee_row_is_not_an_order()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            Body(s.WpfCustomerId, printedAt: DateTimeOffset.UtcNow, isShippingFee: true));

        (await LabelCountAsync(s.ConversationId)).Should().Be(0);
    }

    [Fact]
    public async Task An_unprinted_order_is_not_an_event_yet()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            Body(s.WpfCustomerId, printedAt: null));

        (await LabelCountAsync(s.ConversationId)).Should().Be(0);
    }
}
