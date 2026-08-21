using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Auth;

/// <summary>
/// Parola sıfırlamanın iki ayrı akışında da "tek kullanımlık" vaadinin
/// eşzamanlılık altında ayakta kaldığını doğrular: shopper SMS kodu ve
/// yayıncı e-posta token'ı.
///
/// Neden gerçek SQL Server: her iki akışta da kontrol ile yazma arasında
/// pencere var ("tüketilmemiş mi?" → hash doğrula → tüketildi damgala) ve
/// hakem yalnızca veritabanı olabilir. InMemory sağlayıcısında eşzamanlılık
/// jetonu hiç uygulanmaz; test orada bozuk kodda bile yeşil yanar.
///
/// Neden önemli: tek kullanımlık olmak bu iki mekanizmanın var oluş sebebi.
/// Kod 6 hane — deneme tavanı olmasa kaba kuvvetle dakikalar içinde kırılır.
/// Tavanı yaşatan şey deneme sayacının her denemede BİR artması; eşzamanlı
/// istekler sayacı aynı değerden okuyup aynı değeri yazabiliyorsa saldırgan
/// tek turda milyon tahmin gönderir, sayaç 1'de kalır ve tavan kâğıt üstünde
/// kalır.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PasswordResetConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PasswordResetConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Aynı SMS kodunu N istek aynı anda doğrularsa yalnız biri kabul
    /// edilmeli. Kabul sayısı = sayaç artışı olduğu sürece tavan gerçektir;
    /// jeton olmadan sekiz denemenin sekizi de onaylanıyor ama sayaç yalnız
    /// 1 artıyordu — yani her tur bedava sekiz tahmin.
    /// </summary>
    [Fact]
    public async Task Ayni_sms_kodu_esazamanli_kullanilirsa_yalnizca_biri_kabul_edilir()
    {
        var shopperId = await SeedShopperAsync();
        var code = await IssueCodeAsync(shopperId);

        var results = await Task.WhenAll(Enumerable.Range(0, ParallelAttempts)
            .Select(_ => VerifyInOwnScopeAsync(shopperId, code)));

        results.Count(ok => ok).Should().Be(1,
            "kod tek kullanımlık; birden fazla kabul, aynı turda birden fazla " +
            "tahminin onurlandırıldığı ve deneme tavanının delindiği anlamına gelir");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.ShopperPasswordResetCodes.AsNoTracking().FirstAsync();
        row.ConsumedAt.Should().NotBeNull("kabul edilen deneme kodu tüketmeli");
        row.AttemptCount.Should().Be(1,
            "sayaç 1 arttıysa yalnız bir deneme kayda geçmiştir; kabul sayısıyla " +
            "birebir örtüşmesi tavanın gerçekten çalıştığının kanıtı");
    }

    /// <summary>
    /// Yanlış kodla yapılan eşzamanlı deneme turu kodu tüketmemeli — meşru
    /// kullanıcı hatalı bastıktan sonra doğru kodu hâlâ kullanabilmeli.
    /// Reddedilen istekler yüzünden satırın kilitlenmediğini gösterir.
    /// </summary>
    [Fact]
    public async Task Yanlis_kod_turu_kodu_tuketmez()
    {
        var shopperId = await SeedShopperAsync();
        var code = await IssueCodeAsync(shopperId);

        var wrong = await Task.WhenAll(Enumerable.Range(0, ParallelAttempts)
            .Select(_ => VerifyInOwnScopeAsync(shopperId, "000000")));
        wrong.Should().AllSatisfy(ok => ok.Should().BeFalse());

        (await VerifyInOwnScopeAsync(shopperId, code)).Should().BeTrue(
            "yanlış denemeler tavanı doldurmadıysa doğru kod hâlâ geçmeli");
    }

    /// <summary>
    /// Yayıncı tarafındaki e-posta sıfırlama token'ının aynısı. Bağlantı
    /// e-postada duruyor: sızarsa ikinci kullanımın çalışmaması güvenlik
    /// vaadinin kendisi.
    /// </summary>
    [Fact]
    public async Task Ayni_sifirlama_tokeni_esazamanli_kullanilirsa_yalnizca_biri_kabul_edilir()
    {
        var tokenId = await SeedCustomerResetTokenAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, ParallelAttempts)
            .Select(i => CompleteResetInOwnScopeAsync(tokenId, $"yeni-parola-{i}")));

        results.Count(r => r == PasswordResetResult.Success).Should().Be(1,
            "token tek kullanımlık; ikinci bir kabul, sızmış bağlantının " +
            "yeniden kullanılabildiği anlamına gelir");
        results.Count(r => r == PasswordResetResult.TokenInvalid)
            .Should().Be(ParallelAttempts - 1);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>
    /// Her çağrı KENDİ scope'unda: gerçek istekte de her istek kendi
    /// DbContext'ini alır. Ortak context paylaşılsaydı değişiklik izleyicisi
    /// yarışı yutar, test anlamsızlaşırdı.
    /// </summary>
    private async Task<bool> VerifyInOwnScopeAsync(Guid shopperId, string code)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetCodeService>();
        return await svc.VerifyAndConsumeAsync(shopperId, code);
    }

    private async Task<PasswordResetResult> CompleteResetInOwnScopeAsync(Guid tokenId, string newPassword)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        return await svc.CompleteResetAsync(tokenId, newPassword);
    }

    private async Task<string> IssueCodeAsync(Guid shopperId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetCodeService>();
        var shopper = await db.Shoppers.FirstAsync(s => s.Id == shopperId);
        var code = await svc.IssueAsync(shopper, ip: null);
        code.Should().NotBeNull("kod üretimi ön koşul");
        return code!;
    }

    private async Task<Guid> SeedShopperAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Tam nitelikli: bu dosyanın ad alanında "Shopper" adlı bir test ad
        // alanı da var, kısa ad çakışıyor.
        var shopper = new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Yarış Testi",
            Phone = "+90500" + Random.Shared.Next(1_000_000, 9_999_999),
            PasswordHash = "hash",
            Address = "Ankara",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();
        return shopper.Id;
    }

    private async Task<Guid> SeedCustomerResetTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"reset-{Guid.NewGuid():N}@x",
            Name = "Yayıncı",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var token = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }
}
