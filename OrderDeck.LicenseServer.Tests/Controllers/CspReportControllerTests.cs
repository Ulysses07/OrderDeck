using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

/// <summary>
/// CSP ihlal bildirimi ucu (O-15). Testlerin taşıdığı asıl yük ANONİM
/// erişilebilirlik: ihlal giriş ekranında, henüz token yokken de olabilir, ve
/// bu uç kırıldığında belirti "hata" değil SESSİZLİK olur — kimse fark etmez.
/// </summary>
public class CspReportControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CspReportControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Bildirim_token_olmadan_kabul_ediliyor()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync(
            "/api/public/csp-report",
            new
            {
                documentUri = "https://panel.orderdeckapp.com/wa/baglan",
                blockedUri = "https://connect.facebook.net/en_US/sdk.js",
                effectiveDirective = "connect-src",
                sourceFile = "https://panel.orderdeckapp.com/assets/index.js",
                lineNumber = 42,
                disposition = "enforce"
            });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Yonergesiz_govde_sessizce_yutuluyor()
    {
        // Yönerge yoksa günlüğe yazacak bir şey yok. Yine de 2xx dönmeli:
        // istemci ateşle-unut çalışıyor, hata dönmek onu yeniden denemeye ya da
        // kendi hata yoluna sokmaya davet eder.
        var resp = await _factory.CreateClient().PostAsJsonAsync(
            "/api/public/csp-report", new { documentUri = "https://panel.orderdeckapp.com/" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Asiri_uzun_alan_reddediliyor()
    {
        // Uç anonim: alan tavanı olmadan burası bedava bir günlük şişirme
        // yüzeyi olurdu. Gövdenin kendisi 8 KB sınırının ALTINDA — reddin
        // MaxLength'ten geldiği kesin olsun diye.
        var resp = await _factory.CreateClient().PostAsJsonAsync(
            "/api/public/csp-report",
            new
            {
                effectiveDirective = "script-src",
                documentUri = new string('x', 1024)
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Uçtaki [RequestSizeLimit(8 KB)] BİLEREK test edilmiyor: TestServer o
    // sınırı uygulamıyor (32 KB'lık gövdeyle denendi, 204 döndü — sınır
    // Kestrel'e ait). Burada 413 bekleyen bir test yazmak sınırı doğrulamaz,
    // yalnızca yeşil görünür.
}
