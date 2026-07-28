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

/// <summary>
/// WPF'in (Bearer-Customer) kullandığı gönderim ucu. Test ortamında sender
/// "log" provider olduğu için Graph'a çıkılmaz; burada doğrulanan üç şey:
/// (1) yayıncı yalnız KENDİ lisansına gönderebiliyor, (2) gönderilemedi
/// durumu 200 gövdesinde taşınıyor — çağıran wa.me'ye düşme kararını bu
/// gövdeye bakarak veriyor, (3) mesaj WaMessages'a "wpf-payment" origin'i ile
/// yazılıyor.
/// </summary>
public class LicensesWhatsAppSendTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesWhatsAppSendTests(ApiFactory factory) => _factory = factory;

    private sealed record SendResponse(bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/whatsapp/send";

    /// <summary>Doğrulanmış müşteri + ona ait lisans.</summary>
    private async Task<(HttpClient Client, Guid LicenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-WAS-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary>Admin ucuna gitmeden doğrudan DB'ye aktif hesap satırı yazar.</summary>
    private async Task ConnectAccountAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = $"pnid-{Guid.NewGuid():N}",
            DisplayPhoneNumber = "905550000000",
            AccessTokenProtected = accounts.ProtectToken("token-1234"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>24s pencereyi açmak için müşteriden gelmiş bir mesaj gerekiyor.</summary>
    private async Task OpenServiceWindowAsync(Guid licenseId, string phone)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = phone,
            PhoneNumberId = "pnid-open",
            Status = "open",
            LastInboundAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sends_text_when_service_window_open()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551112233");

        var resp = await client.PostAsJsonAsync(
            Url(licenseId),
            new { toPhone = "+90 555 111 22 33", text = "Merhaba, ödemeniz bekleniyor.", origin = "wpf-payment" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeTrue();
        body.MessageId.Should().NotBeNull();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var msg = await db.WaMessages.SingleAsync(m => m.LicenseId == licenseId);
        msg.Direction.Should().Be("out");
        msg.Origin.Should().Be("wpf-payment");
        msg.Status.Should().Be("sent");
        msg.Body.Should().Be("Merhaba, ödemeniz bekleniyor.");
    }

    [Fact]
    public async Task Defaults_origin_to_wpf_when_omitted()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551112244");

        // origin alanı hiç gönderilmiyor → sunucu "wpf" yazmalı.
        var resp = await client.PostAsJsonAsync(
            Url(licenseId),
            new { toPhone = "+90 555 111 22 44", text = "Merhaba." });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var msg = await db.WaMessages.SingleAsync(m => m.LicenseId == licenseId);
        msg.Origin.Should().Be("wpf");
    }

    [Fact]
    public async Task Reports_window_closed_with_200()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);

        // Hiç gelen mesaj yok → pencere kapalı.
        var resp = await client.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", text = "merhaba" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeFalse();
        body.ErrorCode.Should().Be("window_closed");
    }

    [Fact]
    public async Task Reports_no_account_with_200()
    {
        var (client, licenseId) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", text = "merhaba" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SendResponse>())!.ErrorCode.Should().Be("no_account");
    }

    [Fact]
    public async Task Returns_404_for_license_owned_by_another_customer()
    {
        var (_, otherLicenseId) = await SeedAsync();
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            Url(otherLicenseId), new { toPhone = "905559998877", text = "merhaba" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rejects_invalid_phone_and_empty_text()
    {
        var (client, licenseId) = await SeedAsync();

        (await client.PostAsJsonAsync(Url(licenseId), new { toPhone = "abc", text = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Url(licenseId), new { toPhone = "905559998877", text = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_text_longer_than_limit()
    {
        var (client, licenseId) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", text = new string('x', 4097) });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Accepts_text_exactly_at_limit()
    {
        var (client, licenseId) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", text = new string('x', 4096) });

        // Hesap bağlı olmadığı için gövde no_account döner; buradaki mesele
        // sınır değerin doğrulamadan geçmesi, o yüzden HTTP durumuna bakıyoruz.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requires_customer_auth()
    {
        var (_, licenseId) = await SeedAsync();
        var anon = _factory.CreateClient();

        (await anon.PostAsJsonAsync(Url(licenseId), new { toPhone = "905559998877", text = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Idempotency (2026-07-28) ─────────────────────────────────────────
    // WPF'in HttpClient dayanıklılık katmanı 5xx/ağ hatasında POST'u da
    // yeniden deniyor. Gönderim faturalı ve gerçek müşteriye gidiyor →
    // aynı anahtarla gelen tekrar yeni mesaj YAZMAMALI.

    /// <summary>Verilen lisansa yazılmış giden mesaj sayısı.</summary>
    private async Task<int> CountMessagesAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaMessages.CountAsync(m => m.LicenseId == licenseId);
    }

    /// <summary>Lisansın tek rezervasyon satırını HER SEFERİNDE taze bir scope'tan
    /// okur: amaç kalıcılaşmış hâli görmek, isteğin DbContext'inde takip edilen
    /// örneği değil. <c>Single</c> aynı zamanda "tek satır" iddiasını da taşır.</summary>
    private async Task<WaSendAttempt> SingleAttemptAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaSendAttempts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
    }

    /// <summary>Graph'a çıkmadan doğrudan rezervasyon satırı yazar.</summary>
    private async Task SeedAttemptAsync(Guid licenseId, Guid key, DateTimeOffset createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaSendAttempts.Add(new WaSendAttempt
        {
            Id = key,
            LicenseId = licenseId,
            Status = "pending",
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Same_idempotency_key_sends_only_once()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551110001");

        var key = Guid.NewGuid();
        var body = new
        {
            toPhone = "905551110001", text = "Ödemeniz bekleniyor.",
            origin = "wpf-payment", idempotencyKey = key,
        };

        var first = await client.PostAsJsonAsync(Url(licenseId), body);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = (await first.Content.ReadFromJsonAsync<SendResponse>())!;
        firstBody.Ok.Should().BeTrue();

        var second = await client.PostAsJsonAsync(Url(licenseId), body);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = (await second.Content.ReadFromJsonAsync<SendResponse>())!;
        secondBody.Should().BeEquivalentTo(firstBody);

        (await CountMessagesAsync(licenseId)).Should().Be(1);
    }

    [Fact]
    public async Task Different_idempotency_keys_send_twice()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551110002");

        for (var i = 0; i < 2; i++)
        {
            var resp = await client.PostAsJsonAsync(Url(licenseId), new
            {
                toPhone = "905551110002", text = $"mesaj {i}",
                idempotencyKey = Guid.NewGuid(),
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await CountMessagesAsync(licenseId)).Should().Be(2);
    }

    [Fact]
    public async Task Omitting_idempotency_key_sends_every_time()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551110003");

        var body = new { toPhone = "905551110003", text = "anahtarsız" };
        (await client.PostAsJsonAsync(Url(licenseId), body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(Url(licenseId), body)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CountMessagesAsync(licenseId)).Should().Be(2);
    }

    [Fact]
    public async Task Reports_in_progress_when_reservation_is_unfinished()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551110004");

        var key = Guid.NewGuid();
        await SeedAttemptAsync(licenseId, key, DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync(Url(licenseId), new
        {
            toPhone = "905551110004", text = "tekrar", idempotencyKey = key,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeFalse();
        body.ErrorCode.Should().Be("in_progress");
        (await CountMessagesAsync(licenseId)).Should().Be(0);
    }

    [Fact]
    public async Task Stale_reservation_is_taken_over()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551110005");

        // 5 dakikalık "pending" → istek yarıda kalmış sayılır, devralınır.
        var key = Guid.NewGuid();
        await SeedAttemptAsync(licenseId, key, DateTimeOffset.UtcNow.AddMinutes(-5));

        var resp = await client.PostAsJsonAsync(Url(licenseId), new
        {
            toPhone = "905551110005", text = "devralındı", idempotencyKey = key,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SendResponse>())!.Ok.Should().BeTrue();
        (await CountMessagesAsync(licenseId)).Should().Be(1);
    }

    [Fact]
    public async Task Idempotency_key_belonging_to_another_license_returns_404()
    {
        var (_, otherLicenseId) = await SeedAsync();
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, "905551110006");

        var key = Guid.NewGuid();
        await SeedAttemptAsync(otherLicenseId, key, DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync(Url(licenseId), new
        {
            toPhone = "905551110006", text = "başkasının anahtarı", idempotencyKey = key,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountMessagesAsync(licenseId)).Should().Be(0);
    }

    [Fact]
    public async Task Replays_failure_outcome_for_same_key()
    {
        var (client, licenseId) = await SeedAsync();
        await ConnectAccountAsync(licenseId);
        // Pencere açılmıyor → ilk çağrı window_closed döner.

        var key = Guid.NewGuid();
        var body = new { toPhone = "905551110007", text = "kapalı pencere", idempotencyKey = key };

        var first = (await (await client.PostAsJsonAsync(Url(licenseId), body))
            .Content.ReadFromJsonAsync<SendResponse>())!;
        first.Ok.Should().BeFalse();
        first.ErrorCode.Should().Be("window_closed");

        // Sonucun rezervasyon satırına işlendiği an. Testin taşıyıcı iddiası
        // bunun DEĞİŞMEMESİ: aşağıdaki gövde ve mesaj-sayısı kontrolleri tek
        // başına hiçbir şey kanıtlamıyor, çünkü pencere hâlâ kapalı olduğu için
        // GERÇEK bir ikinci deneme de birebir aynı window_closed gövdesini döner
        // ve yine 0 mesaj yazar. Tekrar-oynatmayı yeniden-denemeden ayıran tek
        // iz bu damga: ikinci kez Graph yoluna girilseydi sonuç aynı satıra daha
        // yeni bir CompletedAt ile üzerine yazılırdı.
        var afterFirst = await SingleAttemptAsync(licenseId);
        afterFirst.Status.Should().Be("done");
        afterFirst.Ok.Should().BeFalse();
        afterFirst.ErrorCode.Should().Be("window_closed");
        afterFirst.CompletedAt.Should().NotBeNull();
        var completedAt = afterFirst.CompletedAt!.Value;

        // Hata da tekrar oynatılır — sessizce yeniden denenmez.
        var second = (await (await client.PostAsJsonAsync(Url(licenseId), body))
            .Content.ReadFromJsonAsync<SendResponse>())!;
        second.Should().BeEquivalentTo(first);

        (await CountMessagesAsync(licenseId)).Should().Be(0);

        // Hâlâ tek rezervasyon satırı ve damga ilk çağrıdaki an olarak duruyor.
        var afterSecond = await SingleAttemptAsync(licenseId);
        afterSecond.Status.Should().Be("done");
        afterSecond.CompletedAt.Should().Be(completedAt);
    }
}
