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
/// <c>shipments/sync</c> mod On'da — spec 2026-10-13 §7.1, §8.3, §4.0, §8.4, §8.7: dosya ve etiket–dosya bağı
/// sunucunundur; protokolsüz (eski sürüm) ve açılış penceresi dışındaki v2 gönderimi hiçbir şey yazmaz (ne güncelleme ne
/// ekleme), yanıt sunucunun güncel satırlarıdır. Protokolsüz isteğin izi yine yazılır. Tek istisna açılış penceresindeki
/// kopyadır: Shipped olmayan, komutun dokunmadığı dosyanın kararı alınır, yeni dosya eklenir, tutar push'tan değil
/// bağlardan türetilir, kargoya geçişte iptalli bağ çözülür. Mod Off'taki davranış (S1)
/// <see cref="ShipmentSyncV2Tests"/>, <see cref="LicensesShipmentsSyncControllerTests"/> ve
/// <see cref="ShipmentShippedOneWayRelationalTests"/>'te; gerçek SQL Server kanıtı (§10.1-21)
/// <see cref="ShipmentSyncOpeningWindowRelationalTests"/>'te.
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

    // ── Açılış penceresi: kararlar alınır, tutar türetilir ─────────────────────

    /// <summary>
    /// Açılış penceresindeki kopyanın Hold kararı (§7.1, §8.4) alınır; push'taki tutar (9999) yok sayılır. Türetilmiş
    /// dosyanın tutarı bağlı, iptal edilmemiş, geçici yedek olmayan siparişlerin toplamıdır (DM7) — saklanan bayat 0
    /// değil. Kabul edilmiş (türetilmemiş) dosya kararı alır, saklanan tutarıyla kalır. Hold bağ çözmez; kaynak ve
    /// müşteri değişmez. Sürüm bir kez artar.
    /// </summary>
    [Fact]
    public async Task Pencere_Hold_karari_alinir_tutar_baglardan_turetilir_push_tutari_yok_sayilir()
    {
        var (client, licenseId, window) = await SetupWindowAsync();
        var derived = await SeedShipmentAsync(licenseId, s => s.CumulativeAmount = 0m);
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = derived.Id; o.Price = 100m; });
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = derived.Id; o.Price = 50m; });
        var cancelled = await SeedOrderAsync(licenseId, o =>
        {
            o.ShipmentId = derived.Id; o.Price = 30m; o.CancelledAt = CreatedAt.AddHours(2);
        });
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = derived.Id; o.Price = 20m; o.IsTentativeBackup = true; });
        var accepted = await SeedShipmentAsync(licenseId, s => { s.AmountDerivedAt = null; s.CumulativeAmount = 777m; });
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = accepted.Id; o.Price = 100m; });
        var heldAt = CreatedAt.AddDays(1);

        var rows = await PushV2Async(client, licenseId, window, seenMode: 0,
            ItemOf(derived) with { Status = "held", CumulativeAmount = 9999m, HeldAt = heldAt },
            ItemOf(accepted) with { Status = "held", CumulativeAmount = 9999m, HeldAt = heldAt });

        var savedDerived = await ShipmentOrNullAsync(derived.Id);
        savedDerived!.Should().BeEquivalentTo(new
        {
            Status = ShipmentStatus.Held,
            HeldAt = (DateTimeOffset?)heldAt,
            CumulativeAmount = 150m,
            Version = derived.Version + 1,
            derived.CustomerId,
            OriginReplicaId = (Guid?)null,
            CommandedAt = (DateTimeOffset?)null,
        });
        savedDerived.UpdatedAt.Should().BeAfter(derived.UpdatedAt);
        var savedAccepted = await ShipmentOrNullAsync(accepted.Id);
        savedAccepted!.Should().BeEquivalentTo(new
        {
            Status = ShipmentStatus.Held,
            HeldAt = (DateTimeOffset?)heldAt,
            CumulativeAmount = 777m,
            Version = accepted.Version + 1,
            AmountDerivedAt = (DateTimeOffset?)null,
        });
        (await OrderAsync(cancelled.Id)).ShipmentId.Should().Be(derived.Id, "Hold bağ çözmez (§7.2)");
        rows.Select(r => (r.Id, r.Status, r.CumulativeAmount)).Should().Equal(
            (derived.Id, "held", 150m), (accepted.Id, "held", 777m));
    }

    /// <summary>
    /// Komutun dokunduğu (<c>CommandedAt</c> dolu) dosyaya pencere dokunmaz: modu görmeden verilmiş karar, sonradan
    /// verilmiş sunucu kararını geri almaz (§7.1, §8.4). Kargolanmış dosya da tek yönlüdür. Aynı gönderimdeki öbür dosya
    /// kararını alır (pencere etkin).
    /// </summary>
    [Fact]
    public async Task Pencere_CommandedAt_ve_kargolanmis_dosyaya_dokunmaz()
    {
        var (client, licenseId, window) = await SetupWindowAsync();
        var commanded = await SeedShipmentAsync(licenseId, s =>
        {
            s.Status = ShipmentStatus.Held; s.HeldAt = CreatedAt.AddHours(1); s.CommandedAt = CreatedAt.AddHours(1);
        });
        var shipped = await SeedShipmentAsync(licenseId, s =>
        {
            s.Status = ShipmentStatus.Shipped; s.ShippedAt = CreatedAt.AddHours(3);
        });
        var plain = await SeedShipmentAsync(licenseId);

        var rows = await PushV2Async(client, licenseId, window, seenMode: 0,
            ItemOf(commanded) with { Status = "pending", HeldAt = null, CumulativeAmount = 9999m },
            ItemOf(shipped) with { Status = "held", ShippedAt = null, HeldAt = CreatedAt.AddDays(1) },
            ItemOf(plain) with { Status = "recipientpays" });

        await AssertUnchangedAsync(commanded);
        await AssertUnchangedAsync(shipped);
        (await ShipmentOrNullAsync(plain.Id))!.Status.Should().Be(ShipmentStatus.RecipientPays);
        rows.Select(r => r.Status).Should().Equal("held", "shipped", "recipientpays");
    }

    /// <summary>
    /// Pencerede kargoya geçiş (§7.2, §7.1): o anda iptal edilmiş bağlı siparişin bağı çözülür (<c>ShipmentId</c> boş,
    /// <c>ShipmentSetAt</c> sunucu saati, sürüm bir artar), tutar onsuz türetilip donar — push'taki tutar değil.
    /// İptal edilmemiş sipariş bağlı kalır ve yazılmaz.
    /// </summary>
    [Fact]
    public async Task Pencere_kargoya_geciste_iptalli_bag_cozulur_tutar_donar()
    {
        var (client, licenseId, window) = await SetupWindowAsync();
        var file = await SeedShipmentAsync(licenseId, s =>
        {
            s.Status = ShipmentStatus.Held; s.HeldAt = CreatedAt.AddHours(1); s.CumulativeAmount = 140m;
        });
        var live = await SeedOrderAsync(licenseId, o => { o.ShipmentId = file.Id; o.Price = 100m; o.SyncVersion = 2; });
        var cancelled = await SeedOrderAsync(licenseId, o =>
        {
            o.ShipmentId = file.Id; o.Price = 40m; o.CancelledAt = CreatedAt.AddHours(2); o.SyncVersion = 2;
        });
        var shippedAt = CreatedAt.AddDays(1);
        var before = DateTimeOffset.UtcNow;

        var row = (await PushV2Async(client, licenseId, window, seenMode: 0,
            ItemOf(file) with { Status = "shipped", ShippedAt = shippedAt, CumulativeAmount = 9999m })).Single();

        var saved = await ShipmentOrNullAsync(file.Id);
        saved!.Should().BeEquivalentTo(new
        {
            Status = ShipmentStatus.Shipped,
            ShippedAt = (DateTimeOffset?)shippedAt,
            CumulativeAmount = 100m,
            Version = file.Version + 1,
        });
        row.CumulativeAmount.Should().Be(100m);
        var unlinked = await OrderAsync(cancelled.Id);
        unlinked.ShipmentId.Should().BeNull("ürün kolide değil");
        unlinked.ShipmentSetAt.Should().BeOnOrAfter(before);
        unlinked.SyncVersion.Should().Be(3);
        unlinked.UpdatedAt.Should().BeOnOrAfter(before);
        var kept = await OrderAsync(live.Id);
        kept.ShipmentId.Should().Be(file.Id);
        kept.ShipmentSetAt.Should().BeNull();
        kept.SyncVersion.Should().Be(2);
    }

    /// <summary>
    /// Pencerede kabul edilmiş (türetilmemiş, §4.10-6) dosyanın kargoya geçişi: iptalli bağ çözülmez — saklanan tutar
    /// iptalle düşmediği için o siparişi içerir; bağı çözülseydi geri alınınca sonraki dosyaya girer ve aynı tutar iki
    /// dosyada sayılırdı (§7.2). Tutar saklanan değeriyle donar, push'taki değer alınmaz.
    /// </summary>
    [Fact]
    public async Task Pencere_kabul_edilmis_dosyayi_kargolarken_iptalli_bagi_cozmez()
    {
        var (client, licenseId, window) = await SetupWindowAsync();
        var accepted = await SeedShipmentAsync(licenseId, s =>
        {
            s.AmountDerivedAt = null; s.Status = ShipmentStatus.Held; s.HeldAt = CreatedAt.AddHours(1);
            s.CumulativeAmount = 550m;
        });
        var cancelled = await SeedOrderAsync(licenseId, o =>
        {
            o.ShipmentId = accepted.Id; o.Price = 250m; o.CancelledAt = CreatedAt.AddHours(2); o.SyncVersion = 2;
        });

        var row = (await PushV2Async(client, licenseId, window, seenMode: 0,
            ItemOf(accepted) with { Status = "shipped", ShippedAt = CreatedAt.AddDays(1), CumulativeAmount = 9999m }))
            .Single();

        row.Should().BeEquivalentTo(new { Status = "shipped", CumulativeAmount = 550m });
        var kept = await OrderAsync(cancelled.Id);
        kept.ShipmentId.Should().Be(accepted.Id, "kabul edilmiş dosyanın tutarı bu siparişi içeriyor");
        kept.ShipmentSetAt.Should().BeNull();
        kept.SyncVersion.Should().Be(2);
    }

    /// <summary>
    /// Pencerede yeni dosya Id'si eklenir (§7.1): türetilmiş (<c>AmountDerivedAt</c> dolu), kaynağı çağıran kopya,
    /// tutarı o anda bağlı siparişlerden — push'tan değil. Bağı henüz olmayan dosya 0 ile doğar; kararı alınır.
    /// </summary>
    [Fact]
    public async Task Pencere_yeni_dosya_turetilmis_eklenir_kaynak_cagiran()
    {
        var (client, licenseId, window) = await SetupWindowAsync();
        var linkedFirst = NewItem() with { Status = "pending" };
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = linkedFirst.Id; o.Price = 70m; });
        await SeedOrderAsync(licenseId, o =>
        {
            o.ShipmentId = linkedFirst.Id; o.Price = 30m; o.CancelledAt = CreatedAt.AddHours(2);
        });
        var bare = NewItem() with { Status = "held", HeldAt = CreatedAt.AddDays(1) };
        var before = DateTimeOffset.UtcNow;

        var rows = await PushV2Async(client, licenseId, window, seenMode: 0, linkedFirst, bare);

        rows.Select(r => r.Id).Should().Equal(linkedFirst.Id, bare.Id);
        var first = await ShipmentOrNullAsync(linkedFirst.Id);
        first!.Should().BeEquivalentTo(new
        {
            LicenseId = licenseId,
            linkedFirst.CustomerId,
            Status = ShipmentStatus.Pending,
            CumulativeAmount = 70m,
            OriginReplicaId = (Guid?)window,
            CreatedAt = CreatedAt,
        });
        first.AmountDerivedAt.Should().BeOnOrAfter(before);
        var second = await ShipmentOrNullAsync(bare.Id);
        second!.Should().BeEquivalentTo(new
        {
            Status = ShipmentStatus.Held,
            HeldAt = bare.HeldAt,
            CumulativeAmount = 0m,
            OriginReplicaId = (Guid?)window,
        });
        second.AmountDerivedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Pencerede durum gerçekten değiştiyse <c>ShipmentStatusChanged</c> etiket kuralı bir kez tetiklenir; aynı kararın
    /// tekrarı ya da yalnız tutarı farklı satır olay değildir.
    /// </summary>
    [Fact]
    public async Task Pencere_durum_degisince_etiket_kurali_bir_kez_tetiklenir()
    {
        var (client, licenseId, window) = await SetupWindowAsync();
        var (conversationId, wpfCustomer) = await SeedLabelRuleAsync(licenseId);
        var file = await SeedShipmentAsync(licenseId, s => s.CustomerId = wpfCustomer);
        var held = ItemOf(file) with { Status = "held", HeldAt = CreatedAt.AddDays(1) };

        await PushV2Async(client, licenseId, window, seenMode: 0, held);
        (await LabelCountAsync(conversationId)).Should().Be(1);

        await ClearLabelsAsync(conversationId);
        await PushV2Async(client, licenseId, window, seenMode: 0, held);
        await PushV2Async(client, licenseId, window, seenMode: 0, held with { CumulativeAmount = 9999m });
        (await LabelCountAsync(conversationId)).Should().Be(0, "durum değişmedi");
    }

    /// <summary>Pencere yolunda da sürüm jetonu: kaydetmede eşzamanlı yazıma çarpan gönderim 409 <c>sync-conflict</c>
    /// alır (500 değil), hiçbir şey yazılmaz. Gerçek jetonun kanıtı ilişkisel testlerde.</summary>
    [Fact]
    public async Task Pencere_yazim_cakismasi_409_sync_conflict()
    {
        using var factory = new ShipmentSyncV2Tests.ShipmentConflictFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        var licenseId = Guid.NewGuid();
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var window = Guid.NewGuid();
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = Guid.NewGuid().ToString("N"),
            CumulativeAmount = 100m, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, AmountDerivedAt = CreatedAt,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = licenseId, LicenseKey = $"pencere-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
                ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                SalesSyncMode = SalesSyncMode.On, SalesSyncModeChangedAt = modeChangedAt,
            });
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = licenseId, ReplicaId = window, Protocol = 2,
                FirstSeenAt = modeChangedAt.AddDays(-2), LastSeenAt = modeChangedAt.AddDays(-2),
            });
            db.Shipments.Add(shipment);
            await db.SaveChangesAsync();
        }
        factory.Armed = true;

        var resp = await client.PostAsJsonAsync(Url(licenseId), new
        {
            shipments = new[] { ItemOf(shipment) with { Status = "held" } },
            replicaId = window, protocol = 2, seenMode = 0,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("sync-conflict");
        factory.Thrown.Should().Be(1, "pencere yolu dosyaya yazmayı denedi");
        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipment.Id)).Status
            .Should().Be(ShipmentStatus.Pending);
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

    private async Task ClearLabelsAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaConversationLabels.RemoveRange(
            await db.WaConversationLabels.Where(x => x.ConversationId == conversationId).ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>Mod On lisansı ve açılış penceresindeki bir kopya: mod açılmadan önce görülmüş, geri yüklenmemiş, klon
    /// izi yok, modu görmemiş (§4.0).</summary>
    private async Task<(HttpClient Client, Guid LicenseId, Guid Window)> SetupWindowAsync()
    {
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var (client, licenseId) = await SetupAsync(modeChangedAt);
        var window = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, window, modeChangedAt.AddDays(-2));
        return (client, licenseId, window);
    }

    private async Task<Order> SeedOrderAsync(Guid licenseId, Action<Order>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Platform = "youtube",
            Username = $"u-{Guid.NewGuid():N}",
            MessageText = "A1",
            Code = "A1",
            Price = 100m,
            AddedAt = CreatedAt,
            PrintedAt = CreatedAt.AddMinutes(1),
            UpdatedAt = CreatedAt,
        };
        change?.Invoke(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Order> OrderAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
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
