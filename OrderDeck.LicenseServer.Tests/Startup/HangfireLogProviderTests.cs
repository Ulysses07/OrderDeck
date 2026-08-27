using Hangfire;
using Hangfire.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Startup;

/// <summary>
/// Hangfire'ın günlük sağlayıcısı <b>süreç geneli statik</b>
/// (<c>LogProvider.CurrentLogProvider</c>). <c>AddHangfire</c>, bir konteynerde
/// <c>IGlobalConfiguration</c> ilk çözüldüğünde o statiğe O HOST'un
/// <c>ILoggerFactory</c>'sine bağlı bir sağlayıcı yazıyor.
///
/// Her test sınıfı <c>IClassFixture&lt;ApiFactory&gt;</c> ile kendi host'unu
/// kuruyor ve xUnit sınıfları paralel koşturuyor. Sonuç: en son kurulan host
/// statiği kendine bağlıyor, o host kapanınca statikte kapatılmış bir
/// <c>LoggerFactory</c> kalıyor ve HÂLÂ ÇALIŞAN başka bir sınıf Hangfire'a
/// dokunduğunda <c>ObjectDisposedException: LoggerFactory</c> alıyor. Düşen
/// test her seferinde farklı — ürün kodunda bir kusur olmadığı hâlde CI
/// rastgele kırmızıya dönüyordu.
///
/// Bu test kaybı deterministik hâle getiriyor: bir host kurup kapatıyor ve
/// statikteki günlük yolunun hâlâ sağlam olduğunu doğruluyor — CI'da bunu
/// kullanacak olan, o sırada koşmaya devam eden başka bir sınıf. Düzeltme
/// <see cref="ApiFactory"/>'de — Hangfire yapılandırma geri çağrısı yerleşik
/// atamadan SONRA çalıştığı için oradaki <c>UseLogProvider</c> kazanıyor.
/// </summary>
public class HangfireLogProviderTests
{
    [Fact]
    public void Kapanan_host_hangfire_gunluk_saglayicisini_bozmuyor()
    {
        // Host kurulurken statiğe kendi LoggerFactory'sini yazan taraf burası;
        // kapanışıyla birlikte o fabrika da kapanıyor.
        ILoggerFactory kapananFabrika;
        using (var kapanan = new ApiFactory())
        {
            kapanan.Services.GetRequiredService<IGlobalConfiguration>();
            kapananFabrika = kapanan.Services.GetRequiredService<ILoggerFactory>();
        }

        // Host kapanışı senkron görünse de fabrikanın gerçekten kapandığını
        // beklemeden ölçmek testin kendisini flake yapar — kaybı ölçen test
        // ölçtüğü kusurdan daha güvenilir olmalı.
        Assert.True(
            SpinWait.SpinUntil(
                () => Record.Exception(() => kapananFabrika.CreateLogger("x")) is ObjectDisposedException,
                TimeSpan.FromSeconds(10)),
            "kapanan host'un LoggerFactory'si kapanmadı");

        var hata = Record.Exception(() => LogProvider.GetLogger("orderdeck-test").Info("dokun"));

        Assert.Null(hata);
    }
}
