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

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelWhatsAppLabelsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppLabelsControllerTests(ApiFactory f) => _factory = f;

    private sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);

    private async Task<(HttpClient Client, Guid LicenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-WALBL-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<LabelDto> CreateAsync(HttpClient client, string name, string color = "#eab308")
    {
        var resp = await client.PostAsJsonAsync("/api/panel/whatsapp-labels", new { name, color });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<LabelDto>())!;
    }

    [Fact]
    public async Task Creates_and_lists_labels_alphabetically()
    {
        var (client, _) = await SeedAsync();

        await CreateAsync(client, "Ödeme bekliyor");
        await CreateAsync(client, "Dekont geldi");

        var list = await client.GetFromJsonAsync<List<LabelDto>>("/api/panel/whatsapp-labels");

        list!.Select(l => l.Name).Should().ContainInOrder("Dekont geldi", "Ödeme bekliyor");
    }

    [Fact]
    public async Task Rejects_a_color_outside_the_palette()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Test", color = "#123456" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_a_duplicate_name_within_the_same_license()
    {
        var (client, _) = await SeedAsync();
        await CreateAsync(client, "Dekont geldi");

        var resp = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Dekont geldi", color = "#eab308" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Renames_a_label()
    {
        var (client, _) = await SeedAsync();
        var label = await CreateAsync(client, "Dekont geldi");

        var resp = await client.PatchAsJsonAsync(
            $"/api/panel/whatsapp-labels/{label.Id}", new { name = "Dekont var", color = "#EF4444" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<LabelDto>())!;
        updated.Name.Should().Be("Dekont var");
        // Büyük harfle gönderildi, kanonik küçük harfle saklanır.
        updated.Color.Should().Be("#ef4444");
    }

    [Fact]
    public async Task Deleting_a_label_also_removes_its_rule_and_conversation_links()
    {
        var (client, licenseId) = await SeedAsync();
        var label = await CreateAsync(client, "Dekont geldi");

        Guid conversationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            conversationId = Guid.NewGuid();
            db.WaConversations.Add(new WaConversation
            {
                Id = conversationId, LicenseId = licenseId,
                CustomerPhone = "905321234567", PhoneNumberId = "PNID_1",
                Status = "open", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                EventKey = WaLabelEvent.CustomerSentDocument, WaLabelId = label.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.WaConversationLabels.Add(new WaConversationLabel
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                ConversationId = conversationId, WaLabelId = label.Id,
                Source = "auto", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.DeleteAsync($"/api/panel/whatsapp-labels/{label.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WaLabels.AnyAsync(l => l.Id == label.Id)).Should().BeFalse();
            (await db.WaLabelRules.AnyAsync(r => r.WaLabelId == label.Id)).Should().BeFalse();
            (await db.WaConversationLabels.AnyAsync(x => x.WaLabelId == label.Id)).Should().BeFalse();
            // Sohbetin kendisi silinmez — etiket düşer, konuşma kalır.
            (await db.WaConversations.AnyAsync(c => c.Id == conversationId)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Another_broadcasters_label_is_not_reachable()
    {
        var (mine, _) = await SeedAsync();
        var (theirs, _) = await SeedAsync();

        var label = await CreateAsync(theirs, "Onların etiketi");

        var get = await mine.GetFromJsonAsync<List<LabelDto>>("/api/panel/whatsapp-labels");
        get!.Should().NotContain(l => l.Id == label.Id);

        var del = await mine.DeleteAsync($"/api/panel/whatsapp-labels/{label.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_an_active_license_the_list_is_empty()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var list = await client.GetFromJsonAsync<List<LabelDto>>("/api/panel/whatsapp-labels");

        list.Should().BeEmpty();
    }
}
