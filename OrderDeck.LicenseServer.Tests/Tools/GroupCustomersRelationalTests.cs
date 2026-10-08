using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>group-customers</c> komutunun gövdesi gerçek SQL Server'da: bozuk eşleşme
/// dosyası veritabanına dokunmadan 2 döner (geçerli satırları da uygulanmaz);
/// uygulama 0 döner ve çıktıda kişisel veri yoktur. Gövde, bağlantısı ve
/// yazıcıları verilmiş <see cref="GroupCustomers.RunAsync(LicenseDbContext, Guid, string, bool, TextWriter, TextWriter, Microsoft.Extensions.Logging.ILoggerFactory, CancellationToken)"/>'ten
/// girilir; ayrıştırma <see cref="GroupCustomersTests"/>'te.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class GroupCustomersRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    private readonly List<string> _files = new();
    public GroupCustomersRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        var cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        foreach (var f in _files) File.Delete(f);
        return Task.CompletedTask;
    }

    private sealed record Seed(Guid LicenseId, WpfCustomerProjection Buyer, WpfCustomerProjection Registered, string GroupId);

    /// <summary>Bir lisans, grupsuz bir alıcı ve gruplu bir kayıtlı satır. Hepsi üretilmiş.</summary>
    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"musteri-{Guid.NewGuid():N}@example.test", Name = "CLI Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = "gcc-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        var groupId = Guid.NewGuid().ToString("N");
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var buyer = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "tiktok", Username = "u" + Guid.NewGuid().ToString("N")[..10],
            FullName = "Ad " + Guid.NewGuid().ToString("N")[..8], UpdatedAt = t0,
        };
        var registered = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "instagram", Username = "u" + Guid.NewGuid().ToString("N")[..10],
            FullName = "Ad " + Guid.NewGuid().ToString("N")[..8], GroupId = groupId, UpdatedAt = t0,
        };
        db.WpfCustomerProjections.AddRange(buyer, registered);
        await db.SaveChangesAsync();
        return new Seed(license.Id, buyer, registered, groupId);
    }

    private string WriteFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"group-customers-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        _files.Add(path);
        return path;
    }

    private async Task<(int Exit, string Output, string Error)> RunCliAsync(Guid license, string path, bool apply)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await GroupCustomers.RunAsync(db, license, path, apply, output, error, NullLoggerFactory.Instance, default);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<WpfCustomerProjection> ReadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task Bozuk_satir_2_doner_veritabanina_dokunmaz_gecerli_satirlar_da_uygulanmaz()
    {
        var seed = await SeedAsync();
        var before = await ReadAsync(seed.Buyer.Id);
        var path = WriteFile(
            "# alıcı kayıtlı",
            $"{seed.Buyer.Id:N} {seed.Registered.Id:N}",
            $"{seed.Buyer.Id:N}");

        var (exit, output, error) = await RunCliAsync(seed.LicenseId, path, apply: true);

        exit.Should().Be(2);
        error.Should().Contain("satır 3").And.NotContain(seed.Buyer.Id.ToString("N"));
        output.Should().BeEmpty();
        var after = await ReadAsync(seed.Buyer.Id);
        after.GroupId.Should().BeNull("dosya bütünüyle reddedildi");
        after.ChangeSeq.Should().Be(before.ChangeSeq);
    }

    [Fact]
    public async Task Ciftsiz_dosya_2_doner_veritabanina_dokunmaz()
    {
        var seed = await SeedAsync();
        var before = await ReadAsync(seed.Buyer.Id);
        var path = WriteFile("# alıcı kayıtlı", "");

        var (exit, output, error) = await RunCliAsync(seed.LicenseId, path, apply: true);

        exit.Should().Be(2);
        error.Should().Contain("hiç çift yok");
        output.Should().BeEmpty("kuru çalıştırma raporu bile yazılmaz — iş hiç koşmadı");
        (await ReadAsync(seed.Buyer.Id)).ChangeSeq.Should().Be(before.ChangeSeq);
    }

    [Fact]
    public async Task Uygulama_0_doner_ciktida_yalniz_sayilar_ve_lisans_Idsi()
    {
        var seed = await SeedAsync();
        var path = WriteFile($"{seed.Buyer.Id:D} {seed.Registered.Id:N}");

        var (dryExit, dryOutput, _) = await RunCliAsync(seed.LicenseId, path, apply: false);
        dryExit.Should().Be(0);
        dryOutput.Should().Contain("KURU ÇALIŞTIRMA").And.Contain("grubu değişecek satır=1");
        (await ReadAsync(seed.Buyer.Id)).GroupId.Should().BeNull();

        var (exit, output, error) = await RunCliAsync(seed.LicenseId, path, apply: true);

        exit.Should().Be(0, output + error);
        var text = output + error;
        text.Should().Contain("UYGULANDI").And.Contain(seed.LicenseId.ToString())
            .And.Contain("çift=1").And.Contain("grubu değişecek satır=1").And.Contain("atlanan bileşen=0");
        foreach (var personal in new[]
                 {
                     seed.Buyer.Username, seed.Buyer.FullName!, seed.Registered.Username, seed.Registered.FullName!,
                     seed.Buyer.Id.ToString("N"), seed.Registered.Id.ToString("N"), seed.GroupId, path,
                 })
            text.Should().NotContainEquivalentOf(personal, "çıktıda yalnız sayılar ve lisans Id'si olur");
        (await ReadAsync(seed.Buyer.Id)).GroupId.Should().Be(seed.GroupId);
    }
}
