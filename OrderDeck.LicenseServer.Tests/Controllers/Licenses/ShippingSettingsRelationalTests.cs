using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>PUT shipping-settings</c>'in eşzamanlı yazım yarışı gerçek SQL Server'da — spec 2026-10-13 §4.8. Aynı sürümü
/// bekleyen iki yazımın ikisi de sürüm denetimini geçer; hakem veritabanıdır: ilk eklemede birincil anahtar,
/// güncellemede <c>Version</c> jetonu. InMemory'de ikisi de yarışmaz, bu yüzden burada.
///
/// <para>Sıra kancayla kurulur (zamanlamayla değil): birinci isteğin ayar yazımı diske inmeden hemen önce ikinci
/// istek baştan sona koşar. Kanca yalnız ayar satırını yazan kaydetmede tetiklenir; isteğin kalp atışı (kopya
/// satırı) ondan önce ayrı kaydedilir ve kancayı tetiklemez.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class ShippingSettingsRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private ShippingHookedApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _licenseId;

    public ShippingSettingsRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new ShippingHookedApiFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        _licenseId = await SeedLicenseAsync(customerId);
    }

    public Task DisposeAsync()
    {
        _factory.Hook.BeforeShippingSave = null;
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Satır yokken <c>expectedVersion = 0</c> ile iki yazım: ikisi de eklemeye gider; ikincisi birincil
    /// anahtara çarpar ve 500 değil 409 <c>stale</c> alır, kazananın satırını görür. Tek satır kalır.</summary>
    [Fact]
    public async Task Eszamanli_iki_ilk_yazimdan_biri_200_digeri_409_stale_tek_satir()
    {
        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();
        HttpResponseMessage? rival = null;
        _factory.Hook.BeforeShippingSave = async () =>
        {
            _factory.Hook.BeforeShippingSave = null;
            rival = await PutAsync(winner, expectedVersion: 0, 500m, 60m);
        };

        var first = await PutAsync(loser, expectedVersion: 0, 400m, 50m);

        rival.Should().NotBeNull("kanca ikinci yazımı birincinin eklemesinden önce koşturmalı");
        rival!.StatusCode.Should().Be(HttpStatusCode.OK, await rival.Content.ReadAsStringAsync());
        first.StatusCode.Should().Be(HttpStatusCode.Conflict, await first.Content.ReadAsStringAsync());
        await AssertStaleAsync(first, version: 1, threshold: 500m, fee: 60m, updatedBy: winner);

        var rows = await RowsAsync();
        rows.Should().ContainSingle();
        rows[0].Should().BeEquivalentTo(new
        {
            Version = 1, FreeShippingThreshold = 500m, ShippingFee = 60m, UpdatedBy = winner.ToString(),
        });
    }

    /// <summary>Satır sürüm 1'deyken iki yazım <c>expectedVersion = 1</c> ile: ikisi de güncellemeye gider; ikincisi
    /// <c>Version</c> jetonuna çarpar (son gelen kazanmaz) ve 409 <c>stale</c> alır.</summary>
    [Fact]
    public async Task Eszamanli_iki_guncellemeden_biri_200_digeri_409_stale()
    {
        await SeedSettingsAsync(version: 1, 300m, 30m);
        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();
        HttpResponseMessage? rival = null;
        _factory.Hook.BeforeShippingSave = async () =>
        {
            _factory.Hook.BeforeShippingSave = null;
            rival = await PutAsync(winner, expectedVersion: 1, 500m, 60m);
        };

        var first = await PutAsync(loser, expectedVersion: 1, 400m, 50m);

        rival.Should().NotBeNull();
        rival!.StatusCode.Should().Be(HttpStatusCode.OK, await rival.Content.ReadAsStringAsync());
        first.StatusCode.Should().Be(HttpStatusCode.Conflict, await first.Content.ReadAsStringAsync());
        await AssertStaleAsync(first, version: 2, threshold: 500m, fee: 60m, updatedBy: winner);

        (await RowsAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Version = 2, FreeShippingThreshold = 500m, ShippingFee = 60m, UpdatedBy = winner.ToString(),
        });
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PutAsync(Guid replicaId, int expectedVersion, decimal? threshold, decimal? fee) =>
        _client.PutAsJsonAsync($"/api/v1/licenses/{_licenseId}/shipping-settings", new
        {
            expectedVersion, freeShippingThreshold = threshold, fee, replicaId, protocol = 2, seenMode = 1,
        });

    private static async Task AssertStaleAsync(
        HttpResponseMessage resp, int version, decimal threshold, decimal fee, Guid updatedBy)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("title").GetString().Should().Be("stale");
        var current = doc.RootElement.GetProperty("current");
        current.GetProperty("version").GetInt32().Should().Be(version);
        current.GetProperty("freeShippingThreshold").GetDecimal().Should().Be(threshold);
        current.GetProperty("fee").GetDecimal().Should().Be(fee);
        current.GetProperty("updatedBy").GetString().Should().Be(updatedBy.ToString());
    }

    private async Task<List<LicenseShippingSettings>> RowsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.LicenseShippingSettings.AsNoTracking().Where(s => s.LicenseId == _licenseId).ToListAsync();
    }

    private async Task SeedSettingsAsync(int version, decimal threshold, decimal fee)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.LicenseShippingSettings.Add(new LicenseShippingSettings
        {
            LicenseId = _licenseId, Version = version, FreeShippingThreshold = threshold, ShippingFee = fee,
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1), UpdatedBy = LicenseShippingSettings.UpdatedByCli,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedLicenseAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            SalesSyncMode = SalesSyncMode.On,
            SalesSyncModeChangedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary>Yalnız ayar satırını ekleyen ya da güncelleyen kaydetmenin hemen öncesinde koşar; yeniden girmez (kanca
    /// gövdesindeki rakip istek kendi kaydetmesinde onu tetiklemez).</summary>
    private sealed class ShippingSaveHook : SaveChangesInterceptor
    {
        private bool _running;

        public Func<Task>? BeforeShippingSave { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (BeforeShippingSave is { } hook && !_running
                && eventData.Context!.ChangeTracker.Entries<LicenseShippingSettings>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                _running = true;
                try { await hook(); }
                finally { _running = false; }
            }
            return result;
        }
    }

    private sealed class ShippingHookedApiFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public ShippingSaveHook Hook { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
