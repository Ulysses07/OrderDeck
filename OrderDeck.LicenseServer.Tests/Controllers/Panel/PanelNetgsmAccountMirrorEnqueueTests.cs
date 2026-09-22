using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Doğrulama başarılı olunca İYS aynası kimse düğmeye basmadan kuyruğa girmeli
/// (spec §2.1): başka sağlayıcıdan geçen, elle yükleyen ya da geri dönen
/// yayıncının İYS'deki onayları yerel tabloya gelsin. Başarısız doğrulamada
/// KUYRUĞA GİRMEMELİ — iş zaten "doğrulanmış hesap yok" diye çıkardı, boşa çağrı.
/// </summary>
public sealed class PanelNetgsmAccountMirrorEnqueueTests : IDisposable
{
    private readonly List<StubApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    /// <summary>Doğrulayıcı yalnız <c>code "0"</c>'ı Ok sayar; başka her kod
    /// Unavailable → hesap Failed. Tek ayarla iki senaryo.</summary>
    private sealed class StubIysClient : IIysClient
    {
        public string SearchCode { get; set; } = "0";

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte push beklenmiyor.");

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult(
                SearchCode, "{}", new Dictionary<string, IysConsentStatus>()));
    }

    private sealed class StubApiFactory : ApiFactory
    {
        public StubIysClient Iys { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(Iys);
            });
        }
    }

    private StubApiFactory NewFactory(string searchCode)
    {
        var f = new StubApiFactory();
        f.Iys.SearchCode = searchCode;
        _factories.Add(f);
        return f;
    }

    /// <summary>Netgsm abone numarası ÜRETİLİR: depo public, sabit bir değer
    /// gerçek bir aboneye ait olabilir.</summary>
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    private static object Body(string userCode, string brandCode) => new
    {
        userCode,
        password = $"pw-{Guid.NewGuid():N}",
        header = "ORDERDECK",
        brandCode,
    };

    private static async Task<(HttpClient Client, Guid LicenseId)> SeedTenantAsync(
        ApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-MIR-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary>ApiFactory Hangfire MemoryStorage kullanır: Enqueue çalışır ama iş
    /// KOŞMAZ — kuyruk monitoring API'den okunabilir.</summary>
    private static bool MirrorEnqueued(ApiFactory factory, Guid licenseId)
    {
        var monitoring = factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        return monitoring.EnqueuedJobs("default", 0, 1000).Any(j =>
            j.Value.Job.Type == typeof(IysMirrorImportJob)
            && j.Value.Job.Args.Contains((object)licenseId));
    }

    [Fact]
    public async Task Dogrulama_basarili_PUT_ayna_isini_kuyruga_atar()
    {
        var factory = NewFactory(searchCode: "0");
        var (client, licenseId) = await SeedTenantAsync(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/panel/netgsm/account", Body(NewUserCode(), NewBrandCode()));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified",
            "ön koşul: stub İYS 'code 0' döndürdü, doğrulama başarılı");

        MirrorEnqueued(factory, licenseId).Should().BeTrue(
            "marka doğrulanınca İYS'deki mevcut onaylar kendiliğinden aynalanmalı — düğme yok");
    }

    [Fact]
    public async Task Dogrulama_basarisiz_PUT_ayna_isi_kuyruga_atmaz()
    {
        var factory = NewFactory(searchCode: "not-configured");
        var (client, licenseId) = await SeedTenantAsync(factory);

        var resp = await client.PutAsJsonAsync(
            "/api/panel/netgsm/account", Body(NewUserCode(), NewBrandCode()));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "PUT sonucu görünümle döner, doğrulama düşse de");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");

        MirrorEnqueued(factory, licenseId).Should().BeFalse(
            "doğrulanmamış hesap için ayna işi 'hesap yok' diye çıkar — boşa kuyruk");
    }
}
