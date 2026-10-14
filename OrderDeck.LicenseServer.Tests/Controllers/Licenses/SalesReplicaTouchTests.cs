using System.Net;
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
/// Satış gönderim uçlarının (<c>sessions/sync</c>, <c>orders/sync</c>, <c>shipments/sync</c>) istek düzeyindeki
/// zarfı — spec 2026-10-13 §4.0. v2 isteği kopya satırını açar ya da kalp atışını ilerletir; protokolsüz (v1)
/// istek kopya açmaz, iki kipte de lisansın <c>SalesLegacyTraces</c> izini saatte en çok bir kez yazar. InMemory: iz
/// burada izlenen varlıkla yazılıyor; ilişkisel koşullu UPDATE yolu <see cref="SalesReplicaRelationalTests"/>'te.
/// </summary>
public sealed class SalesReplicaTouchTests : IClassFixture<ApiFactory>
{
    private const string WpfUserAgent = "OrderDeck-WPF/0.9.11";

    private readonly ApiFactory _factory;

    public SalesReplicaTouchTests(ApiFactory factory) => _factory = factory;

    public static TheoryData<string> Endpoints => new() { "sessions/sync", "orders/sync", "shipments/sync" };

    public static TheoryData<string, SalesSyncMode> EndpointsByMode => new()
    {
        { "sessions/sync", SalesSyncMode.Off },
        { "orders/sync", SalesSyncMode.Off },
        { "shipments/sync", SalesSyncMode.Off },
        { "sessions/sync", SalesSyncMode.On },
        { "orders/sync", SalesSyncMode.On },
        { "shipments/sync", SalesSyncMode.On },
    };

    /// <summary>v2 isteği kopya satırını açar: ilk ve son görülme aynı an, sürüm User-Agent'tan, protokol ve görülen
    /// kip istekten. Yetenek düzeyi hello'ya kadar 0. İkinci istek <c>FirstSeenAt</c>'e dokunmaz, kalp atışını
    /// ilerletir, sürümü tazeler. v2 isteği protokolsüz iz yazmaz.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task V2_istek_kopya_satirini_acar_ve_kalp_atisini_ilerletir(string endpoint)
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        (await PushAsync(client, licenseId, endpoint, V2(replicaId, seenMode: 0), WpfUserAgent))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReplicaAsync(licenseId, replicaId);
        created.Should().NotBeNull();
        created!.FirstSeenAt.Should().BeOnOrAfter(startedAt);
        created.LastSeenAt.Should().Be(created.FirstSeenAt);
        created.ClientVersion.Should().Be("0.9.11");
        created.Protocol.Should().Be(2);
        created.FeatureLevel.Should().Be(0);
        created.SeenMode.Should().Be(SalesSyncMode.Off);
        created.SeenModeAt.Should().BeOnOrAfter(startedAt);
        created.SeenModeOnAt.Should().BeNull();

        var firstSeen = startedAt.AddHours(-2);
        var lastSeen = startedAt.AddHours(-1);
        await UpdateReplicaAsync(licenseId, replicaId, r => { r.FirstSeenAt = firstSeen; r.LastSeenAt = lastSeen; });

        (await PushAsync(client, licenseId, endpoint, V2(replicaId, seenMode: 0), "OrderDeck-WPF/0.9.12"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var touched = await ReplicaAsync(licenseId, replicaId);
        touched!.FirstSeenAt.Should().Be(firstSeen, "ilk görülme yalnız eklemede yazılır");
        touched.LastSeenAt.Should().BeOnOrAfter(startedAt, "her v2 isteği kalp atışıdır");
        touched.ClientVersion.Should().Be("0.9.12");
        (await LegacyTraceAsync(licenseId)).Should().BeNull("v2 isteği protokolsüz iz değildir");
    }

    /// <summary>Zarf istek düzeyinde ek alan; paketin kendisi bugünkü gibi işlenir.</summary>
    [Fact]
    public async Task V2_zarfli_paket_bugunku_gibi_islenir()
    {
        var (client, licenseId) = await SetupAsync();
        var sessionId = Guid.NewGuid();
        var body = new
        {
            sessions = new[]
            {
                new { id = sessionId, title = "Akşam", startedAt = DateTimeOffset.UtcNow, endedAt = (DateTimeOffset?)null,
                      platforms = "youtube", notes = (string?)null },
            },
            replicaId = Guid.NewGuid(),
            protocol = 2,
            seenMode = 0,
        };

        (await PushAsync(client, licenseId, "sessions/sync", body, WpfUserAgent))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.StreamSessions.AnyAsync(s => s.Id == sessionId && s.LicenseId == licenseId)).Should().BeTrue();
    }

    /// <summary>Protokolsüz istek (bugünkü istemci) kopya satırı açmaz ve iki kipte de iz yazar: mod kapısının
    /// "7 gün eski sürüm izi yok" koşulu (§4.10-3) ancak mod Off'ta da yazılan izle ölçülebilir.</summary>
    [Theory]
    [MemberData(nameof(EndpointsByMode))]
    public async Task V1_istek_kopya_acmaz_ve_iki_kipte_iz_yazar(string endpoint, SalesSyncMode mode)
    {
        var (client, licenseId) = await SetupAsync(mode);
        var before = DateTimeOffset.UtcNow;

        (await PushAsync(client, licenseId, endpoint, V1(endpoint), userAgent: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LegacyTraceAsync(licenseId)).Should().BeOnOrAfter(before);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfReplicas.AnyAsync(r => r.LicenseId == licenseId)).Should().BeFalse();
    }

    /// <summary>İz saatte en çok bir kez yazılır: eski sürüm her 30–60 sn'de push'luyor; her istekte Licenses
    /// satırını güncellemek etkinleştirmelerin RowVersion jetonuyla sürekli yarışırdı.</summary>
    [Fact]
    public async Task V1_izi_saatte_bir_yazilir()
    {
        var (client, licenseId) = await SetupAsync();
        (await PushAsync(client, licenseId, "orders/sync", V1("orders/sync"), userAgent: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var first = (await LegacyTraceAsync(licenseId));
        first.Should().NotBeNull();

        (await PushAsync(client, licenseId, "orders/sync", V1("orders/sync"), userAgent: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await LegacyTraceAsync(licenseId)).Should().Be(first, "pencere içinde yeniden yazılmaz");

        var recent = DateTimeOffset.UtcNow.AddMinutes(-50);
        await SetLegacyTraceAsync(licenseId, recent);
        (await PushAsync(client, licenseId, "shipments/sync", V1("shipments/sync"), userAgent: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await LegacyTraceAsync(licenseId)).Should().Be(recent);

        var stale = DateTimeOffset.UtcNow.AddMinutes(-61);
        await SetLegacyTraceAsync(licenseId, stale);
        var beforePush = DateTimeOffset.UtcNow;
        (await PushAsync(client, licenseId, "sessions/sync", V1("sessions/sync"), userAgent: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await LegacyTraceAsync(licenseId)).Should().BeOnOrAfter(beforePush);
    }

    /// <summary>
    /// <c>SeenModeOnAt</c> (§4.0): kopyanın modu ilk gördüğü an. İlk 1 bildiriminde yazılır; 0 bildirimi ona dokunmaz;
    /// aynı On döneminde yeniden 1 gelse de yerinde kalır. Mod yeniden açıldıysa (değişim anı damgadan yeni) bir
    /// sonraki 1 bildiriminde yeniden yazılır — yoksa önceki dönemde modu görmüş kopya yeni dönemin açılış
    /// penceresine hiç giremezdi.
    /// </summary>
    [Fact]
    public async Task SeenModeOnAt_ilk_birde_yazilir_sifir_dokunmaz_yeni_donemde_yenilenir()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On, modeChangedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var replicaId = Guid.NewGuid();

        await PushOkAsync(client, licenseId, replicaId, seenMode: 0);
        (await ReplicaAsync(licenseId, replicaId))!.SeenModeOnAt.Should().BeNull("0 bildirimi modu görmek değildir");

        await PushOkAsync(client, licenseId, replicaId, seenMode: 1);
        var firstOn = (await ReplicaAsync(licenseId, replicaId))!.SeenModeOnAt;
        firstOn.Should().NotBeNull();

        await PushOkAsync(client, licenseId, replicaId, seenMode: 0);
        var afterZero = await ReplicaAsync(licenseId, replicaId);
        afterZero!.SeenModeOnAt.Should().Be(firstOn);
        afterZero.SeenMode.Should().Be(SalesSyncMode.Off, "görülen kip her istekte yazılır");

        await PushOkAsync(client, licenseId, replicaId, seenMode: 1);
        (await ReplicaAsync(licenseId, replicaId))!.SeenModeOnAt.Should().Be(firstOn, "aynı On dönemi");

        // Mod kapatılıp yeniden açıldı: önceki dönemin damgası değişim anından eski.
        var previousPeriod = DateTimeOffset.UtcNow.AddDays(-3);
        await UpdateReplicaAsync(licenseId, replicaId, r => r.SeenModeOnAt = previousPeriod);
        await UpdateLicenseAsync(licenseId, l => l.SalesSyncModeChangedAt = DateTimeOffset.UtcNow.AddMinutes(-5));
        var reopenedAt = DateTimeOffset.UtcNow;

        await PushOkAsync(client, licenseId, replicaId, seenMode: 0);
        (await ReplicaAsync(licenseId, replicaId))!.SeenModeOnAt.Should().Be(previousPeriod);

        await PushOkAsync(client, licenseId, replicaId, seenMode: 1);
        (await ReplicaAsync(licenseId, replicaId))!.SeenModeOnAt.Should().BeOnOrAfter(reopenedAt);
    }

    /// <summary>Yarım zarf sessizce v1 sayılmaz (§1 amaç 5): görünür 400, hiçbir şey yazılmaz. <c>@ID</c> koşuda
    /// üretilen bir kimlikle değişir.</summary>
    [Theory]
    [InlineData("{\"sessions\":[],\"protocol\":3,\"replicaId\":\"@ID\",\"seenMode\":0}")]
    [InlineData("{\"sessions\":[],\"replicaId\":\"@ID\"}")]
    [InlineData("{\"sessions\":[],\"protocol\":2,\"seenMode\":0}")]
    [InlineData("{\"sessions\":[],\"protocol\":2,\"replicaId\":\"00000000-0000-0000-0000-000000000000\",\"seenMode\":0}")]
    [InlineData("{\"sessions\":[],\"protocol\":2,\"replicaId\":\"@ID\"}")]
    [InlineData("{\"sessions\":[],\"protocol\":2,\"replicaId\":\"@ID\",\"seenMode\":2}")]
    public async Task Yarim_zarf_400_doner_ve_iz_birakmaz(string json)
    {
        var (client, licenseId) = await SetupAsync();

        var resp = await client.PostAsync(
            $"/api/v1/licenses/{licenseId}/sessions/sync",
            new StringContent(json.Replace("@ID", Guid.NewGuid().ToString()), System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid-envelope");
        (await LegacyTraceAsync(licenseId)).Should().BeNull();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfReplicas.AnyAsync(r => r.LicenseId == licenseId)).Should().BeFalse();
    }

    /// <summary>Başkasının lisansına v2 isteği: 404, kopya satırı açılmaz (sahiplik zarftan önce).</summary>
    [Fact]
    public async Task Baskasinin_lisansina_v2_istek_404_ve_kopya_acmaz()
    {
        var (_, foreignLicenseId) = await SetupAsync();
        var (client, _) = await SetupAsync();
        var replicaId = Guid.NewGuid();

        (await PushAsync(client, foreignLicenseId, "orders/sync", V2(replicaId, seenMode: 0), WpfUserAgent))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ReplicaAsync(foreignLicenseId, replicaId)).Should().BeNull();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static object V2(Guid replicaId, int seenMode) =>
        new { sessions = Array.Empty<object>(), orders = Array.Empty<object>(), shipments = Array.Empty<object>(),
              replicaId, protocol = 2, seenMode };

    /// <summary>Bugünkü istemcinin boş paketi: zarf alanı yok.</summary>
    private static object V1(string endpoint) => endpoint switch
    {
        "sessions/sync" => new { sessions = Array.Empty<object>() },
        "orders/sync" => new { orders = Array.Empty<object>() },
        _ => new { shipments = Array.Empty<object>() },
    };

    private static async Task PushOkAsync(HttpClient client, Guid licenseId, Guid replicaId, int seenMode) =>
        (await PushAsync(client, licenseId, "orders/sync", V2(replicaId, seenMode), WpfUserAgent))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> PushAsync(
        HttpClient client, Guid licenseId, string endpoint, object body, string? userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/licenses/{licenseId}/{endpoint}")
        {
            Content = JsonContent.Create(body),
        };
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return client.SendAsync(request);
    }

    private async Task<WpfReplica?> ReplicaAsync(Guid licenseId, Guid replicaId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WpfReplicas.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LicenseId == licenseId && r.ReplicaId == replicaId);
    }

    private async Task UpdateReplicaAsync(Guid licenseId, Guid replicaId, Action<WpfReplica> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        change(await db.WpfReplicas.SingleAsync(r => r.LicenseId == licenseId && r.ReplicaId == replicaId));
        await db.SaveChangesAsync();
    }

    /// <summary>Lisansın protokolsüz istek izi; satır yoksa null.</summary>
    private async Task<DateTimeOffset?> LegacyTraceAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == licenseId)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync();
    }

    private async Task SetLegacyTraceAsync(Guid licenseId, DateTimeOffset value)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.SalesLegacyTraces.SingleAsync(t => t.LicenseId == licenseId)).LastLegacySalesPushAt = value;
        await db.SaveChangesAsync();
    }

    private async Task UpdateLicenseAsync(Guid licenseId, Action<License> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        change(await db.Licenses.SingleAsync(l => l.Id == licenseId));
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(
        SalesSyncMode mode = SalesSyncMode.Off, DateTimeOffset? modeChangedAt = null)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-KOPYA-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? modeChangedAt ?? DateTimeOffset.UtcNow.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }
}
