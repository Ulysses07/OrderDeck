using System.Data.Common;
using System.Globalization;
using System.Net;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync enable</c> — mod kapısı (spec 2026-10-13 §4.10, §8.1 adım 5–7, §8.3 M1, §10.2, §15-5, -29, -53) gerçek
/// SQL Server'da. Taban tohum bütün koşulları tutan bir mod Off lisansıdır: açıklanan Y5 farkı olan bir dosya, farksız
/// bir dosya, Shipped bir dosya, kargo bağı bilinmeyen bir sipariş ve geri doldurma ayrışması olan bir kopya; ayrıca
/// koşulların bilerek görmediği satırlar (devre dışı ya da 14 günden eski etkinleştirme, 14 günden eski kopya). Her
/// koşul tek başına bozulduğunda kuru koşu onu tutmuyor diye Id'leriyle basar, öbür koşullar tutar; <c>--apply</c> 1
/// döner ve hiçbir tabloya yazmaz. <c>--accept-shipment</c> açıklanamayan dosyayı kapıdan geçirir; <c>--apply</c> modu
/// açar, kabul edilmemiş açık dosyaları türetir, kabul edileni ve Shipped dosyayı olduğu gibi bırakır; ikinci
/// <c>enable</c> etkisizdir ve S2'nin <c>attach-shipment</c>'ı kabul edilen dosyayı seçmez (§10.1-27). Çıktıda ad,
/// kullanıcı adı, mesaj, yayın başlığı ve makine adı yok.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncEnableRelationalTests : IAsyncLifetime
{
    private static readonly SalesSyncOptions Configured = new() { MinClientVersion = SalesSyncConfiguredApiFactory.MinClientVersion };

    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private EnableFactory _factory = null!;

    public SalesSyncEnableRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new EnableFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── Taban: bütün koşullar tutar, rapor 7 ─────────────────────────────────

    /// <summary>Kuru koşu hiçbir tabloya yazmaz; altı koşul tutar; rapor 7 açıklanan farkı, eşiğin altına inen dosyayı,
    /// kargo bağı bilinmeyen siparişi ve kopyanın geri doldurma ayrışmasını gösterir. Devre dışı ve 14 günden eski
    /// etkinleştirme, 14 günden eski kopya ve Shipped dosya koşullara girmez.</summary>
    [Fact]
    public async Task Kuru_kosu_taban_lisansta_butun_kosullar_tutar_rapor_7_hicbir_sey_yazmaz()
    {
        var seed = await SeedAsync();
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await EnableAsync(seed.LicenseId, Configured, apply: false);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before, "kuru koşu hiçbir satırı, rowversion dahil, değiştirmez");
        output.Should().Contain("KURU ÇALIŞTIRMA").And.Contain($"lisans {seed.LicenseId}").And.Contain("Mod: Off");
        for (var n = 1; n <= 6; n++) ConditionLine(output, n).Should().EndWith("TUTUYOR", output);
        output.Should().Contain("MinClientVersion: 0.9.11");
        output.Should().Contain("son 14 günde görülen etkin etkinleştirme: 1; alt sınırın altında ya da sürümsüz: 0");
        output.Should().Contain("son 14 günde görülen kopya: 2");
        output.Should().Contain("sürüm 2, eşik=500.00, ücret=60.00, yazan=cli");
        output.Should().Contain("Shipped olmayan dosya: 2 (kabul edilen: 0)");

        output.Should().Contain("açıklanan farkı olan dosya (Y5): 1, toplam fark=250.00");
        output.Should().Contain($"{seed.Explained.Id}: saklanan=550.00, türetilen=300.00, fark=250.00, bağlı iptal=250.00, "
            + "bağlı sipariş=3");
        output.Should().Contain("eşik 500.00: türetmeyle altına inen 1, eşiğe çıkan 0");
        output.Should().Contain($"altına inen: {seed.Explained.Id}:");
        output.Should().Contain("kargo bağı bilinmeyen sipariş: 1; bağsız, iptalsiz, geçici yedek olmayan: 1, tutar=70.00");
        output.Should().Contain("geri doldurma birim ayrışması olan kopya: 1");
        output.Should().Contain($"kopya {seed.Owner}: yerel kazanan=2, sunucu kazanan=1");
        output.Should().Contain("Sonuç: bütün koşullar tutuyor.").And.Contain("hiçbir şey yazılmadı");
        output.Should().NotContain(seed.Shipped.Id.ToString(), "Shipped dosya 6. koşula ve rapora girmez");
        AssertNoPersonalData(output, seed);
    }

    // ── Her koşul tek başına ──────────────────────────────────────────────────

    /// <summary>1: alt sınır tanımsız; son 14 günde görülmüş, devre dışı bırakılmamış etkinleştirmenin sürümü eski ya da
    /// hiç yok.</summary>
    [Fact]
    public async Task Kosul_1_tanimsiz_alt_sinir_ya_da_eski_etkinlestirme_engeller()
    {
        var undefined = await SeedAsync();
        await AssertBlockedAsync(undefined, 1, new SalesSyncOptions { MinClientVersion = " " },
            "MinClientVersion: tanımsız", "alt sınırın altında ya da sürümsüz: karşılaştırılamadı");

        var old = await SeedAsync();
        var oldActivation = await AddActivationAsync(old, "0.9.10", DateTimeOffset.UtcNow.AddDays(-2));
        await AssertBlockedAsync(old, 1, Configured,
            "alt sınırın altında ya da sürümsüz: 1", $"{oldActivation} (sürüm=0.9.10)");

        var unknown = await SeedAsync();
        var silent = await AddActivationAsync(unknown, null, DateTimeOffset.UtcNow.AddDays(-13));
        await AssertBlockedAsync(unknown, 1, Configured,
            "alt sınırın altında ya da sürümsüz: 1", $"{silent} (sürüm=bilinmiyor)");
    }

    /// <summary>2: son 14 günde görülmüş kopyada protokol 1, featureLevel 2, eksik geri doldurma, eksik ödeme geçmişi ya
    /// da parmak izi çakışması — her neden ayrı satırda, kopya Id'siyle.</summary>
    [Fact]
    public async Task Kosul_2_her_kopya_kusuru_tek_basina_engeller()
    {
        foreach (var (shape, line) in new (Action<WpfReplica>, string)[]
                 {
                     (r => r.Protocol = 1, "protokol 2 değil: 1"),
                     (r => r.FeatureLevel = 2, "featureLevel < 3: 1"),
                     (r => r.BackfillCompletedAt = null, "geri doldurma tamamlanmamış: 1"),
                     (r => r.PaymentHistoryCompletedAt = null, "ödeme geçmişi tamamlanmamış: 1"),
                     (r => r.HardwareFingerprintConflictAt = DateTimeOffset.UtcNow.AddDays(-1), "parmak izi çakışması: 1"),
                 })
        {
            var seed = await SeedAsync();
            var replica = await AddReplicaAsync(seed, DateTimeOffset.UtcNow.AddDays(-1), shape);
            await AssertBlockedAsync(seed, 2, Configured, line, "son 14 günde görülen kopya: 3");
            Section(await DryOutputAsync(seed), line).Should().Contain(replica.ToString());
        }
    }

    /// <summary>3: protokolsüz satış isteği izi 7 günden yeni (3 gün) engeller; taban tohumdaki 8 günlük iz tutar.</summary>
    [Fact]
    public async Task Kosul_3_yedi_gunden_yeni_eski_surum_izi_engeller()
    {
        var seed = await SeedAsync();
        var recent = Seconds(DateTimeOffset.UtcNow.AddDays(-3));
        await seed.Kit.WithDbAsync(db => db.SalesLegacyTraces.Where(t => t.LicenseId == seed.LicenseId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.LastLegacySalesPushAt, recent)));

        await AssertBlockedAsync(seed, 3, Configured, $"son protokolsüz satış isteği: {U(recent)}");
    }

    /// <summary>4: lisans geneli kargo ayarı satırı yok ya da <c>UpdatedBy</c> boş.</summary>
    [Fact]
    public async Task Kosul_4_kargo_ayari_yoksa_ya_da_yazani_bossa_engeller()
    {
        var missing = await SeedAsync();
        await missing.Kit.WithDbAsync(db => db.LicenseShippingSettings.Where(s => s.LicenseId == missing.LicenseId)
            .ExecuteDeleteAsync());
        await AssertBlockedAsync(missing, 4, Configured, "   satır yok");

        var anonymous = await SeedAsync();
        await anonymous.Kit.WithDbAsync(db => db.LicenseShippingSettings.Where(s => s.LicenseId == anonymous.LicenseId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.UpdatedBy, (string?)null)));
        await AssertBlockedAsync(anonymous, 4, Configured, "yazan=boş");
    }

    /// <summary>5: 24 saatten eski canlı oturum; devredilmiş oturumda <c>SessionReleasedAt</c>'i eksik sipariş.</summary>
    [Fact]
    public async Task Kosul_5_eski_canli_oturum_ya_da_devir_damgasi_eksik_siparis_engeller()
    {
        var live = await SeedAsync();
        var session = await live.Kit.SeedSessionAsync(Kit.Phase.Live);
        await AssertBlockedAsync(live, 5, Configured, "24 saatten eski devredilmemiş oturum: 1", $"{session} (canlı)");

        var unstamped = await SeedAsync();
        await unstamped.Kit.SeedOrderAsync(unstamped.Session, o => o.SessionReleasedAt = null);
        await AssertBlockedAsync(unstamped, 5, Configured, "SessionReleasedAt'i eksik sipariş: 1");
    }

    /// <summary>6, kaynak: açık dosyanın <c>OriginReplicaId</c>'si boş; kaynak kopyanın geri doldurması tamamlanmamış
    /// (14 günden eski kopya da — pencere bu koşulda yok) ya da kopya satırı hiç yok.</summary>
    [Fact]
    public async Task Kosul_6_kaynaksiz_ya_da_geri_doldurmasiz_kaynakli_dosya_engeller()
    {
        var orphan = await SeedAsync();
        var noOrigin = await AddShipmentAsync(orphan, ShipmentStatus.Pending, 0m, origin: null);
        await AssertBlockedAsync(orphan, 6, Configured, "OriginReplicaId'si boş: 1", noOrigin.Id.ToString());

        var retired = await SeedAsync();
        var gone = await AddReplicaAsync(retired, DateTimeOffset.UtcNow.AddDays(-30), r => r.BackfillCompletedAt = null);
        var fromGone = await AddShipmentAsync(retired, ShipmentStatus.Held, 0m, origin: gone);
        await AssertBlockedAsync(retired, 6, Configured,
            "kaynak kopyanın geri doldurması tamamlanmamış: 1", $"{fromGone.Id} (kopya {gone})");

        var unknownReplica = await SeedAsync();
        var stranger = Guid.NewGuid();
        var fromStranger = await AddShipmentAsync(unknownReplica, ShipmentStatus.Pending, 0m, origin: stranger);
        await AssertBlockedAsync(unknownReplica, 6, Configured,
            "kaynak kopyanın geri doldurması tamamlanmamış: 1", $"{fromStranger.Id} (kopya {stranger})");
    }

    /// <summary>6, fark: saklanan türetilenden küçük; fark bağlı iptallerin toplamını aşıyor; tutarı &gt; 0 ve hiç bağlı
    /// siparişi yok. Fark tam iptal toplamına eşitse açıklanır (taban tohumdaki dosya).</summary>
    [Fact]
    public async Task Kosul_6_aciklanamayan_fark_engeller()
    {
        var below = await SeedAsync();
        var under = await AddShipmentAsync(below, ShipmentStatus.Pending, 100m, below.Owner);
        await LinkAsync(below, under, 200m);
        await AssertBlockedAsync(below, 6, Configured, "açıklanamayan fark: 1",
            $"{under.Id}: saklanan=100.00, türetilen=200.00, fark=-100.00, bağlı iptal=0.00, bağlı sipariş=1");

        var over = await SeedAsync();
        var tooMuch = await AddShipmentAsync(over, ShipmentStatus.Held, 400m, over.Owner);
        await LinkAsync(over, tooMuch, 100m);
        await LinkAsync(over, tooMuch, 200m, cancelled: true);
        await AssertBlockedAsync(over, 6, Configured, "açıklanamayan fark: 1",
            $"{tooMuch.Id}: saklanan=400.00, türetilen=100.00, fark=300.00, bağlı iptal=200.00, bağlı sipariş=2");

        var unlinked = await SeedAsync();
        var empty = await AddShipmentAsync(unlinked, ShipmentStatus.Pending, 80m, unlinked.Owner);
        await AssertBlockedAsync(unlinked, 6, Configured, "açıklanamayan fark: 1",
            $"{empty.Id}: saklanan=80.00, türetilen=0.00, fark=80.00, bağlı iptal=0.00, bağlı sipariş=0");
    }

    // ── --accept-shipment ─────────────────────────────────────────────────────

    /// <summary>Kaynağı boş ve açıklanamayan farkı olan dosya: kabul edilmeden 6. koşul iki nedenle tutmaz; kabul edilince
    /// iki parçadan da muaftır ve kapı geçer. Kabul edilen Id bu lisansın Shipped olmayan dosyası değilse 2, hiçbir şey
    /// yazılmaz, çıktı boş.</summary>
    [Fact]
    public async Task Accept_shipment_aciklanamayan_dosyayi_kapidan_gecirir_gecersiz_Id_2()
    {
        var seed = await SeedAsync();
        var unknown = await AddShipmentAsync(seed, ShipmentStatus.Held, 400m, origin: null);

        var refused = await DryOutputAsync(seed);
        ConditionLine(refused, 6).Should().EndWith("TUTMUYOR");
        Section(refused, "OriginReplicaId'si boş: 1").Should().Contain(unknown.Id.ToString());
        refused.Should().Contain("açıklanamayan fark: 1").And.Contain($"{unknown.Id}: saklanan=400.00, türetilen=0.00");

        var (exit, output, error) = await EnableAsync(seed.LicenseId, Configured, apply: false, unknown.Id);
        exit.Should().Be(0, error);
        for (var n = 1; n <= 6; n++) ConditionLine(output, n).Should().EndWith("TUTUYOR", output);
        Section(output, "Kabul edilen dosya (--accept-shipment): 1").Should().Contain(unknown.Id.ToString());
        output.Should().Contain("Shipped olmayan dosya: 3 (kabul edilen: 1)");
        output.Should().Contain("OriginReplicaId'si boş: 0").And.Contain("açıklanamayan fark: 0");
        output.Should().Contain("açıklanan farkı olan dosya (Y5): 1, toplam fark=250.00", "kabul edilen rapora girmez");
        output.Should().Contain("Sonuç: bütün koşullar tutuyor.");

        var other = await SeedAsync(seed.Kit);
        var foreign = await AddShipmentAsync(other, ShipmentStatus.Pending, 0m, other.Owner);
        var before = await TableFingerprintsAsync();
        foreach (var invalid in new[] { seed.Shipped.Id, foreign.Id, Guid.NewGuid() })
        {
            var result = await EnableAsync(seed.LicenseId, Configured, apply: true, unknown.Id, invalid);

            result.Exit.Should().Be(2);
            result.Error.Should().Contain("--accept-shipment").And.Contain(invalid.ToString());
            result.Error.Should().NotContain(unknown.Id.ToString(), "geçerli kabul listelenmez");
            result.Output.Should().BeEmpty();
        }
        (await TableFingerprintsAsync()).Should().Equal(before);

        var unknownLicense = await EnableAsync(Guid.NewGuid(), Configured, apply: true);
        unknownLicense.Exit.Should().Be(2);
        unknownLicense.Error.Should().Contain("Lisans bulunamadı");
        unknownLicense.Output.Should().BeEmpty();
    }

    // ── --apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--apply</c>: mod 1 ve değişim anı yazılır. Kabul edilmemiş açık dosyalar türetilir (tutar, <c>AmountDerivedAt</c>,
    /// sürüm + 1; Status, HeldAt, ShippedAt aynı); kabul edilen dosya olduğu gibi kalır; önceki bir mod On döneminden
    /// türetme bayrağı taşıyan kabul edilmiş dosyanın bayrağı kaldırılır (tutarı aynı); Shipped dosyaya dokunulmaz.
    /// İkinci <c>enable</c> etkisizdir. S2'nin <c>attach-shipment</c>'ı iki kabul edilmiş dosyayı da seçmez, müşteriye
    /// yeni türetilmiş dosya açar (§10.1-27).
    /// </summary>
    [Fact]
    public async Task Apply_modu_acar_kabul_edilmeyenleri_turetir_kabul_edileni_ve_Shipped_dosyayi_birakir()
    {
        var seed = await SeedAsync();
        var accepted = await AddShipmentAsync(seed, ShipmentStatus.Held, 400m, origin: null);
        var acceptedCandidate = await seed.Kit.SeedOrderAsync(seed.Session, o =>
        {
            o.CustomerId = accepted.CustomerId;
            o.Price = 90m;
        });
        var previouslyDerived = await AddShipmentAsync(seed, ShipmentStatus.Pending, 120m, seed.Owner, derived: true);
        await LinkAsync(seed, previouslyDerived, 120m);
        var previouslyDerivedCandidate = await seed.Kit.SeedOrderAsync(seed.Session, o =>
        {
            o.CustomerId = previouslyDerived.CustomerId;
            o.Price = 40m;
        });
        Track(seed, acceptedCandidate);
        Track(seed, previouslyDerivedCandidate);
        var shipmentsBefore = await ShipmentsAsync(seed.LicenseId);
        var start = DateTimeOffset.UtcNow;

        var (exit, output, error) = await EnableAsync(
            seed.LicenseId, Configured, apply: true, accepted.Id, previouslyDerived.Id);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA").And.Contain("Sonuç: bütün koşullar tutuyor.").And.Contain("UYGULANDI: mod On");
        output.Should().Contain("Türetilen dosya: 2 (tutarı değişen: 1, toplam fark (saklanan − türetilen)=250.00)");
        Section(output, "Türetilen dosya: 2 (tutarı değişen: 1, toplam fark (saklanan − türetilen)=250.00)")
            .Should().Contain(seed.Exact.Id.ToString()).And.Contain(seed.Explained.Id.ToString());
        Section(output, "Kabul edilen, türetilmeyen dosya: 2 (türetme bayrağı kaldırılan: 1)")
            .Should().Contain(accepted.Id.ToString()).And.Contain(previouslyDerived.Id.ToString());
        AssertNoPersonalData(output, seed);

        var license = await seed.Kit.ReadDbAsync(db => db.Licenses.AsNoTracking().SingleAsync(l => l.Id == seed.LicenseId));
        license.SalesSyncMode.Should().Be(SalesSyncMode.On);
        license.SalesSyncModeChangedAt.Should().BeOnOrAfter(start.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        var changedAt = license.SalesSyncModeChangedAt!.Value;

        var after = await ShipmentsAsync(seed.LicenseId);
        foreach (var (id, amount) in new[] { (seed.Exact.Id, 300m), (seed.Explained.Id, 300m) })
        {
            var was = shipmentsBefore[id];
            after[id].Should().BeEquivalentTo(new
            {
                CumulativeAmount = amount,
                AmountDerivedAt = (DateTimeOffset?)changedAt,
                Version = was.Version + 1,
                UpdatedAt = changedAt,
            });
            after[id].Should().BeEquivalentTo(was, o => Columns(o)
                    .Excluding(s => s.CumulativeAmount)
                    .Excluding(s => s.AmountDerivedAt)
                    .Excluding(s => s.Version)
                    .Excluding(s => s.UpdatedAt)
                    .Excluding(s => s.ChangeSeq),
                "kararlar (Status, HeldAt, ShippedAt), kaynak ve komut damgası değişmez");
        }
        after[seed.Explained.Id].Status.Should().Be(ShipmentStatus.Held);
        after[accepted.Id].Should().BeEquivalentTo(shipmentsBefore[accepted.Id], Columns,
            "kabul edilen dosya türetilmez: AmountDerivedAt boş, saklanan tutar, sürüm ve ChangeSeq aynı");
        after[seed.Shipped.Id].Should().BeEquivalentTo(shipmentsBefore[seed.Shipped.Id], Columns,
            "Shipped dosyaya dokunulmaz");
        after[previouslyDerived.Id].Should().BeEquivalentTo(new
        {
            CumulativeAmount = 120m,
            AmountDerivedAt = (DateTimeOffset?)null,
            Version = shipmentsBefore[previouslyDerived.Id].Version + 1,
            shipmentsBefore[previouslyDerived.Id].Status,
        }, "kabul edilen dosyanın eski türetme bayrağı kaldırılır, tutarı korunur");

        // İkinci enable etkisiz (kuru ve --apply).
        var fingerprints = await TableFingerprintsAsync();
        foreach (var apply in new[] { false, true })
        {
            var again = await EnableAsync(seed.LicenseId, Configured, apply);
            again.Exit.Should().Be(0, again.Error);
            again.Output.Should().Contain("Mod zaten açık (On").And.Contain("hiçbir şey yazılmadı");
        }
        (await TableFingerprintsAsync()).Should().Equal(fingerprints, "ikinci enable hiçbir şey yazmaz");

        // §10.1-27: attach-shipment kabul edilmiş (türetilmemiş) dosyayı seçmez, yeni dosya açar.
        foreach (var (shipment, candidate) in new[]
                 {
                     (accepted, acceptedCandidate), (previouslyDerived, previouslyDerivedCandidate),
                 })
        {
            var proposed = Guid.NewGuid();
            var response = await seed.Kit.SendAsync(
                seed.Kit.AttachShipment(Guid.NewGuid(), shipment.CustomerId, proposed, [candidate]));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await Kit.ReadAsync(response)).Result.GetProperty("shipmentId").GetGuid()
                .Should().Be(proposed, "kabul edilmiş dosya seçilmez");
            (await seed.Kit.ShipmentAsync(proposed)).CumulativeAmount.Should().Be(candidate.Price);
            (await seed.Kit.OrderAsync(candidate.Id)).ShipmentId.Should().Be(proposed);
        }
        var finalAccepted = await seed.Kit.ShipmentAsync(accepted.Id);
        finalAccepted.CumulativeAmount.Should().Be(400m);
        finalAccepted.AmountDerivedAt.Should().BeNull();
        (await seed.Kit.ShipmentAsync(previouslyDerived.Id)).CumulativeAmount.Should().Be(120m);
    }

    // ── Yarışlar ──────────────────────────────────────────────────────────────

    /// <summary>Değerlendirme ile koşullu UPDATE arasında başka bir <c>enable</c> modu açarsa UPDATE satır bulmaz: geri
    /// alınır, 1, dosyalar türetilmez.</summary>
    [Fact]
    public async Task Apply_esanli_mod_degisikliginde_1_doner_dosya_turetmez()
    {
        var seed = await SeedAsync();
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[SalesSyncMode]", StringComparison.Ordinal),
            before: () => seed.Kit.WithDbAsync(db => db.Licenses.Where(l => l.Id == seed.LicenseId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.SalesSyncMode, SalesSyncMode.On))));

        var (exit, output, error) = await HookedEnableAsync(hook, seed.LicenseId);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1);
        error.Should().Contain("Mod bu arada değişti").And.Contain("yeniden çalıştır");
        output.Should().NotContain("UYGULANDI");
        (await seed.Kit.ShipmentAsync(seed.Exact.Id)).AmountDerivedAt.Should().BeNull();
        (await seed.Kit.ShipmentAsync(seed.Explained.Id)).CumulativeAmount.Should().Be(550m);
    }

    /// <summary>Değerlendirmeden sonra kaynağı boş yeni bir açık dosya gelirse işlem içindeki yeniden değerlendirme onu
    /// görür: geri alınır, 1, mod 0 kalır.</summary>
    [Fact]
    public async Task Apply_degerlendirmeden_sonra_kosulu_bozan_dosya_gelirse_1_doner_hicbir_sey_yazmaz()
    {
        var seed = await SeedAsync();
        var late = Guid.NewGuid();
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[SalesSyncMode]", StringComparison.Ordinal),
            before: () => seed.Kit.WithDbAsync(db =>
            {
                db.Shipments.Add(NewShipment(seed.LicenseId, late, ShipmentStatus.Pending, 50m, origin: null));
                return Task.CompletedTask;
            }));

        var (exit, output, error) = await HookedEnableAsync(hook, seed.LicenseId);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1);
        error.Should().Contain("6. koşulu artık tutmuyor").And.Contain(late.ToString()).And.Contain("yeniden çalıştır");
        output.Should().NotContain("UYGULANDI");
        await AssertModeOffAsync(seed);
        (await seed.Kit.ShipmentAsync(seed.Exact.Id)).AmountDerivedAt.Should().BeNull();
    }

    /// <summary>Türetmenin yüklediği bir dosya kaydetmeden önce başka bir yazımla değişirse sürüm jetonu çarpar: işlem —
    /// mod yazımı dahil — geri alınır, 1.</summary>
    [Fact]
    public async Task Apply_dosya_esanli_yazilirsa_isleme_geri_alinir_1_doner()
    {
        var seed = await SeedAsync();
        var hook = new BeforeShipmentSaveHook(() => seed.Kit.WithDbAsync(db => db.Shipments
            .Where(s => s.Id == seed.Exact.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Version, s => s.Version + 1))));

        var (exit, output, error) = await HookedEnableAsync(hook, seed.LicenseId);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1);
        error.Should().Contain("bu arada yazıldı").And.Contain("yeniden çalıştır");
        output.Should().NotContain("UYGULANDI");
        await AssertModeOffAsync(seed);
        (await seed.Kit.ShipmentAsync(seed.Explained.Id)).Should().BeEquivalentTo(
            new { CumulativeAmount = 550m, AmountDerivedAt = (DateTimeOffset?)null });
    }

    // ── Yardımcılar: koşul denetimi ───────────────────────────────────────────

    /// <summary>Kuru koşu yalnız <paramref name="condition"/>'ı tutmuyor gösterir ve <paramref name="expected"/>
    /// satırlarını içerir; <c>--apply</c> 1 döner ve hiçbir tabloya yazmaz, mod 0 kalır.</summary>
    private async Task AssertBlockedAsync(Seed seed, int condition, SalesSyncOptions options, params string[] expected)
    {
        var dry = await EnableAsync(seed.LicenseId, options, apply: false);
        dry.Exit.Should().Be(0, dry.Error);
        for (var n = 1; n <= 6; n++)
            ConditionLine(dry.Output, n).Should().EndWith(n == condition ? "TUTMUYOR" : "TUTUYOR", dry.Output);
        foreach (var line in expected) dry.Output.Should().Contain(line);
        dry.Output.Should().Contain($"Sonuç: koşul {condition} tutmuyor; mod açılamaz.");
        AssertNoPersonalData(dry.Output, seed);

        var before = await TableFingerprintsAsync();
        var apply = await EnableAsync(seed.LicenseId, options, apply: true);
        apply.Exit.Should().Be(1);
        apply.Error.Should().Contain($"Mod açılmadı: koşul {condition} tutmuyor; hiçbir şey yazılmadı.");
        apply.Output.Should().NotContain("UYGULANDI");
        (await TableFingerprintsAsync()).Should().Equal(before, "reddedilen --apply hiçbir satırı değiştirmez");
        await AssertModeOffAsync(seed);
    }

    private async Task AssertModeOffAsync(Seed seed)
    {
        var license = await seed.Kit.ReadDbAsync(db => db.Licenses.AsNoTracking().SingleAsync(l => l.Id == seed.LicenseId));
        license.SalesSyncMode.Should().Be(SalesSyncMode.Off);
        license.SalesSyncModeChangedAt.Should().BeNull();
    }

    /// <summary>Çıktıda kişisel veri, yayın başlığı ve makine adı yok.</summary>
    private static void AssertNoPersonalData(string output, Seed seed)
    {
        foreach (var secret in seed.Secrets)
            output.Should().NotContain(secret, "çıktıda ad, kullanıcı adı, mesaj, müşteri ve makine adı yok");
        output.Should().NotContain("Akşam yayını").And.NotContain("PC-A");
    }

    /// <summary>"Koşul n — …: TUTUYOR|TUTMUYOR" satırı.</summary>
    private static string ConditionLine(string output, int n) =>
        output.Split(Environment.NewLine).Single(l => l.StartsWith($"Koşul {n} — ", StringComparison.Ordinal));

    /// <summary>Başlık satırından bir sonraki girintisiz satıra ya da daha az girintili satıra kadarki bölüm.</summary>
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

    // ── Yardımcılar: tohum ────────────────────────────────────────────────────

    /// <param name="Exact">Farksız açık dosya (100 + 200 bağlı).</param>
    /// <param name="Explained">Açıklanan Y5 farkı olan Held dosya (300 bağlı + 250 bağlı ve iptal, saklanan 550).</param>
    /// <param name="Shipped">Shipped dosya; saklanan tutarı bağlarından farklı (kapsam dışı).</param>
    /// <param name="Secrets">Çıktıda görünmemesi gereken kişisel alanlar ve makine adları.</param>
    private sealed record Seed(
        Kit Kit,
        Guid LicenseId,
        Guid Owner,
        Guid Session,
        Shipment Exact,
        Shipment Explained,
        Shipment Shipped,
        List<string> Secrets);

    /// <summary>
    /// Bütün koşulları tutan mod Off lisansı (<paramref name="parent"/> verilirse aynı yayıncının yeni lisansı):
    /// <list type="bullet">
    ///   <item>1: Ws sürümünde bir etkinleştirme; devre dışı bırakılmış eski sürümlü ve 20 gün önce görülmüş sürümsüz
    ///   etkinleştirme sayılmaz.</item>
    ///   <item>2: iki tam kopya (sahip ve ikinci PC; sahipte geri doldurma ayrışması); 20 gün önce görülmüş v1 kopya
    ///   sayılmaz.</item>
    ///   <item>3: 8 gün önceki protokolsüz istek izi. 4: <c>cli</c>'nın yazdığı kargo ayarı (eşik 500, ücret 60).</item>
    ///   <item>5: devredilmiş bir yayın, siparişleri damgalı.</item>
    ///   <item>6: farksız Pending dosya, açıklanan farkı olan Held dosya, kapsam dışı Shipped dosya; kargo bağı
    ///   bilinmeyen bir sipariş (70).</item>
    /// </list>
    /// </summary>
    private async Task<Seed> SeedAsync(Kit? parent = null)
    {
        var kit = parent is null
            ? await Kit.CreateAsync(_factory, SalesSyncMode.Off)
            : await parent.SecondLicenseAsync(SalesSyncMode.Off);
        var now = DateTimeOffset.UtcNow;
        var secrets = new List<string>();
        var seed = new Seed(kit, kit.LicenseId, kit.OwnerReplicaId, Guid.Empty, null!, null!, null!, secrets);

        await AddReplicaAsync(seed, now.AddHours(-1), r =>
        {
            r.ReplicaId = kit.OwnerReplicaId;
            r.BackfillDivergedLocal = 2;
            r.BackfillDivergedServer = 1;
        });
        await AddReplicaAsync(seed, now.AddDays(-13), r => r.ReplicaId = kit.ReplicaId);
        await AddReplicaAsync(seed, now.AddDays(-20), r =>
        {
            r.Protocol = 1;
            r.FeatureLevel = 0;
            r.PaymentHistoryCompletedAt = null;
        });
        await AddActivationAsync(seed, SalesSyncConfiguredApiFactory.MinClientVersion, now.AddHours(-2));
        await AddActivationAsync(seed, "0.9.10", now.AddHours(-3), deactivated: true);
        await AddActivationAsync(seed, null, now.AddDays(-20));
        await kit.WithDbAsync(db =>
        {
            db.SalesLegacyTraces.Add(new SalesLegacyTrace { LicenseId = kit.LicenseId, LastLegacySalesPushAt = now.AddDays(-8) });
            db.LicenseShippingSettings.Add(new LicenseShippingSettings
            {
                LicenseId = kit.LicenseId, Version = 2, FreeShippingThreshold = 500m, ShippingFee = 60m,
                UpdatedAt = now.AddDays(-1), UpdatedBy = LicenseShippingSettings.UpdatedByCli,
            });
            return Task.CompletedTask;
        });

        seed = seed with { Session = await kit.SeedSessionAsync(Kit.Phase.Released) };
        var exact = await AddShipmentAsync(seed, ShipmentStatus.Pending, 300m, kit.OwnerReplicaId);
        await LinkAsync(seed, exact, 100m);
        await LinkAsync(seed, exact, 200m);
        var explained = await AddShipmentAsync(seed, ShipmentStatus.Held, 550m, kit.ReplicaId);
        await LinkAsync(seed, explained, 100m);
        await LinkAsync(seed, explained, 200m);
        await LinkAsync(seed, explained, 250m, cancelled: true);
        var shipped = await AddShipmentAsync(seed, ShipmentStatus.Shipped, 999m, kit.OwnerReplicaId);
        await LinkAsync(seed, shipped, 100m);
        Track(seed, await kit.SeedOrderAsync(seed.Session, o =>
        {
            o.Price = 70m;
            o.OriginReplicaId = null;
        }));

        return seed with { Exact = exact, Explained = explained, Shipped = shipped };
    }

    /// <summary>Tam bir kopya (protokol 2, featureLevel 3, geri doldurma ve ödeme geçmişi tamam) — ardından
    /// <paramref name="shape"/>.</summary>
    private static async Task<Guid> AddReplicaAsync(Seed seed, DateTimeOffset lastSeenAt, Action<WpfReplica> shape)
    {
        var replica = new WpfReplica
        {
            LicenseId = seed.LicenseId, ReplicaId = Guid.NewGuid(),
            MachineName = $"PC-{Guid.NewGuid():N}"[..14], HardwareFingerprint = $"fp-{Guid.NewGuid():N}",
            ClientVersion = SalesSyncConfiguredApiFactory.MinClientVersion, Protocol = 2, FeatureLevel = 3,
            FirstSeenAt = lastSeenAt.AddDays(-40), LastSeenAt = lastSeenAt,
            BackfillCompletedAt = lastSeenAt.AddDays(-30), PaymentHistoryCompletedAt = lastSeenAt.AddDays(-30),
        };
        shape(replica);
        seed.Secrets.Add(replica.MachineName);
        seed.Secrets.Add(replica.HardwareFingerprint);
        await seed.Kit.WithDbAsync(db =>
        {
            db.WpfReplicas.Add(replica);
            return Task.CompletedTask;
        });
        return replica.ReplicaId;
    }

    private static async Task<Guid> AddActivationAsync(
        Seed seed, string? appVersion, DateTimeOffset lastSeenAt, bool deactivated = false)
    {
        var activation = new Activation
        {
            Id = Guid.NewGuid(), LicenseId = seed.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
            MachineName = $"MAKINE-{Guid.NewGuid():N}"[..16], ActivatedAt = lastSeenAt.AddDays(-60),
            LastSeenAt = lastSeenAt, DeactivatedAt = deactivated ? lastSeenAt : null, AppVersion = appVersion,
        };
        seed.Secrets.Add(activation.MachineName!);
        seed.Secrets.Add(activation.HardwareFingerprint);
        await seed.Kit.WithDbAsync(db =>
        {
            db.Activations.Add(activation);
            return Task.CompletedTask;
        });
        return activation.Id;
    }

    /// <summary>Kendi müşterisi olan bir dosya (müşteri Id'si çıktıda görünmemeli).</summary>
    private static async Task<Shipment> AddShipmentAsync(
        Seed seed, ShipmentStatus status, decimal amount, Guid? origin, bool derived = false)
    {
        var shipment = NewShipment(seed.LicenseId, Guid.NewGuid(), status, amount, origin);
        if (derived) shipment.AmountDerivedAt = Kit.StartedAt.AddHours(5);
        seed.Secrets.Add(shipment.CustomerId);
        await seed.Kit.WithDbAsync(db =>
        {
            db.Shipments.Add(shipment);
            return Task.CompletedTask;
        });
        return shipment;
    }

    private static Shipment NewShipment(Guid licenseId, Guid id, ShipmentStatus status, decimal amount, Guid? origin) => new()
    {
        Id = id,
        LicenseId = licenseId,
        CustomerId = Guid.NewGuid().ToString("N"),
        Status = status,
        CumulativeAmount = amount,
        CreatedAt = Kit.StartedAt.AddHours(3),
        HeldAt = status == ShipmentStatus.Held ? Kit.StartedAt.AddHours(3.5) : null,
        ShippedAt = status == ShipmentStatus.Shipped ? Kit.StartedAt.AddHours(4) : null,
        UpdatedAt = Kit.StartedAt.AddHours(3),
        Version = 3,
        OriginReplicaId = origin,
    };

    /// <summary>Dosyaya bağlı, dosyanın müşterisine ait basılmış bir sipariş.</summary>
    private static async Task LinkAsync(Seed seed, Shipment shipment, decimal price, bool cancelled = false)
    {
        var order = await seed.Kit.SeedOrderAsync(seed.Session, o =>
        {
            o.CustomerId = shipment.CustomerId;
            o.ShipmentId = shipment.Id;
            o.Price = price;
            if (cancelled)
            {
                o.CancelledAt = Kit.StartedAt.AddHours(5);
                o.CancelReason = "customer";
            }
        });
        Track(seed, order);
    }

    private static void Track(Seed seed, Order order)
    {
        seed.Secrets.Add(order.Username);
        seed.Secrets.Add(order.DisplayName!);
        seed.Secrets.Add(order.MessageText);
        seed.Secrets.Add(order.CustomerId);
    }

    // ── Yardımcılar: koşturma ve okuma ────────────────────────────────────────

    private async Task<(int Exit, string Output, string Error)> EnableAsync(
        Guid licenseId, SalesSyncOptions options, bool apply, params Guid[] accepted)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(
            db, new SalesSync.EnableArgs(licenseId, accepted, apply), options, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<string> DryOutputAsync(Seed seed)
    {
        var (exit, output, error) = await EnableAsync(seed.LicenseId, Configured, apply: false);
        exit.Should().Be(0, error);
        return output;
    }

    /// <summary><c>--apply</c>, CLI'ın bağlamına takılı kancayla (fabrikanın bağlamlarından ayrı bir bağlantı).</summary>
    private async Task<(int Exit, string Output, string Error)> HookedEnableAsync(IInterceptor hook, Guid licenseId)
    {
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).AddInterceptors(hook).Options);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(
            db, new SalesSync.EnableArgs(licenseId, [], Apply: true), Configured, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<Dictionary<Guid, Shipment>> ShipmentsAsync(Guid licenseId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().Where(s => s.LicenseId == licenseId).ToDictionaryAsync(s => s.Id);
    }

    /// <summary>Dosyanın kolonları (gezinme özelliği hariç).</summary>
    private static EquivalencyAssertionOptions<Shipment> Columns(EquivalencyAssertionOptions<Shipment> options) =>
        options.Excluding(s => s.License);

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

    private static string U(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    private static DateTimeOffset Seconds(DateTimeOffset at) =>
        new(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, at.Offset);

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

    /// <summary>Gerçek SQL Server; satış senkronu komutlarının sürüm kapısı tanımlı
    /// (<see cref="Kit.WpfUserAgent"/> onu geçer) — mod açıldıktan sonra <c>attach-shipment</c> için.</summary>
    private sealed class EnableFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        };
    }
}
