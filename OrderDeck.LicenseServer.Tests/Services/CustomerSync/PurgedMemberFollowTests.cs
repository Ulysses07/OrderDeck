using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Silinmiş üyenin numara izlemesi, senkron yolu (spec 2026-10-11 §4.11, §4.12, K3): grup boşalınca silinmiş üye
/// ayrılanların numarasına geçer; Ayır'da, dağılan ayrılışta ve grupsuz asıl kayda kopya dönüşünde eski numarada kalır.
/// </summary>
public sealed class PurgedMemberFollowTests : IClassFixture<PurgedMemberFollowTests.LogFactory>
{
    private const string HookCategory = "OrderDeck.LicenseServer.Services.CustomerSync.PersonGroupSaveHook";

    private readonly LogFactory _factory;

    public PurgedMemberFollowTests(LogFactory factory) => _factory = factory;

    [Fact]
    public async Task Iki_partide_birlesen_grupta_silinmis_uye_son_canli_uye_ayrilinca_yeni_numaraya_gecer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        await kit.MemberAsync(g0);
        var pBalanceBefore = (await kit.BalanceRowAsync(p))!.UpdatedAt;

        (await kit.SyncGroupAsync(a1, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1, "g1'de canlı üye (a2) var");

        var before = DateTimeOffset.UtcNow;
        (await kit.SyncGroupAsync(a2, g0)).EnsureSuccessStatusCode();
        var after = DateTimeOffset.UtcNow;
        var moved = await kit.ProjectionAsync(p);
        moved.GroupId.Should().Be(g0, "g1'i boşaltan kayıttaki ayrılanların numarası");
        // P damgasız kuruldu: CustomerGroupUnion.StampAt koşunun anını yazar — herhangi bir değer değil.
        moved.GroupIdChangedAt.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(after);
        moved.PurgedAt.Should().NotBeNull("silinmişlik değişmez");
        (await kit.BalanceRowAsync(p))!.UpdatedAt.Should().BeAfter(pBalanceBefore, "taşıma da bir GroupId değişimi");
        (await kit.BalanceAsync(p)).Should().Be(40m);
    }

    [Fact]
    public async Task Ayir_da_silinmis_uye_eski_numarada_tek_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);

        (await kit.SyncGroupAsync(a, null)).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    [Fact]
    public async Task Canli_uye_kaldikca_silinmis_uye_yerinde_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);

        (await kit.SyncGroupAsync(a1, Kit.NewGroup())).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    /// <summary>§4.11: ayrılanlar aynı kayıtta farklı numaralara gidince silinmiş üye yerinde kalır ve günlüğe TEK bir
    /// uyarı yazılır — lisans Id'siyle; numara ya da kullanıcı adı (kişisel veri) taşımadan.</summary>
    [Fact]
    public async Task Ayrilanlar_ayni_kayitta_farkli_numaralara_giderse_silinmis_uye_yerinde_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var stamp = DateTimeOffset.UtcNow;
        var to1 = Kit.NewGroup();
        var to2 = Kit.NewGroup();

        var resp = await kit.SendAsync(HttpMethod.Post, $"/api/v1/licenses/{kit.LicenseId}/wpf-customers/sync", new
        {
            customers = new[]
            {
                await SyncItemAsync(kit, a1, to1, stamp),
                await SyncItemAsync(kit, a2, to2, stamp),
            },
        });
        resp.EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
        var warning = HookWarnings(kit).Should().ContainSingle().Subject;
        warning.Should().Contain(kit.LicenseId.ToString()).And.Contain("1 silinmiş üye");
        var names = new[] { a1, a2, p }.Select(async id => (await kit.ProjectionAsync(id)).Username);
        foreach (var name in await Task.WhenAll(names)) warning.Should().NotContain(name);
        warning.Should().NotContain(g1).And.NotContain(to1).And.NotContain(to2);
    }

    /// <summary>
    /// §4.11, §10 açık karar: numaranın son canlı üyesi grupsuz bir asıl kayda kopya olunca (kimlik birleşmesi
    /// <c>--at-twins</c> dışında numaraları birleştirmez) hedef boştur; silinmiş üye eski numarada tek kalır, Ayır gibi.
    /// Günlüğe yazılmaz (verify'ın §7.3-4 kalemi görür).
    /// </summary>
    [Fact]
    public async Task Kopyaya_donen_son_canli_uyenin_asil_kaydi_grupsuzsa_silinmis_uye_eski_numarada_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var canonical = await kit.MemberAsync(null);

        await kit.Sales.WithDbAsync(async db =>
        {
            var row = await db.WpfCustomerProjections.IgnoreQueryFilters().AsTracking().SingleAsync(x => x.Id == a);
            row.MergedIntoId = canonical;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        });

        (await kit.ProjectionAsync(a)).MergedIntoId.Should().Be(canonical);
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
        (await kit.ProjectionAsync(canonical)).GroupId.Should().BeNull();
        HookWarnings(kit).Should().BeEmpty();
    }

    private IReadOnlyList<string> HookWarnings(Kit kit) =>
        _factory.Log.Entries
            .Where(e => e.Category == HookCategory && e.Level == LogLevel.Warning
                        && e.Message.Contains(kit.LicenseId.ToString()))
            .Select(e => e.Message)
            .ToList();

    private static async Task<object> SyncItemAsync(Kit kit, Guid id, string groupId, DateTimeOffset at)
    {
        var row = await kit.ProjectionAsync(id);
        return new
        {
            id, platform = row.Platform, username = row.Username, updatedAt = at, format = 2,
            groupId, groupIdChangedAt = at,
        };
    }

    /// <summary><see cref="ApiFactory"/> + günlük satırları (kategori, düzey, metin); kanca günlüğü bağlamın
    /// <c>ILoggerFactory</c>'sinden, yani uygulamanınkinden yazar.</summary>
    public sealed class LogFactory : ApiFactory
    {
        public LogRecorderProvider Log { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
        }
    }

    public sealed class LogRecorderProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

        public void Dispose() { }

        private sealed class Recorder(LogRecorderProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) owner._entries.Enqueue((category, logLevel, formatter(state, exception)));
            }
        }
    }
}
