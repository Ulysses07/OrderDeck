using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// F09 (denetim 2026-09-09): aynı ClientRequestId ile eşzamanlı kampanya
/// Create istekleri TEK kampanya + TEK alıcı seti yazmalı (kredi sistemi
/// emekli — çift kredi düşümü iddiası öldü, atomiklik iddiası yaşıyor).
///
/// Gerçek SQL Server şart: koruma (LicenseId, ClientRequestId) filtreli
/// unique index'ine dayanıyor ve InMemory unique index uygulamaz — ön
/// kontrolü aynı anda geçen iki isteğin yarışı orada hiç görünmez.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SmsCampaignIdempotencyConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 4;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public SmsCampaignIdempotencyConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record CreateResponse(Guid CampaignId, int RecipientCount, int TotalCredits);

    [Fact]
    public async Task Ayni_anahtarla_eszamanli_istekler_tek_kampanya_acar()
    {
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = await SeedAsync(customerId);

        // İstemciler döngüden önce kuruluyor; CreateClient gecikmesi istekleri
        // ayırıp yarış penceresini daraltırdı (bkz. PanelPaymentDecisionConcurrencyTests).
        var clients = Enumerable.Range(0, ParallelAttempts)
            .Select(_ =>
            {
                var c = _factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                return c;
            })
            .ToArray();

        // Isınma: yabancı lisansa istek — yönlendirme + model bağlama +
        // EF sorgu yolu JIT edilsin.
        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/v1/licenses/{Guid.NewGuid()}/sms-campaigns",
            new { messageBody = "isinma", clientRequestId = Guid.NewGuid() })));

        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns",
            new { messageBody = "Indirim!", clientRequestId = key })));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
            "retry'lar da dahil hepsi aynı kampanyanın yanıtını almalı");
        var bodies = await Task.WhenAll(responses.Select(r =>
            r.Content.ReadFromJsonAsync<CreateResponse>()));
        bodies.Select(b => b!.CampaignId).Distinct().Should().HaveCount(1,
            "aynı ClientRequestId tek kampanyaya çözülmeli");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.SmsCampaigns.CountAsync(c => c.LicenseId == licenseId))
            .Should().Be(1, "yarışta ikinci kampanya satırı açılmamalı");
        // Kaybeden isteğin SaveChanges'i atomik geri alındı: alıcı satırları
        // da yalnız kazanan kampanya için, tek set olarak yazılmış olmalı.
        var winnerId = bodies[0]!.CampaignId;
        (await db.SmsCampaignRecipients.CountAsync(r => r.CampaignId == winnerId))
            .Should().Be(2, "yarışta alıcı seti tek kez yazılmalı");
    }

    private async Task<Guid> SeedAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "smsi-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        for (var i = 0; i < 2; i++)
        {
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "S " + Guid.NewGuid().ToString("N")[..6],
                Phone = "+90500" + Guid.NewGuid().ToString("N")[..7],
                PasswordHash = "hash",
                Address = "addr",
                SmsConsent = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Shoppers.Add(shopper);
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopper.Id,
                LicenseId = license.Id,
                Platform = "youtube",
                Username = "u-" + Guid.NewGuid().ToString("N")[..8],
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        // Kampanya kapısı (§3.2): Create doğrulanmış NetgsmAccount ister.
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            // UserCode kolonu 32 karakter — InMemory yutar, gerçek SQL keser.
            UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100000, 999999).ToString(),
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return license.Id;
    }
}
