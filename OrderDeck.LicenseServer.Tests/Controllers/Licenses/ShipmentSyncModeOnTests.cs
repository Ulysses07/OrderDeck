using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>shipments/sync</c> mod On'da — spec 2026-10-13 §7.1, §8.3, §4.0, §8.7: dosya ve etiket–dosya bağı sunucunundur;
/// protokolsüz (eski sürüm) ve açılış penceresi dışındaki v2 gönderimi hiçbir şey yazmaz (ne güncelleme ne ekleme),
/// yanıt sunucunun güncel satırlarıdır. Protokolsüz isteğin izi yine yazılır. Mod Off'taki davranış (S1)
/// <see cref="ShipmentSyncV2Tests"/>, <see cref="LicensesShipmentsSyncControllerTests"/> ve
/// <see cref="ShipmentShippedOneWayRelationalTests"/>'te.
/// </summary>
public sealed class ShipmentSyncModeOnTests : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;

    public ShipmentSyncModeOnTests(ApiFactory factory) => _factory = factory;

    // ── Açılış penceresi dışı: yok sayılır ─────────────────────────────────────

    /// <summary>
    /// Mod On'da protokolsüz gönderim (§8.3 tablosu): değişen durum ve tutar yazılmaz, yeni dosya Id'si eklenmez, sürüm
    /// ilerlemez. Yanıt bugünkü DTO biçiminde sunucunun satırlarıdır (eklenmeyen Id yok). Protokolsüz istek izi yazılır.
    /// </summary>
    [Fact]
    public async Task Mod_On_v1_gonderimi_hicbir_sey_yazmaz_iz_yazilir_yanit_sunucu_satirlari()
    {
        var (client, licenseId) = await SetupAsync();
        var existing = await SeedShipmentAsync(licenseId);
        var inserted = NewItem();
        string[] dtoFields = ["id", "customerId", "status", "cumulativeAmount", "createdAt", "heldAt", "shippedAt", "updatedAt"];

        var resp = await client.PostAsJsonAsync(Url(licenseId), new
        {
            shipments = new[]
            {
                ItemOf(existing) with { Status = "held", CumulativeAmount = 250m, HeldAt = CreatedAt.AddDays(1) },
                inserted,
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray().Should().ContainSingle("eklenmeyen Id yanıtta yok").Subject;
        row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(dtoFields, "v1 yanıtı bugünkü DTO");
        row.GetProperty("id").GetGuid().Should().Be(existing.Id);
        row.GetProperty("status").GetString().Should().Be("pending");
        row.GetProperty("cumulativeAmount").GetDecimal().Should().Be(100m);

        await AssertUnchangedAsync(existing);
        (await ShipmentOrNullAsync(inserted.Id)).Should().BeNull("mod On'da yeni dosyayı yalnız sunucu açar");
        (await LegacyTraceAsync(licenseId)).Should().NotBeNull("protokolsüz istek izi iki kipte yazılır");
    }

    public static TheoryData<string> OutsideWindow => new()
    {
        "geri-yuklenmis", "modu-gormus", "klon", "mod-sonrasi-dogmus", "seenMode-1",
    };

    /// <summary>
    /// Açılış penceresi dışındaki v2 gönderimi (§4.0, §8.7, M21) On kurallarıyla yargılanır: yedekten geri yüklenmiş
    /// kopya (<c>PreviousReplicaId</c>), bu açılıştan beri modu görmüş kopya (<c>SeenModeOnAt</c> mod değişiminden
    /// yeni), disk klonu (ikinci parmak izi), mod açıldıktan sonra doğmuş kopya — hepsi <c>SeenMode = 0</c> bildirse de —
    /// ve modu gören kopya (<c>SeenMode = 1</c>). Hiçbir şey yazılmaz; yanıt sunucunun güncel satırı (akış öğesi).
    /// </summary>
    [Theory]
    [MemberData(nameof(OutsideWindow))]
    public async Task Mod_On_v2_pencere_disi_gonderim_yok_sayilir(string kind)
    {
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var (client, licenseId) = await SetupAsync(modeChangedAt);
        var replica = Guid.NewGuid();
        var beforeMode = modeChangedAt.AddDays(-2);
        switch (kind)
        {
            case "geri-yuklenmis":
                await SeedReplicaAsync(licenseId, replica, beforeMode, r => r.PreviousReplicaId = Guid.NewGuid());
                break;
            case "modu-gormus":
                await SeedReplicaAsync(licenseId, replica, beforeMode, r => r.SeenModeOnAt = modeChangedAt.AddMinutes(5));
                break;
            case "klon":
                await SeedReplicaAsync(licenseId, replica, beforeMode,
                    r => r.HardwareFingerprintConflictAt = modeChangedAt.AddMinutes(-10));
                break;
            case "mod-sonrasi-dogmus":
                break;
            case "seenMode-1":
                await SeedReplicaAsync(licenseId, replica, beforeMode);
                break;
        }
        var existing = await SeedShipmentAsync(licenseId);
        var inserted = NewItem();

        var rows = await PushV2Async(client, licenseId, replica, seenMode: kind == "seenMode-1" ? 1 : 0,
            ItemOf(existing) with { Status = "held", CumulativeAmount = 9999m, HeldAt = CreatedAt.AddDays(1) },
            inserted);

        var row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(existing.Id);
        row.Status.Should().Be("pending");
        row.CumulativeAmount.Should().Be(100m);
        row.Version.Should().Be(existing.Version);
        row.OriginReplicaId.Should().BeNull("boş kaynak da doldurulmaz");
        await AssertUnchangedAsync(existing);
        (await ShipmentOrNullAsync(inserted.Id)).Should().BeNull();
    }

    /// <summary>Mod On'da protokolsüz gönderimin durum değişikliği olay değildir: yazılmadı, etiket kuralı
    /// tetiklenmez.</summary>
    [Fact]
    public async Task Mod_On_v1_durum_degisikligi_etiket_kuralini_tetiklemez()
    {
        var (client, licenseId) = await SetupAsync();
        var (conversationId, wpfCustomer) = await SeedLabelRuleAsync(licenseId);
        var existing = await SeedShipmentAsync(licenseId, s => s.CustomerId = wpfCustomer);

        (await client.PostAsJsonAsync(Url(licenseId), new { shipments = new[] { ItemOf(existing) with { Status = "shipped" } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LabelCountAsync(conversationId)).Should().Be(0);
        await AssertUnchangedAsync(existing);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    internal sealed record Item(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt);

    private static Item NewItem() =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "pending", 9999m, CreatedAt, null, null);

    private static Item ItemOf(Shipment s) => new(
        s.Id, s.CustomerId, s.Status.ToString().ToLowerInvariant(), s.CumulativeAmount, s.CreatedAt, s.HeldAt,
        s.ShippedAt);

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/shipments/sync";

    private static async Task<List<SalesShipmentItem>> PushV2Async(
        HttpClient client, Guid licenseId, Guid replicaId, int seenMode, params Item[] items)
    {
        var resp = await client.PostAsJsonAsync(Url(licenseId),
            new { shipments = items, replicaId, protocol = 2, seenMode });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<SalesShipmentItem>>())!;
    }

    /// <summary>Dosyanın karar, tutar, kaynak, müşteri ve sürüm alanları tohumdaki gibi.</summary>
    private async Task AssertUnchangedAsync(Shipment seeded)
    {
        var stored = await ShipmentOrNullAsync(seeded.Id);
        stored.Should().NotBeNull();
        stored!.Should().BeEquivalentTo(new
        {
            seeded.Status,
            seeded.CumulativeAmount,
            seeded.HeldAt,
            seeded.ShippedAt,
            seeded.CustomerId,
            seeded.OriginReplicaId,
            seeded.Version,
            seeded.UpdatedAt,
        });
    }

    private async Task<Shipment> SeedShipmentAsync(Guid licenseId, Action<Shipment>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Status = ShipmentStatus.Pending,
            CumulativeAmount = 100m,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 3,
            AmountDerivedAt = CreatedAt,
        };
        change?.Invoke(shipment);
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private async Task SeedReplicaAsync(
        Guid licenseId, Guid replicaId, DateTimeOffset firstSeenAt, Action<WpfReplica>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var replica = new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = firstSeenAt,
        };
        change?.Invoke(replica);
        db.WpfReplicas.Add(replica);
        await db.SaveChangesAsync();
    }

    /// <summary>Telefonu olan bir müşteri, onun sohbeti ve <c>ShipmentStatusChanged</c> kuralı
    /// (<see cref="ShipmentSyncLabelTests"/> deseni). Müşterinin "N" biçimli Id'si döner.</summary>
    private async Task<(Guid ConversationId, string WpfCustomerId)> SeedLabelRuleAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var wpfCustomerId = Guid.NewGuid();
        var phone = TestPhone.NewE164();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId, LicenseId = licenseId, Platform = "youtube", Username = $"u-{Guid.NewGuid():N}",
            Phone = phone, UpdatedAt = DateTimeOffset.UtcNow,
        });
        var conversation = new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerPhone = phone[1..], PhoneNumberId = "PNID_1",
            Status = "open", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaConversations.Add(conversation);
        var label = new WaLabel
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Name = "Kargo durumu değişti", Color = "#3b82f6",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, EventKey = WaLabelEvent.ShipmentStatusChanged,
            WaLabelId = label.Id, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (conversation.Id, wpfCustomerId.ToString("N"));
    }

    private async Task<int> LabelCountAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId);
    }

    private async Task<Shipment?> ShipmentOrNullAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
    }

    private async Task<DateTimeOffset?> LegacyTraceAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == licenseId)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync();
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(DateTimeOffset? modeChangedAt = null)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-KARGO-ON-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = SalesSyncMode.On,
            SalesSyncModeChangedAt = modeChangedAt ?? DateTimeOffset.UtcNow.AddHours(-1),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }
}
