using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using V = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifier;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalanceVerifier.Analyze"/> — spec 2026-10-11 §7.3 (rapor maddeleri 1–10), §3.3 (D1, D4, D5, D6),
/// §8.1 (yalnız tabana göre YENİ sapma uyarır; taban yokken eksi/D1/kopya yalnız sayılır).
/// </summary>
public sealed class PersonBalanceVerifierTests
{
    private static readonly Guid Lic = Guid.NewGuid();
    private static readonly DateTimeOffset Issued = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static V.Snapshot Snap(
        IEnumerable<V.ProjectionRow>? projections = null, IEnumerable<V.BalanceRow>? balances = null,
        IEnumerable<V.LedgerRow>? ledger = null, IEnumerable<V.DrawRow>? draws = null,
        IEnumerable<Guid>? reversed = null, IEnumerable<V.BaselineRow>? baselines = null,
        IEnumerable<V.ActivationRow>? pcs = null, DateTimeOffset? issued = null) =>
        new([new V.LicenseRow(Lic, issued ?? Issued)],
            (projections ?? Array.Empty<V.ProjectionRow>()).ToList(),
            (balances ?? Array.Empty<V.BalanceRow>()).ToList(),
            (ledger ?? Array.Empty<V.LedgerRow>()).ToList(),
            (draws ?? Array.Empty<V.DrawRow>()).ToList(),
            (reversed ?? Array.Empty<Guid>()).ToHashSet(),
            (baselines ?? Array.Empty<V.BaselineRow>()).ToList(),
            (pcs ?? Array.Empty<V.ActivationRow>()).ToList(),
            ProjectionCount: 0, TransactionCount: 0);

    private static V.LicenseReport Single(V.Snapshot s) => V.Analyze(s).Licenses.Should().ContainSingle().Subject;

    private static V.ProjectionRow P(Guid id, string? group = null, bool purged = false, Guid? mergedInto = null) =>
        new(id, Lic, group, mergedInto, purged);

    private static V.BalanceRow B(Guid id, decimal balance) => new(Lic, id, balance);

    private static V.LedgerRow L(Guid id, decimal sum, int count = 1) => new(Lic, id, sum, count);

    private static V.BaselineRow Marker(DateTimeOffset? at = null, Guid? license = null) =>
        new(license ?? Lic, V.LicenseMarker, 0m, 0m, at ?? Captured, null);

    /// <summary>Örnek 1'in grubu: K (T, 0, Original 120), L1 (I, −100), L2 (Y, −20).</summary>
    private static (Guid K, Guid L1, Guid L2, List<V.DrawRow> Rows) Example1Draw(
        string legKind = "purchase-deduction", decimal original = 120m)
    {
        var (t, i, y) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (k, l1, l2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var rows = new List<V.DrawRow>
        {
            new(k, Lic, t, 0m, "purchase-deduction", k, original, "session:42"),
            new(l1, Lic, i, -100m, legKind, k, null, null),
            new(l2, Lic, y, -20m, "purchase-deduction", k, null, null),
        };
        return (k, l1, l2, rows);
    }

    [Fact]
    public void Tutarli_cekim_grubunda_kural_bozulmasi_yok()
    {
        var (_, _, _, rows) = Example1Draw();

        var r = Single(Snap(draws: rows));

        (r.D4, r.D5, r.D6, r.OrphanLegs).Should().Be((0, 0, 0, 0));
        r.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void D4_bacagin_turu_capadan_farkli()
    {
        var (k, _, _, rows) = Example1Draw(legKind: "manual-adjustment");

        var r = Single(Snap(draws: rows));

        r.D4.Should().Be(1);
        r.Details.RuleBreakingGroups.Should().Equal(k);
        r.Alerts.Should().ContainSingle().Which.Should().StartWith("kural:");
    }

    [Fact]
    public void D5_bacaklarin_bir_kismi_geri_alinmis_hepsi_alinmissa_bozulma_yok()
    {
        var (k, l1, l2, rows) = Example1Draw();

        Single(Snap(draws: rows, reversed: [l1])).D5.Should().Be(1);
        Single(Snap(draws: rows, reversed: [k, l1, l2])).D5.Should().Be(0);
    }

    [Fact]
    public void D6_bacaklarin_toplami_OriginalAmounti_asamaz()
    {
        var (_, _, _, rows) = Example1Draw(original: 100m);

        Single(Snap(draws: rows)).D6.Should().Be(1, "−Σ = 120 > 100");
    }

    [Theory]
    [InlineData("session:42", null)]
    [InlineData(null, 20.0)]
    public void D4_bacakta_kapsam_ya_da_OriginalAmount_dolu(string? saleScope, double? original)
    {
        var (k, _, _, rows) = Example1Draw();
        rows[2] = rows[2] with { SaleScope = saleScope, OriginalAmount = (decimal?)original };   // L2

        var r = Single(Snap(draws: rows));

        r.D4.Should().Be(1, "bacak türü çapayla aynı, ama SaleScope ve OriginalAmount bacakta NULL olmalı");
        r.Details.RuleBreakingGroups.Should().Equal(k);
    }

    [Fact]
    public void Capasi_olmayan_bacak_yetimdir()
    {
        var leg = new V.DrawRow(Guid.NewGuid(), Lic, Guid.NewGuid(), -5m, "purchase-deduction", Guid.NewGuid(), null, null);

        var r = Single(Snap(draws: [leg]));

        r.OrphanLegs.Should().Be(1);
        r.Details.OrphanLegs.Should().Equal(leg.Id);
        r.Alerts.Should().ContainSingle().Which.Should().Contain("yetim=1");
    }

    [Fact]
    public void Taban_hic_alinmamisken_eksi_D1_ve_kopya_yalniz_sayilir_uyarmaz()
    {
        var (neg, broken, copy, head) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var r = Single(Snap(
            projections: [P(neg), P(broken), P(head), P(copy, mergedInto: head)],
            balances: [B(neg, -10m), B(broken, 50m)],
            ledger: [L(neg, -10m), L(broken, 40m), L(copy, 0m, count: 2)]));

        r.HasBaseline.Should().BeFalse();
        r.NegativeRows.Should().Be(new V.Tally(0, 1, -10m));
        r.LedgerMismatches.Should().Be(new V.Tally(0, 1, 10m));
        r.CopyRows.Outside.Should().Be(1);
        r.CopyRowsWithTransactions.Should().Be(1);
        r.Alerts.Should().BeEmpty("§8.1: taban alınana kadar yalnız sayı");
    }

    [Fact]
    public void Isaretli_lisansta_taban_disi_ve_daha_da_eksiye_inen_satir_uyarir_ayni_kalan_uyarmaz()
    {
        var (old, fresh) = (Guid.NewGuid(), Guid.NewGuid());
        V.BaselineRow Base(decimal balance) => new(Lic, old, balance, balance, Captured, Captured);

        var same = Single(Snap(projections: [P(old)], balances: [B(old, -10m)], ledger: [L(old, -10m)],
            baselines: [Marker(), Base(-10m)]));
        same.Alerts.Should().BeEmpty();
        same.NegativeRows.Should().Be(new V.Tally(1, 0, -10m));

        var worse = Single(Snap(projections: [P(old)], balances: [B(old, -15m)], ledger: [L(old, -15m)],
            baselines: [Marker(), Base(-10m)]));
        worse.Alerts.Should().ContainSingle().Which.Should().Be("eksi: taban dışı 0, daha da eksiye inen 1");

        var outside = Single(Snap(projections: [P(old), P(fresh)], balances: [B(old, -10m), B(fresh, -5m)],
            ledger: [L(old, -10m), L(fresh, -5m)], baselines: [Marker(), Base(-10m)]));
        outside.Alerts.Should().ContainSingle().Which.Should().Be("eksi: taban dışı 1, daha da eksiye inen 0");
        outside.Details.NegativeOutside.Should().Equal(fresh);
    }

    [Fact]
    public void Tabandaki_kopya_satir_daha_da_eksiye_inerse_uyarir()
    {
        var (head, copy) = (Guid.NewGuid(), Guid.NewGuid());
        V.Snapshot At(decimal balance) => Snap(
            projections: [P(head), P(copy, mergedInto: head)],
            balances: [B(copy, balance)], ledger: [L(copy, balance)],
            baselines: [Marker(), new V.BaselineRow(Lic, copy, -5m, -5m, Captured, null)]);

        Single(At(-5m)).Alerts.Should().BeEmpty();
        var worse = Single(At(-8m));
        worse.Alerts.Should().ContainSingle().Which.Should().Be("eksi: taban dışı 0, daha da eksiye inen 1",
            "tabana alınmış kopyaya sonradan yazım (§4.12'nin dar penceresi) de D2'nin izidir");
        worse.Details.WorsenedBaselineRows.Should().Equal(copy);
    }

    [Fact]
    public void Tabandaki_D1_farki_ayni_kalirsa_uyarmaz_degisirse_uyarir()
    {
        var row = Guid.NewGuid();
        var baseline = new V.BaselineRow(Lic, row, 100m, 90m, Captured, null);

        Single(Snap(projections: [P(row)], balances: [B(row, 70m)], ledger: [L(row, 60m)],
            baselines: [Marker(), baseline])).Alerts.Should().BeEmpty("fark 10 korunuyor");
        Single(Snap(projections: [P(row)], balances: [B(row, 70m)], ledger: [L(row, 70m)],
            baselines: [Marker(), baseline])).Alerts.Should().ContainSingle()
            .Which.Should().Be("D1: taban dışı 0, farkı değişen 1");
    }

    [Fact]
    public void Ilk_yakalamadan_sonra_verilen_lisans_isaretsiz_de_tabanli_sayilir()
    {
        var neg = Guid.NewGuid();

        var r = Single(Snap(projections: [P(neg)], balances: [B(neg, -3m)], ledger: [L(neg, -3m)],
            baselines: [Marker(Captured, license: Guid.NewGuid())], issued: Captured.AddDays(1)));

        r.HasMarker.Should().BeFalse();
        r.HasBaseline.Should().BeTrue("yeni lisansın S1 öncesi verisi olamaz");
        r.Alerts.Should().ContainSingle().Which.Should().StartWith("eksi:");
    }

    [Fact]
    public void Ilk_yakalamadan_once_verilen_isaretsiz_lisans_tabansiz_sayilir()
    {
        var neg = Guid.NewGuid();

        var r = Single(Snap(projections: [P(neg)], balances: [B(neg, -3m)], ledger: [L(neg, -3m)],
            baselines: [Marker(Captured, license: Guid.NewGuid())]));

        (r.HasMarker, r.HasBaseline).Should().Be((false, false),
            "başka lisansın işareti, yakalamadan önce verilmiş lisansın S1 öncesi verisini kapsamaz");
        r.Alerts.Should().BeEmpty();
    }

    [Fact]
    public void Isaretli_lisansta_taban_disi_kopya_satir_uyarir()
    {
        var (head, copy) = (Guid.NewGuid(), Guid.NewGuid());

        var r = Single(Snap(projections: [P(head), P(copy, mergedInto: head)],
            balances: [B(copy, 3m)], ledger: [L(copy, 3m)], baselines: [Marker()]));

        r.Alerts.Should().ContainSingle().Which.Should().Be("kopya: taban dışı 1", "D1 tutuyor, kopya eksi sayılmaz");
        r.Details.CopyOutside.Should().Equal(copy);
    }

    [Fact]
    public void Havuzu_eksi_kisi_butun_eksi_satirlari_tabanda_ve_kotulesmemisse_aciklanir()
    {
        var g = $"g{Guid.NewGuid():N}";
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        V.Snapshot At(decimal balanceOfA) => Snap(
            projections: [P(a, g), P(b, g)],
            balances: [B(a, balanceOfA), B(b, 2m)], ledger: [L(a, balanceOfA), L(b, 2m)],
            baselines: [Marker(), new V.BaselineRow(Lic, a, -10m, -10m, Captured, null)]);

        var same = Single(At(-10m));
        (same.NegativePersons, same.NegativePersonsExplained).Should().Be((1, 1), "−10 + 2 < 0, eksi satır tabanda");

        var worse = Single(At(-12m));
        (worse.NegativePersons, worse.NegativePersonsExplained).Should().Be((1, 0), "eksi satır tabandakinin altına indi");
    }

    [Fact]
    public void Kisi_kalemleri_yayilmis_havuz_silinmis_kirpma_eksi_kisi_ve_surumler()
    {
        var g = $"g{Guid.NewGuid():N}";
        var orphanGroup = $"g{Guid.NewGuid():N}";
        var (a, b, purged, stranded, spaced, tabbed, neg) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var r = Single(Snap(
            projections:
            [
                P(a, g), P(b, g), P(purged, g, purged: true), P(stranded, orphanGroup, purged: true),
                P(spaced, "x "), P(tabbed, "y\tz"), P(neg, "   "),
            ],
            balances: [B(a, 30m), B(b, 20m), B(purged, 5m), B(stranded, 7m), B(neg, -2m)],
            ledger: [L(a, 30m), L(b, 20m), L(purged, 5m), L(stranded, 7m), L(neg, -2m)],
            pcs: [new(Lic, "0.9.11"), new(Lic, "0.9.11"), new(Lic, null)]));

        (r.SpreadPersons, r.SpreadPoolTotal).Should().Be((1, 55m), "g'de üç üyenin bakiyesi var");
        (r.PurgedWithBalance, r.PurgedBalanceTotal).Should().Be((2, 12m));
        (r.StrandedPurged, r.StrandedPurgedTotal).Should().Be((1, 7m));
        r.Details.StrandedPurged.Should().Equal(stranded);
        (r.TrimMismatches, r.ControlCharGroups).Should().Be((1, 1), "yalnız boşluk numara grupsuz sayılır");
        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0));
        r.ActivePcVersions.Should().BeEquivalentTo(new Dictionary<string, int> { ["0.9.11"] = 2, ["yok"] = 1 });
    }

    [Fact]
    public void Yakalanacak_taban_satirlari_tekildir_ve_isaret_eklenir()
    {
        var row = Guid.NewGuid();
        var s = Snap(projections: [P(row)], balances: [B(row, -10m)], ledger: [L(row, -8m)]);
        var r = Single(s);

        var rows = V.BaselineRowsToCapture(s, r, Captured);

        rows.Should().HaveCount(2, "eksi ve D1 aynı satır — bir kez; artı işaret");
        rows.Should().ContainSingle(x => x.WpfCustomerId == row)
            .Which.Should().BeEquivalentTo(new { Balance = -10m, LedgerSum = -8m, CapturedAt = Captured, ReviewedAt = (DateTimeOffset?)null });
        rows.Should().ContainSingle(x => x.WpfCustomerId == V.LicenseMarker);
    }
}

/// <summary><see cref="PersonBalanceVerifier.LoadAsync"/> InMemory'de: anlık görüntü lisansın gerçek satırlarından.</summary>
public sealed class PersonBalanceVerifierLoadTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceVerifierLoadTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Lisansin_verisinden_rapor_kurulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var negative = await kit.MemberAsync(null, -10m);
        var stranded = await kit.MemberAsync(Kit.NewGroup(), 7m, purged: true);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: ex.T);
        await kit.Sales.WithDbAsync(db =>
        {
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = copy, Balance = 3m,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-3), LastSeenAt = DateTimeOffset.UtcNow, AppVersion = "0.9.11",
            });
            db.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-30), LastSeenAt = DateTimeOffset.UtcNow.AddDays(-20),
                AppVersion = "0.9.9",
            });
            return Task.CompletedTask;
        });

        using var scope = _factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>();
        var report = V.Analyze(await verifier.LoadAsync(kit.LicenseId, DateTimeOffset.UtcNow, default));

        var r = report.Licenses.Should().ContainSingle().Subject;
        r.LicenseId.Should().Be(kit.LicenseId);
        (r.D4, r.D5, r.D6, r.OrphanLegs).Should().Be((0, 0, 0, 0), "Örnek 1 tutarlı");
        r.SpreadPersons.Should().Be(0, "Örnek 1'in sonunda yalnız Y'de para var");
        r.NegativeRows.Should().Be(new V.Tally(0, 1, -10m));
        r.Details.NegativeOutside.Should().Equal(negative);
        r.LedgerMismatches.Outside.Should().Be(1, "kopyanın 3'ü defterde yok");
        (r.StrandedPurged, r.StrandedPurgedTotal).Should().Be((1, 7m));
        r.Details.StrandedPurged.Should().Equal(stranded);
        (r.CopyRowsWithBalance, r.CopyRows.Outside).Should().Be((1, 1));
        r.ActivePcVersions.Should().BeEquivalentTo(new Dictionary<string, int> { ["0.9.11"] = 1 });
        report.ProjectionCount.Should().BeGreaterThan(0);
        report.TransactionCount.Should().BeGreaterThan(0);
    }
}

/// <summary>
/// Taban yakalaması her lisansa işaret yazar (plan fark 7): lisanssız yük verisi olmayan lisansı da getirir. Yakalama
/// anında verisi olmayan ama ondan önce verilmiş bir lisans sonradan sapma alırsa uyarır — §8.1 susmayı yalnız S1
/// deploy'u ile §7.1 adım 2 arasına verir. Kendi fabrikasıyla: yakalama fabrikadaki bütün lisanslara yazar.
/// </summary>
public sealed class PersonBalanceVerifierCaptureTests
{
    [Fact]
    public async Task Yakalamada_verisi_olmayan_eski_lisans_isaret_alir_sonraki_eksi_satirda_uyarir()
    {
        using var factory = new ApiFactory();
        var kit = await Kit.CreateAsync(factory);
        await kit.MemberAsync(null, 5m);
        var idle = await kit.Sales.SecondLicenseAsync(SalesSyncMode.Off);   // yakalamada verisi yok
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(1);   // iki lisans da yakalamadan önce verilmiş

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var snapshot = await scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>()
                .LoadAsync(null, capturedAt, default);
            foreach (var license in V.Analyze(snapshot).Licenses)
                db.PersonBalanceBaselines.AddRange(V.BaselineRowsToCapture(snapshot, license, capturedAt));
            await db.SaveChangesAsync();
        }

        var negative = Guid.NewGuid();
        await idle.WithDbAsync(db =>
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = negative, LicenseId = idle.LicenseId, Platform = "youtube", Username = $"p{Guid.NewGuid():N}",
                UpdatedAt = capturedAt.AddHours(1),
            });
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = idle.LicenseId, WpfCustomerId = negative, Balance = -4m,
                UpdatedAt = capturedAt.AddHours(1),
            });
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = idle.LicenseId, WpfCustomerId = negative, Amount = -4m,
                Kind = Kit.KindManual, CreatedByCustomerId = idle.CustomerId, CreatedAt = capturedAt.AddHours(1),
            });
            return Task.CompletedTask;
        });

        using var after = factory.Services.CreateScope();
        var report = V.Analyze(await after.ServiceProvider.GetRequiredService<PersonBalanceVerifier>()
            .LoadAsync(null, capturedAt.AddDays(1), default));

        var r = report.Licenses.Should().ContainSingle(l => l.LicenseId == idle.LicenseId).Subject;
        (r.HasMarker, r.HasBaseline).Should().Be((true, true), "yakalama verisiz lisansa da işaret yazmalı");
        r.Alerts.Should().ContainSingle().Which.Should().Be("eksi: taban dışı 1, daha da eksiye inen 0");
        r.Details.NegativeOutside.Should().Equal(negative);
    }
}

/// <summary>
/// <see cref="PersonBalanceVerifier.LoadAsync"/> gerçek SQL Server'da: günlük iş prod'da lisanssız (<c>null</c>) yükü
/// koşar; sorguların (null'da düşen lisans süzgeci, defter gruplaması, geri alma hedefleri) çevrildiği ve lisanslı
/// yükle aynı raporu verdiği yalnız burada kanıtlanır.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceVerifierRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PersonBalanceVerifierRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Lisansli_ve_lisanssiz_yukleme_ayni_raporu_kurar_verisiz_lisans_bos_raporla_gelir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var negative = await kit.MemberAsync(null, -10m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: ex.T);
        await kit.Sales.WithDbAsync(db =>
        {
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = copy, Balance = 3m,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });
        var idle = await kit.Sales.SecondLicenseAsync(SalesSyncMode.Off);   // verisiz lisans

        using var scope = _factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<PersonBalanceVerifier>();
        var now = DateTimeOffset.UtcNow;
        var single = V.Analyze(await verifier.LoadAsync(kit.LicenseId, now, default));
        var all = V.Analyze(await verifier.LoadAsync(null, now, default));

        all.Licenses.Single(l => l.LicenseId == kit.LicenseId).Should().BeEquivalentTo(single.Licenses.Single());
        var empty = all.Licenses.Should().ContainSingle(l => l.LicenseId == idle.LicenseId,
            "taban yakalaması verisiz lisansa da işaret yazar (fark 7)").Subject;
        (empty.NegativeRows, empty.LedgerMismatches, empty.CopyRows, empty.SpreadPersons, empty.Alerts.Count)
            .Should().Be((default(V.Tally), default(V.Tally), default(V.Tally), 0, 0));
        var r = single.Licenses.Single();
        (r.D4, r.D5, r.D6, r.OrphanLegs).Should().Be((0, 0, 0, 0), "grup geri alması bütün bacakları kapsar");
        (r.SpreadPersons, r.SpreadPoolTotal).Should().Be((1, 130m), "geri almadan sonra I'da 100, Y'de 30");
        r.NegativeRows.Should().Be(new V.Tally(0, 1, -10m));
        r.Details.NegativeOutside.Should().Equal(negative);
        (r.CopyRowsWithBalance, r.LedgerMismatches.Outside).Should().Be((1, 1));
        single.TransactionCount.Should().Be(9, "üç üye hareketi (I, Y, eksi), üç bacak, üç geri alma; T'nin hareketi yok");
    }
}
