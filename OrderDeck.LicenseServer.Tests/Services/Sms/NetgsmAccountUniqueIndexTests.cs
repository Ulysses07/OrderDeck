using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Tekil index yalnız GERÇEK SQL Server'da kanıtlanabilir — InMemory
/// sağlayıcısı index ihlali fırlatmaz (bkz. IysConsentUniqueIndexTests).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class NetgsmAccountUniqueIndexTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public NetgsmAccountUniqueIndexTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="NetgsmAccount.UserCode"/> ve <see cref="NetgsmAccount.Header"/>
    /// satır başına ÜRETİLİR (sabit değil): sabit olsalardı marka testi, tekil
    /// index yanlışlıkla o iki kolona konmuş olsa bile aynı
    /// <see cref="DbUpdateException"/>'ı alır ve doğru sebeple geçtiğini
    /// sanardık. Ayrıca repo kuralı: testte sabit kimlik-bilgisi metni yazma.
    /// </summary>
    private static NetgsmAccount Row(Guid licenseId, string brandCode) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        PasswordProtected = $"pw-{Guid.NewGuid():N}",
        Header = $"OD{Guid.NewGuid():N}"[..11],
        BrandCode = brandCode,
        Status = NetgsmAccountStatus.Verified,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Boş bir yayıncı lisansı açar. Ortak bir <c>TestData</c> yardımcısı
    /// bu repoda YOK — mevcut testler (ör. <c>BroadcastPostCleanupJobTests</c>)
    /// aynı gövdeyi kendi içlerinde tutuyor; kalıbı bozmuyoruz.</summary>
    private async Task<Guid> NewLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Netgsm-IX",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    [Fact]
    public async Task Ayni_lisansa_ikinci_hesap_reddedilir()
    {
        var licenseId = await NewLicenseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(licenseId, "731734"));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(licenseId, "763208"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "bir lisansın tek Netgsm hesabı olur");
        }
    }

    [Fact]
    public async Task Ayni_marka_kodu_iki_lisansa_verilemez()
    {
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, "731734"));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, "731734"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "marka→hesap araması tek satır dönmeli; iki lisans aynı markayı " +
                "paylaşırsa push işi hangi kimlikle gideceğini bilemez");
        }
    }

    [Fact]
    public async Task Bos_marka_kodu_reddedilir()
    {
        var licenseId = await NewLicenseAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.NetgsmAccounts.Add(Row(licenseId, ""));

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "boş marka kodu global tekil index'te bir yer kapar; ikinci boş " +
            "kayıt çakışır ve marka→hesap araması \"\" ile gerçek bir kiracının " +
            "satırını döndürerek onayı yanlış markaya yazardı");
    }
}
