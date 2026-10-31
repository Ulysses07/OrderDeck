using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Gate = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceEnableGate;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalanceEnableGate"/>: spec 2026-10-11 §7.2 "<c>enable</c> ön koşulları" 1–4. 1: son
/// <c>ActivePcWindowDays</c> günde görülmüş, devre dışı bırakılmamış her PC alt sınırda; alt sınır boş ya da bozuksa
/// tutmaz. 2: D4/D5/D6/yetim bacak/kırpma farkı 0. 3: taban var, hepsi incelenmiş, tabana göre yeni sapma yok. 4: Wh ⊇ Ws.
/// Salt okuma, InMemory yeter (yarış iddiası yok).
/// </summary>
public sealed class PersonBalanceEnableGateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceEnableGateTests(ApiFactory factory) => _factory = factory;

    private static PersonBalanceOptions Options(
        string? pb = "0.9.11", int window = PersonBalanceOptions.DefaultActivePcWindowDays) =>
        new() { MinClientVersion = pb, ActivePcWindowDays = window };

    private static SalesSyncOptions Sales(string? ss = "0.9.11") => new() { MinClientVersion = ss };

    /// <summary>
    /// Bir yıl önce verilmiş lisans. Veritabanı sınıfın testleri arasında paylaşılır ve ilk yakalama anı bütün
    /// lisansların işaretlerinin en küçük <c>CapturedAt</c>'idir. O andan SONRA verilen lisans işaretsiz de "tabanı var"
    /// sayılır (<c>PersonBalanceVerifier.cs:308</c>). Geri tarihleme bu yüzden gerekli: aksi hâlde tabansız lisans
    /// testi, başka bir testin işaretine göre geçerdi.
    /// </summary>
    private async Task<Kit> OldLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.Sales.WithDbAsync(async db =>
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1));
        return kit;
    }

    /// <summary>Taban alınmış ve işareti incelenmiş, verisi olmayan eski lisans.</summary>
    private async Task<Kit> ReadyLicenseAsync()
    {
        var kit = await OldLicenseAsync();
        await MarkerAsync(kit);
        return kit;
    }

    private static Task MarkerAsync(Kit kit, bool reviewed = true) =>
        BaselineRowAsync(kit, PersonBalanceVerifier.LicenseMarker, 0m, 0m, reviewed);

    private static Task BaselineRowAsync(Kit kit, Guid row, decimal balance, decimal ledgerSum, bool reviewed) =>
        kit.Sales.WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = row, Balance = balance, LedgerSum = ledgerSum,
                CapturedAt = now.AddDays(-1), ReviewedAt = reviewed ? now : null,
            });
            return Task.CompletedTask;
        });

    /// <summary>Etkinleştirme: <paramref name="lastSeenDaysAgo"/> gün önce görülmüş. Parmak izi üretilir.</summary>
    private static async Task<Guid> ActivationAsync(Kit kit, string? version, int lastSeenDaysAgo, bool deactivated = false)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await kit.Sales.WithDbAsync(db =>
        {
            db.Activations.Add(new Activation
            {
                Id = id, LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = now.AddDays(-60), LastSeenAt = now.AddDays(-lastSeenDaysAgo), AppVersion = version,
                DeactivatedAt = deactivated ? now.AddDays(-1) : null,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    private async Task<Gate.Result> EvaluateAsync(
        Kit kit, PersonBalanceOptions? personBalance = null, SalesSyncOptions? salesSync = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await Gate.EvaluateAsync(
            db, kit.LicenseId, personBalance ?? Options(), salesSync ?? Sales(), DateTimeOffset.UtcNow, default);
    }

    // ── Hepsi ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hepsi_tutunca_gecer()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.11", lastSeenDaysAgo: 1);
        // §7.3-1 "davranışı değişecek kişi" bir koşul değil, bilgidir.
        var g = Kit.NewGroup();
        await kit.MemberAsync(g, 30m);
        await kit.MemberAsync(g, 20m);
        // Başka lisansın incelenmemiş işareti bu lisansı bağlamaz (taban satırları bütün lisanslardan yüklenir).
        await MarkerAsync(await OldLicenseAsync(), reviewed: false);

        var r = await EvaluateAsync(kit);

        r.Passes.Should().BeTrue();
        r.Failing.Should().BeEmpty();
        r.WindowDays.Should().Be(PersonBalanceOptions.DefaultActivePcWindowDays);
        (r.Clients.MinClientVersion, r.Clients.RecentActivations, r.Clients.Passes).Should().Be(("0.9.11", 1, true));
        r.Clients.Old.Should().BeEmpty();
        r.Verify.Should().Be(new Gate.VerifyCheck(0, 0, 0, 0, 0, 0));
        r.Baseline.Should().Be(new Gate.BaselineCheck(
            HasMarker: true, HasBaseline: true, Unreviewed: 0, NegativeOutside: 0, Worsened: 0,
            LedgerMismatchOutside: 0, ChangedMismatch: 0, CopyOutside: 0));
        r.Superset.Should().Be(new Gate.SupersetCheck("0.9.11", "0.9.11", true));
    }

    // ── 1. İstemci sürümü ─────────────────────────────────────────────────────

    [Fact]
    public async Task Kosul1_eski_surumlu_etkin_PC_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.11", lastSeenDaysAgo: 1);
        var old = await ActivationAsync(kit, "0.9.10", lastSeenDaysAgo: 2);

        var r = await EvaluateAsync(kit);

        r.Clients.RecentActivations.Should().Be(2);
        r.Clients.Old.Should().Equal(new SalesModeGate.OldActivation(old, "0.9.10"));
        r.Failing.Should().Equal(1);
        r.Passes.Should().BeFalse();
    }

    /// <summary>Sürüm bildirmeyen ya da ayrıştırılamayan sürüm bildiren PC eski sayılır; ham metin olduğu gibi döner.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    public async Task Kosul1_surumsuz_ya_da_ayristirilamayan_PC_eski_sayilir(string? version)
    {
        var kit = await ReadyLicenseAsync();
        var unknown = await ActivationAsync(kit, version, lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit);

        r.Clients.Old.Should().Equal(new SalesModeGate.OldActivation(unknown, version));
        r.Failing.Should().Equal(1);
    }

    /// <summary>
    /// Pencere ≤ 0 ise sorgu hiçbir etkinleştirme döndürmez; koşul açık-hata ile geçer, eski PC'ler görünmezdi.
    /// Kapalı-hata: tutmaz. Web host'ta <c>ValidateOnStart</c>, CLI'da Görev 12'nin denetimi bunu zaten engeller; kapı
    /// bu güvenceyi çağıranın disiplinine bırakmaz.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Kosul1_gecersiz_pencere_tutmaz(int window)
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit, Options(window: window));

        r.WindowDays.Should().Be(window);
        r.Clients.Passes.Should().BeFalse();
        r.Failing.Should().Equal(1);
    }

    [Fact]
    public async Task Kosul1_pencere_disi_ve_devre_disi_PC_sayilmaz()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 20);
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 1, deactivated: true);

        var r = await EvaluateAsync(kit);

        r.Clients.RecentActivations.Should().Be(0);
        r.Clients.Passes.Should().BeTrue();
        r.Passes.Should().BeTrue();
    }

    [Fact]
    public async Task Kosul1_pencere_ayardan_okunur()
    {
        var kit = await ReadyLicenseAsync();
        var stale = await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 20);

        var r = await EvaluateAsync(kit, Options(window: 30));

        r.WindowDays.Should().Be(30);
        r.Clients.Old.Should().ContainSingle().Which.Id.Should().Be(stale);
        r.Failing.Should().Equal(1);
    }

    /// <summary>R06: alt sınır tanımsızsa karşılaştırılmaz, koşul tutmaz (kapalı-hata). 4. koşul da tutmaz.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", "abc")]
    public async Task Kosul1_alt_sinir_bos_ya_da_bozuk_tutmaz(string? pb, string? shown)
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.11", lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit, Options(pb: pb));

        (r.Clients.MinClientVersion, r.Clients.RecentActivations, r.Clients.Passes).Should().Be(((string?)null, 1, false));
        r.Clients.Old.Should().BeEmpty("alt sınır tanımsızken karşılaştırma yapılmaz");
        r.Superset.Should().Be(new Gate.SupersetCheck(shown, "0.9.11", false));
        r.Failing.Should().Equal(1, 4);
    }

    // ── 2. Kural sayıları ─────────────────────────────────────────────────────

    [Fact]
    public async Task Kosul2_yetim_bacak_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 10m);
        // Çapası olmayan bir çekim grubuna bağlı bacak; D1 korunur (bakiye de 4 düşer).
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -4m,
                Kind = Kit.KindDeduction, DrawGroupId = Guid.NewGuid(), CreatedByCustomerId = kit.CustomerId,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance -= 4m;
        });

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(0, 0, 0, OrphanLegs: 1, 0, 0));
        r.Verify.Passes.Should().BeFalse();
        r.Failing.Should().Equal(2);
    }

    /// <summary>D4: çapadan başka türde bacak. Kayıt eşitliği sayının D4'e (D5/D6'ya değil) bağlandığını sabitler.</summary>
    [Fact]
    public async Task Kosul2_D4_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 50m);
        var anchor = Guid.NewGuid();
        // Çapa düşüm (OriginalAmount 100 ≥ 15, D6 yok), bacak elle ayar (D4). Geri alma yok (D5 yok). D1 korunur.
        await kit.Sales.WithDbAsync(async db =>
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-5);
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = anchor, LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -10m, Kind = Kit.KindDeduction,
                OriginalAmount = 100m, DrawGroupId = anchor, CreatedByCustomerId = kit.CustomerId, CreatedAt = at,
            });
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -5m, Kind = Kit.KindManual,
                DrawGroupId = anchor, CreatedByCustomerId = kit.CustomerId, CreatedAt = at,
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance -= 15m;
        });

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(D4: 1, 0, 0, 0, 0, 0));
        r.Failing.Should().Equal(2);
    }

    /// <summary>
    /// D5: iki bacaklı düşüm grubunun yalnız bir bacağı geri alınmış (ya hepsi ya hiçbiri kuralı bozulur; S1 emsali
    /// <c>PersonBalanceVerifierTests.D5_bacaklarin_bir_kismi_geri_alinmis…</c>, <c>reversed: [l1]</c>). Kayıt eşitliği
    /// sayının D5'e (D4/D6'ya değil) bağlandığını sabitler.
    /// </summary>
    [Fact]
    public async Task Kosul2_D5_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var r1 = await kit.MemberAsync(null, 50m);
        var r2 = await kit.MemberAsync(null, 50m);
        // Çapa düşüm (OriginalAmount 15 = −Σ, D6 yok), bacak aynı türde ve alanları boş (D4 yok).
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(r1, 10m), (r2, 5m)], originalAmount: 15m);
        // Yalnız ikinci bacağı geri alan tekil geri alma: grupsuz (DrawGroupId NULL), D1 korunur (r2 +5).
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = r2, Amount = 5m,
                Kind = Kit.KindReversal, ReversesTransactionId = ids[1], CreatedByCustomerId = kit.CustomerId,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == r2))
                .Balance += 5m;
        });

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(0, D5: 1, 0, 0, 0, 0));
        r.Failing.Should().Equal(2);
    }

    /// <summary>D6: bacakların toplamı çapanın <c>OriginalAmount</c>'unu aşar (−Σ = 120 &gt; 100).</summary>
    [Fact]
    public async Task Kosul2_D6_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 150m);
        await kit.SeedDrawAsync(Kit.KindDeduction, [(row, 120m)], originalAmount: 100m);

        var r = await EvaluateAsync(kit);

        r.Verify.Should().Be(new Gate.VerifyCheck(0, 0, D6: 1, 0, 0, 0));
        r.Failing.Should().Equal(2);
    }

    /// <summary>Kırpma farkı = <c>TrimMismatches</c> + <c>ControlCharGroups</c> (plan fark 15): sondaki boşluk yalnız
    /// birincisini, sekme ikisini de sayar.</summary>
    [Theory]
    [InlineData(" ", 1, 0)]
    [InlineData("\t", 1, 1)]
    public async Task Kosul2_kirpma_farki_tutmaz(string suffix, int trim, int control)
    {
        var kit = await ReadyLicenseAsync();
        var g = Kit.NewGroup();
        await kit.MemberAsync(g);
        await kit.MemberAsync(g + suffix);

        var r = await EvaluateAsync(kit);

        (r.Verify.TrimMismatches, r.Verify.ControlCharGroups).Should().Be((trim, control));
        r.Failing.Should().Equal(2);
    }

    // ── 3. Taban ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kosul3_tabani_olmayan_lisans_tutmaz()
    {
        var kit = await OldLicenseAsync();

        var r = await EvaluateAsync(kit);

        (r.Baseline.HasMarker, r.Baseline.HasBaseline).Should().Be((false, false));
        r.Baseline.Passes.Should().BeFalse("önce verify --capture-baseline");
        r.Failing.Should().Equal(3);
    }

    [Fact]
    public async Task Kosul3_incelenmemis_isaret_tutmaz()
    {
        var kit = await OldLicenseAsync();
        await MarkerAsync(kit, reviewed: false);

        var r = await EvaluateAsync(kit);

        (r.Baseline.HasMarker, r.Baseline.HasBaseline, r.Baseline.Unreviewed).Should().Be((true, true, 1));
        r.Failing.Should().Equal(3);
    }

    /// <summary>Tabandaki eksi satır yeni sapma değildir; incelenmemişse açılışı yine engeller.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Kosul3_taban_ici_eksi_satir_incelenmisse_gecer_incelenmemisse_tutmaz(bool reviewed)
    {
        var kit = await ReadyLicenseAsync();
        var negative = await kit.MemberAsync(null, -5m);
        await BaselineRowAsync(kit, negative, -5m, -5m, reviewed);

        var r = await EvaluateAsync(kit);

        r.Baseline.NegativeOutside.Should().Be(0);
        r.Baseline.Unreviewed.Should().Be(reviewed ? 0 : 1);
        r.Failing.Should().Equal(reviewed ? Array.Empty<int>() : new[] { 3 });
    }

    [Fact]
    public async Task Kosul3_taban_disi_yeni_eksi_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        await kit.MemberAsync(null, -5m);

        var r = await EvaluateAsync(kit);

        r.Baseline.NegativeOutside.Should().Be(1);
        r.Failing.Should().Equal(3);
    }

    /// <summary>
    /// Tabandaki eksi satır daha da eksiye inerse (−5 → −8) yeni sapmadır. Taban dışı eksi ve farkı değişen D1 sıfır
    /// kalır; böylece sayı yalnız <c>WorsenedBaselineRows</c>'tan gelebilir.
    /// </summary>
    [Fact]
    public async Task Kosul3_daha_da_eksiye_inen_taban_satiri_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, -5m);
        await BaselineRowAsync(kit, row, -5m, -5m, reviewed: true);
        // Yeni bir elle ayar: defter −8, bakiye −8 (D1 korunur, fark 0 kalır).
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = row, Amount = -3m, Kind = Kit.KindManual,
                CreatedByCustomerId = kit.CustomerId, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance -= 3m;
        });

        var r = await EvaluateAsync(kit);

        (r.Baseline.NegativeOutside, r.Baseline.Worsened, r.Baseline.ChangedMismatch).Should().Be((0, 1, 0));
        r.Failing.Should().Equal(3);
    }

    /// <summary>Taban dışı D1: bakiye defterden 2 fazla. Öbür yeni sapma kalemleri sıfır kalır.</summary>
    [Fact]
    public async Task Kosul3_taban_disi_D1_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 10m);
        await kit.Sales.WithDbAsync(async db =>
            (await db.CustomerBalances.SingleAsync(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == row))
                .Balance += 2m);

        var r = await EvaluateAsync(kit);

        (r.Baseline.NegativeOutside, r.Baseline.Worsened, r.Baseline.LedgerMismatchOutside, r.Baseline.ChangedMismatch,
            r.Baseline.CopyOutside).Should().Be((0, 0, 1, 0, 0));
        r.Failing.Should().Equal(3);
    }

    /// <summary>
    /// Taban dışı kopya satır: kopyaya bağlı hareket ve bakiye. Bakiye defterle tutarlıdır (D1 yok), kopya eksi sayısına
    /// girmez; böylece sayı yalnız <c>CopyRows.Outside</c>'tan gelebilir.
    /// </summary>
    [Fact]
    public async Task Kosul3_taban_disi_kopya_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var canonical = await kit.MemberAsync(null);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: canonical);
        await kit.FundAsync(copy, 3m);

        var r = await EvaluateAsync(kit);

        (r.Baseline.NegativeOutside, r.Baseline.Worsened, r.Baseline.LedgerMismatchOutside, r.Baseline.ChangedMismatch,
            r.Baseline.CopyOutside).Should().Be((0, 0, 0, 0, 1));
        r.Failing.Should().Equal(3);
    }

    /// <summary>
    /// Sahip kararı 2 (29.10, (a)): tabandaki D1 satırının farkı değişirse (burada düzeltilip 0'a inmiş) günlük
    /// uyarıyla aynı kural geçerli olur ve açılış engellenir. Kapıda özel istisna yoktur.
    /// </summary>
    [Fact]
    public async Task Kosul3_farki_degisen_taban_D1_satiri_tutmaz()
    {
        var kit = await ReadyLicenseAsync();
        var row = await kit.MemberAsync(null, 10m);
        // Yakalamada Balance 10, LedgerSum 8 (fark 2); bugün defter 10 (fark 0).
        await BaselineRowAsync(kit, row, 10m, 8m, reviewed: true);

        var r = await EvaluateAsync(kit);

        (r.Baseline.LedgerMismatchOutside, r.Baseline.ChangedMismatch).Should().Be((0, 1));
        r.Failing.Should().Equal(3);
    }

    // ── 4. Wh ⊇ Ws ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("abc", "abc")]
    public async Task Kosul4_SalesSync_alt_siniri_bos_tutmaz(string? ss, string? shown)
    {
        var kit = await ReadyLicenseAsync();

        var r = await EvaluateAsync(kit, salesSync: Sales(ss));

        r.Superset.Should().Be(new Gate.SupersetCheck("0.9.11", shown, false));
        r.Failing.Should().Equal(4);
    }

    /// <summary>Ön sürüm eki aynı numaranın yayımlanmış sürümünden önce gelir (<c>ClientVersion.IsAtLeast</c>).</summary>
    [Theory]
    [InlineData("0.9.10")]
    [InlineData("0.9.11-rc1")]
    public async Task Kosul4_PersonBalance_alt_siniri_kucukse_tutmaz(string pb)
    {
        var kit = await ReadyLicenseAsync();

        var r = await EvaluateAsync(kit, Options(pb: pb), Sales("0.9.11"));

        r.Superset.Should().Be(new Gate.SupersetCheck(pb, "0.9.11", false));
        r.Failing.Should().Equal(4);
    }

    [Theory]
    [InlineData("0.9.12", "0.9.11")]
    [InlineData("0.9.11", "0.9.11")]
    public async Task Kosul4_esit_ya_da_buyuk_tutar(string pb, string ss)
    {
        var kit = await ReadyLicenseAsync();

        var r = await EvaluateAsync(kit, Options(pb: pb), Sales(ss));

        r.Superset.Should().Be(new Gate.SupersetCheck(pb, ss, true));
        r.Passes.Should().BeTrue();
    }

    // ── Özet ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Failing_tutmayan_kosullari_artan_sirayla_verir()
    {
        var kit = await ReadyLicenseAsync();
        await ActivationAsync(kit, "0.9.9", lastSeenDaysAgo: 1);

        var r = await EvaluateAsync(kit, Options(pb: "0.9.10"), Sales("0.9.11"));

        r.Failing.Should().Equal(1, 4);
        r.Passes.Should().BeFalse();
    }
}
