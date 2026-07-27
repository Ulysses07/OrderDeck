using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Salt-okunur sohbet görüntüleme. İki şeyi koruyor: <b>tenant izolasyonu</b>
/// (sohbet id'si bilinse bile başka lisansın yazışması okunamaz) ve
/// <b>24s pencere göstergesinin doğruluğu</b> — gönderim neden reddedildi
/// sorusunun cevabı bu alandan okunuyor.
/// </summary>
public class AdminWhatsAppConversationsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminWhatsAppConversationsTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"wa-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-WAC-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private async Task<Guid> SeedConversationAsync(
        Guid licenseId, string phone, DateTimeOffset? lastInboundAt, DateTimeOffset? lastMessageAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var id = Guid.NewGuid();
        db.WaConversations.Add(new WaConversation
        {
            Id = id,
            LicenseId = licenseId,
            CustomerPhone = phone,
            ProfileName = "Ayşe",
            PhoneNumberId = "pnid-1",
            Status = "open",
            LastInboundAt = lastInboundAt,
            LastMessageAt = lastMessageAt,
            UnreadCount = 2,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedMessageAsync(
        Guid licenseId, Guid conversationId, string direction, string body, DateTimeOffset timestamp)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        db.WaMessages.Add(new WaMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            LicenseId = licenseId,
            WamId = $"wamid.{Guid.NewGuid():N}",
            Direction = direction,
            Type = "text",
            Body = body,
            Status = direction == "in" ? "received" : "sent",
            Timestamp = timestamp,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed record ConversationSummary(
        Guid Id, string CustomerPhone, string? ProfileName, string Status,
        bool WindowOpen, DateTimeOffset? WindowExpiresAt, DateTimeOffset? LastInboundAt,
        DateTimeOffset? LastMessageAt, int UnreadCount);

    private sealed record MessageItem(
        Guid Id, string Direction, string Type, string? Body, string Status,
        string? Origin, string? TemplateName, string? MediaMimeType,
        string? ErrorCode, string? ErrorMessage, DateTimeOffset Timestamp);

    private static string Url(Guid licenseId) =>
        $"/api/v1/admin/licenses/{licenseId}/whatsapp/conversations";

    [Fact]
    public async Task List_orders_by_last_activity_and_reports_open_window()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var now = DateTimeOffset.UtcNow;

        await SeedConversationAsync(licenseId, "905550000001", now.AddHours(-30), now.AddHours(-30));
        await SeedConversationAsync(licenseId, "905550000002", now.AddMinutes(-10), now.AddMinutes(-5));

        var resp = await admin.GetAsync(Url(licenseId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await resp.Content.ReadFromJsonAsync<List<ConversationSummary>>())!;
        rows.Should().HaveCount(2);

        rows[0].CustomerPhone.Should().Be("905550000002", "en son hareket eden başa gelir");
        rows[0].WindowOpen.Should().BeTrue();
        rows[0].WindowExpiresAt.Should().BeCloseTo(
            now.AddMinutes(-10).AddHours(24), TimeSpan.FromMinutes(1),
            "pencere son GELEN mesajdan 24 saat sonra kapanır");
        rows[0].ProfileName.Should().Be("Ayşe");
        rows[0].UnreadCount.Should().Be(2);

        // 30 saat önce yazmış → pencere kapalı, serbest metin gidemez.
        rows[1].CustomerPhone.Should().Be("905550000001");
        rows[1].WindowOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Conversation_without_inbound_is_reported_closed()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();

        await SeedConversationAsync(licenseId, "905550000003", null, DateTimeOffset.UtcNow);

        var rows = (await (await admin.GetAsync(Url(licenseId)))
            .Content.ReadFromJsonAsync<List<ConversationSummary>>())!;

        rows.Should().ContainSingle();
        rows[0].WindowOpen.Should().BeFalse();
        rows[0].WindowExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task List_only_returns_conversations_of_the_requested_license()
    {
        var mine = await SeedLicenseAsync();
        var other = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var now = DateTimeOffset.UtcNow;

        await SeedConversationAsync(mine, "905550000010", now, now);
        await SeedConversationAsync(other, "905550000011", now, now);

        var rows = (await (await admin.GetAsync(Url(mine)))
            .Content.ReadFromJsonAsync<List<ConversationSummary>>())!;

        rows.Should().ContainSingle();
        rows[0].CustomerPhone.Should().Be("905550000010");
    }

    [Fact]
    public async Task Messages_are_returned_oldest_first_by_meta_timestamp()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var now = DateTimeOffset.UtcNow;
        var convo = await SeedConversationAsync(licenseId, "905550000020", now, now);

        // Kasıtlı olarak ters sırayla yazılıyor — sıralama Timestamp'e dayanmalı,
        // satırın yazılma sırasına değil (gecikmeli webhook senaryosu).
        await SeedMessageAsync(licenseId, convo, "out", "üçüncü", now.AddMinutes(-1));
        await SeedMessageAsync(licenseId, convo, "in", "birinci", now.AddMinutes(-10));
        await SeedMessageAsync(licenseId, convo, "out", "ikinci", now.AddMinutes(-5));

        var resp = await admin.GetAsync($"{Url(licenseId)}/{convo}/messages");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var msgs = (await resp.Content.ReadFromJsonAsync<List<MessageItem>>())!;
        msgs.Select(m => m.Body).Should().ContainInOrder("birinci", "ikinci", "üçüncü");
        msgs[0].Direction.Should().Be("in");
        msgs[0].Status.Should().Be("received");
    }

    [Fact]
    public async Task Messages_of_another_license_conversation_are_not_readable()
    {
        var mine = await SeedLicenseAsync();
        var other = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var now = DateTimeOffset.UtcNow;

        var foreign = await SeedConversationAsync(other, "905550000030", now, now);
        await SeedMessageAsync(other, foreign, "in", "gizli", now);

        // Sohbet id'si doğru ama lisans başkasının → 404.
        var resp = await admin.GetAsync($"{Url(mine)}/{foreign}/messages");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Limit_is_honoured_and_out_of_range_values_fall_back_to_default()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var now = DateTimeOffset.UtcNow;

        await SeedConversationAsync(licenseId, "905550000040", now, now.AddMinutes(-1));
        await SeedConversationAsync(licenseId, "905550000041", now, now.AddMinutes(-2));

        var limited = (await (await admin.GetAsync($"{Url(licenseId)}?limit=1"))
            .Content.ReadFromJsonAsync<List<ConversationSummary>>())!;
        limited.Should().ContainSingle();

        // Saçma değer istemci hatası sayılmaz, varsayılana düşer.
        var absurd = (await (await admin.GetAsync($"{Url(licenseId)}?limit=99999"))
            .Content.ReadFromJsonAsync<List<ConversationSummary>>())!;
        absurd.Should().HaveCount(2);
    }

    [Fact]
    public async Task Returns_404_for_unknown_license()
    {
        var admin = await AdminClientAsync();

        (await admin.GetAsync(Url(Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requires_admin_auth()
    {
        var licenseId = await SeedLicenseAsync();
        var anon = _factory.CreateClient();

        (await anon.GetAsync(Url(licenseId))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"{Url(licenseId)}/{Guid.NewGuid()}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
