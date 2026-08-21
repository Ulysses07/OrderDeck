using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Startup;

/// <summary>
/// Kuralın <see cref="Program"/>'a gerçekten BAĞLI olduğunu sınar.
///
/// <see cref="ProviderSelectionTests"/> kuralın kendisini doğruluyor ama tek
/// başına yetmez: <c>ProviderName.ResolveLive</c> çağrısı Program.cs'ten
/// düşerse o testler yeşil kalır ve düzeltme tamamen etkisiz hâle gelir.
/// Buradaki test sunucuyu üretim ortamında ayağa kaldırmayı deniyor.
/// </summary>
public class ProviderProductionStartupTests
{
    private sealed class ProductionApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(Environments.Production);
        }
    }

    [Fact]
    public void Uretimde_saglayici_yapilandirmasi_eksikse_sunucu_acilmaz()
    {
        using var factory = new ProductionApiFactory();

        var act = () => factory.CreateClient();

        var failure = act.Should().Throw<Exception>(
            "SMS/WhatsApp/push/medya yapılandırılmamışken üretimde açılan sunucu " +
            "hiçbir belirti vermez: sağlık kontrolü yeşil yanar, gönderimler " +
            "'başarılı' döner, hiçbiri hedefine ulaşmaz").Which;

        Flatten(failure).Should().Contain(
            e => e is InvalidOperationException && e.Message.Contains("Provider"),
            "hata sağlayıcı seçiminden gelmeli, ilgisiz bir açılış hatasından değil");
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is not AggregateException agg) continue;
            foreach (var inner in agg.InnerExceptions.SelectMany(Flatten))
                yield return inner;
        }
    }
}
