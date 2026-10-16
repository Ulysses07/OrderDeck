using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync disable</c> — acil kapatma (spec 2026-10-13 §4.10 "disable", §3.1 <c>Shipments.OriginReplicaId</c>,
/// §8.5, §8.7, M19, §12 risk 15, §15-38, §10.1-25) gerçek SQL Server'da. Tohum mod On bir lisanstır: PC-B'nin açtığı ama
/// yalnız PC-A etiketleri taşıyan dosya, karışık dosya (A 300, B 200 + iptal edilmiş ve geçici yedek), eşit paylı
/// karışık dosya, oturum sahibi boş (siparişin kaynağı sayılır) dosya, geri yükleme zinciri A1→A2'deki dosya, sahibi
/// zaten doğru dosya, bağsız dosya, bağlı siparişinin sahibi bilinmeyen dosya, Shipped dosya ve aynı yayıncının başka
/// lisansındaki dosya. Kuru koşu hiçbir tabloya yazmaz, yeniden yazılacakları ve karışıkları listeler; <c>--apply</c>
/// modu 0 yapar, <c>SalesSyncModeChangedAt</c>'i ilerletir, yalnız sahibi değişen dosyaları yazar (sürüm + 1, ChangeSeq
/// ilerler, akış onları taşır); ikinci koşu etkisizdir. Çıktıda ad, kullanıcı adı, mesaj, müşteri, yayın başlığı ve
/// makine adı yok.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncDisableRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public SalesSyncDisableRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Kuru koşu ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Kuru koşu hiçbir satırı, rowversion dahil, değiştirmez. Sayılar: Shipped olmayan sekiz dosya, beşi yeniden
    /// yazılacak (PC-B'nin açtığı PC-A dosyası §15-38, karışık, eşit paylı karışık, oturum sahibi boş, geri yükleme
    /// zinciri), biri zaten doğru, ikisinin sahibi bilinmiyor (biri bağsız). Karışık dosyalar kopya başına bağlı tutarla
    /// listelenir. Shipped dosya ve başka lisansın dosyası çıktıda yok.
    /// </summary>
    [Fact]
    public async Task Kuru_kosu_yeniden_yazilacak_ve_karisik_dosyalari_listeler_hicbir_sey_yazmaz()
    {
        var seed = await SeedAsync();
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await DisableAsync(seed.LicenseId, apply: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before, "kuru koşu hiçbir satırı, rowversion dahil, değiştirmez");
        output.Should().Contain("KURU ÇALIŞTIRMA").And.Contain($"lisans {seed.LicenseId}").And.Contain("Mod: On");
        output.Should().Contain("Shipped olmayan dosya: 8; bağlı sipariş: 13 (sahibi bilinmeyen: 1)");

        Section(output, "Sahibi yeniden yazılacak dosya: 5").Should()
            .Contain($"{seed.Opened.Id}: {seed.B} → {seed.A}", "PC-B'nin açtığı, yalnız PC-A etiketli dosya PC-A'ya geçer")
            .And.Contain($"{seed.Mixed.Id}: {seed.B} → {seed.A}")
            .And.Contain($"{seed.Tie.Id}: {seed.Large} → {seed.Small}", "eşitlikte küçük ReplicaId")
            .And.Contain($"{seed.Legacy.Id}: {seed.B} → {seed.C}", "oturum sahibi boşsa siparişin kaynağı")
            .And.Contain($"{seed.Restored.Id}: {seed.A1} → {seed.A2}", "geri yükleme zinciri en yeni kopyaya çözülür");
        output.Should().Contain("Sahibi zaten doğru, değişmeyecek: 1");
        Section(output, "Bağlı siparişlerinin sahibi bilinmeyen, değişmeyecek: 2 (bağlı siparişi olmayan: 1)").Should()
            .Contain(seed.Empty.Id.ToString()).And.Contain(seed.UnknownOwner.Id.ToString());
        Section(output, "Karışık dosya (birden çok kopyanın etiketi): 2").Should()
            .Contain($"{seed.Mixed.Id} → {seed.A}: {seed.A}=300.00, {seed.B}=200.00",
                "iptal edilmiş ve geçici yedek bağlı tutara girmez (DM7)")
            .And.Contain($"{seed.Tie.Id} → {seed.Small}: {seed.Small}=150.00, {seed.Large}=150.00");
        output.Should().Contain("Kuru çalıştırma: hiçbir şey yazılmadı.");
        output.Should().NotContain(seed.Shipped.Id.ToString(), "Shipped dosya kapsam dışı")
            .And.NotContain(seed.Foreign.Id.ToString(), "başka lisansın dosyası kapsam dışı")
            .And.NotContain(seed.Correct.Id.ToString(), "sahibi zaten doğru dosya yalnız sayılır");
        AssertNoPersonalData(output, seed);
    }

    // ── --apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--apply</c>: mod 0, <c>SalesSyncModeChangedAt</c> şimdi (Off turunun tetiği, §8.5). Sahibi değişen beş dosyada
    /// <c>OriginReplicaId</c> yeni sahip, sürüm + 1, <c>UpdatedAt</c> = değişim anı, ChangeSeq ilerler; öbür kolonlar
    /// (kararlar, tutar, türetme ve komut damgası) aynı. Öbür dosyalar, Shipped dosya ve başka lisansın dosyası satır
    /// olarak aynı (ChangeSeq dahil); başka lisansın modu On kalır. Licenses ve Shipments dışında hiçbir tablo değişmez
    /// (sipariş birim damgaları, ödeme kiraları). <c>sales/changes</c> önceki imleçten tam olarak yeniden yazılan
    /// dosyaları yeni değerle taşır. İkinci koşu (kuru ve <c>--apply</c>) etkisizdir.
    /// </summary>
    [Fact]
    public async Task Apply_modu_kapatir_dosya_sahiplerini_yazar_akis_tasir_ikinci_kosu_etkisiz()
    {
        var seed = await SeedAsync();
        var shipmentsBefore = await ShipmentsAsync();
        var licenseBefore = await LicenseAsync(seed.LicenseId);
        var cursor = await FeedHorizonAsync(seed.Kit);
        var before = await TableFingerprintsAsync();
        var start = DateTimeOffset.UtcNow;

        var (exit, output, error) = await DisableAsync(seed.LicenseId, apply: true);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA").And.Contain("Sahibi yeniden yazılacak dosya: 5")
            .And.Contain("UYGULANDI: mod Off").And.Contain("sahibi yeniden yazılan dosya: 5");
        AssertNoPersonalData(output, seed);

        var license = await LicenseAsync(seed.LicenseId);
        license.SalesSyncMode.Should().Be(SalesSyncMode.Off);
        license.SalesSyncModeChangedAt.Should().BeAfter(licenseBefore.SalesSyncModeChangedAt!.Value)
            .And.BeOnOrAfter(start.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        var changedAt = license.SalesSyncModeChangedAt!.Value;
        output.Should().Contain($"SalesSyncModeChangedAt={U(changedAt)}");
        (await LicenseAsync(seed.Foreign.LicenseId)).SalesSyncMode.Should().Be(SalesSyncMode.On);

        var after = await ShipmentsAsync();
        var rewritten = new Dictionary<Guid, Guid>
        {
            [seed.Opened.Id] = seed.A,
            [seed.Mixed.Id] = seed.A,
            [seed.Tie.Id] = seed.Small,
            [seed.Legacy.Id] = seed.C,
            [seed.Restored.Id] = seed.A2,
        };
        foreach (var (id, owner) in rewritten)
        {
            var was = shipmentsBefore[id];
            after[id].Should().BeEquivalentTo(new
            {
                OriginReplicaId = (Guid?)owner,
                Version = was.Version + 1,
                UpdatedAt = changedAt,
            });
            after[id].ChangeSeq.Should().BeGreaterThan(was.ChangeSeq);
            after[id].Should().BeEquivalentTo(was, o => Columns(o)
                    .Excluding(s => s.OriginReplicaId)
                    .Excluding(s => s.Version)
                    .Excluding(s => s.UpdatedAt)
                    .Excluding(s => s.ChangeSeq),
                "kararlar, tutar, türetme ve komut damgası değişmez");
        }
        foreach (var id in new[] { seed.Correct.Id, seed.Empty.Id, seed.UnknownOwner.Id, seed.Shipped.Id, seed.Foreign.Id })
            after[id].Should().BeEquivalentTo(shipmentsBefore[id], Columns, "dosya yazılmaz (ChangeSeq dahil)");

        var changed = (await TableFingerprintsAsync())
            .Where(t => !before.TryGetValue(t.Key, out var was) || was != t.Value)
            .Select(t => t.Key);
        changed.Should().BeEquivalentTo(["Licenses", "Shipments"],
            "birim damgaları, siparişler, oturumlar ve ödeme kiraları değişmez");

        var page = await ChangesAsync(seed.Kit, cursor);
        page.Items.Should().OnlyContain(i => i.Kind == SalesChangeItem.KindShipment);
        page.Items.Select(i => i.Shipment!).Should().BeEquivalentTo(
            rewritten.Select(r => new { Id = r.Key, OriginReplicaId = (Guid?)r.Value, after[r.Key].Version }),
            "Off turunun son akışı yeniden yazılan dosyaları taşır (§8.5-1)");

        var fingerprints = await TableFingerprintsAsync();
        foreach (var apply in new[] { false, true })
        {
            var again = await DisableAsync(seed.LicenseId, apply);
            again.Exit.Should().Be(0, again.Error);
            again.Output.Should().Contain($"Mod zaten kapalı (Off, son değişim={U(changedAt)})")
                .And.Contain("hiçbir şey yazılmadı");
            again.Output.Should().NotContain("UYGULANDI");
        }
        (await TableFingerprintsAsync()).Should().Equal(fingerprints, "ikinci disable hiçbir şey yazmaz");
    }

    /// <summary>Mod zaten Off: kuru koşu ve <c>--apply</c> mesaj basar, hiçbir şey yazmaz, 0; dosyanın kaynağı (PC-A
    /// etiketli, PC-B'nin) değişmez. Bilinmeyen lisans 2, çıktı boş.</summary>
    [Fact]
    public async Task Mod_zaten_Off_ise_yazmaz_0_bilinmeyen_lisans_2()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var shipment = await AddShipmentAsync(kit, ShipmentStatus.Pending, 100m, kit.ReplicaId);
        await LinkAsync(kit, session, shipment, 100m);
        var before = await TableFingerprintsAsync();

        foreach (var apply in new[] { false, true })
        {
            var (exit, output, error) = await DisableAsync(kit.LicenseId, apply);
            exit.Should().Be(0, error);
            output.Should().Contain("Mod zaten kapalı (Off, son değişim=yok)").And.Contain("hiçbir şey yazılmadı");
            output.Should().NotContain(shipment.Id.ToString());
        }
        (await TableFingerprintsAsync()).Should().Equal(before);
        (await kit.ShipmentAsync(shipment.Id)).OriginReplicaId.Should().Be(kit.ReplicaId);

        var unknown = await DisableAsync(Guid.NewGuid(), apply: true);
        unknown.Exit.Should().Be(2);
        unknown.Error.Should().Contain("Lisans bulunamadı").And.Contain("Hiçbir şey yazılmadı");
        unknown.Output.Should().BeEmpty();
    }

    // ── Yarışlar ──────────────────────────────────────────────────────────────

    /// <summary>Okuma ile koşullu UPDATE arasında başka bir <c>disable</c> modu kapatırsa UPDATE satır bulmaz: geri
    /// alınır, 1, hiçbir dosya yazılmaz.</summary>
    [Fact]
    public async Task Apply_esanli_mod_degisikliginde_1_doner_dosya_yazmaz()
    {
        var seed = await SeedAsync();
        var licenseBefore = await LicenseAsync(seed.LicenseId);
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[SalesSyncMode]", StringComparison.Ordinal),
            before: () => seed.Kit.WithDbAsync(db => db.Licenses.Where(l => l.Id == seed.LicenseId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.SalesSyncMode, SalesSyncMode.Off))));
        var shipmentsBefore = await ShipmentsAsync();

        var (exit, output, error) = await HookedDisableAsync(hook, seed.LicenseId);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1);
        error.Should().Contain("Mod bu arada değişti").And.Contain("hiçbir şey yazılmadı");
        output.Should().NotContain("UYGULANDI");
        var after = await ShipmentsAsync();
        foreach (var id in shipmentsBefore.Keys)
            after[id].Should().BeEquivalentTo(shipmentsBefore[id], Columns);
        (await LicenseAsync(seed.LicenseId)).SalesSyncModeChangedAt.Should().Be(
            licenseBefore.SalesSyncModeChangedAt, "mod değişim anını CLI yazmadı");
    }

    /// <summary>Yeniden yazılacak bir dosya kaydetmeden önce başka bir yazımla değişirse sürüm jetonu çarpar: işlem —
    /// mod yazımı dahil — geri alınır, 1. Mod On ve değişim anı aynı kalır, hiçbir dosyanın kaynağı yazılmaz.</summary>
    [Fact]
    public async Task Apply_dosya_esanli_yazilirsa_isleme_geri_alinir_1_doner()
    {
        var seed = await SeedAsync();
        var licenseBefore = await LicenseAsync(seed.LicenseId);
        var hook = new BeforeShipmentSaveHook(() => seed.Kit.WithDbAsync(db => db.Shipments
            .Where(s => s.Id == seed.Opened.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Version, s => s.Version + 1))));
        var shipmentsBefore = await ShipmentsAsync();

        var (exit, output, error) = await HookedDisableAsync(hook, seed.LicenseId);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1);
        error.Should().Contain("bu arada yazıldı").And.Contain("yeniden çalıştır");
        output.Should().NotContain("UYGULANDI");
        var license = await LicenseAsync(seed.LicenseId);
        license.SalesSyncMode.Should().Be(SalesSyncMode.On);
        license.SalesSyncModeChangedAt.Should().Be(licenseBefore.SalesSyncModeChangedAt);
        var after = await ShipmentsAsync();
        after[seed.Opened.Id].OriginReplicaId.Should().Be(seed.B);
        foreach (var id in shipmentsBefore.Keys.Where(id => id != seed.Opened.Id))
            after[id].Should().BeEquivalentTo(shipmentsBefore[id], Columns, "geri alınan işlem hiçbir dosyayı yazmaz");
    }

    // ── Yardımcılar: tohum ────────────────────────────────────────────────────

    /// <param name="A">PC-A, yayın sahibi (<see cref="Kit.OwnerReplicaId"/>).</param>
    /// <param name="B">PC-B, dosyaları açan (<see cref="Kit.ReplicaId"/>).</param>
    /// <param name="C">Oturum sahibi boş yayının siparişini gönderen kopya.</param>
    /// <param name="A1">Geri yüklenmeden önceki kopya; <paramref name="A2"/> onun ardılı.</param>
    /// <param name="Small">A ile B'den "N" metni ordinal küçük olan; <paramref name="Large"/> öbürü.</param>
    /// <param name="Secrets">Çıktıda görünmemesi gereken kişisel alanlar ve makine adları.</param>
    private sealed record Seed(
        Kit Kit,
        Guid LicenseId,
        Guid A,
        Guid B,
        Guid C,
        Guid A1,
        Guid A2,
        Guid Small,
        Guid Large,
        Shipment Opened,
        Shipment Mixed,
        Shipment Tie,
        Shipment Legacy,
        Shipment Restored,
        Shipment Correct,
        Shipment Empty,
        Shipment UnknownOwner,
        Shipment Shipped,
        Shipment Foreign,
        List<string> Secrets);

    /// <summary>
    /// Mod On bir lisans (§10.1-25):
    /// <list type="bullet">
    ///   <item>Opened: PC-B'nin açtığı, yalnız PC-A yayınının iki etiketi (siparişlerin kaynağı B; oturum sahibi önce gelir).</item>
    ///   <item>Mixed: A 100 + 200; B 200, ayrıca B'nin iptal edilmiş 500'ü ve geçici yedek 400'ü (DM7'ye girmez).</item>
    ///   <item>Tie: A 150, B 150; kaynağı büyük Id.</item>
    ///   <item>Legacy: oturum sahibi boş yayının, kaynağı C olan etiketi.</item>
    ///   <item>Restored: kaynağı A1, A1 yayınının etiketi; A2 A1'in geri yüklenmiş ardılı.</item>
    ///   <item>Correct: kaynağı A, A etiketi. Empty: bağsız. UnknownOwner: oturum sahibi ve kaynağı boş etiket.</item>
    ///   <item>Shipped: kaynağı B, A etiketi. Foreign: aynı yayıncının başka (mod On) lisansında PC-B'nin PC-A dosyası.</item>
    /// </list>
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var secrets = new List<string> { "Akşam yayını", "PC-A" };
        var (a, b) = (kit.OwnerReplicaId, kit.ReplicaId);
        var (c, a1, a2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (small, large) = string.CompareOrdinal(a.ToString("N"), b.ToString("N")) < 0 ? (a, b) : (b, a);
        var now = DateTimeOffset.UtcNow;
        await AddReplicaAsync(kit, a, null, now.AddHours(-1), secrets);
        await AddReplicaAsync(kit, b, null, now.AddHours(-2), secrets);
        await AddReplicaAsync(kit, c, null, now.AddDays(-3), secrets);
        await AddReplicaAsync(kit, a1, null, now.AddDays(-10), secrets);
        await AddReplicaAsync(kit, a2, a1, now.AddHours(-3), secrets);

        var sA = await kit.SeedSessionAsync(Kit.Phase.Released);
        var sB = await kit.SeedSessionAsync(Kit.Phase.Released, owner: b);
        var sA1 = await kit.SeedSessionAsync(Kit.Phase.Released, owner: a1);
        var sLegacy = await kit.SeedSessionAsync(Kit.Phase.Released);
        await kit.WithDbAsync(async db => (await db.StreamSessions.FindAsync(sLegacy))!.OwnerReplicaId = null);

        var opened = await AddShipmentAsync(kit, ShipmentStatus.Pending, 300m, b, secrets);
        await LinkAsync(kit, sA, opened, 100m, secrets, origin: b);
        await LinkAsync(kit, sA, opened, 200m, secrets, origin: b);

        var mixed = await AddShipmentAsync(kit, ShipmentStatus.Held, 500m, b, secrets, derived: true);
        await LinkAsync(kit, sA, mixed, 100m, secrets);
        await LinkAsync(kit, sA, mixed, 200m, secrets);
        await LinkAsync(kit, sB, mixed, 200m, secrets, origin: b);
        await LinkAsync(kit, sB, mixed, 500m, secrets, origin: b, cancelled: true);
        await LinkAsync(kit, sB, mixed, 400m, secrets, origin: b, tentative: true);

        var tie = await AddShipmentAsync(kit, ShipmentStatus.Pending, 300m, large, secrets);
        await LinkAsync(kit, sA, tie, 150m, secrets);
        await LinkAsync(kit, sB, tie, 150m, secrets, origin: b);

        var legacy = await AddShipmentAsync(kit, ShipmentStatus.Pending, 100m, b, secrets);
        await LinkAsync(kit, sLegacy, legacy, 100m, secrets, origin: c);

        var restored = await AddShipmentAsync(kit, ShipmentStatus.Pending, 100m, a1, secrets);
        await LinkAsync(kit, sA1, restored, 100m, secrets, origin: a1);

        var correct = await AddShipmentAsync(kit, ShipmentStatus.Held, 100m, a, secrets);
        await LinkAsync(kit, sA, correct, 100m, secrets);

        var empty = await AddShipmentAsync(kit, ShipmentStatus.Pending, 0m, b, secrets);

        var unknownOwner = await AddShipmentAsync(kit, ShipmentStatus.Pending, 100m, b, secrets);
        await LinkAsync(kit, sLegacy, unknownOwner, 100m, secrets, noOrigin: true);

        var shipped = await AddShipmentAsync(kit, ShipmentStatus.Shipped, 100m, b, secrets);
        await LinkAsync(kit, sA, shipped, 100m, secrets);

        var other = await kit.SecondLicenseAsync();
        var otherSession = await other.SeedSessionAsync(Kit.Phase.Released);
        var foreign = await AddShipmentAsync(other, ShipmentStatus.Pending, 100m, other.ReplicaId, secrets);
        await LinkAsync(other, otherSession, foreign, 100m, secrets);

        return new Seed(kit, kit.LicenseId, a, b, c, a1, a2, small, large, opened, mixed, tie, legacy, restored,
            correct, empty, unknownOwner, shipped, foreign, secrets);
    }

    private static async Task AddReplicaAsync(
        Kit kit, Guid replicaId, Guid? previous, DateTimeOffset lastSeenAt, List<string> secrets)
    {
        var replica = new WpfReplica
        {
            LicenseId = kit.LicenseId, ReplicaId = replicaId, PreviousReplicaId = previous,
            MachineName = $"PC-{Guid.NewGuid():N}"[..14], HardwareFingerprint = $"fp-{Guid.NewGuid():N}",
            ClientVersion = "0.9.11", Protocol = 2, FeatureLevel = 3,
            FirstSeenAt = lastSeenAt.AddDays(-5), LastSeenAt = lastSeenAt, SeenMode = SalesSyncMode.On,
        };
        secrets.Add(replica.MachineName);
        secrets.Add(replica.HardwareFingerprint);
        await kit.WithDbAsync(db =>
        {
            db.WpfReplicas.Add(replica);
            return Task.CompletedTask;
        });
    }

    /// <summary>Kendi müşterisi olan bir dosya; <paramref name="derived"/> ise tutarı türetilmiş (mod On).</summary>
    private static async Task<Shipment> AddShipmentAsync(
        Kit kit, ShipmentStatus status, decimal amount, Guid? origin, List<string>? secrets = null, bool derived = false)
    {
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = kit.LicenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Status = status,
            CumulativeAmount = amount,
            CreatedAt = Kit.StartedAt.AddHours(3),
            HeldAt = status == ShipmentStatus.Held ? Kit.StartedAt.AddHours(3.5) : null,
            ShippedAt = status == ShipmentStatus.Shipped ? Kit.StartedAt.AddHours(4) : null,
            UpdatedAt = Kit.StartedAt.AddHours(3),
            Version = 3,
            AmountDerivedAt = derived ? Kit.StartedAt.AddHours(5) : null,
            CommandedAt = derived ? Kit.StartedAt.AddHours(5) : null,
            OriginReplicaId = origin,
        };
        secrets?.Add(shipment.CustomerId);
        await kit.WithDbAsync(db =>
        {
            db.Shipments.Add(shipment);
            return Task.CompletedTask;
        });
        return shipment;
    }

    /// <summary>Dosyaya bağlı, dosyanın müşterisine ait basılmış bir sipariş. Kaynağı <paramref name="origin"/>;
    /// verilmezse yayın sahibi (<see cref="Kit.OwnerReplicaId"/>), <paramref name="noOrigin"/> ise boş (kargo bağı
    /// bilinmeyen).</summary>
    private static async Task LinkAsync(
        Kit kit, Guid session, Shipment shipment, decimal price, List<string>? secrets = null,
        Guid? origin = null, bool noOrigin = false, bool cancelled = false, bool tentative = false)
    {
        var order = await kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = shipment.CustomerId;
            o.ShipmentId = shipment.Id;
            o.Price = price;
            o.IsTentativeBackup = tentative;
            if (noOrigin) o.OriginReplicaId = null;
            else if (origin is { } given) o.OriginReplicaId = given;
            if (cancelled)
            {
                o.CancelledAt = Kit.StartedAt.AddHours(5);
                o.CancelReason = "customer";
            }
        });
        if (secrets is null) return;
        secrets.Add(order.Username);
        secrets.Add(order.DisplayName!);
        secrets.Add(order.MessageText);
    }

    // ── Yardımcılar: koşturma ve okuma ────────────────────────────────────────

    private async Task<(int Exit, string Output, string Error)> DisableAsync(Guid licenseId, bool apply)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(db, new SalesSync.DisableArgs(licenseId, apply), output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary><c>--apply</c>, CLI'ın bağlamına takılı kancayla (fabrikanın bağlamlarından ayrı bir bağlantı).</summary>
    private async Task<(int Exit, string Output, string Error)> HookedDisableAsync(IInterceptor hook, Guid licenseId)
    {
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).AddInterceptors(hook).Options);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(db, new SalesSync.DisableArgs(licenseId, Apply: true), output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<License> LicenseAsync(Guid licenseId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == licenseId);
    }

    /// <summary>Bütün lisansların dosyaları.</summary>
    private async Task<Dictionary<Guid, Shipment>> ShipmentsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().ToDictionaryAsync(s => s.Id);
    }

    /// <summary>Dosyanın kolonları (gezinme özelliği hariç).</summary>
    private static EquivalencyAssertionOptions<Shipment> Columns(EquivalencyAssertionOptions<Shipment> options) =>
        options.Excluding(s => s.License);

    /// <summary>Akışın şimdiki ufku: boş sayfaya kadar okunur, son imleç döner.</summary>
    private static async Task<long> FeedHorizonAsync(Kit kit)
    {
        var cursor = 0L;
        while (true)
        {
            var page = await ChangesAsync(kit, cursor);
            if (page.Items.Count == 0) return cursor;
            cursor = page.NextAfterSeq;
        }
    }

    private static async Task<SalesChangesPage> ChangesAsync(Kit kit, long afterSeq)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/licenses/{kit.LicenseId}/sales/changes?afterSeq={afterSeq}&take=500");
        request.Headers.UserAgent.ParseAdd(Kit.WpfUserAgent);
        var resp = await kit.Client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<SalesChangesPage>())!;
    }

    /// <summary>Çıktıda kişisel veri, yayın başlığı ve makine adı yok.</summary>
    private static void AssertNoPersonalData(string output, Seed seed)
    {
        foreach (var secret in seed.Secrets)
            output.Should().NotContain(secret, "çıktıda ad, kullanıcı adı, mesaj, müşteri ve makine adı yok");
    }

    /// <summary>Başlık satırından bir sonraki daha az ya da eşit girintili satıra kadarki bölüm.</summary>
    private static string Section(string output, string heading)
    {
        var lines = output.Split(Environment.NewLine);
        var index = Array.FindIndex(lines, l => l.TrimStart().StartsWith(heading, StringComparison.Ordinal));
        index.Should().BeGreaterThanOrEqualTo(0, $"\"{heading}\" satırı olmalı:{Environment.NewLine}{output}");
        var indent = lines[index].Length - lines[index].TrimStart().Length;
        var body = lines.Skip(index + 1)
            .TakeWhile(l => l.Length - l.TrimStart().Length > indent && l.Trim().Length > 0);
        return string.Join(Environment.NewLine, body.Prepend(lines[index]));
    }

    /// <summary>
    /// Veritabanındaki her kullanıcı tablosunun satır sayısı ve satır sağlama toplamı (<see cref="SalesSyncRelationalTests"/>
    /// ile aynı). rowversion kolonları da toplama girer: bir UPDATE değeri aynı yazsa bile toplam değişir.
    /// </summary>
    private async Task<Dictionary<string, (long Rows, int Checksum)>> TableFingerprintsAsync()
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        var result = new Dictionary<string, (long, int)>();
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT_BIG(*), ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) FROM [{table}]";
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            result[table] = (reader.GetInt64(0), reader.GetInt32(1));
        }
        return result;
    }

    private static string U(DateTimeOffset at) => at.ToString("u", System.Globalization.CultureInfo.InvariantCulture);

    // ── Kancalar ──────────────────────────────────────────────────────────────

    /// <summary>Okuyucusuz komut kancası: <c>when</c>'i tutan ilk komutun hemen öncesinde <c>before</c>'u bir kez
    /// koşturur.</summary>
    private sealed class BeforeNonQueryHook(Func<string, bool> when, Func<Task> before) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Ran && when(command.CommandText))
            {
                Ran = true;
                await before();
            }
            return result;
        }
    }

    /// <summary>Bir dosyayı değiştiren ilk kaydetmenin hemen öncesinde bir kez koşar.</summary>
    private sealed class BeforeShipmentSaveHook(Func<Task> body) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && eventData.Context!.ChangeTracker.Entries<Shipment>().Any(e => e.State == EntityState.Modified))
            {
                Ran = true;
                await body();
            }
            return result;
        }
    }
}
