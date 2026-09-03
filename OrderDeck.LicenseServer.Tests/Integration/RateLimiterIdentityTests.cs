using System.Collections.Concurrent;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Integration;

/// <summary>
/// Rate limiter middleware'inin pipeline'daki yerini davranışla bağlar.
///
/// Sıra hatası burada özel olarak sinsi: limiter kimlik doğrulamadan önce
/// çalışırsa <c>ctx.User</c> boş olur, kullanıcıya göre bölünen politikalar
/// sessizce yedek anahtara düşer ve hiçbir şey hata vermez. Tek belirti,
/// kullanıcı başına olması gereken bir limitin platform geneline dönüşmesidir
/// — ki bu ancak gerçek yük altında, üstelik "bazen 429 alıyorum" şeklinde
/// fark edilir.
/// </summary>
public sealed class RateLimiterIdentityTests
{
    /// <summary>
    /// Limit sayılarını değil, kararın hangi kimlikle verildiğini kaydeder.
    /// </summary>
    private sealed class ProbeFactory : ApiFactory
    {
        public ConcurrentBag<(string Policy, string? UserId)> Seen { get; } = new();

        protected override Action<string, HttpContext>? OnRateLimitPartition =>
            (policy, ctx) => Seen.Add((policy, ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)));
    }

    [Fact]
    public async Task Kullaniciya_gore_bolunen_politika_kimligi_gorebiliyor()
    {
        using var factory = new ProbeFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        // Var olmayan bir yedek: 404 dönecek, ama limiter middleware'i uçtan
        // ÖNCE çalıştığı için politika kararı yine de verilir. Testin ilgilendiği
        // tek şey o karar anı.
        var resp = await client.DeleteAsync($"/api/v1/me/backups/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var seen = factory.Seen.Where(s => s.Policy == "backup-delete").ToList();
        seen.Should().ContainSingle("backup-delete politikası tam bir kez değerlendirilmeli");
        seen[0].UserId.Should().Be(
            customerId.ToString(),
            "limiter kimlik doğrulamadan sonra çalışmazsa ctx.User boş kalır ve "
            + "backup-delete herkesi tek bir \"anon\" kovasına toplar");
    }

    [Fact]
    public async Task Anonim_uctaki_politika_kimliksiz_de_calisiyor()
    {
        using var factory = new ProbeFactory();
        var client = factory.CreateClient();

        // Sıra düzeltmesinin anonim uçları kırmadığının kanıtı: yetkilendirme
        // bu ucu geçiriyor, dolayısıyla limiter'a hâlâ ulaşıyor.
        await client.GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=ZZZZZZ");

        factory.Seen.Should().Contain(s => s.Policy == "auth-login",
            "anonim uçlar yetkilendirmeden geçtiği için limiter'a ulaşmaya devam etmeli");
    }

    /// <summary>
    /// Razor Pages uç nokta üstverisini sayfa TİPİNDEN okuyor: handler metoduna
    /// konan <c>[EnableRateLimiting]</c> hiçbir zaman uygulanmıyor ve HATA DA
    /// VERMİYOR. Kayıt formu gönderimi tam bu yüzden uzun süre sınırsız kaldı —
    /// öznitelik kodda duruyordu, üstelik ApiFactory politika adını taradığı için
    /// testlerde de "kayıtlı" görünüyordu.
    ///
    /// Not: politikanın POST/GET ayrımı <c>Program.cs</c> içindeki fabrikada;
    /// ApiFactory limitleri kapatmak için o fabrikayı değiştirdiğinden burada
    /// doğrulanamaz. Buranın kanıtladığı şey, kararın VERİLİYOR olması.
    /// </summary>
    [Fact]
    public async Task Kayit_formu_gonderimi_limiter_a_ulasiyor()
    {
        using var factory = new ProbeFactory();
        var client = factory.CreateClient();

        // Slug'ın geçerli olması gerekmiyor: limiter middleware'i uçtan önce
        // çalışıyor, yani yanıt ne olursa olsun politika kararı verilmiş oluyor.
        await client.PostAsync($"/r/{Guid.NewGuid():N}?handler=Submit",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        factory.Seen.Should().Contain(s => s.Policy == "intake-form-submit",
            "kayıt formu POST'u hız sınırı politikasından geçmeli; öznitelik sayfa "
            + "sınıfından handler metoduna taşınırsa sessizce etkisiz kalır");
    }
}
