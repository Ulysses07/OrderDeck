using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// CustomerIdResolver (A5b): müşteri Id'si kabul eden her sınırın tek
/// çözümleyicisi — kopya asıl kayda çevrilir; kopya olmayan (asıl kayıt,
/// bilinmeyen Id, başka lisansın Id'si) aynen döner. InMemory yeterli: yalnız
/// okuma yapıyor ve sorgu filtresi InMemory'de de uygulanıyor.
/// </summary>
public sealed class CustomerIdResolverTests
{
    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"resolver-{Guid.NewGuid():N}").Options);

    private static WpfCustomerProjection Row(LicenseDbContext db, Guid licenseId, Guid? mergedInto = null)
    {
        var row = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Platform = "tiktok",
            Username = "r-" + Guid.NewGuid().ToString("N")[..8],
            MergedIntoId = mergedInto,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WpfCustomerProjections.Add(row);
        db.SaveChanges();
        return row;
    }

    /// <summary>Yayıncı (kiracı) + lisansı. Kiracı denetimi yalnız
    /// <c>License.CustomerId</c>'ye bakar; Customer satırı gerekmez.</summary>
    private static (Guid Tenant, Guid LicenseId) SeedLicense(LicenseDbContext db)
    {
        var tenant = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = tenant,
            LicenseKey = "LDK-RES-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        db.SaveChanges();
        return (tenant, licenseId);
    }

    [Fact]
    public async Task Kopya_olmayan_ve_bilinmeyen_Id_kendisine_cozulur()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var canonical = Row(db, lic);
        var unknown = Guid.NewGuid();

        var map = await new CustomerIdResolver(db)
            .CanonicalOfAsync(lic, [canonical.Id, unknown], CancellationToken.None);

        map[canonical.Id].Should().Be(canonical.Id);
        map[unknown].Should().Be(unknown);
    }

    [Fact]
    public async Task Kopya_asil_kayda_cozulur()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var canonical = Row(db, lic);
        var alias = Row(db, lic, canonical.Id);

        (await new CustomerIdResolver(db).CanonicalOfAsync(lic, alias.Id, CancellationToken.None))
            .Should().Be(canonical.Id);
    }

    [Fact]
    public async Task Zincir_asil_kayda_kadar_izlenir()
    {
        // Zinciri birleştirme işi düzleştirir; çözümleyici yine de savunma
        // amaçlı izler (kopya → kopya → asıl).
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var canonical = Row(db, lic);
        var middle = Row(db, lic, canonical.Id);
        var alias = Row(db, lic, middle.Id);

        var map = await new CustomerIdResolver(db)
            .CanonicalOfAsync(lic, [alias.Id, middle.Id], CancellationToken.None);

        map[alias.Id].Should().Be(canonical.Id);
        map[middle.Id].Should().Be(canonical.Id);
    }

    [Fact]
    public async Task Baska_lisansin_kopyasi_cozulmez()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var otherLic = Guid.NewGuid();
        var canonical = Row(db, otherLic);
        var alias = Row(db, otherLic, canonical.Id);

        (await new CustomerIdResolver(db).CanonicalOfAsync(lic, alias.Id, CancellationToken.None))
            .Should().Be(alias.Id, "çağıranın lisansında böyle bir kopya yok — Id aynen döner");
    }

    [Fact]
    public async Task Zincir_CanonicalOf_ile_ayni_kayda_varir_izledigi_kopyalarin_bayragini_tasir()
    {
        // lic: K3→K2→K1→P (K2 geçici kopya), Q→R (Q silinmiş kopya), S (geçici ASIL kayıt), X→Y (Y başka lisansta),
        // Z4→Z3→Z2→Z1→W (4 adım), N (projeksiyonu yok).
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var p = Row(db, lic);
        var k1 = Row(db, lic, p.Id);
        var k2 = Row(db, lic, k1.Id);
        var k3 = Row(db, lic, k2.Id);
        var r = Row(db, lic);
        var q = Row(db, lic, r.Id);
        var s = Row(db, lic);
        var y = Row(db, Guid.NewGuid());
        var x = Row(db, lic, y.Id);
        var w = Row(db, lic);
        var z1 = Row(db, lic, w.Id);
        var z2 = Row(db, lic, z1.Id);
        var z3 = Row(db, lic, z2.Id);
        var z4 = Row(db, lic, z3.Id);
        var n = Guid.NewGuid();
        k2.CreatedByShopper = true;
        q.PurgedAt = DateTimeOffset.UtcNow;
        s.CreatedByShopper = true;
        db.SaveChanges();
        Guid[] ids = [k3.Id, k2.Id, k1.Id, p.Id, q.Id, r.Id, s.Id, x.Id, y.Id, z4.Id, z3.Id, z2.Id, z1.Id, w.Id, n];
        var resolver = new CustomerIdResolver(db);

        var chains = await resolver.ChainOfAsync(lic, ids, CancellationToken.None);
        var canonical = await resolver.CanonicalOfAsync(lic, ids, CancellationToken.None);

        ids.Should().OnlyContain(id => chains[id].Canonical == canonical[id]);
        chains[k3.Id].Should().Be(new CustomerChain(p.Id, ViaProvisionalCopy: true, ViaPurgedCopy: false));
        chains[k1.Id].Should().Be(new CustomerChain(p.Id, false, false));
        chains[q.Id].Should().Be(new CustomerChain(r.Id, false, true));
        chains[s.Id].Should().Be(new CustomerChain(s.Id, false, false), "asıl kaydın kendi bayrağı zincir değildir");
        (chains[x.Id].Canonical, chains[z4.Id].Canonical, chains[n].Canonical).Should().Be((y.Id, z1.Id, n));
    }

    [Fact]
    public async Task Hex_surumu_kopyayi_asil_hexe_cevirir_digerinin_yazimina_dokunmaz()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var canonical = Row(db, lic);
        var alias = Row(db, lic, canonical.Id);
        var aliasHex = alias.Id.ToString("N");
        var canonicalUpperHex = canonical.Id.ToString("N").ToUpperInvariant();

        var map = await new CustomerIdResolver(db)
            .CanonicalHexOfAsync(lic, [aliasHex, canonicalUpperHex, aliasHex], CancellationToken.None);

        map[aliasHex].Should().Be(canonical.Id.ToString("N"));
        map[canonicalUpperHex].Should().Be(canonicalUpperHex, "kopya olmayan değer aynen döner");
    }

    [Fact]
    public async Task Guid_olmayan_hex_aynen_doner()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var dashed = Guid.NewGuid().ToString("D");

        var map = await new CustomerIdResolver(db)
            .CanonicalHexOfAsync(lic, ["c1hex", "", dashed], CancellationToken.None);

        map["c1hex"].Should().Be("c1hex");
        map[""].Should().Be("");
        map[dashed].Should().Be(dashed);
    }

    [Fact]
    public async Task Kiraci_icin_kopyanin_lisansi_ve_asil_kaydi_bulunur()
    {
        using var db = NewDb();
        var (tenant, lic) = SeedLicense(db);
        var canonical = Row(db, lic);
        var alias = Row(db, lic, canonical.Id);
        var resolver = new CustomerIdResolver(db);

        (await resolver.LocateForTenantAsync(tenant, alias.Id, CancellationToken.None))
            .Should().Be((lic, canonical.Id));
        (await resolver.LocateForTenantAsync(tenant, canonical.Id, CancellationToken.None))
            .Should().Be((lic, canonical.Id));
    }

    [Fact]
    public async Task Baska_kiracinin_kopyasi_ve_bilinmeyen_Id_bulunmaz()
    {
        using var db = NewDb();
        var (tenantA, licA) = SeedLicense(db);
        var (tenantB, _) = SeedLicense(db);
        var canonical = Row(db, licA);
        var alias = Row(db, licA, canonical.Id);
        var resolver = new CustomerIdResolver(db);

        (await resolver.LocateForTenantAsync(tenantB, alias.Id, CancellationToken.None))
            .Should().BeNull("kiracı yalıtımı kopyalar için de geçerli");
        (await resolver.LocateForTenantAsync(tenantA, Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }
}
