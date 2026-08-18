using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelWhatsAppLabelRulesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelWhatsAppLabelRulesControllerTests(ApiFactory f) => _factory = f;

    private sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);
    private sealed record RuleDto(string EventKey, string Description, Guid? WaLabelId);

    private async Task<(HttpClient Client, Guid LabelId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(), CustomerId = customerId,
                LicenseKey = "LDK-WARUL-" + Guid.NewGuid().ToString("N"),
                SkuCode = "STD", ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "Dekont geldi", color = "#eab308" });
        // Hata gövdesi de LabelDto'ya sessizce çözülür (ortak alan yok) ve
        // Id boş Guid kalır; o hâlde testler yanlış nedenle geçerdi.
        created.EnsureSuccessStatusCode();
        var label = (await created.Content.ReadFromJsonAsync<LabelDto>())!;
        return (client, label.Id);
    }

    [Fact]
    public async Task Lists_every_event_even_when_no_rule_exists()
    {
        var (client, _) = await SeedAsync();

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");

        rules!.Should().HaveCount(5);
        rules.Select(r => r.EventKey).Should().BeEquivalentTo(new[]
        {
            "PaymentApproved", "PaymentRejected", "OrderReceived",
            "ShipmentStatusChanged", "CustomerSentDocument",
        });
        // Enum'a altıncı olay eklenip açıklama sözlüğü unutulursa uç onu hiç
        // göstermez ve panelden atanamaz — LabelRuleApplier tetiklese bile
        // sessizce hiçbir şey olmaz. Bu satır o sessizliği bozar.
        rules.Select(r => r.EventKey).Should().BeEquivalentTo(Enum.GetNames<WaLabelEvent>());
        rules.Should().OnlyContain(r => r.WaLabelId == null);
        rules.Should().OnlyContain(r => r.Description.Length > 0);
    }

    [Fact]
    public async Task Assigning_a_label_to_an_event_is_readable_back()
    {
        var (client, labelId) = await SeedAsync();

        var put = await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/CustomerSentDocument", new { waLabelId = labelId });
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");
        rules!.Single(r => r.EventKey == "CustomerSentDocument").WaLabelId.Should().Be(labelId);
    }

    [Fact]
    public async Task Assigning_twice_replaces_instead_of_duplicating()
    {
        var (client, first) = await SeedAsync();
        var second = (await (await client.PostAsJsonAsync(
            "/api/panel/whatsapp-labels", new { name = "İnsan baksın", color = "#ef4444" }))
            .Content.ReadFromJsonAsync<LabelDto>())!;

        await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/OrderReceived", new { waLabelId = first });
        await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/OrderReceived", new { waLabelId = second.Id });

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");
        rules!.Single(r => r.EventKey == "OrderReceived").WaLabelId.Should().Be(second.Id);

        // Sayım bu iki etiketle sınırlanır: ApiFactory veritabanı sınıftaki
        // bütün testlerde ortak, filtresiz sayım komşu testlere bağımlı olurdu.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaLabelRules.Count(r =>
            r.EventKey == WaLabelEvent.OrderReceived
            && (r.WaLabelId == first || r.WaLabelId == second.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_null_label_clears_the_rule()
    {
        var (client, labelId) = await SeedAsync();
        await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/PaymentApproved", new { waLabelId = labelId });

        var clear = await client.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/PaymentApproved", new { waLabelId = (Guid?)null });
        clear.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rules = await client.GetFromJsonAsync<List<RuleDto>>("/api/panel/whatsapp-label-rules");
        rules!.Single(r => r.EventKey == "PaymentApproved").WaLabelId.Should().BeNull();
    }

    // "3" ve virgüllü liste: Enum.TryParse ikisini de kabul ederdi. Tel biçimi
    // olay ADI olduğuna göre sayı değerleri sözleşmenin parçası değil.
    [Theory]
    [InlineData("SomethingElse")]
    [InlineData("paymentapproved")]
    [InlineData("3")]
    [InlineData("PaymentApproved,PaymentRejected")]
    public async Task An_unknown_event_key_is_rejected(string eventKey)
    {
        var (client, labelId) = await SeedAsync();

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/whatsapp-label-rules/{eventKey}", new { waLabelId = labelId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unknown-event");
    }

    [Fact]
    public async Task Another_broadcasters_label_cannot_be_bound()
    {
        var (mine, _) = await SeedAsync();
        var (_, theirLabelId) = await SeedAsync();

        var resp = await mine.PutAsJsonAsync(
            "/api/panel/whatsapp-label-rules/PaymentApproved", new { waLabelId = theirLabelId });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
