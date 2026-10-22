using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// §8.2-13: silinmiş üyenin numara izlemesi gerçek SQL Server'da — senkronla iki partide birleşme, Ayır,
/// <c>group-customers</c> ve <c>--at-twins</c> (iki iş de işlem açar; InMemory'de koşmaz). BIN2 harmanında SQL aday
/// daraltır, eşitlik bellekte ordinal. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PurgedMemberFollowRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PurgedMemberFollowRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Senkronla_iki_partide_birlesmede_silinmis_uye_yeni_numaraya_Ayir_da_eski_numarada()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        // Sondaki boşlukla ayrılan numara başka kişidir: oradaki silinmiş üye taşınmaz (ordinal).
        var pSpaced = await kit.MemberAsync(g1 + " ", 5m, purged: true);

        (await kit.SyncGroupAsync(a1, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
        (await kit.SyncGroupAsync(a2, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g0);
        (await kit.ProjectionAsync(pSpaced)).GroupId.Should().Be(g1 + " ");

        var g2 = Kit.NewGroup();
        var b = await kit.MemberAsync(g2);
        var q = await kit.MemberAsync(g2, 7m, purged: true);
        (await kit.SyncGroupAsync(b, null)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(q)).GroupId.Should().Be(g2, "Ayır: silinmiş üye eski numarada tek kalır (K3 istisnası)");
    }

    [Fact]
    public async Task group_customers_sonrasi_silinmis_uye_hedef_numarada()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var buyer = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var registered = await kit.MemberAsync(g0);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var report = await new CustomerGroupingJob(db)
                .RunAsync(kit.LicenseId, [new CustomerGroupingJob.Pair(buyer, registered)], apply: true, default);
            report.FailedComponents.Should().Be(0);
        }

        (await kit.ProjectionAsync(buyer)).GroupId.Should().Be(g0);
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g0);
    }

    [Fact]
    public async Task at_twins_sonrasi_kopyaya_donen_son_canli_uyenin_silinmis_esi_hedef_numarada()
    {
        // g1 = {"@x" (canlı), P (silinmiş)}; g0 = {"x" (canlı), B}. "@x" "x"e birleşir ve kopyaya döner: g1'in son
        // canlı üyesi GroupId değiştirmeden ayrılır (fark 5); hedef asıl kaydın numarası g0.
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var name = $"u{Guid.NewGuid():N}"[..16];
        var twin = await kit.MemberAsync(g1, platform: "instagram", username: "@" + name);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var canonical = await kit.MemberAsync(g0, platform: "instagram", username: name);
        await kit.MemberAsync(g0);

        using (var scope = _factory.Services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>()
                .RunAsync(kit.LicenseId, apply: true, default, atTwins: true);
            report.FailedGroups.Should().Be(0);
        }

        (await kit.ProjectionAsync(twin)).MergedIntoId.Should().Be(canonical);
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g0);
    }
}
