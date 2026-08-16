using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Controllers.Panel;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelBarcodesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelBarcodesControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Istenen_kadar_ardisik_numara_dondurur()
    {
        var client = await SeedLicensedClientAsync();

        var res = await client.PostAsync("/api/panel/barcodes/next?count=3", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content
            .ReadFromJsonAsync<PanelBarcodesController.NextBarcodesDto>();
        body!.Barcodes.Should().HaveCount(3);
        body.Barcodes.Should().OnlyContain(b => b.Length == 10);
    }

    [Fact]
    public async Task Sifir_ya_da_negatif_adet_reddedilir()
    {
        var client = await SeedLicensedClientAsync();

        var res = await client.PostAsync("/api/panel/barcodes/next?count=0", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tavanin_ustunde_adet_reddedilir()
    {
        var client = await SeedLicensedClientAsync();

        var res = await client.PostAsync("/api/panel/barcodes/next?count=201", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ikinci_cagri_yeni_numaralar_verir()
    {
        // Bu ucun VAR OLMA sebebini sınayan test. Ayırıcı sayacı yalnız
        // değiştiriyor, kaydetmiyor; kaydetme sorumluluğu bu controller'da.
        // Biri "gereksiz" diye SaveChangesAsync'i silerse, kapsam bittiğinde
        // sayaç satırı hiç yazılmamış olur, sonraki istek "sayaç yok"
        // dalına girer ve AYNI numaraları döndürür. Aşağıdaki kesişim
        // kontrolü, o gerilemeyi yakalayan tek testtir.
        var client = await SeedLicensedClientAsync();

        var first = await NextAsync(client, 2);
        var second = await NextAsync(client, 2);

        second.Should().NotIntersectWith(first);
    }

    [Fact]
    public async Task Kimliksiz_istek_reddedilir()
    {
        var client = _factory.CreateClient();

        var res = await client.PostAsync("/api/panel/barcodes/next?count=1", null);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<IReadOnlyList<string>> NextAsync(HttpClient client, int count)
    {
        var res = await client.PostAsync($"/api/panel/barcodes/next?count={count}", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content
            .ReadFromJsonAsync<PanelBarcodesController.NextBarcodesDto>();
        return body!.Barcodes;
    }

    /// <summary>
    /// Kimliği doğrulanmış bir istemci üretir ve ona aktif bir lisans bağlar.
    /// Kurulum satırları <c>PanelProductVariantsControllerTests.SeedAsync</c>'ten
    /// kopyalandı; ortak bir yardımcı sınıfa çıkarmak bu görevin kapsamı değil.
    /// </summary>
    private async Task<HttpClient> SeedLicensedClientAsync()
    {
        var (client, customerId, _) =
            await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-BARC-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }
}
