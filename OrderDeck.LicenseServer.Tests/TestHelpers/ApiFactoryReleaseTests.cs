using System.Runtime.CompilerServices;
using FluentAssertions;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Dispose edilen bir <see cref="ApiFactory"/> (host'u, servis sağlayıcısı ve
/// InMemory deposuyla) çöp toplayıcıya bırakılmalı.
///
/// Neden test var: paket binlerce fabrika açıyor. Bir tanesi bile statik bir
/// kökten (zamanlayıcı, statik olay, önbellek) erişilebilir kalırsa hepsi
/// kalır; 2026-10-22'de fabrika başına ~8,5 MB, testhost 8 GB'ı geçti ve CI
/// runner'ı bellekten düştü. Kök, ASP.NET Core'un hız sınırlayıcı ara
/// katmanının hiç dispose edilmeyen zamanlayıcısıydı (bkz.
/// <c>ApiFactory.TrackRateLimiters</c>). O düzeltme özel alanlara yansımayla
/// ulaşıyor; çerçeve sürümü değişip ara katman bulunamazsa testler yine
/// yeşil koşar, yalnız sızıntı sessizce geri gelir — bu test onu yakalar.
/// </summary>
public sealed class ApiFactoryReleaseTests
{
    [Fact]
    public async Task Dispose_edilen_fabrika_ve_turettigi_fabrika_bellekten_birakilir()
    {
        // Önce ve sonra birer fabrika: iki kütüphane TEK bir host'u statik
        // olarak tutuyor; birikmiyor ama ölçülen fabrika o host olursa test
        // sızıntı yokken kırmızı yanar (yerel prob, 2026-10-23):
        //  - OpenTelemetry'nin statik öz-tanılama işçisi, süreçte ilk sağlayıcı
        //    kurulurken o anki ExecutionContext'i yakalıyor; içindeki
        //    HostFactoryResolver AsyncLocal'ı İLK host'a gidiyor.
        //  - Hangfire.JobActivator.Current her host kurulurken üzerine yazılan
        //    bir statik; SON kurulan host'u gösteriyor.
        await UseAndDisposeAsync();
        var (factory, derived) = await UseAndDisposeAsync();
        await UseAndDisposeAsync();

        for (var i = 0; i < 10 && (factory.IsAlive || derived.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100);
        }

        factory.IsAlive.Should().BeFalse("dispose edilen fabrikayı statik bir kök tutmamalı");
        derived.IsAlive.Should().BeFalse("WithWebHostBuilder ile türetilen fabrikayı da");
    }

    // Ayrı yöntem: yerel değişkenler çağıranın durum makinesinde kalıp
    // fabrikayı canlı tutmasın.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Factory, WeakReference Derived)> UseAndDisposeAsync()
    {
        var factory = new ApiFactory();
        var derived = factory.WithWebHostBuilder(_ => { });

        using (var client = factory.CreateClient())
            (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();
        using (var client = derived.CreateClient())
            (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        var weak = (new WeakReference(factory), new WeakReference(derived));
        // Testlerin çoğu gibi eşzamanlı Dispose: WebApplicationFactory onu
        // DisposeAsync'e (ApiFactory'nin ezdiği) yönlendirir; türetilen fabrikayı
        // da kapatır.
        factory.Dispose();
        factory = null!;
        derived = null!;
        return weak;
    }
}
