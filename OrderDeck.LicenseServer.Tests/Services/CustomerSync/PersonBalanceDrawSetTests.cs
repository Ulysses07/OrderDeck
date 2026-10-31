using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Çekim kümesi P ve 409 kuralları — spec 2026-10-11 §4.0 kural 1–4, §8.3. W = memberIds ∪ {a}, asıl kayda
/// çözülmüş; W'nin yalnız canlıları kullanılır, silinmişler S'den alınır (K3). Panelin kümesi P = S (K5); oynatmanın
/// havuzu 409 kuralları uygulanmadan max(0, Σ). Yalnız okuma: yarış iddiası yok (InMemory).
/// </summary>
public sealed class PersonBalanceDrawSetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceDrawSetTests(ApiFactory factory) => _factory = factory;

    private async Task<T> WithServiceAsync<T>(Func<PersonBalance, Task<T>> body)
    {
        using var scope = _factory.Services.CreateScope();
        return await body(scope.ServiceProvider.GetRequiredService<PersonBalance>());
    }

    private Task<PersonBalance.DrawSetResult> Resolve(Kit kit, Guid a, params Guid[] w) =>
        WithServiceAsync(s => s.ResolveDrawSetAsync(kit.LicenseId, a, w, default));

    private Task<decimal> Pool(Kit kit, Guid a, IReadOnlyCollection<Guid>? w) =>
        WithServiceAsync(s => s.PoolBalanceAsync(kit.LicenseId, a, w, default));

    private static PersonBalance.DrawSet Accepted(PersonBalance.DrawSetResult result)
    {
        result.Rejection.Should().BeNull();
        result.Set.Should().NotBeNull();
        return result.Set!;
    }

    private static PersonBalance.DrawSetRejection Rejected(PersonBalance.DrawSetResult result)
    {
        result.Set.Should().BeNull();
        result.Rejection.Should().NotBeNull();
        return result.Rejection!;
    }

    private static PersonBalance.Member Live(Guid id, decimal balance) => new(id, balance, false);

    // ── Kural 1 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural1_hicbir_W_uyesi_sunucuda_yok_no_balance_kesin()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = Guid.NewGuid();                                    // yeni açılmış, henüz gönderilmemiş sahip satırı

        var rejection = Rejected(await Resolve(kit, a, a, Guid.NewGuid()));

        rejection.Code.Should().Be("no-balance");
        rejection.RowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Kural1_a_yok_ama_bir_uye_var_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var b = await kit.MemberAsync(Kit.NewGroup(), 30m);
        var a = Guid.NewGuid();                                    // sahip satırı sunucuya ulaşmadı, kardeşi ulaştı

        var rejection = Rejected(await Resolve(kit, a, b));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(a);
    }

    // ── Kural 2 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural2_sunucuda_olmayan_W_idsi_atilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 50m);

        var set = Accepted(await Resolve(kit, a, a, Guid.NewGuid()));

        set.AnchorId.Should().Be(a);
        set.Draw.Should().Equal(Live(a, 50m));
        set.LockedNegatives.Should().BeEmpty();
        set.Pool.Should().Be(50m);
    }

    // ── Kural 3 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural3a_W_de_olup_S_de_olmayan_canli_uye_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 10m);
        var x = await kit.MemberAsync(Kit.NewGroup());             // WPF birleştirdi, sunucu henüz görmedi

        var rejection = Rejected(await Resolve(kit, a, a, x));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(x);
    }

    [Fact]
    public async Task Kural3b_S_de_olup_W_de_olmayan_pozitif_canli_uye_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);                     // başka PC ekledi, bu PC henüz çekmedi

        var rejection = Rejected(await Resolve(kit, a, a));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(b);
    }

    [Fact]
    public async Task Kural3a_ve_3b_birlikte_RowIds_birlesim_ordinal_sirali()
    {
        // RowIds = 3a ∪ 3b, "N" ordinal sıralı (§5.5 günlüğü bu sırayı yazar).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);                     // 3b: S'de, W'de yok, pozitif
        var x = await kit.MemberAsync(Kit.NewGroup());             // 3a: W'de, S'de yok

        var rejection = Rejected(await Resolve(kit, a, a, x));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(new[] { b, x }.OrderBy(id => id.ToString("N"), StringComparer.Ordinal));
    }

    [Fact]
    public async Task Kural3b_W_disindaki_sifir_ve_eksi_uyeler_gecer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, balanceRow: false);       // bakiye satırı yok → 0 sayılır, 3b tetiklenmez
        var c = await kit.MemberAsync(g, -5m);

        var set = Accepted(await Resolve(kit, a, a));

        set.Person.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(new[] { a, b, c });
        set.Person.Members.Should().Contain(Live(b, 0m));
        set.Draw.Should().Equal(Live(a, 10m));
        set.LockedNegatives.Should().Equal(Live(c, -5m));          // sıfır (b) yok sayılır
        set.Pool.Should().Be(5m);
    }

    // ── Kural 4 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kural4_A100_B_eksi60_W_A_havuz_40()
    {
        // spec §8.3: A = 100, B = −60, W = {A} → kullanılabilir 40. Görülmeyen borç havuzu açmaz (D3).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);

        var set = Accepted(await Resolve(kit, a, a));

        set.Draw.Should().Equal(Live(a, 100m));
        set.LockedNegatives.Should().Equal(Live(b, -60m));
        set.Pool.Should().Be(40m);
    }

    [Fact]
    public async Task Silinmis_uye_S_den_alinir_W_de_olmasa_da()
    {
        // Silinmiş satırın yerel grup numarası PC'ler arasında ayrışabilir, bu yüzden W'de eksik olabilir; sunucu S'nin
        // silinmişlerini kendisi ekler (K3).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 40m, purged: true);

        var set = Accepted(await Resolve(kit, a, a));

        set.Draw.Should().BeEquivalentTo(new[] { Live(a, 0m), new PersonBalance.Member(p, 40m, true) });
        set.LockedNegatives.Should().BeEmpty();
        set.Pool.Should().Be(40m);
    }

    [Fact]
    public async Task W_deki_silinmis_uye_yok_sayilir()
    {
        // Silinmiş satırın yerel grup numarası PC'ler arasında ayrışabilir: W'deki silinmiş Id ne 3a'yı tetikler
        // ne kümeye girer.
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 10m);
        var z = await kit.MemberAsync(Kit.NewGroup(), 5m, purged: true);

        var set = Accepted(await Resolve(kit, a, a, z));

        set.Draw.Should().Equal(Live(a, 10m));
        set.Draw.Select(m => m.WpfCustomerId).Should().NotContain(z);
        set.Pool.Should().Be(10m);
    }

    // ── Kimlik ve numara ─────────────────────────────────────────────────────

    [Fact]
    public async Task Kopya_Id_asil_kayda_cozulur()
    {
        // Eski bir PC b'yi birleştirmeden önceki kopyanın Id'siyle tanıyor: kopya çözülmeseydi süzgeç onu gizler,
        // kural 2 atar ve b'nin 40'ı kural 3b'yi tetiklerdi.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: b);

        var set = Accepted(await Resolve(kit, a, a, copy));

        set.Draw.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(new[] { a, b });
        set.Pool.Should().Be(40m);
    }

    [Fact]
    public async Task Ordinal_numara_ayri_kisi()
    {
        // §4.13, M5: "g…" ile "G…" ayrı kişi; büyük harfli numaranın satırı W'de → 3a.
        var kit = await Kit.CreateAsync(_factory);
        var stem = Kit.NewGroup();
        var a = await kit.MemberAsync(stem, 10m);
        var x = await kit.MemberAsync(stem.ToUpperInvariant(), 20m);

        var rejection = Rejected(await Resolve(kit, a, a, x));

        rejection.Code.Should().Be("customer-not-synced");
        rejection.RowIds.Should().Equal(x);
    }

    [Fact]
    public async Task Grupsuz_a_tek_uyeli_kisi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 20m);
        await kit.MemberAsync(null, 7m);                           // başka grupsuz satır kişiye girmez

        var set = Accepted(await Resolve(kit, a, a));

        set.Person.Members.Should().Equal(Live(a, 20m));
        set.Draw.Should().Equal(Live(a, 20m));
        set.Pool.Should().Be(20m);
    }

    // ── Panel (K5) ve oynatma ────────────────────────────────────────────────

    [Fact]
    public async Task PersonDrawSetAsync_S_nin_hepsi_eksiler_Draw_icinde()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);

        var set = await WithServiceAsync(s => s.PersonDrawSetAsync(kit.LicenseId, a, default));

        set.AnchorId.Should().Be(a);
        set.Draw.Should().BeEquivalentTo(new[] { Live(a, 10m), Live(b, 40m), Live(c, -20m) });
        set.LockedNegatives.Should().BeEmpty();
        set.Pool.Should().Be(30m);
    }

    [Fact]
    public async Task PoolBalanceAsync_memberIds_yokken_S_toplami()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m);
        await kit.MemberAsync(g, -10m);

        (await Pool(kit, a, null)).Should().Be(30m);
        (await Pool(kit, a, Array.Empty<Guid>())).Should().Be(30m);
    }

    [Fact]
    public async Task PoolBalanceAsync_kural_ihlalinde_ret_vermez()
    {
        // Kayıtlı anahtarın oynatması customer-not-synced almaz (§4.2): 3b ihlalinde de Σ_P döner.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);

        (await Pool(kit, a, new[] { a })).Should().Be(0m);
        (await Pool(kit, a, new[] { a, b })).Should().Be(40m);
    }

    [Fact]
    public async Task PoolBalanceAsync_eksi_havuz_sifir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, -60m);

        (await Pool(kit, a, new[] { a, b })).Should().Be(0m);
    }
}
