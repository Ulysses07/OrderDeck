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

/// <summary>
/// PR-D (2026-05-13): Mobile Panel Shipment read endpoint integration tests.
/// Read-only — kararlar WPF tarafında, Panel sadece listeler.
/// </summary>
public class PanelShipmentsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelShipmentsControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record ShipmentDto(
        Guid Id, Guid LicenseId, string CustomerId, string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt,
        string? CustomerUsername = null, string? CustomerDisplayName = null,
        string? CustomerPlatform = null);

    private async Task<(HttpClient client, Guid licenseId)> SeedFixtureAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PANEL-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = now,
                ExpiresAt = now.AddDays(30)
            };
            db.Licenses.Add(license);

            db.Shipments.AddRange(
                new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = "ca",
                    Status = ShipmentStatus.Held, CumulativeAmount = 3000m,
                    CreatedAt = now.AddHours(-2), HeldAt = now.AddHours(-1), UpdatedAt = now.AddMinutes(-30) },
                new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = "cb",
                    Status = ShipmentStatus.RecipientPays, CumulativeAmount = 800m,
                    CreatedAt = now.AddHours(-3), UpdatedAt = now.AddMinutes(-20) },
                new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = "ca",
                    Status = ShipmentStatus.Shipped, CumulativeAmount = 7500m,
                    CreatedAt = now.AddDays(-1), ShippedAt = now.AddHours(-5), UpdatedAt = now.AddHours(-5) });

            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, licenseId);
    }

    [Fact]
    public async Task List_returns_all_shipments_for_caller()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();
        body.Should().HaveCount(3);
    }

    [Fact]
    public async Task List_filters_by_status_held()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?status=held");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();

        body.Should().HaveCount(1);
        body![0].Status.Should().Be("held");
    }

    [Fact]
    public async Task List_filters_by_status_recipientpays()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?status=recipientpays");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();

        body.Should().HaveCount(1);
        body![0].Status.Should().Be("recipientpays");
    }

    [Fact]
    public async Task List_filters_by_customerId()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?customerId=ca");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();

        body.Should().HaveCount(2);
        body!.Should().OnlyContain(s => s.CustomerId == "ca");
    }

    [Fact]
    public async Task List_invalid_status_returns_400()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?status=garbage");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_isolates_tenants_other_users_shipments_hidden()
    {
        // Bizim shipment'larımızı seed et
        var (client, myLicense) = await SeedFixtureAsync();

        // Başka bir customer'ın lisansı ve shipment'ı
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var otherCustomer = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"other-{Guid.NewGuid():N}@example.com",
                EmailConfirmedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var otherLicense = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-OTHER-" + Guid.NewGuid().ToString("N"),
                CustomerId = otherCustomer.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Customers.Add(otherCustomer);
            db.Licenses.Add(otherLicense);
            db.Shipments.Add(new Shipment
            {
                Id = Guid.NewGuid(),
                LicenseId = otherLicense.Id,
                CustomerId = "stranger",
                Status = ShipmentStatus.Held,
                CumulativeAmount = 9999m,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/shipments");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();
        body!.Should().OnlyContain(s => s.LicenseId == myLicense);
    }

    // ── Müşteri adı (Gelen Ödemeler, #528): panelin "Bekleyen Kargolar" /
    //    "Alıcı Ödemeli" listeleri artık kargo dosyalarından okunuyor ve
    //    müşteriyi Id yerine adıyla göstermeli.

    /// <summary>Her satır kendi müşterisinin projeksiyonundan kullanıcı adı +
    /// takma ad + platform alır; takma adı olmayan müşteride takma ad null
    /// (panel kullanıcı adına düşer). Yalnız ad gider: telefon, form adı
    /// (FullName) yanıtta yok.</summary>
    [Fact]
    public async Task List_returns_customer_name_fields_from_projection()
    {
        var (client, _, licenseId) = await SeedLicenseAsync();
        var namedId = Guid.NewGuid();
        var plainId = Guid.NewGuid();
        var named = Projection(licenseId, namedId, "@alici", "Alıcı Takma");
        var plain = Projection(licenseId, plainId, "@sade", displayName: null, platform: "tiktok");
        await AddAsync(named, plain,
            HeldShipment(licenseId, namedId.ToString("N")),
            HeldShipment(licenseId, plainId.ToString("N")));

        var raw = await client.GetStringAsync("/api/panel/shipments");
        var rows = JsonSerializer.Deserialize<List<ShipmentDto>>(raw, JsonSerializerOptions.Web)!;

        rows.Should().HaveCount(2);
        var namedRow = rows.Single(r => r.CustomerId == namedId.ToString("N"));
        namedRow.CustomerUsername.Should().Be("@alici");
        namedRow.CustomerDisplayName.Should().Be("Alıcı Takma");
        namedRow.CustomerPlatform.Should().Be("instagram");
        var plainRow = rows.Single(r => r.CustomerId == plainId.ToString("N"));
        plainRow.CustomerUsername.Should().Be("@sade");
        plainRow.CustomerDisplayName.Should().BeNull();
        plainRow.CustomerPlatform.Should().Be("tiktok");

        raw.Should().Contain("\"customerUsername\"", "sözleşme camelCase");
        raw.Should().NotContain(named.Phone!).And.NotContain(plain.Phone!)
            .And.NotContain(named.FullName!);
    }

    /// <summary>Projeksiyonu sunucuya hiç gelmemiş müşteri ve GUID olmayan eski
    /// yerel Id: hata değil, üç alan da null (panel Id'ye düşer).</summary>
    [Fact]
    public async Task List_customer_fields_null_when_projection_missing_or_id_not_guid()
    {
        var (client, _, licenseId) = await SeedLicenseAsync();
        await AddAsync(
            HeldShipment(licenseId, Guid.NewGuid().ToString("N")),
            HeldShipment(licenseId, "eski-yerel-id"));

        var rows = await client.GetFromJsonAsync<List<ShipmentDto>>("/api/panel/shipments");

        rows.Should().HaveCount(2);
        rows!.Should().OnlyContain(r => HasNoCustomerFields(r));
    }

    /// <summary>KVKK ile silinmiş müşteri: kullanıcı adı kimlik olarak
    /// projeksiyonda KALIR, ama panele hiçbiri gitmez.</summary>
    [Fact]
    public async Task List_customer_fields_null_when_projection_purged()
    {
        var (client, _, licenseId) = await SeedLicenseAsync();
        var id = Guid.NewGuid();
        var purged = Projection(licenseId, id, "@silinen", "Silinen Takma");
        purged.MarkPurged(DateTimeOffset.UtcNow);
        purged.Username.Should().Be("@silinen", "ön koşul: kimlik silmede kalır");
        await AddAsync(purged, HeldShipment(licenseId, id.ToString("N")));

        var row = await GetSingleAsync(client, id.ToString("N"));

        HasNoCustomerFields(row).Should().BeTrue();
    }

    /// <summary>Aynı Id'li projeksiyon başka bir lisansa aitse (başka yayıncının
    /// ya da aynı yayıncının öbür lisansının) kullanılmaz: kargo dosyasının
    /// müşterisi yalnız kendi lisansında aranır.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_ignores_projection_of_another_license(bool sameTenant)
    {
        var (client, tenantId, licenseId) = await SeedLicenseAsync();
        var otherLicenseId = await AddLicenseAsync(sameTenant ? tenantId : await AddOtherTenantAsync());
        var id = Guid.NewGuid();
        await AddAsync(
            Projection(otherLicenseId, id, "@yabanci", "Yabancı Takma"),
            HeldShipment(licenseId, id.ToString("N")));

        var row = await GetSingleAsync(client, id.ToString("N"));

        row.LicenseId.Should().Be(licenseId);
        HasNoCustomerFields(row).Should().BeTrue();
    }

    /// <summary>Birleştirmeden sonra eski bir bilgisayar kargo dosyasını
    /// kopyanın Id'siyle göndermiş olabilir: ad asıl kayıttan gelir (kopyanın
    /// kişisel alanları boş). Satırın CustomerId'si değişmez.</summary>
    [Fact]
    public async Task List_merged_copy_id_resolves_to_canonical_name()
    {
        var (client, _, licenseId) = await SeedLicenseAsync();
        var canonicalId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        var copy = new WpfCustomerProjection
        {
            Id = copyId,
            LicenseId = licenseId,
            Platform = "instagram",
            Username = "@Asil",
            MergedIntoId = canonicalId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await AddAsync(
            Projection(licenseId, canonicalId, "@asil", "Asıl Takma"), copy,
            HeldShipment(licenseId, copyId.ToString("N")));

        var row = await GetSingleAsync(client, copyId.ToString("N"));

        row.CustomerId.Should().Be(copyId.ToString("N"));
        row.CustomerUsername.Should().Be("@asil");
        row.CustomerDisplayName.Should().Be("Asıl Takma");
        row.CustomerPlatform.Should().Be("instagram");
    }

    private static bool HasNoCustomerFields(ShipmentDto row) =>
        row.CustomerUsername is null && row.CustomerDisplayName is null && row.CustomerPlatform is null;

    private async Task<ShipmentDto> GetSingleAsync(HttpClient client, string customerId)
    {
        var rows = await client.GetFromJsonAsync<List<ShipmentDto>>(
            $"/api/panel/shipments?customerId={customerId}");
        return rows.Should().ContainSingle().Subject;
    }

    /// <summary>Kimliği doğrulanmış yayıncı + kargosuz bir lisansı.</summary>
    private async Task<(HttpClient client, Guid tenantId, Guid licenseId)> SeedLicenseAsync()
    {
        var (client, tenantId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        return (client, tenantId, await AddLicenseAsync(tenantId));
    }

    private async Task<Guid> AddLicenseAsync(Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = $"LDK-PANEL-{Guid.NewGuid():N}",
            CustomerId = tenantId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30)
        };
        await AddAsync(license);
        return license.Id;
    }

    private async Task<Guid> AddOtherTenantAsync()
    {
        var other = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"other-{Guid.NewGuid():N}@example.com",
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await AddAsync(other);
        return other.Id;
    }

    private async Task AddAsync(params object[] entities)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private static Shipment HeldShipment(Guid licenseId, string customerId)
    {
        var now = DateTimeOffset.UtcNow;
        return new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = customerId,
            Status = ShipmentStatus.Held,
            CumulativeAmount = 500m,
            CreatedAt = now,
            HeldAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Kişisel alanları dolu bir asıl kayıt — yanıta yalnız adın
    /// gittiğini doğrulamak için telefon ve form adı da taşır.</summary>
    private static WpfCustomerProjection Projection(Guid licenseId, Guid id, string username,
        string? displayName, string platform = "instagram") => new()
    {
        Id = id,
        LicenseId = licenseId,
        Platform = platform,
        Username = username,
        DisplayName = displayName,
        FullName = "Örnek Form Adı",
        Phone = TestPhone.NewE164(),
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
