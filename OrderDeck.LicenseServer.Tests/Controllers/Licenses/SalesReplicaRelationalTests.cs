using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Kopya kaydı ve protokolsüz istek izi, gerçek SQL Server'da — spec 2026-10-13 §4.0. InMemory'de ne birincil
/// anahtar yarışı ne de koşullu <c>ExecuteUpdate</c> var: yeni bir kopyanın eşzamanlı iki ilk isteği ve izin
/// veritabanındaki saatlik koşulu ancak burada kanıtlanır.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SalesReplicaRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private HookedRelationalApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _licenseId;

    public SalesReplicaRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new HookedRelationalApiFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        _licenseId = await SeedLicenseAsync(customerId);
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Reset();
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Yeni kopyanın iki isteği aynı anda: ikisi de satırı yok görür ve ekler. Sıra kancayla kurulur (zamanlamayla
    /// değil): birinci isteğin eklemesi diske inmeden ikinci istek baştan sona koşar ve satırı ekler; birinci istek
    /// birincil anahtara çarpar. Beklenen: ikisi de 200, tek satır, ikinci dokunuş kazananın satırına işlendi.
    /// </summary>
    [Fact]
    public async Task Yeni_kopyanin_eszamanli_iki_ilk_istegi_tek_satir_acar_ikisi_de_200()
    {
        var replicaId = Guid.NewGuid();
        HttpResponseMessage? rival = null;
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;
            rival = await _client.PostAsJsonAsync(Url("orders/sync"), V2(replicaId, seenMode: 1));
        };

        var first = await _client.PostAsJsonAsync(Url("sessions/sync"), V2(replicaId, seenMode: 0));

        rival.Should().NotBeNull("kanca ikinci isteği birincinin eklemesinden önce koşturmalı");
        rival!.StatusCode.Should().Be(HttpStatusCode.OK);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.WpfReplicas.AsNoTracking().Where(r => r.LicenseId == _licenseId).ToListAsync();
        rows.Should().ContainSingle();
        var row = rows[0];
        row.ReplicaId.Should().Be(replicaId);
        row.SeenMode.Should().Be(SalesSyncMode.Off, "birinci istek kazananın satırını güncelledi");
        row.SeenModeOnAt.Should().NotBeNull("kazananın (ikinci istek) 1 bildirimi korunur");
        row.LastSeenAt.Should().BeOnOrAfter(row.FirstSeenAt);
    }

    /// <summary>
    /// Protokolsüz istek izi ilişkisel yolda: ilk istek lisansın iz satırını ekler, pencere içindeki istek yazmaz,
    /// pencere dışındaki tek bir koşullu UPDATE ile yeniden yazar. İz Licenses satırına hiç dokunmaz: Licenses'ın
    /// RowVersion'ı (eşzamanlılık jetonu) üç istekte de değişmez, License yazımlarıyla yarışmaz.
    /// </summary>
    [Fact]
    public async Task Protokolsuz_istek_izi_kosullu_update_ile_saatte_bir()
    {
        var licenseVersion = await LicenseVersionAsync();
        var before = DateTimeOffset.UtcNow;
        (await _client.PostAsJsonAsync(Url("orders/sync"), new { orders = Array.Empty<object>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var firstTrace = await TraceAsync();
        firstTrace.Should().BeOnOrAfter(before);

        (await _client.PostAsJsonAsync(Url("shipments/sync"), new { shipments = Array.Empty<object>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await TraceAsync()).Should().Be(firstTrace, "pencere içinde yeniden yazılmaz");

        await SetTraceAsync(DateTimeOffset.UtcNow.AddMinutes(-61));
        var beforeStale = DateTimeOffset.UtcNow;
        (await _client.PostAsJsonAsync(Url("sessions/sync"), new { sessions = Array.Empty<object>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await TraceAsync()).Should().BeOnOrAfter(beforeStale);

        (await LicenseVersionAsync()).Should().Equal(licenseVersion, "iz Licenses satırına yazılmaz");
    }

    /// <summary>
    /// Protokolsüz istek izi bugünkü istemcilerin License yazımlarıyla yarışmaz (§4.0; S1 v0.9.10'un davranışını
    /// değiştirmemeli). Licenses.RowVersion eşzamanlılık jetonudur ve izlenen License'ı okuyup kaydeden yollar
    /// (WPF'in payment-account'u, panelin shopper-code'u) çakışmayı yakalamıyor. Sıra kancayla kurulur: payment-account
    /// License'ı okudu; kaydetmeden hemen önce aynı lisansın v0.9.10 push'u izi yazar. Beklenen: 204 ve yazım indi,
    /// iz de yazıldı.
    /// </summary>
    [Fact]
    public async Task Protokolsuz_istek_izi_payment_account_kaydiyla_yarismaz()
    {
        var holder = $"Hesap-{Guid.NewGuid():N}";
        HttpResponseMessage? legacyPush = null;
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;
            legacyPush = await _client.PostAsJsonAsync(Url("orders/sync"), new { orders = Array.Empty<object>() });
        };

        var resp = await _client.PostAsJsonAsync(Url("payment-account"), new { iban = (string?)null, accountHolder = holder });

        legacyPush.Should().NotBeNull("kanca push'u payment-account'un kaydından önce koşturmalı");
        legacyPush!.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, await resp.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == _licenseId)).PaymentAccountHolder.Should().Be(holder);
        (await TraceAsync()).Should().NotBeNull("iz yine yazılır");
    }

    /// <summary>
    /// <c>sales/state</c> her 30 sn'de bir kalp atışıdır ve kopya satırına yazılır; sahip olduğu oturumun satırına
    /// yazmaz. Oturumun ChangeSeq'i (rowversion, akış imleci §4.4) bu yüzden ilerlemez. Sorgu gerçek SQL Server'da
    /// filtreli indeksle ve kopya satırına bağlı alt sorguyla koşar.
    /// </summary>
    [Fact]
    public async Task State_kalp_atisi_oturumun_ChangeSeq_ini_ilerletmez()
    {
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = _licenseId, ReplicaId = replicaId, Protocol = 2,
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1), LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            db.StreamSessions.Add(new StreamSession
            {
                Id = sessionId, LicenseId = _licenseId, Title = "Yayın", StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                Platforms = "youtube", UpdatedAt = DateTimeOffset.UtcNow, OwnerReplicaId = replicaId,
                OwnerMachineName = "PC-A",
            });
            await db.SaveChangesAsync();
        }
        var seqBefore = await SessionSeqAsync(sessionId);
        var before = DateTimeOffset.UtcNow;

        var resp = await _client.GetAsync($"/api/v1/licenses/{_licenseId}/sales/state?replicaId={replicaId}&seenMode=0");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await SessionSeqAsync(sessionId)).Should().Be(seqBefore);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WpfReplicas.AsNoTracking().SingleAsync(r => r.ReplicaId == replicaId))
                .LastSeenAt.Should().BeOnOrAfter(before);
        }
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain(sessionId.ToString()).And.Contain("\"state\":\"live\"");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private string Url(string endpoint) => $"/api/v1/licenses/{_licenseId}/{endpoint}";

    private static object V2(Guid replicaId, int seenMode) =>
        new { sessions = Array.Empty<object>(), orders = Array.Empty<object>(), replicaId, protocol = 2, seenMode };

    private async Task<long> SessionSeqAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StreamSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.ChangeSeq).SingleAsync();
    }

    /// <summary>Lisansın protokolsüz istek izi; satır yoksa null.</summary>
    private async Task<DateTimeOffset?> TraceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == _licenseId)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync();
    }

    private async Task<byte[]> LicenseVersionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Licenses.AsNoTracking().Where(l => l.Id == _licenseId).Select(l => l.RowVersion).SingleAsync();
    }

    private async Task SetTraceAsync(DateTimeOffset value)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await db.SalesLegacyTraces.Where(t => t.LicenseId == _licenseId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastLegacySalesPushAt, value));
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
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary><see cref="RelationalApiFactory"/> + paylaşılan kaydetme kancası (<see cref="HookedApiFactory"/>'nin
    /// gerçek SQL Server'lı hâli).</summary>
    private sealed class HookedRelationalApiFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public SaveHookInterceptor Hook { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
