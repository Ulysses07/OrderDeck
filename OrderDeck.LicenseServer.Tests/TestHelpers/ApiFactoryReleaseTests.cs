using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Dispose edilen bir <see cref="ApiFactory"/> (host'u ve servis sağlayıcısıyla)
/// çöp toplayıcıya bırakılmalı; InMemory deposu fabrikaya özel bir köke bağlı
/// olmalı (ayrı test: adlı depo fabrikayı tutmadığı için ilk test onu görmez).
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

    // Yukarıdaki test yalnız fabrika nesnesini izliyor. Adlı InMemory deposu EF'in
    // statik iç önbelleğinde durur ve fabrikaya geri referans tutmaz: fabrika başına
    // kök (ApiFactory._dbRoot) kaldırılırsa fabrika yine toplanır, o test yeşil kalır,
    // depolar ise süreç boyunca birikir. Kökün fabrikaya özel olduğunu ayrıca sabitle.
    [Fact]
    public void Her_fabrika_kendi_InMemory_kokunu_kullanir()
    {
        using var a = new ApiFactory();
        using var b = new ApiFactory();

        var rootA = InMemoryRoot(a);
        rootA.Should().NotBeNull("adlı depo süreç geneli önbellekte kalmamalı");
        rootA.Should().NotBeSameAs(InMemoryRoot(b));
    }

    private static object? InMemoryRoot(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<LicenseDbContext>>();
#pragma warning disable EF1001 // InMemoryOptionsExtension iç API; yalnız testte okunuyor
        return options.FindExtension<InMemoryOptionsExtension>()?.DatabaseRoot;
#pragma warning restore EF1001
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
