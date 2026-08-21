using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

/// <summary>
/// Yetim yedek blob mutabakatı. İş YIKICI — yanlış kurgulanırsa müşterinin tek
/// yedeğini siler; bu yüzden testler yalnız "siliyor mu"yu değil, <b>neye
/// dokunmadığını</b> da bağlıyor.
/// </summary>
public class BackupOrphanCleanupJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupOrphanCleanupJobTests(ApiFactory f) => _factory = f;

    /// <summary>Ödemsiz süreden (24 saat) rahatça eski bir damga.</summary>
    private static DateTime OldUtc => DateTime.UtcNow.AddDays(-3);

    /// <summary>Diske gerçek bir blob yazar ve son yazılma zamanını damgalar.</summary>
    private string SeedBlob(Guid customerId, DateTime lastWriteUtc, string extension = ".bin")
    {
        var dir = Path.Combine(_factory.BackupRoot, customerId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private async Task<Guid> CreateCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"orphan-blob-{Guid.NewGuid():N}@t.com",
            Name = "Yetim",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private async Task AddBackupRowAsync(Guid customerId, string blobPath)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.CustomerBackups.Add(new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BlobPath = blobPath,
            SizeBytes = new FileInfo(blobPath).Length,
            ChecksumSha256 = new string('0', 64),
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false,
        });
        await db.SaveChangesAsync();
    }

    private async Task RunJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();
        await new BackupOrphanCleanupJob(
            db, storage, NullLogger<BackupOrphanCleanupJob>.Instance).RunAsync();
    }

    [Fact]
    public async Task RunAsync_deletes_an_aged_blob_that_no_row_references()
    {
        var customerId = await CreateCustomerAsync();
        var orphan = SeedBlob(customerId, OldUtc);

        await RunJobAsync();

        File.Exists(orphan).Should().BeFalse(
            "veritabanında karşılığı olmayan ve ödemsiz süreden eski blob silinmeli");
    }

    [Fact]
    public async Task RunAsync_keeps_a_blob_that_a_row_still_references()
    {
        var customerId = await CreateCustomerAsync();
        var live = SeedBlob(customerId, OldUtc);
        await AddBackupRowAsync(customerId, live);

        await RunJobAsync();

        File.Exists(live).Should().BeTrue(
            "satırı duran blob ödemsiz süreden eski olsa da yaşamalı — " +
            "silinirse müşteri listede görüp geri yükleyemediği bir yedekle kalır");
    }

    /// <summary>
    /// Yükleme akışında blob diske yazıldıktan sonra satır kaydedilene kadar
    /// geçen aralıkta dosya veritabanında GÖRÜNMEZ. Ödemsiz süre olmasaydı iş,
    /// sürmekte olan bir yüklemenin blob'unu silip tam da engellemeye çalıştığı
    /// hayalet satırı kendi eliyle üretirdi.
    /// </summary>
    [Fact]
    public async Task RunAsync_keeps_a_fresh_orphan_inside_the_grace_period()
    {
        var customerId = await CreateCustomerAsync();
        var fresh = SeedBlob(customerId, DateTime.UtcNow.AddMinutes(-5));

        await RunJobAsync();

        File.Exists(fresh).Should().BeTrue(
            "az önce yazılmış, satırı henüz kaydedilmemiş blob korunmalı");
    }

    /// <summary>
    /// Kapsam sorgu mantığıyla değil YAPI ile daraltılıyor: yalnız adı GUID
    /// olan alt klasörlerdeki <c>*.bin</c> dosyaları. Kök bir gün başka bir
    /// şeyle paylaşılırsa iş oraya uzanmasın.
    /// </summary>
    [Fact]
    public async Task RunAsync_ignores_files_outside_the_expected_structure()
    {
        var customerId = await CreateCustomerAsync();
        var wrongExtension = SeedBlob(customerId, OldUtc, ".log");

        var strayDir = Path.Combine(_factory.BackupRoot, "not-a-guid");
        Directory.CreateDirectory(strayDir);
        var strayFile = Path.Combine(strayDir, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(strayFile, new byte[] { 9 });
        File.SetLastWriteTimeUtc(strayFile, OldUtc);

        var rootFile = Path.Combine(_factory.BackupRoot, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(rootFile, new byte[] { 9 });
        File.SetLastWriteTimeUtc(rootFile, OldUtc);

        await RunJobAsync();

        File.Exists(wrongExtension).Should().BeTrue(".bin olmayan dosya kapsam dışı");
        File.Exists(strayFile).Should().BeTrue("adı GUID olmayan klasör kapsam dışı");
        File.Exists(rootFile).Should().BeTrue("kökün doğrudan altındaki dosya kapsam dışı");
    }

    /// <summary>
    /// En korkutucu arıza: veritabanı okunamazken işin "hiç satır yok, demek ki
    /// hepsi yetim" sonucuna varıp müşterilerin bütün yedeklerini silmesi.
    /// Referans kümesi tek bir silmeden ÖNCE okunduğu için sorgu patladığında iş
    /// hiçbir dosyaya dokunmadan düşer.
    /// </summary>
    [Fact]
    public async Task RunAsync_deletes_nothing_when_the_database_is_unreachable()
    {
        var customerId = await CreateCustomerAsync();
        var orphan = SeedBlob(customerId, OldUtc);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();
        db.Dispose();  // sonraki her sorgu ObjectDisposedException fırlatır

        var job = new BackupOrphanCleanupJob(
            db, storage, NullLogger<BackupOrphanCleanupJob>.Instance);

        await job.Invoking(j => j.RunAsync()).Should().ThrowAsync<ObjectDisposedException>();

        File.Exists(orphan).Should().BeTrue(
            "veritabanı okunamıyorsa iş hiçbir şey silmemeli — yetim olsa bile");
    }
}
