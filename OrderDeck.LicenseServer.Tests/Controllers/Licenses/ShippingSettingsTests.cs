using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>GET/PUT shipping-settings</c> ve <c>sales/state.shipping</c> — spec 2026-10-13 §4.8, §4.5, §6.2 (sahip kararı Y4),
/// InMemory. PUT yalnız mod On'da ve v2 zarfıyla yazar (mod Off'ta ayarı yalnız CLI yazar); beklenen sürüm eşit değilse
/// 409 <c>stale</c> ve güncel satır döner; değerler <c>ShippingSettings.IsEnabled</c> kuralıyla doğrulanır. Eşzamanlı
/// yazım yarışı gerçek SQL Server'da (<see cref="ShippingSettingsRelationalTests"/>).
/// </summary>
public sealed class ShippingSettingsTests : IClassFixture<ApiFactory>
{
    private const string WpfUserAgent = "OrderDeck-WPF/0.9.11";

    private readonly ApiFactory _factory;

    public ShippingSettingsTests(ApiFactory factory) => _factory = factory;

    // ── GET ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_satir_yokken_surum_0_ve_bos_degerler_doner()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.Off);

        var settings = await GetAsync(client, licenseId);

        settings.Should().Be(new SettingsBody(0, null, null, null, null));
    }

    [Fact]
    public async Task Get_satiri_doner()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.Off);
        var updatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await SeedSettingsAsync(licenseId, version: 4, threshold: 500m, fee: 60m, updatedAt, LicenseShippingSettings.UpdatedByCli);

        var settings = await GetAsync(client, licenseId);

        settings.Should().Be(new SettingsBody(4, 500m, 60m, updatedAt, "cli"));
    }

    /// <summary>Ayar lisansa aittir: çağıran kendi lisansını okur, başkasınınkini 404 görür (satır olsa da).</summary>
    [Fact]
    public async Task Get_baskasinin_lisansina_404()
    {
        var (_, foreignLicenseId) = await SetupAsync(SalesSyncMode.On);
        await SeedSettingsAsync(foreignLicenseId, version: 1, threshold: 500m, fee: 60m, DateTimeOffset.UtcNow, "cli");
        var (client, ownLicenseId) = await SetupAsync(SalesSyncMode.On);

        (await SendAsync(client, HttpMethod.Get, Url(ownLicenseId))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Get, Url(foreignLicenseId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PUT ─────────────────────────────────────────────────────────────────

    /// <summary>İlk yazım sürüm 1, <c>UpdatedBy</c> = çağıran ReplicaId; sonraki yazım güncel sürümü bekler ve bir
    /// artırır. Çağrı kalp atışıdır: kopya satırı açılır ya da ilerler.</summary>
    [Fact]
    public async Task Put_mod_On_da_yazar_surumu_artirir_ve_kalp_atisi_sayilir()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var replicaId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        var first = await PutAsync(client, licenseId, Body(replicaId, expectedVersion: 0, 500m, 60m));

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var written = (await first.Content.ReadFromJsonAsync<SettingsBody>())!;
        written.Version.Should().Be(1);
        written.FreeShippingThreshold.Should().Be(500m);
        written.Fee.Should().Be(60m);
        written.UpdatedBy.Should().Be(replicaId.ToString());
        written.UpdatedAt.Should().BeOnOrAfter(before);
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 1, FreeShippingThreshold = 500m, ShippingFee = 60m, UpdatedBy = replicaId.ToString(),
        });
        var replica = await ReplicaAsync(licenseId, replicaId);
        replica.Should().NotBeNull("PUT bir v2 isteğidir ve kalp atışı sayılır");
        replica!.LastSeenAt.Should().BeOnOrAfter(before);
        replica.SeenMode.Should().Be(SalesSyncMode.On);

        var other = Guid.NewGuid();
        var second = await PutAsync(client, licenseId, Body(other, expectedVersion: 1, 750m, 75.5m));

        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await second.Content.ReadFromJsonAsync<SettingsBody>())!.Version.Should().Be(2);
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 2, FreeShippingThreshold = 750m, ShippingFee = 75.5m, UpdatedBy = other.ToString(),
        });
        (await GetAsync(client, licenseId)).Version.Should().Be(2);
    }

    /// <summary>İkisi de boş = kargo kapalı; geçerli bir yazımdır ve sürümü artırır.</summary>
    [Fact]
    public async Task Put_ikisi_de_bos_kargoyu_kapatir()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        await SeedSettingsAsync(licenseId, version: 3, threshold: 500m, fee: 60m, DateTimeOffset.UtcNow.AddDays(-1), "cli");

        var resp = await PutAsync(client, licenseId, Body(Guid.NewGuid(), expectedVersion: 3, null, null));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 4, FreeShippingThreshold = (decimal?)null, ShippingFee = (decimal?)null,
        });
    }

    /// <summary>Beklenen sürüm güncel değilse (satır yoksa güncel 0) hiçbir şey yazılmaz; 409 <c>stale</c> güncel
    /// satırı taşır.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(1, null)]
    public async Task Put_bayat_surum_409_stale_ve_guncel_satir_doner(int expectedVersion, int? currentVersion)
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var updatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        if (currentVersion is { } v)
            await SeedSettingsAsync(licenseId, v, threshold: 500m, fee: 60m, updatedAt, "cli");

        var resp = await PutAsync(client, licenseId, Body(Guid.NewGuid(), expectedVersion, 900m, 90m));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("stale");
        var current = problem.GetProperty("current").Deserialize<SettingsBody>(JsonOptions)!;
        if (currentVersion is { } version)
        {
            current.Should().Be(new SettingsBody(version, 500m, 60m, updatedAt, "cli"));
            (await RowAsync(licenseId)).Should().BeEquivalentTo(new { Version = version, FreeShippingThreshold = 500m });
        }
        else
        {
            current.Should().Be(new SettingsBody(0, null, null, null, null));
            (await RowAsync(licenseId)).Should().BeNull("bayat yazım satır açmaz");
        }
    }

    public static TheoryData<decimal?, decimal?> InvalidValues() => new()
    {
        { 500m, null },
        { null, 60m },
        { 0m, 60m },
        { 500m, 0m },
        { -1m, 60m },
        { 500m, -60m },
        { 0m, 0m },
        { 500.005m, 60m },
        { 500m, 60.001m },
    };

    /// <summary>Yalnız biri dolu, sıfır, negatif ya da kuruştan ince değer 400; mod On'da bile hiçbir şey yazılmaz.
    /// WPF'in <c>IsEnabled</c> kuralı bunları "kapalı" sayar; sunucu sessizce kapalıya çevirmez, reddeder.</summary>
    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task Put_gecersiz_deger_400(decimal? threshold, decimal? fee)
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);

        var resp = await PutAsync(client, licenseId, Body(Guid.NewGuid(), expectedVersion: 0, threshold, fee));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-shipping");
        (await RowAsync(licenseId)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task Put_beklenen_surum_eksik_ya_da_negatif_400(int? expectedVersion)
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);

        var resp = await PutAsync(client, licenseId, new
        {
            expectedVersion, freeShippingThreshold = 500m, fee = 60m,
            replicaId = Guid.NewGuid(), protocol = 2, seenMode = 1,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RowAsync(licenseId)).Should().BeNull();
    }

    /// <summary>Mod Off'ta lisans geneli ayarı yalnız Burak'ın CLI'ı yazar (§4.8); WPF'in PUT'u 409 <c>mode-off</c> alır
    /// ve yerel ayarı yazmaya devam eder. Var olan satır değişmez.</summary>
    [Fact]
    public async Task Put_mod_Off_ta_409_mode_off_hicbir_sey_yazmaz()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.Off);
        var fresh = await PutAsync(client, licenseId, Body(Guid.NewGuid(), expectedVersion: 0, 500m, 60m));

        fresh.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemAsync(fresh)).GetProperty("title").GetString().Should().Be("mode-off");
        (await RowAsync(licenseId)).Should().BeNull();

        var updatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await SeedSettingsAsync(licenseId, version: 2, threshold: 400m, fee: 50m, updatedAt, "cli");
        var existing = await PutAsync(client, licenseId, Body(Guid.NewGuid(), expectedVersion: 2, 500m, 60m));

        existing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemAsync(existing)).GetProperty("title").GetString().Should().Be("mode-off");
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 2, FreeShippingThreshold = 400m, ShippingFee = 50m, UpdatedBy = "cli", UpdatedAt = updatedAt,
        });
    }

    /// <summary>PUT yalnız v2 konuşur (UpdatedBy çağıran kopyadır): zarfsız, yarım ya da protokolü yanlış istek 400
    /// <c>invalid-envelope</c>; hiçbir şey yazılmaz, kopya satırı da açılmaz.</summary>
    [Theory]
    [InlineData("zarfsiz")]
    [InlineData("protokolsuz-kimlik")]
    [InlineData("kimliksiz-v2")]
    [InlineData("kipsiz-v2")]
    [InlineData("v1")]
    public async Task Put_zarfsiz_400_invalid_envelope(string variant)
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        object body = variant switch
        {
            "zarfsiz" => new { expectedVersion = 0, freeShippingThreshold = 500m, fee = 60m },
            "protokolsuz-kimlik" => new { expectedVersion = 0, freeShippingThreshold = 500m, fee = 60m, replicaId = Guid.NewGuid(), seenMode = 1 },
            "kimliksiz-v2" => new { expectedVersion = 0, freeShippingThreshold = 500m, fee = 60m, protocol = 2, seenMode = 1 },
            "kipsiz-v2" => new { expectedVersion = 0, freeShippingThreshold = 500m, fee = 60m, replicaId = Guid.NewGuid(), protocol = 2 },
            "v1" => new { expectedVersion = 0, freeShippingThreshold = 500m, fee = 60m, replicaId = Guid.NewGuid(), protocol = 1, seenMode = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var resp = await PutAsync(client, licenseId, body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-envelope");
        (await RowAsync(licenseId)).Should().BeNull();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WpfReplicas.AnyAsync(r => r.LicenseId == licenseId)).Should().BeFalse();
    }

    /// <summary>Aynı gövde çağıranın kendi lisansına yazılır, başkasının lisansına 404 alır ve orada hiçbir şey
    /// yazmaz.</summary>
    [Fact]
    public async Task Put_baskasinin_lisansina_404_hicbir_sey_yazmaz()
    {
        var (_, foreignLicenseId) = await SetupAsync(SalesSyncMode.On);
        var (client, ownLicenseId) = await SetupAsync(SalesSyncMode.On);

        (await PutAsync(client, ownLicenseId, Body(Guid.NewGuid(), expectedVersion: 0, 500m, 60m)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var resp = await PutAsync(client, foreignLicenseId, Body(Guid.NewGuid(), expectedVersion: 0, 500m, 60m));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RowAsync(foreignLicenseId)).Should().BeNull();
    }

    // ── sales/state ──────────────────────────────────────────────────────────

    /// <summary><c>sales/state.shipping</c> iki kipte satırdan dolar (istemci mod On'da kullanır, <c>disable</c> turunda
    /// yerele kopyalar, §4.5, §8.5); satır yoksa null.</summary>
    [Theory]
    [InlineData(SalesSyncMode.Off)]
    [InlineData(SalesSyncMode.On)]
    public async Task State_kargo_ayarini_satirdan_doner(SalesSyncMode mode)
    {
        var (client, licenseId) = await SetupAsync(mode);
        (await StateShippingAsync(client, licenseId)).ValueKind.Should().Be(JsonValueKind.Null, "satır yok");

        await SeedSettingsAsync(licenseId, version: 5, threshold: 500m, fee: 60m, DateTimeOffset.UtcNow, "cli");

        var shipping = await StateShippingAsync(client, licenseId);
        shipping.GetProperty("version").GetInt32().Should().Be(5);
        shipping.GetProperty("freeShippingThreshold").GetDecimal().Should().Be(500m);
        shipping.GetProperty("fee").GetDecimal().Should().Be(60m);
    }

    [Fact]
    public async Task State_kapali_ayari_bos_degerlerle_doner()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        await SeedSettingsAsync(licenseId, version: 2, threshold: null, fee: null, DateTimeOffset.UtcNow, "cli");

        var shipping = await StateShippingAsync(client, licenseId);

        shipping.GetProperty("version").GetInt32().Should().Be(2);
        shipping.GetProperty("freeShippingThreshold").ValueKind.Should().Be(JsonValueKind.Null);
        shipping.GetProperty("fee").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record SettingsBody(
        int Version, decimal? FreeShippingThreshold, decimal? Fee, DateTimeOffset? UpdatedAt, string? UpdatedBy);

    private static object Body(Guid replicaId, int expectedVersion, decimal? threshold, decimal? fee) => new
    {
        expectedVersion, freeShippingThreshold = threshold, fee, replicaId, protocol = 2, seenMode = 1,
    };

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/shipping-settings";

    private static async Task<SettingsBody> GetAsync(HttpClient client, Guid licenseId)
    {
        var resp = await SendAsync(client, HttpMethod.Get, Url(licenseId));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<SettingsBody>())!;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid licenseId, object body) =>
        SendAsync(client, HttpMethod.Put, Url(licenseId), body);

    private static async Task<JsonElement> StateShippingAsync(HttpClient client, Guid licenseId)
    {
        var resp = await SendAsync(client, HttpMethod.Get, $"/api/v1/licenses/{licenseId}/sales/state");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("shipping").Clone();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        request.Headers.UserAgent.ParseAdd(WpfUserAgent);
        return client.SendAsync(request);
    }

    private async Task<LicenseShippingSettings?> RowAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.LicenseShippingSettings.AsNoTracking().SingleOrDefaultAsync(s => s.LicenseId == licenseId);
    }

    private async Task<WpfReplica?> ReplicaAsync(Guid licenseId, Guid replicaId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WpfReplicas.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LicenseId == licenseId && r.ReplicaId == replicaId);
    }

    private async Task SeedSettingsAsync(
        Guid licenseId, int version, decimal? threshold, decimal? fee, DateTimeOffset updatedAt, string updatedBy)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.LicenseShippingSettings.Add(new LicenseShippingSettings
        {
            LicenseId = licenseId,
            Version = version,
            FreeShippingThreshold = threshold,
            ShippingFee = fee,
            UpdatedAt = updatedAt,
            UpdatedBy = updatedBy,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(SalesSyncMode mode)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-KARGO-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 5,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? DateTimeOffset.UtcNow.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }
}
