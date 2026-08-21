using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Backups;

/// <summary>
/// Dosya sistemi ile veritabanı aynı işleme giremez, dolayısıyla biri diğeri
/// olmadan başarılı olabilir. Soru "nasıl atomik yaparız" değil — <b>hangi
/// tarafın patlamasına izin veririz</b>:
/// <list type="bullet">
/// <item><b>Yetim blob</b> (dosya var, satır yok): disk yer kaplar, kimse görmez,
/// mutabakat işi toplar.</item>
/// <item><b>Hayalet satır</b> (satır var, dosya yok): müşteri yedeğini listede
/// görür, geri yüklemeye kalktığı an — tam ihtiyaç duyduğu anda — yoktur.</item>
/// </list>
/// Bu testler seçimi bağlar: veritabanı yazması patladığında geriye <b>asla</b>
/// hayalet satır kalmamalı. Arıza gerçek bir <c>SaveChanges</c> hatasıyla
/// üretiliyor, kod yolu taklit edilmiyor.
/// </summary>
public class BackupWriteOrderingTests
{
    private static byte[] Payload()
    {
        var b = new byte[512];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IReadOnlyList<string> BlobFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).ToList()
            : Array.Empty<string>();

    private static async Task<HttpResponseMessage?> TryUploadAsync(HttpClient client, byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha256Hex(payload));
        // Arızalı kaydetme, istisna olarak da 500 olarak da yüzeye çıkabilir;
        // hangisi olduğu bu testin konusu değil — geride kalan durum konusu.
        try { return await client.PostAsync("/api/v1/me/backups", content); }
        catch { return null; }
    }

    [Fact]
    public async Task Yukleme_satir_yazmasi_patlarsa_diskte_blob_birakmaz()
    {
        using var factory = new FailBackupInsertApiFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await TryUploadAsync(client, Payload());
        resp?.IsSuccessStatusCode.Should().NotBe(true, "satır yazılamadıysa yükleme başarılı sayılamaz");

        BlobFiles(factory.BackupRoot).Should().BeEmpty(
            "satır yazılamadıysa az önce yazılan blob geride bırakılmamalı");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.CustomerBackups.CountAsync(b => b.CustomerId == customerId)).Should().Be(0);
    }

    [Fact]
    public async Task Silme_satir_yazmasi_patlarsa_blob_diskte_kalir()
    {
        using var factory = new FailBackupDeleteApiFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var upload = await TryUploadAsync(client, Payload());
        upload!.IsSuccessStatusCode.Should().BeTrue("silme testi için önce gerçek bir yedek gerekiyor");

        Guid backupId;
        string blobPath;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = await db.CustomerBackups.SingleAsync(b => b.CustomerId == customerId);
            backupId = row.Id;
            blobPath = row.BlobPath;
        }
        File.Exists(blobPath).Should().BeTrue();

        try { await client.DeleteAsync($"/api/v1/me/backups/{backupId}"); }
        catch { /* arıza istisna olarak da yüzeye çıkabilir */ }

        // Satır silinemediyse dosya DA durmalı: ikisi tutarlı kalır, müşteri
        // listede gördüğü yedeği hâlâ geri yükleyebilir.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.CustomerBackups.CountAsync(b => b.Id == backupId)).Should().Be(1);
        }
        File.Exists(blobPath).Should().BeTrue(
            "satır duruyorsa blob da durmalı — aksi hâlde geri yüklenemeyen bir hayalet yedek kalır");
    }

    /// <summary>
    /// Saklama kırpması toplu siler: ters sırada tek bir <c>SaveChanges</c>
    /// hatası bir anda BİRDEN ÇOK hayalet satır bırakabilirdi. Yollar içinde
    /// hasarı en büyük olan bu.
    /// </summary>
    [Fact]
    public async Task Kirpma_satir_yazmasi_patlarsa_hicbir_blob_silinmez()
    {
        using var factory = new FailBackupDeleteApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider
            .GetRequiredService<OrderDeck.LicenseServer.Services.Backup.BackupStorageService>();
        var retention = scope.ServiceProvider
            .GetRequiredService<OrderDeck.LicenseServer.Services.Backup.BackupRetentionService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"trim-order-{Guid.NewGuid():N}@t.com",
            Name = "T",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        // Kırpma eşiği 5; 6 kilometre taşı olmayan satır bir tanesinin
        // silinmesini tetikler.
        var newestId = Guid.Empty;
        var paths = new List<string>();
        for (var day = 1; day <= 6; day++)
        {
            var (envelope, _) = storage.Encrypt(new byte[] { 1, 2, 3 });
            var path = await storage.WriteBlobAsync(customer.Id, envelope);
            paths.Add(path);
            var row = new CustomerBackup
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                BlobPath = path,
                SizeBytes = new FileInfo(path).Length,
                ChecksumSha256 = new string('0', 64),
                CreatedAt = new DateTimeOffset(2026, 3, day, 10, 0, 0, TimeSpan.Zero),
                IsMonthlyMilestone = false,
            };
            db.CustomerBackups.Add(row);
            newestId = row.Id;
        }
        await db.SaveChangesAsync();

        await retention.Invoking(r => r.EnforceAfterInsertAsync(customer.Id, newestId))
            .Should().ThrowAsync<InvalidOperationException>();

        (await db.CustomerBackups.CountAsync(b => b.CustomerId == customer.Id)).Should().Be(6);
        paths.Should().OnlyContain(p => File.Exists(p),
            "satırlar silinemediyse hiçbir blob silinmemeli — aksi hâlde tek seferde " +
            "birden çok hayalet yedek oluşur");
    }

    /// <summary>Yeni bir <see cref="CustomerBackup"/> satırı yazılmaya
    /// çalışıldığında kaydetmeyi patlatır.</summary>
    private sealed class FailOnBackupInsertInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Guard(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Guard(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void Guard(DbContextEventData eventData)
        {
            if (eventData.Context is null) return;
            if (eventData.Context.ChangeTracker.Entries<CustomerBackup>()
                    .Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Test: yedek satırı yazımı patladı.");
        }
    }

    /// <summary>Bir <see cref="CustomerBackup"/> satırı silinmeye
    /// çalışıldığında kaydetmeyi patlatır. Ekleme etkilenmez — testin önce
    /// gerçek bir yedek oluşturabilmesi gerekiyor.</summary>
    private sealed class FailOnBackupDeleteInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Guard(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Guard(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void Guard(DbContextEventData eventData)
        {
            if (eventData.Context is null) return;
            if (eventData.Context.ChangeTracker.Entries<CustomerBackup>()
                    .Any(e => e.State == EntityState.Deleted))
                throw new InvalidOperationException("Test: yedek satırı silme yazımı patladı.");
        }
    }

    private sealed class FailBackupInsertApiFactory : ApiFactory
    {
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) =>
            opt.AddInterceptors(new FailOnBackupInsertInterceptor());
    }

    private sealed class FailBackupDeleteApiFactory : ApiFactory
    {
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) =>
            opt.AddInterceptors(new FailOnBackupDeleteInterceptor());
    }
}
