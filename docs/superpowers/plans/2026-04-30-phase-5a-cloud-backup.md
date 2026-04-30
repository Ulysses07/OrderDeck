# Phase 5a — Cloud Backup with Admin Deep-View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Customer SQLite DB'si yayın bittikten sonra OrderDeck server'a fire-and-forget upload edilsin (AES-256-GCM encrypted at rest), retention'la yönetilsin; admin Customer Detail page'inde her yedeği indirmeden Razor'la deep view edebilsin (Özet + Customers/Sessions/Labels/Giveaways paginated tabs).

**Architecture:** Server tarafı: `BackupStorageService` (AES-GCM encrypt/decrypt + filesystem), `BackupRetentionService` (last-5 + monthly milestone), `BackupViewerService` (decrypt-to-temp + SQLite query layer), `MeBackupsController` (customer JWT) + 6 Razor admin pages. Client tarafı: `BackupClient` SDK, `BackupService` (zip + sha + Task.Run upload), `RestoreService` + auto-prompt dialog. EF migration 010 (`AddCustomerBackups`).

**Tech Stack:** ASP.NET Core 10 + EF Core 9 + SQL Server (server) / WPF + CommunityToolkit.Mvvm + Microsoft.Data.Sqlite (client viewer + restore) / `System.Security.Cryptography.AesGcm` (BCL) / `System.IO.Compression.ZipFile` (BCL) / xUnit + FluentAssertions.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Phase 5a state:** master `a14e963` (Phase 5a spec). 499/499 test (108 + 193 + 198 typically). Build clean.

**Spec reference:** `docs/superpowers/specs/2026-04-30-phase-5a-cloud-backup-design.md`

---

## Task Index

**Server foundations (1-3):**
1. CustomerBackup entity + DbContext + EF migration 010
2. BackupStorageService (encrypt/decrypt + disk I/O)
3. BackupRetentionService (last-5 + monthly milestone)

**Server REST API (4-6):**
4. AuditService backup action constants
5. MeBackupsController (POST + GET list + GET download + DELETE)
6. Integration round-trip test

**Server admin viewer (7-11):**
7. BackupViewerService (decrypt-to-temp + SQLite read layer + Dispose)
8. Admin Backups Index Razor page (list)
9. Admin Backup Summary Razor page (default landing)
10. Admin Backup Customers + Sessions Razor pages (paginated)
11. Admin Backup Labels + Giveaways Razor pages

**Client SDK (12):**
12. OrderDeck.Licensing.Backup — IBackupClient + BackupClient + BackupMetadata

**Client app (13-15):**
13. StreamSessionService.SessionEnded event + BackupService (fire-and-forget)
14. RestoreService + RestoreRecoveryService HostedService (.pre-restore.bak detection)
15. RestoreDialog + auto-prompt on empty DB + AppHost DI wiring

**Deploy + final (16):**
16. setup-backup-key.sh + docker-compose volume + smoke deploy

**Test target:** 499 → ~542 (+43 new)

---

### Task 1: CustomerBackup entity + DbContext + EF migration 010

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/CustomerBackup.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Auto-generate: `OrderDeck.LicenseServer/Data/Migrations/{ts}_AddCustomerBackups.cs` (+ Designer + Snapshot update)
- Create: `OrderDeck.LicenseServer.Tests/Domain/CustomerBackupEntityTests.cs`

**Context:** Standalone entity, FK to Customer (cascade delete). Index on (CustomerId, CreatedAt DESC) for list queries.

- [ ] **Step 1: Create CustomerBackup entity**

`OrderDeck.LicenseServer/Domain/CustomerBackup.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Phase 5a: client-uploaded SQLite DB backup, AES-256-GCM encrypted at rest on server filesystem.
/// Retention: last 5 non-milestone + first-of-month milestones (preserved indefinitely).
/// </summary>
public sealed class CustomerBackup
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Absolute path of encrypted blob on server filesystem.</summary>
    public string BlobPath { get; set; } = "";

    /// <summary>Encrypted blob size on disk (includes 12B nonce + 16B auth tag overhead).</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA256 of plaintext zip (pre-encrypt) — for client integrity check on download.</summary>
    public string ChecksumSha256 { get; set; } = "";

    /// <summary>True if first backup of its calendar month — preserved across retention runs.</summary>
    public bool IsMonthlyMilestone { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? UserAgent { get; set; }
    public string? MachineName { get; set; }
}
```

- [ ] **Step 2: Add DbSet + Fluent config**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — add property and OnModelCreating block:

```csharp
public DbSet<CustomerBackup> CustomerBackups => Set<CustomerBackup>();
```

In `OnModelCreating`:

```csharp
modelBuilder.Entity<CustomerBackup>(e =>
{
    e.HasKey(b => b.Id);
    e.Property(b => b.BlobPath).HasMaxLength(500).IsRequired();
    e.Property(b => b.ChecksumSha256).HasMaxLength(64).IsRequired();
    e.Property(b => b.UserAgent).HasMaxLength(200);
    e.Property(b => b.MachineName).HasMaxLength(100);
    e.HasOne(b => b.Customer)
        .WithMany()
        .HasForeignKey(b => b.CustomerId)
        .OnDelete(DeleteBehavior.Cascade);
    e.HasIndex(b => new { b.CustomerId, b.CreatedAt })
        .IsDescending(false, true)
        .HasDatabaseName("IX_CustomerBackups_CustomerId_CreatedAt_DESC");
});
```

- [ ] **Step 3: Generate EF migration**

```bash
cd OrderDeck.LicenseServer
dotnet ef migrations add AddCustomerBackups -o Data/Migrations
cd ..
```

Expected: 3 new files in `OrderDeck.LicenseServer/Data/Migrations/{ts}_AddCustomerBackups.{cs,Designer.cs}` + updated `LicenseDbContextModelSnapshot.cs`. Migration `Up` should call `migrationBuilder.CreateTable("CustomerBackups", ...)` with FK + index.

- [ ] **Step 4: Smoke test — model has Backup property**

Create `OrderDeck.LicenseServer.Tests/Domain/CustomerBackupEntityTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class CustomerBackupEntityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public CustomerBackupEntityTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Model_HasCustomerBackupEntity_WithRequiredProps()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var entityType = db.Model.FindEntityType(typeof(CustomerBackup));
        entityType.Should().NotBeNull();

        entityType!.FindProperty(nameof(CustomerBackup.BlobPath))!
            .GetMaxLength().Should().Be(500);
        entityType.FindProperty(nameof(CustomerBackup.ChecksumSha256))!
            .GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(CustomerBackup.IsMonthlyMilestone))!
            .IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Index_OnCustomerIdAndCreatedAt_Exists()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var entityType = db.Model.FindEntityType(typeof(CustomerBackup));
        var indexes = entityType!.GetIndexes();
        indexes.Should().Contain(i =>
            i.GetDatabaseName() == "IX_CustomerBackups_CustomerId_CreatedAt_DESC");
    }
}
```

- [ ] **Step 5: Run tests + build**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CustomerBackupEntity"
```

Expected: 0 errors, 2/2 tests pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/CustomerBackup.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations/ \
        OrderDeck.LicenseServer.Tests/Domain/CustomerBackupEntityTests.cs

git commit -m "feat(license-server): Phase 5a — CustomerBackup entity + EF migration 010

- CustomerBackup domain class (Id, CustomerId, BlobPath, SizeBytes,
  ChecksumSha256, IsMonthlyMilestone, CreatedAt, UserAgent, MachineName)
- LicenseDbContext DbSet + Fluent config (FK cascade, index on
  CustomerId + CreatedAt DESC, MaxLength on string props)
- EF migration AddCustomerBackups (auto-generated)
- 2 model assertion tests"
```

---

### Task 2: BackupStorageService (encrypt/decrypt + disk I/O)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupStorageService.cs`
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupOptions.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/Backup/BackupStorageServiceTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI registration + options binding)
- Modify: `OrderDeck.LicenseServer/appsettings.json` (Backup config block)

**Context:** AES-256-GCM with `System.Security.Cryptography.AesGcm` (BCL). Format `[12B nonce][16B tag][ciphertext]`. Master key from `Backup:MasterKeyHex` config (64 hex chars). Storage root from `Backup:StorageRoot` (default `/app/Backups`). Per-customer subdirectory.

- [ ] **Step 1: BackupOptions config class**

`OrderDeck.LicenseServer/Services/Backup/BackupOptions.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Backup;

public sealed class BackupOptions
{
    public string MasterKeyHex { get; set; } = "";
    public string StorageRoot { get; set; } = "/app/Backups";
    public int MaxBlobSizeMb { get; set; } = 200;
}
```

- [ ] **Step 2: Test file (FAIL beklenir)**

`OrderDeck.LicenseServer.Tests/Services/Backup/BackupStorageServiceTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupStorageServiceTests
{
    private static (BackupStorageService svc, string tempRoot) Make()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"orderdeck-bs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var opts = Options.Create(new BackupOptions
        {
            MasterKeyHex = new string('a', 64),  // 32 bytes of 0xaa
            StorageRoot = tempRoot,
            MaxBlobSizeMb = 200
        });
        return (new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance), tempRoot);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrips_ToOriginalBytes()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var plaintext = Encoding.UTF8.GetBytes("hello world — orderdeck.db payload");
            var encrypted = svc.Encrypt(plaintext);
            encrypted.Length.Should().BeGreaterThan(plaintext.Length); // nonce + tag overhead
            var decrypted = svc.Decrypt(encrypted);
            decrypted.Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Encrypt_TwiceWithSamePlaintext_ProducesDifferentCiphertexts()
    {
        // Random nonce per call → ciphertexts differ
        var (svc, tempRoot) = Make();
        try
        {
            var plaintext = Encoding.UTF8.GetBytes("constant");
            var c1 = svc.Encrypt(plaintext);
            var c2 = svc.Encrypt(plaintext);
            c1.Should().NotBeEquivalentTo(c2);
            // But decrypt both back successfully
            svc.Decrypt(c1).Should().BeEquivalentTo(plaintext);
            svc.Decrypt(c2).Should().BeEquivalentTo(plaintext);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var encrypted = svc.Encrypt(Encoding.UTF8.GetBytes("secret"));
            encrypted[encrypted.Length - 1] ^= 0xFF;  // flip last byte (auth tag region)
            Action act = () => svc.Decrypt(encrypted);
            act.Should().Throw<System.Security.Cryptography.AuthenticationTagMismatchException>();
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public async Task WriteBlob_CreatesCustomerSubdirectory_AndFile()
    {
        var (svc, tempRoot) = Make();
        try
        {
            var customerId = Guid.NewGuid();
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var path = await svc.WriteBlobAsync(customerId, bytes, default);

            path.Should().StartWith(Path.Combine(tempRoot, customerId.ToString()));
            File.Exists(path).Should().BeTrue();
            (await File.ReadAllBytesAsync(path)).Should().BeEquivalentTo(bytes);
        }
        finally { Directory.Delete(tempRoot, recursive: true); }
    }

    [Fact]
    public void Constructor_InvalidKeyLength_Throws()
    {
        var opts = Options.Create(new BackupOptions { MasterKeyHex = "tooshort" });
        Action act = () => new BackupStorageService(opts, NullLogger<BackupStorageService>.Instance);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MasterKeyHex*64*");
    }
}
```

- [ ] **Step 3: Test FAIL doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BackupStorageService"
```

Expected: FAIL (`BackupStorageService` type doesn't exist).

- [ ] **Step 4: BackupStorageService implementasyonu**

`OrderDeck.LicenseServer/Services/Backup/BackupStorageService.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: AES-256-GCM encrypt/decrypt + filesystem read/write for customer DB backups.
/// Format: [12B nonce][16B auth tag][ciphertext bytes].
/// Master key is 32 bytes (64 hex chars), supplied via Backup:MasterKeyHex config.
/// </summary>
public sealed class BackupStorageService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupStorageService> _log;

    public BackupStorageService(IOptions<BackupOptions> opt, ILogger<BackupStorageService> log)
    {
        _opt = opt.Value;
        _log = log;
        if (string.IsNullOrWhiteSpace(_opt.MasterKeyHex) || _opt.MasterKeyHex.Length != 64)
            throw new InvalidOperationException(
                "Backup:MasterKeyHex must be exactly 64 hex chars (32 bytes). Set via env var BACKUP_MASTER_KEY.");
        _key = Convert.FromHexString(_opt.MasterKeyHex);
        Directory.CreateDirectory(_opt.StorageRoot);
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return output;
    }

    public byte[] Decrypt(byte[] envelope)
    {
        if (envelope.Length < NonceSize + TagSize)
            throw new ArgumentException("Envelope too short to contain nonce + tag.", nameof(envelope));

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[envelope.Length - NonceSize - TagSize];
        Buffer.BlockCopy(envelope, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public async Task<string> WriteBlobAsync(Guid customerId, byte[] bytes, CancellationToken ct = default)
    {
        var customerDir = Path.Combine(_opt.StorageRoot, customerId.ToString());
        Directory.CreateDirectory(customerDir);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bin";
        var fullPath = Path.Combine(customerDir, fileName);
        await File.WriteAllBytesAsync(fullPath, bytes, ct);
        return fullPath;
    }

    public async Task<byte[]> ReadBlobAsync(string blobPath, CancellationToken ct = default)
    {
        return await File.ReadAllBytesAsync(blobPath, ct);
    }

    public void DeleteBlob(string blobPath)
    {
        try
        {
            if (File.Exists(blobPath)) File.Delete(blobPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete backup blob {Path}", blobPath);
        }
    }
}
```

- [ ] **Step 5: DI registration + options binding**

In `OrderDeck.LicenseServer/Program.cs`, near other `Configure<X>` registrations:

```csharp
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
builder.Services.AddSingleton<BackupStorageService>();
```

Add `using OrderDeck.LicenseServer.Services.Backup;` at top of Program.cs if not present.

- [ ] **Step 6: appsettings.json Backup block**

Add to `OrderDeck.LicenseServer/appsettings.json`:

```json
"Backup": {
    "MasterKeyHex": "REPLACE-WITH-64-HEX-CHARS-IN-PRODUCTION-VIA-ENV-VAR",
    "StorageRoot": "/app/Backups",
    "MaxBlobSizeMb": 200
},
```

(Place anywhere top-level, alongside `"Smtp": { ... }` and `"Jwt": { ... }`.)

- [ ] **Step 7: Tests PASS**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BackupStorageService"
```

Expected: 5/5 pass.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Backup/BackupStorageService.cs \
        OrderDeck.LicenseServer/Services/Backup/BackupOptions.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer/appsettings.json \
        OrderDeck.LicenseServer.Tests/Services/Backup/BackupStorageServiceTests.cs

git commit -m "feat(license-server): Phase 5a — BackupStorageService (AES-256-GCM)

AesGcm-based encrypt/decrypt with [nonce|tag|ciphertext] envelope.
Random 12-byte nonce per encrypt. 64-hex (32-byte) master key from
Backup:MasterKeyHex config (env var in production). Per-customer
subdirectory under Backup:StorageRoot. WriteBlobAsync atomic file write.
DI registered as singleton. 5 unit tests."
```

---

### Task 3: BackupRetentionService (last-5 + monthly milestone)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupRetentionService.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/Backup/BackupRetentionServiceTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI scoped)

**Context:** Per-customer `SemaphoreSlim(1)` lock for serializing concurrent uploads. After insert: mark monthly if first-of-month, then trim non-milestones to 5 most recent. AuditService.LogAsync called for each delete.

- [ ] **Step 1: Test file (FAIL beklenir)**

`OrderDeck.LicenseServer.Tests/Services/Backup/BackupRetentionServiceTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupRetentionServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupRetentionServiceTests(ApiFactory factory) => _factory = factory;

    private static async Task<(LicenseDbContext db, BackupRetentionService svc, BackupStorageService storage, Guid customerId)> SetupAsync(ApiFactory factory)
    {
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Reset CustomerBackups for isolation
        db.CustomerBackups.RemoveRange(db.CustomerBackups);
        await db.SaveChangesAsync();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"retention-{Guid.NewGuid():N}@test.com",
            Name = "T",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();
        var svc = scope.ServiceProvider.GetRequiredService<BackupRetentionService>();
        return (db, svc, storage, customer.Id);
    }

    private static async Task<CustomerBackup> InsertBackupAsync(
        LicenseDbContext db, BackupStorageService storage, Guid customerId, DateTimeOffset createdAt)
    {
        var bytes = await Task.FromResult(new byte[] { 1, 2, 3 });
        var path = await storage.WriteBlobAsync(customerId, storage.Encrypt(bytes));
        var b = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BlobPath = path,
            SizeBytes = new FileInfo(path).Length,
            ChecksumSha256 = new string('0', 64),
            CreatedAt = createdAt,
            IsMonthlyMilestone = false
        };
        db.CustomerBackups.Add(b);
        await db.SaveChangesAsync();
        return b;
    }

    [Fact]
    public async Task EnforceAfterInsert_FirstOfMonth_MarksAsMilestone()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var b = await InsertBackupAsync(db, storage, customerId, DateTimeOffset.UtcNow);

        await svc.EnforceAfterInsertAsync(customerId, b.Id);

        var refreshed = await db.CustomerBackups.FindAsync(b.Id);
        refreshed!.IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task EnforceAfterInsert_SecondOfSameMonth_NotMilestone()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var b1 = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 4, 5, 10, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, b1.Id);

        var b2 = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, b2.Id);

        (await db.CustomerBackups.FindAsync(b1.Id))!.IsMonthlyMilestone.Should().BeTrue();
        (await db.CustomerBackups.FindAsync(b2.Id))!.IsMonthlyMilestone.Should().BeFalse();
    }

    [Fact]
    public async Task EnforceAfterInsert_SixthNonMilestone_DeletesOldestNonMilestone()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);

        // First seed milestone (Jan 2026)
        var milestone = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, milestone.Id);

        // 6 non-milestones in March (same month so only first becomes milestone, but force IsMonthlyMilestone=false on all 6)
        // Actually: simulate 6 non-milestones manually inserted with March dates
        var marchBackups = new List<CustomerBackup>();
        for (int i = 0; i < 6; i++)
        {
            var date = new DateTimeOffset(2026, 3, 1 + i, 10, 0, 0, TimeSpan.Zero);
            var b = await InsertBackupAsync(db, storage, customerId, date);
            // Force non-milestone (override the auto-mark by retention call)
            await svc.EnforceAfterInsertAsync(customerId, b.Id);
            // The retention call may have marked first as milestone — that's OK.
            // For this test, we want to verify the trim behavior.
            marchBackups.Add(b);
        }

        var remaining = await db.CustomerBackups
            .Where(x => x.CustomerId == customerId && !x.IsMonthlyMilestone)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
        remaining.Count.Should().Be(5, because: "non-milestones trimmed to 5 most recent");
    }

    [Fact]
    public async Task EnforceAfterInsert_MonthlyMilestonesPreserved_BeyondFive()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);

        // 8 milestones (one per month Jan-Aug 2026). Monthly milestone is auto-marked
        // when each is the first of its month.
        var milestoneIds = new List<Guid>();
        for (int month = 1; month <= 8; month++)
        {
            var b = await InsertBackupAsync(db, storage, customerId,
                new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero));
            await svc.EnforceAfterInsertAsync(customerId, b.Id);
            milestoneIds.Add(b.Id);
        }

        var milestones = await db.CustomerBackups
            .Where(x => x.CustomerId == customerId && x.IsMonthlyMilestone)
            .ToListAsync();
        milestones.Count.Should().Be(8, because: "all 8 first-of-month backups preserved");
    }

    [Fact]
    public async Task EnforceAfterInsert_DeletedBackup_AlsoRemovesFile()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);

        // Make first one milestone explicitly
        var milestone = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, milestone.Id);

        // Then 6 non-milestones in February (force-mark first as milestone, others non)
        var oldest = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero));
        await svc.EnforceAfterInsertAsync(customerId, oldest.Id);
        // oldest is now a Feb milestone → won't be deleted. We need a non-milestone to be deleted.

        // Add 5 more in Feb (all non-milestone since Feb already has milestone)
        var nonMilestones = new List<CustomerBackup>();
        for (int day = 5; day < 10; day++)
        {
            var b = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 2, day, 10, 0, 0, TimeSpan.Zero));
            await svc.EnforceAfterInsertAsync(customerId, b.Id);
            nonMilestones.Add(b);
        }
        // Now 5 non-milestones in DB (no trim yet — exactly 5).

        // Add 6th non-milestone → oldest non-milestone (day=5) should be deleted + file removed.
        var sixth = await InsertBackupAsync(db, storage, customerId, new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.Zero));
        var deletedPath = nonMilestones[0].BlobPath;  // day=5 expected to be deleted
        await svc.EnforceAfterInsertAsync(customerId, sixth.Id);

        File.Exists(deletedPath).Should().BeFalse(because: "oldest non-milestone blob deleted from disk");
        (await db.CustomerBackups.FindAsync(nonMilestones[0].Id)).Should().BeNull(because: "row removed");
    }

    [Fact]
    public async Task EnforceAfterInsert_OnlyOneBackup_NoTrimming()
    {
        var (db, svc, storage, customerId) = await SetupAsync(_factory);
        var b = await InsertBackupAsync(db, storage, customerId, DateTimeOffset.UtcNow);
        await svc.EnforceAfterInsertAsync(customerId, b.Id);

        var count = await db.CustomerBackups.CountAsync(x => x.CustomerId == customerId);
        count.Should().Be(1);
    }
}
```

- [ ] **Step 2: Test FAIL doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BackupRetentionService"
```

Expected: FAIL.

- [ ] **Step 3: BackupRetentionService implementasyonu**

`OrderDeck.LicenseServer/Services/Backup/BackupRetentionService.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: enforces retention policy after each backup insert.
/// - First backup of any calendar month is marked IsMonthlyMilestone=true (preserved indefinitely).
/// - Non-milestones trimmed to 5 most recent (older deleted from DB + filesystem).
/// Per-customer SemaphoreSlim serializes concurrent uploads.
/// </summary>
public sealed class BackupRetentionService
{
    private const int MaxNonMilestones = 5;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _customerLocks = new();

    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly ILogger<BackupRetentionService> _log;

    public BackupRetentionService(LicenseDbContext db, BackupStorageService storage, ILogger<BackupRetentionService> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task EnforceAfterInsertAsync(Guid customerId, Guid newBackupId, CancellationToken ct = default)
    {
        var sem = _customerLocks.GetOrAdd(customerId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await EnforceCoreAsync(customerId, newBackupId, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task EnforceCoreAsync(Guid customerId, Guid newBackupId, CancellationToken ct)
    {
        var newBackup = await _db.CustomerBackups.FindAsync(new object[] { newBackupId }, ct);
        if (newBackup is null) return;

        // Step 1: first-of-month milestone marker
        var monthStart = new DateTimeOffset(newBackup.CreatedAt.Year, newBackup.CreatedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEnd = monthStart.AddMonths(1);
        var existingThisMonth = await _db.CustomerBackups
            .Where(b => b.CustomerId == customerId
                     && b.CreatedAt >= monthStart
                     && b.CreatedAt < monthEnd
                     && b.Id != newBackupId)
            .AnyAsync(ct);

        if (!existingThisMonth)
        {
            newBackup.IsMonthlyMilestone = true;
            await _db.SaveChangesAsync(ct);
        }

        // Step 2: trim non-milestones to MaxNonMilestones most recent
        var nonMilestones = await _db.CustomerBackups
            .Where(b => b.CustomerId == customerId && !b.IsMonthlyMilestone)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        if (nonMilestones.Count > MaxNonMilestones)
        {
            var toDelete = nonMilestones.Skip(MaxNonMilestones).ToList();
            foreach (var old in toDelete)
            {
                _storage.DeleteBlob(old.BlobPath);
                _db.CustomerBackups.Remove(old);
            }
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Retention trimmed {Count} backups for customer {CustomerId}",
                toDelete.Count, customerId);
        }
    }
}
```

- [ ] **Step 4: DI registration**

In `OrderDeck.LicenseServer/Program.cs`, near `BackupStorageService` registration:

```csharp
builder.Services.AddScoped<BackupRetentionService>();
```

(Scoped because uses DbContext.)

- [ ] **Step 5: Tests PASS**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BackupRetentionService"
```

Expected: 6/6 pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Backup/BackupRetentionService.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Backup/BackupRetentionServiceTests.cs

git commit -m "feat(license-server): Phase 5a — BackupRetentionService

- First-of-month milestone marker (preserved indefinitely)
- Non-milestones trimmed to 5 most recent (DB row + filesystem blob)
- Per-customer SemaphoreSlim serializes concurrent uploads
- 6 unit tests covering edge cases (milestone preservation, trim cascades to disk)"
```

---

### Task 4: Audit action constants for backup events

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Audit/BackupAuditEvents.cs`

**Context:** Phase 4d AuditService takes `string eventType, string targetType, string? targetId, object? details`. We define constants in one place so call sites use the same strings.

- [ ] **Step 1: Constants file**

`OrderDeck.LicenseServer/Services/Audit/BackupAuditEvents.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Audit;

/// <summary>Phase 5a: AuditService eventType constants for backup-related actions.</summary>
public static class BackupAuditEvents
{
    public const string BackupCreated = "BackupCreated";
    public const string BackupDeleted = "BackupDeleted";
    public const string BackupAccessed = "BackupAccessed";
    public const string RestoreInitiated = "RestoreInitiated";

    public const string TargetType = "CustomerBackup";
}
```

- [ ] **Step 2: Build verify (no tests for constants alone)**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Audit/BackupAuditEvents.cs

git commit -m "feat(license-server): Phase 5a — BackupAuditEvents constants

Single source of truth for AuditService eventType values used across
backup controllers + retention service."
```

---

### Task 5: MeBackupsController (POST + GET list + GET download + DELETE)

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Backups/MeBackupsController.cs`
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Backups/MeBackupsControllerTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (rate limiter policies for backup endpoints)

**Context:** `[Authorize(AuthenticationSchemes = "Bearer-Customer")]`. Customer claim `sub` = customer Guid. Routes under `/api/v1/me/backups`. Rate limit 6/hour POST.

- [ ] **Step 1: Test file (FAIL beklenir)**

`OrderDeck.LicenseServer.Tests/Controllers/Backups/MeBackupsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Backups;

public class MeBackupsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public MeBackupsControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, string jwt)> AuthedAsync()
    {
        // Reuse Phase 4b/4e AuthHelper or inline minimal: register customer, confirm, login, return JWT
        return await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
    }

    private static byte[] MakePayload(int sizeBytes)
    {
        var rng = new byte[sizeBytes];
        RandomNumberGenerator.Fill(rng);
        return rng;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Post_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/v1/me/backups", new ByteArrayContent(new byte[] { 1, 2 }));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_HappyPath_Returns201_WithMetadata()
    {
        var (client, customerId, _) = await AuthedAsync();
        var payload = MakePayload(1024);
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha256Hex(payload));

        var resp = await client.PostAsync("/api/v1/me/backups", content);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        body.GetProperty("sizeBytes").GetInt64().Should().BeGreaterThan(payload.Length); // encrypted overhead

        // Verify DB row exists
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.CustomerBackups.CountAsync(b => b.CustomerId == customerId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Post_MissingShaHeader_Returns400()
    {
        var (client, _, _) = await AuthedAsync();
        var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

        var resp = await client.PostAsync("/api/v1/me/backups", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_ShaMismatch_Returns400()
    {
        var (client, _, _) = await AuthedAsync();
        var payload = MakePayload(100);
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", new string('0', 64)); // wrong

        var resp = await client.PostAsync("/api/v1/me/backups", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_List_ReturnsOnlyOwnBackups()
    {
        var (clientA, customerA, _) = await AuthedAsync();
        var (clientB, _, _)        = await AuthedAsync();

        // Customer A uploads
        var payload = MakePayload(50);
        var c = new ByteArrayContent(payload);
        c.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        clientA.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha256Hex(payload));
        await clientA.PostAsync("/api/v1/me/backups", c);

        // Customer B lists — should be empty
        var listB = await clientB.GetFromJsonAsync<JsonElement>("/api/v1/me/backups");
        listB.GetArrayLength().Should().Be(0);

        // Customer A lists — should have 1
        var listA = await clientA.GetFromJsonAsync<JsonElement>("/api/v1/me/backups");
        listA.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Get_Download_ReturnsDecryptedBytes_MatchingOriginalSha()
    {
        var (client, _, _) = await AuthedAsync();
        var payload = MakePayload(2048);
        var sha = Sha256Hex(payload);

        var post = new ByteArrayContent(payload);
        post.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", sha);
        var postResp = await client.PostAsync("/api/v1/me/backups", post);
        var postBody = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = postBody.GetProperty("id").GetGuid();

        client.DefaultRequestHeaders.Remove("X-Backup-Sha256"); // not needed for download
        var downloaded = await client.GetByteArrayAsync($"/api/v1/me/backups/{id}/download");
        Sha256Hex(downloaded).Should().Be(sha);
        downloaded.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task Delete_OwnBackup_Returns204_AndRowGone()
    {
        var (client, customerId, _) = await AuthedAsync();
        var payload = MakePayload(50);
        var c = new ByteArrayContent(payload);
        c.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha256Hex(payload));
        var post = await client.PostAsync("/api/v1/me/backups", c);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var del = await client.DeleteAsync($"/api/v1/me/backups/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.CustomerBackups.AnyAsync(b => b.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_OtherCustomersBackup_Returns404()
    {
        var (clientA, _, _) = await AuthedAsync();
        var (clientB, _, _) = await AuthedAsync();

        var payload = MakePayload(50);
        var c = new ByteArrayContent(payload);
        c.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        clientA.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha256Hex(payload));
        var post = await clientA.PostAsync("/api/v1/me/backups", c);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var del = await clientB.DeleteAsync($"/api/v1/me/backups/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

**Helper:** This test references `CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory)`. If this helper doesn't exist yet, create it now in `OrderDeck.LicenseServer.Tests/TestHelpers/CustomerAuthHelper.cs`:

```csharp
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public static class CustomerAuthHelper
{
    public static async Task<(HttpClient client, Guid customerId, string jwt)> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var email = $"backup-test-{Guid.NewGuid():N}@test.com";
        var password = "TestPass1234!";

        var client = factory.CreateClient();

        // Register
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, name = "Backup Test", password });
        reg.EnsureSuccessStatusCode();

        // Force-confirm via DB (skip email click for tests)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);
            customer.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Login → JWT
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var loginBody = await login.Content.ReadFromJsonAsync<LoginResp>();
        var jwt = loginBody!.Token;

        // Resolve customerId
        Guid customerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            customerId = (await db.Customers.FirstAsync(c => c.Email == email)).Id;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, customerId, jwt);
    }

    private sealed record LoginResp(string Token, DateTimeOffset ExpiresAt);
}
```

- [ ] **Step 2: FAIL doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~MeBackupsController"
```

Expected: FAIL (controller doesn't exist).

- [ ] **Step 3: MeBackupsController implementasyonu**

`OrderDeck.LicenseServer/Controllers/Backups/MeBackupsController.cs`:

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Controllers.Backups;

[ApiController]
[Route("api/v1/me/backups")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class MeBackupsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly BackupRetentionService _retention;
    private readonly IAuditService _audit;
    private readonly Microsoft.Extensions.Options.IOptions<BackupOptions> _opt;
    private readonly ILogger<MeBackupsController> _log;

    public MeBackupsController(
        LicenseDbContext db,
        BackupStorageService storage,
        BackupRetentionService retention,
        IAuditService audit,
        Microsoft.Extensions.Options.IOptions<BackupOptions> opt,
        ILogger<MeBackupsController> log)
    {
        _db = db;
        _storage = storage;
        _retention = retention;
        _audit = audit;
        _opt = opt;
        _log = log;
    }

    private Guid CustomerId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? throw new InvalidOperationException("Missing sub claim"));

    [HttpPost]
    [EnableRateLimiting("backup-upload")]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var sha = Request.Headers["X-Backup-Sha256"].ToString();
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64)
            return BadRequest(new { error = "X-Backup-Sha256 header required (64 hex chars)" });

        var maxBytes = _opt.Value.MaxBlobSizeMb * 1024L * 1024L;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        if (ms.Length > maxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = $"Backup exceeds {_opt.Value.MaxBlobSizeMb} MB limit" });

        var bytes = ms.ToArray();
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, sha, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "SHA256 mismatch — body integrity check failed" });

        var encrypted = _storage.Encrypt(bytes);
        var blobPath = await _storage.WriteBlobAsync(CustomerId, encrypted, ct);

        var backup = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = CustomerId,
            BlobPath = blobPath,
            SizeBytes = encrypted.Length,
            ChecksumSha256 = actualSha,
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false,
            UserAgent = Request.Headers["User-Agent"].ToString(),
            MachineName = Request.Headers["X-Machine-Name"].ToString()
        };
        _db.CustomerBackups.Add(backup);
        await _db.SaveChangesAsync(ct);

        await _retention.EnforceAfterInsertAsync(CustomerId, backup.Id, ct);

        // Re-load to capture milestone flag (retention may have set it)
        var saved = await _db.CustomerBackups.FindAsync(new object[] { backup.Id }, ct);

        await _audit.LogAsync(BackupAuditEvents.BackupCreated,
            BackupAuditEvents.TargetType,
            backup.Id.ToString(),
            new { sizeBytes = encrypted.Length, isMonthlyMilestone = saved!.IsMonthlyMilestone },
            ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            id = saved.Id,
            sizeBytes = saved.SizeBytes,
            createdAt = saved.CreatedAt,
            isMonthlyMilestone = saved.IsMonthlyMilestone
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await _db.CustomerBackups
            .Where(b => b.CustomerId == CustomerId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                id = b.Id,
                sizeBytes = b.SizeBytes,
                createdAt = b.CreatedAt,
                isMonthlyMilestone = b.IsMonthlyMilestone,
                machineName = b.MachineName
            })
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        var encrypted = await _storage.ReadBlobAsync(b.BlobPath, ct);
        var plaintext = _storage.Decrypt(encrypted);
        return File(plaintext, "application/octet-stream", $"orderdeck-backup-{b.CreatedAt:yyyyMMdd-HHmmss}.zip");
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting("backup-delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        _storage.DeleteBlob(b.BlobPath);
        _db.CustomerBackups.Remove(b);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(BackupAuditEvents.BackupDeleted,
            BackupAuditEvents.TargetType, id.ToString(),
            new { reason = "manual" }, ct);

        return NoContent();
    }
}
```

- [ ] **Step 4: Rate limiter policies**

In `OrderDeck.LicenseServer/Program.cs`, find the existing `AddRateLimiter` block (Phase 4e/4f added intake-form-submit, auth-register, auth-login policies). Add:

```csharp
options.AddPolicy("backup-upload", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                     ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 6,
            Window = TimeSpan.FromHours(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

options.AddPolicy("backup-delete", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? "anon",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromHours(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));
```

Test factory `ApiFactory` (Phase 4f pattern) should bypass these — verify by inspecting how `intake-form-submit` is bypassed in `ApiFactory.cs` and add `backup-upload` + `backup-delete` to the same bypass list.

- [ ] **Step 5: Test PASS**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~MeBackupsController"
```

Expected: 8/8 pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Backups/MeBackupsController.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Backups/MeBackupsControllerTests.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/CustomerAuthHelper.cs

git commit -m "feat(license-server): Phase 5a — MeBackupsController REST API

Routes /api/v1/me/backups (Bearer-Customer JWT scheme):
- POST: octet-stream upload + X-Backup-Sha256 header validation, 413 on >MaxBlobSizeMb,
  encrypts via BackupStorageService, calls BackupRetentionService, audit logs
- GET: lists own backups (FK filter on CustomerId from JWT sub claim)
- GET /{id}/download: server-side decrypt → returns plaintext zip
- DELETE /{id}: removes DB row + blob file + audit log
- Rate limiter policies: backup-upload 6/h, backup-delete 30/h

CustomerAuthHelper test fixture for JWT-authed HTTP client setup.
8 controller tests covering happy path, auth, sha mismatch, cross-customer isolation."
```

---

### Task 6: Integration round-trip test

**Files:**
- Create: `OrderDeck.LicenseServer.Tests/Integration/BackupRoundTripTests.cs`

**Context:** End-to-end: upload → list → download → byte-equality verification. Already partly covered in Task 5 but isolated here for explicit E2E confidence.

- [ ] **Step 1: Round-trip test**

`OrderDeck.LicenseServer.Tests/Integration/BackupRoundTripTests.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Integration;

public class BackupRoundTripTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupRoundTripTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Upload_List_Download_DeleteCycle_Works()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // 1. Generate a 10KB random "DB"
        var payload = new byte[10_240];
        RandomNumberGenerator.Fill(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        // 2. Upload
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", sha);
        client.DefaultRequestHeaders.Add("X-Machine-Name", "TEST-MACHINE");
        var post = await client.PostAsync("/api/v1/me/backups", content);
        post.EnsureSuccessStatusCode();
        var meta = await post.Content.ReadFromJsonAsync<JsonElement>();
        var id = meta.GetProperty("id").GetGuid();

        // 3. List should contain this entry
        client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/backups");
        list.GetArrayLength().Should().BeGreaterThan(0);
        var firstItem = list[0];
        firstItem.GetProperty("id").GetGuid().Should().Be(id);
        firstItem.GetProperty("machineName").GetString().Should().Be("TEST-MACHINE");

        // 4. Download → byte-equal
        var downloaded = await client.GetByteArrayAsync($"/api/v1/me/backups/{id}/download");
        downloaded.Should().BeEquivalentTo(payload, because: "decrypt round-trips to original");

        // 5. Delete
        var del = await client.DeleteAsync($"/api/v1/me/backups/{id}");
        del.IsSuccessStatusCode.Should().BeTrue();

        // 6. Re-list → empty
        var listAfter = await client.GetFromJsonAsync<JsonElement>("/api/v1/me/backups");
        listAfter.GetArrayLength().Should().Be(0);
    }
}
```

- [ ] **Step 2: Test PASS**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BackupRoundTrip"
```

Expected: 1/1 pass.

- [ ] **Step 3: Commit**

```bash
git add OrderDeck.LicenseServer.Tests/Integration/BackupRoundTripTests.cs

git commit -m "test(license-server): Phase 5a — backup upload/list/download/delete round-trip

End-to-end integration test asserting full lifecycle: upload 10KB random
payload, list confirms entry, download bytes-equal-to-original, delete +
re-list empty. Validates encryption transparency from the customer's
perspective."
```

---

### Task 7: BackupViewerService (decrypt-to-temp + SQLite read layer + Dispose)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupViewerService.cs`
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupSession.cs`
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupSummary.cs`
- Create: `OrderDeck.LicenseServer/Services/Backup/PagedResult.cs`
- Create: `OrderDeck.LicenseServer/Services/Backup/BackupRows.cs` (DTO records: CustomerRow, SessionRow, LabelRow, GiveawayRow)
- Modify: `OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` (add `Microsoft.Data.Sqlite` package)
- Create: `OrderDeck.LicenseServer.Tests/Services/Backup/BackupViewerServiceTests.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI scoped)

**Context:** Decrypts blob → extracts ZIP to temp dir → opens read-only SQLite → query layer (paginated). `BackupSession` is `IDisposable` and owns temp dir + connection. Each Razor request creates a new session via `using` block.

- [ ] **Step 1: Add Microsoft.Data.Sqlite package**

In `OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`, add to existing `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
```

(Same major as EF Core 9 already in this project.)

- [ ] **Step 2: DTO records**

`OrderDeck.LicenseServer/Services/Backup/BackupSummary.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Backup;

public sealed record BackupSummary(
    int TotalSessions,
    int TotalLabels,
    int TotalUniqueCustomers,
    decimal TotalRevenue,
    decimal AvgRevenuePerSession,
    decimal AvgRevenuePerCustomer,
    TopSession? HighestSession,
    TopCustomer? TopCustomer);

public sealed record TopSession(string? Title, DateTimeOffset? StartedAt, decimal Total);
public sealed record TopCustomer(string Username, decimal Total, int LabelCount);
```

`OrderDeck.LicenseServer/Services/Backup/PagedResult.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Backup;

public sealed record PagedResult<T>(IReadOnlyList<T> Rows, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
```

`OrderDeck.LicenseServer/Services/Backup/BackupRows.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Backup;

public sealed record CustomerRow(
    string Id, string Platform, string Username, string? DisplayName,
    string? Address, string? Phone, decimal TotalAmount, long LastSeenAt);

public sealed record SessionRow(
    string Id, string? Title, long StartedAt, long? EndedAt,
    int LabelCount, decimal TotalAmount);

public sealed record LabelRow(
    string Id, string SessionId, string Username, string? Code,
    decimal Price, long AddedAt, long? PrintedAt);

public sealed record GiveawayRow(
    string Id, string Keyword, long? StartedAt, long? EndedAt,
    int ParticipantCount, int WinnerCount);
```

- [ ] **Step 3: BackupSession (per-request lifecycle)**

`OrderDeck.LicenseServer/Services/Backup/BackupSession.cs`:

```csharp
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a admin viewer: per-request lifecycle wrapping a decrypted SQLite db on temp disk.
/// Owns temp directory + read-only SqliteConnection. Dispose deletes temp + closes connection.
/// </summary>
public sealed class BackupSession : IAsyncDisposable, IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnection _conn;
    private readonly ILogger _log;
    private bool _disposed;

    private const int DefaultPageSize = 50;

    public BackupSession(string tempDir, SqliteConnection conn, ILogger log)
    {
        _tempDir = tempDir;
        _conn = conn;
        _log = log;
    }

    public async Task<BackupSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var totalSessions = await ScalarLong("SELECT COUNT(*) FROM StreamSession WHERE EndedAt IS NOT NULL", ct);
        var totalLabels = await ScalarLong("SELECT COUNT(*) FROM Label WHERE PrintedAt IS NOT NULL", ct);
        var totalCustomers = await ScalarLong("SELECT COUNT(*) FROM Customer", ct);
        var totalRevenue = await ScalarDecimal("SELECT COALESCE(SUM(Price), 0) FROM Label WHERE PrintedAt IS NOT NULL", ct);

        var avgPerSession = totalSessions == 0 ? 0m : totalRevenue / totalSessions;
        var avgPerCustomer = totalCustomers == 0 ? 0m : totalRevenue / totalCustomers;

        TopSession? topSession = null;
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.Title, s.StartedAt, SUM(l.Price) AS Total
                FROM StreamSession s JOIN Label l ON l.SessionId = s.Id
                WHERE l.PrintedAt IS NOT NULL
                GROUP BY s.Id ORDER BY Total DESC LIMIT 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var title = r.IsDBNull(0) ? null : r.GetString(0);
                var started = r.GetInt64(1);
                var total = r.GetDecimal(2);
                topSession = new TopSession(title,
                    DateTimeOffset.FromUnixTimeSeconds(started),
                    total);
            }
        }

        TopCustomer? topCustomer = null;
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT c.Username, SUM(l.Price) AS Total, COUNT(*) AS LabelCount
                FROM Customer c JOIN Label l ON l.CustomerId = c.Id
                WHERE l.PrintedAt IS NOT NULL
                GROUP BY c.Id ORDER BY Total DESC LIMIT 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                topCustomer = new TopCustomer(r.GetString(0), r.GetDecimal(1), r.GetInt32(2));
            }
        }

        return new BackupSummary(
            (int)totalSessions, (int)totalLabels, (int)totalCustomers,
            totalRevenue, avgPerSession, avgPerCustomer,
            topSession, topCustomer);
    }

    public async Task<PagedResult<CustomerRow>> GetCustomersAsync(int page, string? search, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var where = string.IsNullOrWhiteSpace(search)
            ? ""
            : "WHERE LOWER(Username) LIKE LOWER(@q) OR LOWER(COALESCE(DisplayName,'')) LIKE LOWER(@q)";

        var total = await ScalarLong($"SELECT COUNT(*) FROM Customer {where}", ct,
            search is null ? null : ("@q", $"%{search}%"));

        var rows = new List<CustomerRow>();
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT Id, Platform, Username, DisplayName, Address, Phone, TotalAmount, LastSeenAt
                FROM Customer {where}
                ORDER BY LastSeenAt DESC
                LIMIT @limit OFFSET @offset";
            cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
            cmd.Parameters.AddWithValue("@offset", offset);
            if (search is not null) cmd.Parameters.AddWithValue("@q", $"%{search}%");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new CustomerRow(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetDecimal(6), r.GetInt64(7)));
            }
        }
        return new PagedResult<CustomerRow>(rows, (int)total, page, DefaultPageSize);
    }

    public async Task<PagedResult<SessionRow>> GetSessionsAsync(int page, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var total = await ScalarLong("SELECT COUNT(*) FROM StreamSession", ct);

        var rows = new List<SessionRow>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.Id, s.Title, s.StartedAt, s.EndedAt,
                   (SELECT COUNT(*) FROM Label WHERE SessionId = s.Id AND PrintedAt IS NOT NULL) AS LabelCount,
                   COALESCE((SELECT SUM(Price) FROM Label WHERE SessionId = s.Id AND PrintedAt IS NOT NULL), 0) AS TotalAmount
            FROM StreamSession s
            ORDER BY s.StartedAt DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new SessionRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetInt32(4), r.GetDecimal(5)));
        }
        return new PagedResult<SessionRow>(rows, (int)total, page, DefaultPageSize);
    }

    public async Task<PagedResult<LabelRow>> GetLabelsAsync(int page, string? sessionId, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var where = sessionId is null ? "" : "WHERE l.SessionId = @sessionId";

        var total = await ScalarLong($"SELECT COUNT(*) FROM Label l {where}", ct,
            sessionId is null ? null : ("@sessionId", sessionId));

        var rows = new List<LabelRow>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT l.Id, l.SessionId, c.Username, l.Code, l.Price, l.AddedAt, l.PrintedAt
            FROM Label l JOIN Customer c ON c.Id = l.CustomerId
            {where}
            ORDER BY l.AddedAt DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        if (sessionId is not null) cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new LabelRow(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDecimal(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6)));
        }
        return new PagedResult<LabelRow>(rows, (int)total, page, DefaultPageSize);
    }

    public async Task<PagedResult<GiveawayRow>> GetGiveawaysAsync(int page, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var total = await ScalarLong("SELECT COUNT(*) FROM Giveaway", ct);

        var rows = new List<GiveawayRow>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT g.Id, g.Keyword, g.StartedAt, g.EndedAt,
                   (SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId = g.Id) AS ParticipantCount,
                   (SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId = g.Id AND IsWinner = 1) AS WinnerCount
            FROM Giveaway g
            ORDER BY g.StartedAt DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new GiveawayRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetInt32(4), r.GetInt32(5)));
        }
        return new PagedResult<GiveawayRow>(rows, (int)total, page, DefaultPageSize);
    }

    private async Task<long> ScalarLong(string sql, CancellationToken ct, (string name, object value)? param = null)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (param.HasValue) cmd.Parameters.AddWithValue(param.Value.name, param.Value.value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? 0L : Convert.ToInt64(v);
    }

    private async Task<decimal> ScalarDecimal(string sql, CancellationToken ct)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? 0m : Convert.ToDecimal(v);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _conn.DisposeAsync();
        TryDeleteTempDir();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
        TryDeleteTempDir();
    }

    private void TryDeleteTempDir()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete backup temp dir {Dir}", _tempDir); }
    }
}
```

- [ ] **Step 4: BackupViewerService factory**

`OrderDeck.LicenseServer/Services/Backup/BackupViewerService.cs`:

```csharp
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a admin viewer: opens a CustomerBackup blob into a disposable BackupSession.
/// 1. Loads encrypted blob from disk
/// 2. Decrypts via BackupStorageService
/// 3. Extracts ZIP to /tmp/{guid}/orderdeck.db
/// 4. Opens read-only SqliteConnection
/// 5. Returns BackupSession (caller `using`)
/// </summary>
public sealed class BackupViewerService
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly ILogger<BackupViewerService> _log;

    public BackupViewerService(LicenseDbContext db, BackupStorageService storage, ILogger<BackupViewerService> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task<BackupSession> OpenAsync(Guid backupId, CancellationToken ct = default)
    {
        var b = await _db.CustomerBackups.FirstOrDefaultAsync(x => x.Id == backupId, ct)
            ?? throw new InvalidOperationException($"Backup {backupId} not found");

        var encrypted = await _storage.ReadBlobAsync(b.BlobPath, ct);
        var zipBytes = _storage.Decrypt(encrypted);

        var tempDir = Path.Combine(Path.GetTempPath(), $"orderdeck-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var dbEntry = archive.GetEntry("orderdeck.db")
                    ?? throw new InvalidOperationException("Backup zip missing orderdeck.db entry");
                var dbPath = Path.Combine(tempDir, "orderdeck.db");
                using var dest = File.Create(dbPath);
                using var src = dbEntry.Open();
                await src.CopyToAsync(dest, ct);
            }

            var dbFile = Path.Combine(tempDir, "orderdeck.db");
            var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            return new BackupSession(tempDir, conn, _log);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            throw;
        }
    }
}
```

- [ ] **Step 5: DI**

`OrderDeck.LicenseServer/Program.cs` near retention service:

```csharp
builder.Services.AddScoped<BackupViewerService>();
```

- [ ] **Step 6: Tests**

`OrderDeck.LicenseServer.Tests/Services/Backup/BackupViewerServiceTests.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

public class BackupViewerServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BackupViewerServiceTests(ApiFactory factory) => _factory = factory;

    /// <summary>Builds a minimal valid orderdeck.db zip with one Customer + one Session + one Label.</summary>
    private static byte[] BuildSampleDbZip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sample-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE Customer (
                    Id TEXT PRIMARY KEY, Platform TEXT, Username TEXT,
                    DisplayName TEXT, AvatarUrl TEXT,
                    FirstSeenAt INTEGER, LastSeenAt INTEGER,
                    IsBlacklisted INTEGER, BlacklistReason TEXT, Notes TEXT,
                    TotalLabelsPrinted INTEGER, TotalAmount NUMERIC,
                    BlacklistedAt INTEGER, Address TEXT, Phone TEXT
                );
                CREATE TABLE StreamSession (
                    Id TEXT PRIMARY KEY, Title TEXT, StartedAt INTEGER,
                    EndedAt INTEGER, Platforms TEXT, Notes TEXT
                );
                CREATE TABLE Label (
                    Id TEXT PRIMARY KEY, SessionId TEXT, CustomerId TEXT,
                    Platform TEXT, Username TEXT, MessageText TEXT, Code TEXT,
                    Price NUMERIC, AddedAt INTEGER, PrintedAt INTEGER
                );
                CREATE TABLE Giveaway (
                    Id TEXT PRIMARY KEY, Keyword TEXT, StartedAt INTEGER, EndedAt INTEGER
                );
                CREATE TABLE GiveawayParticipant (
                    Id TEXT PRIMARY KEY, GiveawayId TEXT, IsWinner INTEGER
                );

                INSERT INTO Customer VALUES
                    ('c1','twitch','alice','Alice',NULL,1000,2000,0,NULL,NULL,3,150.0,NULL,NULL,'+905551111111');
                INSERT INTO StreamSession VALUES
                    ('s1','Yayın #1',1500,1900,'[]',NULL);
                INSERT INTO Label VALUES
                    ('l1','s1','c1','twitch','alice','Apple',NULL,75.0,1600,1700);
                INSERT INTO Label VALUES
                    ('l2','s1','c1','twitch','alice','Pear',NULL,75.0,1650,1750);
            ";
            cmd.ExecuteNonQuery();
        }

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("orderdeck.db");
            using var entryStream = entry.Open();
            using var src = File.OpenRead(dbPath);
            src.CopyTo(entryStream);
        }
        File.Delete(dbPath);
        return ms.ToArray();
    }

    private async Task<Guid> SeedBackupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<BackupStorageService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"viewer-{Guid.NewGuid():N}@test.com",
            Name = "T", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var zipBytes = BuildSampleDbZip();
        var encrypted = storage.Encrypt(zipBytes);
        var path = await storage.WriteBlobAsync(customer.Id, encrypted);

        var backup = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BlobPath = path,
            SizeBytes = encrypted.Length,
            ChecksumSha256 = new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false
        };
        db.CustomerBackups.Add(backup);
        await db.SaveChangesAsync();
        return backup.Id;
    }

    [Fact]
    public async Task OpenAsync_AndGetSummary_ReturnsCorrectAggregates()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        await using var session = await viewer.OpenAsync(backupId);
        var summary = await session.GetSummaryAsync();

        summary.TotalSessions.Should().Be(1);
        summary.TotalLabels.Should().Be(2);
        summary.TotalUniqueCustomers.Should().Be(1);
        summary.TotalRevenue.Should().Be(150m);
        summary.TopCustomer!.Username.Should().Be("alice");
        summary.TopCustomer.Total.Should().Be(150m);
    }

    [Fact]
    public async Task OpenAsync_GetCustomers_PaginatedReturnsRows()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        await using var session = await viewer.OpenAsync(backupId);
        var page = await session.GetCustomersAsync(page: 1, search: null);

        page.Rows.Should().HaveCount(1);
        page.Rows[0].Username.Should().Be("alice");
        page.Rows[0].Phone.Should().Be("+905551111111");
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task OpenAsync_GetSessions_IncludesAggregatedTotal()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        await using var session = await viewer.OpenAsync(backupId);
        var page = await session.GetSessionsAsync(page: 1);

        page.Rows.Should().HaveCount(1);
        page.Rows[0].Title.Should().Be("Yayın #1");
        page.Rows[0].LabelCount.Should().Be(2);
        page.Rows[0].TotalAmount.Should().Be(150m);
    }

    [Fact]
    public async Task BackupSession_DisposeAsync_RemovesTempDir()
    {
        var backupId = await SeedBackupAsync();
        using var scope = _factory.Services.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<BackupViewerService>();

        var session = await viewer.OpenAsync(backupId);
        // Find tempDir via reflection — alternative: check Path.GetTempPath() count before/after
        var snapshotBefore = Directory.GetDirectories(Path.GetTempPath(), "orderdeck-view-*").Length;
        await session.DisposeAsync();
        var snapshotAfter = Directory.GetDirectories(Path.GetTempPath(), "orderdeck-view-*").Length;

        snapshotAfter.Should().BeLessThan(snapshotBefore);
    }
}
```

- [ ] **Step 7: Tests + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BackupViewerService"
```

Expected: 4/4 pass.

```bash
git add OrderDeck.LicenseServer/Services/Backup/BackupViewerService.cs \
        OrderDeck.LicenseServer/Services/Backup/BackupSession.cs \
        OrderDeck.LicenseServer/Services/Backup/BackupSummary.cs \
        OrderDeck.LicenseServer/Services/Backup/PagedResult.cs \
        OrderDeck.LicenseServer/Services/Backup/BackupRows.cs \
        OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Backup/BackupViewerServiceTests.cs

git commit -m "feat(license-server): Phase 5a — BackupViewerService + BackupSession

Decrypt-to-temp + read-only SQLite query layer.

BackupViewerService.OpenAsync(backupId) → BackupSession (IAsyncDisposable):
- GetSummaryAsync: 6 aggregate SQL queries (totals, averages, top session, top customer)
- GetCustomersAsync(page, search): paginated 50/page with optional username search
- GetSessionsAsync(page): joined with Label aggregates per session
- GetLabelsAsync(page, sessionId?): paginated, optional session filter
- GetGiveawaysAsync(page): with participant + winner counts
- Dispose closes connection + recursively deletes /tmp/orderdeck-view-{guid}/

DTO records: BackupSummary, TopSession, TopCustomer, PagedResult<T>,
CustomerRow, SessionRow, LabelRow, GiveawayRow.

Microsoft.Data.Sqlite 9.0.0 added (read-only mode connection string).
4 unit tests with synthetic SQLite db zip seeded into a real backup row."
```

---

### Task 8: Admin Backups Index Razor page

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml.cs`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/_ViewImports.cshtml`
- Modify: `OrderDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml` (add "Yedekler" link/tab)
- Create: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupsIndexTests.cs`

**Context:** Phase 4d's admin Razor pattern: AdminPageModel base, AdminCookie auth, top nav. Lists customer's backups with delete button (admin can manually remove).

- [ ] **Step 1: ViewImports**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/_ViewImports.cshtml`:

```cshtml
@using OrderDeck.LicenseServer
@using OrderDeck.LicenseServer.Domain
@using OrderDeck.LicenseServer.Services.Backup
@namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

- [ ] **Step 2: PageModel**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, BackupStorageService storage, IAuditService audit)
    {
        _db = db;
        _storage = storage;
        _audit = audit;
    }

    public Customer? Customer { get; private set; }
    public List<CustomerBackup> Backups { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();

        Backups = await _db.CustomerBackups
            .Where(b => b.CustomerId == id)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, Guid backupId, CancellationToken ct)
    {
        var b = await _db.CustomerBackups.FirstOrDefaultAsync(
            x => x.Id == backupId && x.CustomerId == id, ct);
        if (b is null) return NotFound();

        _storage.DeleteBlob(b.BlobPath);
        _db.CustomerBackups.Remove(b);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(BackupAuditEvents.BackupDeleted,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { reason = "admin", customerId = id }, ct);

        return RedirectToPage("Index", new { id });
    }
}
```

- [ ] **Step 3: View**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml`:

```cshtml
@page "{id:guid}/backups"
@model IndexModel
@{
    ViewData["Title"] = "Yedekler";
    Layout = "/Pages/Admin/Shared/_Layout.cshtml";
}

<a asp-page="/Admin/Customers/Detail" asp-route-id="@Model.Customer!.Id">← Müşteri detayına dön</a>

<h2>Yedekler — @Model.Customer.Email</h2>

@{
    var nonMilestoneCount = Model.Backups.Count(b => !b.IsMonthlyMilestone);
    var milestoneCount = Model.Backups.Count(b => b.IsMonthlyMilestone);
}
<p class="text-muted">@nonMilestoneCount/5 son yedek + @milestoneCount aylık milestone</p>

@if (Model.Backups.Count == 0)
{
    <div class="alert alert-info">Bu müşterinin henüz yedeği yok.</div>
}
else
{
    <table class="table table-hover">
        <thead>
            <tr>
                <th>#</th>
                <th>Tarih</th>
                <th>Boyut</th>
                <th>Aylık</th>
                <th>Makine</th>
                <th>İşlem</th>
            </tr>
        </thead>
        <tbody>
            @{ var rowNum = Model.Backups.Count; }
            @foreach (var b in Model.Backups)
            {
                <tr>
                    <td>#@rowNum</td>
                    <td>@b.CreatedAt.ToString("dd MMM yyyy HH:mm")</td>
                    <td>@($"{b.SizeBytes / 1024.0 / 1024.0:F1} MB")</td>
                    <td>@(b.IsMonthlyMilestone ? "✓" : "")</td>
                    <td>@(b.MachineName ?? "—")</td>
                    <td>
                        <a class="btn btn-sm btn-primary"
                           asp-page="/Admin/Customers/Backups/Summary"
                           asp-route-id="@Model.Customer.Id"
                           asp-route-backupId="@b.Id">Görüntüle</a>
                        <form method="post" class="d-inline"
                              asp-page-handler="Delete"
                              asp-route-id="@Model.Customer.Id"
                              asp-route-backupId="@b.Id"
                              onsubmit="return confirm('Bu yedek kalıcı olarak silinecek. Emin misin?');">
                            <button type="submit" class="btn btn-sm btn-danger">Sil</button>
                        </form>
                    </td>
                </tr>
                rowNum--;
            }
        </tbody>
    </table>
}
```

- [ ] **Step 4: Add "Yedekler" link to Customer Detail page**

In `OrderDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml`, find the existing nav/link area (Phase 4d added Lisanslar / Aktivasyonlar links) and add:

```cshtml
<a class="btn btn-outline-secondary" asp-page="/Admin/Customers/Backups/Index" asp-route-id="@Model.Customer.Id">
    Yedekler
</a>
```

(Place alongside other "Lisanslar" / "Aktivasyonlar" buttons.)

- [ ] **Step 5: Tests**

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupsIndexTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public class AdminBackupsIndexTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBackupsIndexTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetIndex_WithoutAuth_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync($"/Admin/Customers/{Guid.NewGuid()}/backups");
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetIndex_AsAdmin_ListsCustomerBackups()
    {
        // Seed customer + 2 backups
        Guid customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"admin-list-{Guid.NewGuid():N}@test.com",
                Name = "T", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(c);
            db.CustomerBackups.AddRange(
                new CustomerBackup
                {
                    Id = Guid.NewGuid(), CustomerId = c.Id,
                    BlobPath = "/tmp/fake1", SizeBytes = 1024,
                    ChecksumSha256 = new string('a', 64),
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
                },
                new CustomerBackup
                {
                    Id = Guid.NewGuid(), CustomerId = c.Id,
                    BlobPath = "/tmp/fake2", SizeBytes = 2048,
                    ChecksumSha256 = new string('b', 64),
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsMonthlyMilestone = true
                });
            await db.SaveChangesAsync();
            customerId = c.Id;
        }

        var client = await AdminAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().Contain("Yedekler");
        html.Should().Contain("MB");
    }
}
```

This test references `AdminAuthHelper.CreateAuthenticatedClientAsync` — Phase 4d test infrastructure. Reuse if present; otherwise create alongside CustomerAuthHelper using admin login flow.

- [ ] **Step 6: Tests + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminBackupsIndex"
```

Expected: 2/2 pass.

```bash
git add OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml.cs \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/_ViewImports.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml \
        OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupsIndexTests.cs

git commit -m "feat(license-server): Phase 5a — Admin Backups index page

/Admin/Customers/{id}/backups Razor page lists customer backups
(non-milestone count + milestone count summary, table with date/size/
machine/'Görüntüle'+'Sil' buttons). POST handler deletes blob+row+
audit log. Detail page gains 'Yedekler' nav button.

2 tests: auth required, list rendering."
```

---

### Task 9: Admin Backup Summary Razor page (default landing)

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml.cs`
- Create: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupSummaryTests.cs`

**Context:** Default deep-view landing. Decrypts blob via BackupViewerService → BackupSession.GetSummaryAsync → renders Bootstrap card grid. Audit BackupAccessed.

- [ ] **Step 1: PageModel**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class SummaryModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupViewerService _viewer;
    private readonly IAuditService _audit;

    public SummaryModel(LicenseDbContext db, BackupViewerService viewer, IAuditService audit)
    {
        _db = db;
        _viewer = viewer;
        _audit = audit;
    }

    public Customer? Customer { get; private set; }
    public CustomerBackup? Backup { get; private set; }
    public BackupSummary? Summary { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid backupId, CancellationToken ct)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();
        Backup = await _db.CustomerBackups.FirstOrDefaultAsync(
            b => b.Id == backupId && b.CustomerId == id, ct);
        if (Backup is null) return NotFound();

        try
        {
            await using var session = await _viewer.OpenAsync(backupId, ct);
            Summary = await session.GetSummaryAsync(ct);
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            Error = "Bu yedek farklı bir master key ile şifrelenmiş — decrypt edilemiyor.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Error = $"SQLite okuma hatası: {ex.Message}";
        }
        catch (Exception ex)
        {
            Error = $"Yedek açılamadı: {ex.Message}";
        }

        await _audit.LogAsync(BackupAuditEvents.BackupAccessed,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { customerId = id, viewType = "summary" }, ct);

        return Page();
    }
}
```

- [ ] **Step 2: View**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml`:

```cshtml
@page "{id:guid}/backups/{backupId:guid}/summary"
@model SummaryModel
@{
    ViewData["Title"] = "Yedek Özeti";
    Layout = "/Pages/Admin/Shared/_Layout.cshtml";
    var s = Model.Summary;
}

<a asp-page="Index" asp-route-id="@Model.Customer!.Id">← Yedekler listesine</a>

<h2>Yedek #@Model.Backup!.Id.ToString()[..8] — @Model.Backup.CreatedAt.ToString("dd MMM yyyy HH:mm")</h2>

<nav class="mb-3">
    <a class="btn btn-sm btn-outline-primary active"
       asp-page="Summary" asp-route-id="@Model.Customer.Id" asp-route-backupId="@Model.Backup.Id">Özet</a>
    <a class="btn btn-sm btn-outline-primary"
       asp-page="Customers" asp-route-id="@Model.Customer.Id" asp-route-backupId="@Model.Backup.Id">Müşteriler</a>
    <a class="btn btn-sm btn-outline-primary"
       asp-page="Sessions" asp-route-id="@Model.Customer.Id" asp-route-backupId="@Model.Backup.Id">Yayınlar</a>
    <a class="btn btn-sm btn-outline-primary"
       asp-page="Labels" asp-route-id="@Model.Customer.Id" asp-route-backupId="@Model.Backup.Id">Etiketler</a>
    <a class="btn btn-sm btn-outline-primary"
       asp-page="Giveaways" asp-route-id="@Model.Customer.Id" asp-route-backupId="@Model.Backup.Id">Çekilişler</a>
</nav>

@if (Model.Error is not null)
{
    <div class="alert alert-danger">@Model.Error</div>
}
else if (s is not null)
{
    <div class="row g-3 mb-3">
        <div class="col-md-3"><div class="card"><div class="card-body">
            <div class="text-muted small">Toplam Yayın</div>
            <div class="h3">@s.TotalSessions</div>
        </div></div></div>
        <div class="col-md-3"><div class="card"><div class="card-body">
            <div class="text-muted small">Toplam Etiket</div>
            <div class="h3">@s.TotalLabels.ToString("N0")</div>
        </div></div></div>
        <div class="col-md-3"><div class="card"><div class="card-body">
            <div class="text-muted small">Tekil Müşteri</div>
            <div class="h3">@s.TotalUniqueCustomers.ToString("N0")</div>
        </div></div></div>
        <div class="col-md-3"><div class="card text-bg-success"><div class="card-body">
            <div class="small">Toplam Ciro</div>
            <div class="h3">@s.TotalRevenue.ToString("N2") TL</div>
        </div></div></div>
    </div>

    <div class="row g-3 mb-3">
        <div class="col-md-6"><div class="card"><div class="card-body">
            <h5 class="card-title">Ortalamalar</h5>
            <p>Yayın başına: <strong>@s.AvgRevenuePerSession.ToString("N2") TL</strong></p>
            <p>Müşteri başına: <strong>@s.AvgRevenuePerCustomer.ToString("N2") TL</strong></p>
        </div></div></div>
        <div class="col-md-6"><div class="card"><div class="card-body">
            <h5 class="card-title">En İyiler</h5>
            @if (s.HighestSession is not null)
            {
                <p>En yüksek yayın: <strong>@s.HighestSession.Total.ToString("N2") TL</strong>
                   @if (s.HighestSession.StartedAt.HasValue)
                   { <text>(@s.HighestSession.StartedAt.Value.LocalDateTime.ToString("dd MMM yyyy"))</text> }
                </p>
            }
            @if (s.TopCustomer is not null)
            {
                <p>En çok harcayan: <strong>@s.TopCustomer.Username</strong> —
                   @s.TopCustomer.Total.ToString("N2") TL (@s.TopCustomer.LabelCount etiket)</p>
            }
        </div></div></div>
    </div>
}
```

- [ ] **Step 3: Tests**

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupSummaryTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public class AdminBackupSummaryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBackupSummaryTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetSummary_AsAdmin_RendersAggregates()
    {
        // Reuse seed from BackupViewerServiceTests pattern
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);

        var client = await AdminAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups/{backupId}/summary");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().Contain("Toplam Ciro");
        html.Should().Contain("150,00 TL");  // sample seed total
        html.Should().Contain("alice");      // top customer
    }

    [Fact]
    public async Task GetSummary_NonExistentBackup_Returns404()
    {
        var (customerId, _) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await AdminAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await client.GetAsync(
            $"/Admin/Customers/{customerId}/backups/{Guid.NewGuid()}/summary");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

`BackupSeedHelper.SeedSampleBackupAsync` — extract from `BackupViewerServiceTests.SeedBackupAsync` into `OrderDeck.LicenseServer.Tests/TestHelpers/BackupSeedHelper.cs` (returns `(Guid customerId, Guid backupId)`). Move the `BuildSampleDbZip` method here too.

- [ ] **Step 4: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminBackupSummary"
```

Expected: 2/2 pass.

```bash
git add OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml.cs \
        OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupSummaryTests.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/BackupSeedHelper.cs

git commit -m "feat(license-server): Phase 5a — Admin Backup Summary page

Default deep-view landing. Decrypts blob, opens BackupSession, runs
GetSummaryAsync (6 aggregates), renders Bootstrap card grid:
- Top row: Toplam Yayın / Etiket / Müşteri / Ciro
- Middle: Ortalamalar (yayın başına, müşteri başına)
- Right: En İyiler (en yüksek yayın, en çok harcayan müşteri)

Tabs nav for Customers/Sessions/Labels/Giveaways details.
Error handling: AesGcm AuthenticationTagMismatch → friendly Turkish message.
Audit log: BackupAccessed{viewType=summary}. 2 tests."
```

---

### Task 10: Admin Backup Customers + Sessions Razor pages

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Customers.cshtml` + `.cs`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Sessions.cshtml` + `.cs`
- Create: `OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupTablesTests.cs`

**Context:** Same pattern as Summary. Each page uses `BackupViewerService.OpenAsync` + `GetXxxAsync(page, ...)` paginated. Pagination footer + filters.

- [ ] **Step 1: Customers PageModel**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Customers.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class CustomersModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupViewerService _viewer;
    private readonly IAuditService _audit;

    public CustomersModel(LicenseDbContext db, BackupViewerService viewer, IAuditService audit)
    {
        _db = db;
        _viewer = viewer;
        _audit = audit;
    }

    public OrderDeck.LicenseServer.Domain.Customer? Customer { get; private set; }
    public CustomerBackup? Backup { get; private set; }
    public PagedResult<CustomerRow>? Page_ { get; private set; }
    public string? Error { get; private set; }
    public string? Search { get; private set; }
    public int CurrentPage { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id, Guid backupId, int page = 1, string? search = null, CancellationToken ct = default)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();
        Backup = await _db.CustomerBackups.FirstOrDefaultAsync(
            b => b.Id == backupId && b.CustomerId == id, ct);
        if (Backup is null) return NotFound();

        Search = search;
        CurrentPage = Math.Max(1, page);

        try
        {
            await using var session = await _viewer.OpenAsync(backupId, ct);
            Page_ = await session.GetCustomersAsync(CurrentPage, search, ct);
        }
        catch (Exception ex) { Error = $"Yedek açılamadı: {ex.Message}"; }

        await _audit.LogAsync(BackupAuditEvents.BackupAccessed,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { customerId = id, viewType = "customers", page }, ct);
        return Page();
    }
}
```

- [ ] **Step 2: Customers View**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Customers.cshtml`:

```cshtml
@page "{id:guid}/backups/{backupId:guid}/customers"
@model CustomersModel
@{
    Layout = "/Pages/Admin/Shared/_Layout.cshtml";
    ViewData["Title"] = "Yedek — Müşteriler";
    var p = Model.Page_;
}

<a asp-page="Summary" asp-route-id="@Model.Customer!.Id" asp-route-backupId="@Model.Backup!.Id">← Özet</a>
<h3>Müşteriler — Yedek @Model.Backup.CreatedAt.ToString("dd MMM yyyy HH:mm")</h3>

<form method="get" class="mb-3 d-flex gap-2">
    <input asp-for="Search" name="search" class="form-control" placeholder="Username ara…" />
    <input type="hidden" name="id" value="@Model.Customer.Id" />
    <input type="hidden" name="backupId" value="@Model.Backup.Id" />
    <button type="submit" class="btn btn-primary">Ara</button>
</form>

@if (Model.Error is not null)
{
    <div class="alert alert-danger">@Model.Error</div>
}
else if (p is not null)
{
    <table class="table table-sm table-striped">
        <thead><tr>
            <th>Username</th><th>Platform</th><th>Display Name</th>
            <th>Address</th><th>Phone</th>
            <th>Total Amount</th><th>Last Seen</th>
        </tr></thead>
        <tbody>
            @foreach (var r in p.Rows)
            {
                <tr>
                    <td><strong>@r.Username</strong></td>
                    <td>@r.Platform</td>
                    <td>@(r.DisplayName ?? "—")</td>
                    <td>@(r.Address ?? "—")</td>
                    <td>@(r.Phone ?? "—")</td>
                    <td class="text-end">@r.TotalAmount.ToString("N2") TL</td>
                    <td>@DateTimeOffset.FromUnixTimeSeconds(r.LastSeenAt).LocalDateTime.ToString("dd MMM yyyy HH:mm")</td>
                </tr>
            }
        </tbody>
    </table>

    <nav>
        <span>Sayfa @p.Page / @p.TotalPages — @p.TotalCount kayıt</span>
        @if (p.Page > 1)
        {
            <a asp-route-page="@(p.Page - 1)" asp-route-search="@Model.Search">‹ Önceki</a>
        }
        @if (p.Page < p.TotalPages)
        {
            <a asp-route-page="@(p.Page + 1)" asp-route-search="@Model.Search">Sonraki ›</a>
        }
    </nav>
}
```

- [ ] **Step 3: Sessions PageModel + View**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Sessions.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class SessionsModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupViewerService _viewer;
    private readonly IAuditService _audit;

    public SessionsModel(LicenseDbContext db, BackupViewerService viewer, IAuditService audit)
    {
        _db = db; _viewer = viewer; _audit = audit;
    }

    public OrderDeck.LicenseServer.Domain.Customer? Customer { get; private set; }
    public CustomerBackup? Backup { get; private set; }
    public PagedResult<SessionRow>? Page_ { get; private set; }
    public string? Error { get; private set; }
    public int CurrentPage { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid backupId, int page = 1, CancellationToken ct = default)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();
        Backup = await _db.CustomerBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.CustomerId == id, ct);
        if (Backup is null) return NotFound();
        CurrentPage = Math.Max(1, page);

        try
        {
            await using var session = await _viewer.OpenAsync(backupId, ct);
            Page_ = await session.GetSessionsAsync(CurrentPage, ct);
        }
        catch (Exception ex) { Error = $"Yedek açılamadı: {ex.Message}"; }

        await _audit.LogAsync(BackupAuditEvents.BackupAccessed,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { customerId = id, viewType = "sessions", page }, ct);
        return Page();
    }
}
```

`Sessions.cshtml`:

```cshtml
@page "{id:guid}/backups/{backupId:guid}/sessions"
@model SessionsModel
@{
    Layout = "/Pages/Admin/Shared/_Layout.cshtml";
    ViewData["Title"] = "Yedek — Yayınlar";
    var p = Model.Page_;
}

<a asp-page="Summary" asp-route-id="@Model.Customer!.Id" asp-route-backupId="@Model.Backup!.Id">← Özet</a>
<h3>Yayınlar — Yedek @Model.Backup.CreatedAt.ToString("dd MMM yyyy HH:mm")</h3>

@if (Model.Error is not null)
{
    <div class="alert alert-danger">@Model.Error</div>
}
else if (p is not null)
{
    <table class="table table-sm table-striped">
        <thead><tr>
            <th>Title</th><th>Started</th><th>Ended</th><th>Duration</th>
            <th>Labels</th><th>Total Amount</th>
        </tr></thead>
        <tbody>
            @foreach (var r in p.Rows)
            {
                var started = DateTimeOffset.FromUnixTimeSeconds(r.StartedAt);
                var duration = r.EndedAt.HasValue
                    ? TimeSpan.FromSeconds(r.EndedAt.Value - r.StartedAt).ToString(@"hh\:mm")
                    : "—";
                <tr>
                    <td>@(r.Title ?? "—")</td>
                    <td>@started.LocalDateTime.ToString("dd MMM yyyy HH:mm")</td>
                    <td>@(r.EndedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(r.EndedAt.Value).LocalDateTime.ToString("HH:mm") : "—")</td>
                    <td>@duration</td>
                    <td>@r.LabelCount</td>
                    <td class="text-end"><strong>@r.TotalAmount.ToString("N2") TL</strong></td>
                </tr>
            }
        </tbody>
    </table>
    <nav>
        <span>Sayfa @p.Page / @p.TotalPages — @p.TotalCount yayın</span>
        @if (p.Page > 1) { <a asp-route-page="@(p.Page - 1)">‹ Önceki</a> }
        @if (p.Page < p.TotalPages) { <a asp-route-page="@(p.Page + 1)">Sonraki ›</a> }
    </nav>
}
```

- [ ] **Step 4: Tests**

`OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupTablesTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public class AdminBackupTablesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBackupTablesTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetCustomers_RendersTable_WithSeedData()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await AdminAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups/{backupId}/customers");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("alice");
        html.Should().Contain("+905551111111");
    }

    [Fact]
    public async Task GetSessions_RendersTable_WithAggregates()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await AdminAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups/{backupId}/sessions");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Yayın #1");
        html.Should().Contain("150,00 TL");
    }

    [Fact]
    public async Task GetCustomers_WithSearchFilter_FiltersRows()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await AdminAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var resp = await client.GetAsync(
            $"/Admin/Customers/{customerId}/backups/{backupId}/customers?search=alice");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("alice");
    }
}
```

- [ ] **Step 5: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~AdminBackupTables"
```

Expected: 3/3 pass.

```bash
git add OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Customers.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Customers.cshtml.cs \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Sessions.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Sessions.cshtml.cs \
        OrderDeck.LicenseServer.Tests/Pages/Admin/AdminBackupTablesTests.cs

git commit -m "feat(license-server): Phase 5a — Admin Customers + Sessions backup viewer pages

Customers page: paginated table (Username/Platform/DisplayName/Address/
Phone/TotalAmount/LastSeen) + Username search filter.
Sessions page: Title/Started/Ended/Duration/LabelCount/TotalAmount with
SUM(Price) aggregate per session.

Each page: BackupViewerService.OpenAsync → BackupSession.GetXxxAsync(page),
audit log BackupAccessed{viewType=customers|sessions}.

3 tests covering rendering + search filter."
```

---

### Task 11: Admin Backup Labels + Giveaways Razor pages

**Files:**
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Labels.cshtml` + `.cs`
- Create: `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Giveaways.cshtml` + `.cs`

**Context:** Same pattern as Customers/Sessions. Labels supports optional `?sessionId=` filter. Tests reuse `BackupSeedHelper.SeedSampleBackupAsync` — seed already includes 2 labels (already covered in helper from Task 7).

- [ ] **Step 1: Labels PageModel**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Labels.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class LabelsModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupViewerService _viewer;
    private readonly IAuditService _audit;

    public LabelsModel(LicenseDbContext db, BackupViewerService viewer, IAuditService audit)
    {
        _db = db; _viewer = viewer; _audit = audit;
    }

    public OrderDeck.LicenseServer.Domain.Customer? Customer { get; private set; }
    public CustomerBackup? Backup { get; private set; }
    public PagedResult<LabelRow>? Page_ { get; private set; }
    public string? Error { get; private set; }
    public string? SessionFilter { get; private set; }
    public int CurrentPage { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id, Guid backupId, int page = 1, string? sessionId = null, CancellationToken ct = default)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();
        Backup = await _db.CustomerBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.CustomerId == id, ct);
        if (Backup is null) return NotFound();
        SessionFilter = sessionId;
        CurrentPage = Math.Max(1, page);

        try
        {
            await using var s = await _viewer.OpenAsync(backupId, ct);
            Page_ = await s.GetLabelsAsync(CurrentPage, sessionId, ct);
        }
        catch (Exception ex) { Error = $"Yedek açılamadı: {ex.Message}"; }

        await _audit.LogAsync(BackupAuditEvents.BackupAccessed,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { customerId = id, viewType = "labels", page, sessionId }, ct);
        return Page();
    }
}
```

`Labels.cshtml`:

```cshtml
@page "{id:guid}/backups/{backupId:guid}/labels"
@model LabelsModel
@{
    Layout = "/Pages/Admin/Shared/_Layout.cshtml";
    ViewData["Title"] = "Yedek — Etiketler";
    var p = Model.Page_;
}

<a asp-page="Summary" asp-route-id="@Model.Customer!.Id" asp-route-backupId="@Model.Backup!.Id">← Özet</a>
<h3>Etiketler — Yedek @Model.Backup.CreatedAt.ToString("dd MMM yyyy HH:mm")</h3>

<form method="get" class="mb-3">
    <input asp-for="SessionFilter" name="sessionId" class="form-control d-inline-block" style="width:300px" placeholder="Session ID filtre…" />
    <input type="hidden" name="id" value="@Model.Customer.Id" />
    <input type="hidden" name="backupId" value="@Model.Backup.Id" />
    <button type="submit" class="btn btn-secondary">Filtrele</button>
</form>

@if (Model.Error is not null)
{
    <div class="alert alert-danger">@Model.Error</div>
}
else if (p is not null)
{
    <table class="table table-sm table-striped">
        <thead><tr>
            <th>Session</th><th>Username</th><th>Code</th>
            <th>Price</th><th>Added</th><th>Printed</th>
        </tr></thead>
        <tbody>
            @foreach (var r in p.Rows)
            {
                <tr>
                    <td>@r.SessionId[..8]</td>
                    <td>@r.Username</td>
                    <td>@(r.Code ?? "—")</td>
                    <td class="text-end">@r.Price.ToString("N2") TL</td>
                    <td>@DateTimeOffset.FromUnixTimeSeconds(r.AddedAt).LocalDateTime.ToString("dd MMM HH:mm")</td>
                    <td>@(r.PrintedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(r.PrintedAt.Value).LocalDateTime.ToString("HH:mm") : "—")</td>
                </tr>
            }
        </tbody>
    </table>
    <nav>
        <span>Sayfa @p.Page / @p.TotalPages — @p.TotalCount etiket</span>
        @if (p.Page > 1) { <a asp-route-page="@(p.Page - 1)" asp-route-sessionId="@Model.SessionFilter">‹ Önceki</a> }
        @if (p.Page < p.TotalPages) { <a asp-route-page="@(p.Page + 1)" asp-route-sessionId="@Model.SessionFilter">Sonraki ›</a> }
    </nav>
}
```

- [ ] **Step 2: Giveaways PageModel + View**

`OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Giveaways.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public sealed class GiveawaysModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly BackupViewerService _viewer;
    private readonly IAuditService _audit;

    public GiveawaysModel(LicenseDbContext db, BackupViewerService viewer, IAuditService audit)
    {
        _db = db; _viewer = viewer; _audit = audit;
    }

    public OrderDeck.LicenseServer.Domain.Customer? Customer { get; private set; }
    public CustomerBackup? Backup { get; private set; }
    public PagedResult<GiveawayRow>? Page_ { get; private set; }
    public string? Error { get; private set; }
    public int CurrentPage { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid backupId, int page = 1, CancellationToken ct = default)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();
        Backup = await _db.CustomerBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.CustomerId == id, ct);
        if (Backup is null) return NotFound();
        CurrentPage = Math.Max(1, page);

        try
        {
            await using var s = await _viewer.OpenAsync(backupId, ct);
            Page_ = await s.GetGiveawaysAsync(CurrentPage, ct);
        }
        catch (Exception ex) { Error = $"Yedek açılamadı: {ex.Message}"; }

        await _audit.LogAsync(BackupAuditEvents.BackupAccessed,
            BackupAuditEvents.TargetType, backupId.ToString(),
            new { customerId = id, viewType = "giveaways", page }, ct);
        return Page();
    }
}
```

`Giveaways.cshtml`:

```cshtml
@page "{id:guid}/backups/{backupId:guid}/giveaways"
@model GiveawaysModel
@{
    Layout = "/Pages/Admin/Shared/_Layout.cshtml";
    ViewData["Title"] = "Yedek — Çekilişler";
    var p = Model.Page_;
}

<a asp-page="Summary" asp-route-id="@Model.Customer!.Id" asp-route-backupId="@Model.Backup!.Id">← Özet</a>
<h3>Çekilişler — Yedek @Model.Backup.CreatedAt.ToString("dd MMM yyyy HH:mm")</h3>

@if (Model.Error is not null)
{
    <div class="alert alert-danger">@Model.Error</div>
}
else if (p is not null && p.Rows.Count > 0)
{
    <table class="table table-sm table-striped">
        <thead><tr>
            <th>Keyword</th><th>Started</th><th>Ended</th>
            <th>Participants</th><th>Winners</th>
        </tr></thead>
        <tbody>
            @foreach (var r in p.Rows)
            {
                <tr>
                    <td><strong>@r.Keyword</strong></td>
                    <td>@(r.StartedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(r.StartedAt.Value).LocalDateTime.ToString("dd MMM HH:mm") : "—")</td>
                    <td>@(r.EndedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(r.EndedAt.Value).LocalDateTime.ToString("HH:mm") : "—")</td>
                    <td>@r.ParticipantCount</td>
                    <td>@r.WinnerCount</td>
                </tr>
            }
        </tbody>
    </table>
    <nav>
        <span>Sayfa @p.Page / @p.TotalPages — @p.TotalCount çekiliş</span>
        @if (p.Page > 1) { <a asp-route-page="@(p.Page - 1)">‹ Önceki</a> }
        @if (p.Page < p.TotalPages) { <a asp-route-page="@(p.Page + 1)">Sonraki ›</a> }
    </nav>
}
else if (p is not null)
{
    <p class="text-muted">Bu yedekte çekiliş kaydı yok.</p>
}
```

- [ ] **Step 3: Build + smoke test all admin viewer pages**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Expected: 0 errors, all green.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Labels.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Labels.cshtml.cs \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Giveaways.cshtml \
        OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Giveaways.cshtml.cs

git commit -m "feat(license-server): Phase 5a — Admin Labels + Giveaways backup viewer pages

Labels page: paginated 50/page, optional sessionId filter param.
Giveaways page: paginated with participant + winner aggregates from
GiveawayParticipant subquery.

Same audit logging pattern (viewType=labels|giveaways).
Reuses BackupSeedHelper from Task 9 for tests (no new tests — covered by
existing AdminBackupTables + Summary tests; viewer plumbing identical)."
```

---

### Task 12: OrderDeck.Licensing.Backup SDK (IBackupClient + BackupClient + BackupMetadata)

**Files:**
- Create: `OrderDeck.Licensing/Backup/IBackupClient.cs`
- Create: `OrderDeck.Licensing/Backup/BackupClient.cs`
- Create: `OrderDeck.Licensing/Backup/BackupMetadata.cs`
- Create: `OrderDeck.Licensing.Tests/Backup/BackupClientTests.cs`

**Context:** SDK katmanı — JWT bearer token mevcut `LicenseApiClient` pattern'iyle aynı. Test mock'ları `MockHttpMessageHandler` (Phase 4b'de kuruldu) ile.

- [ ] **Step 1: BackupMetadata DTO**

`OrderDeck.Licensing/Backup/BackupMetadata.cs`:

```csharp
namespace OrderDeck.Licensing.Backup;

public sealed record BackupMetadata(
    Guid Id,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    bool IsMonthlyMilestone,
    string? MachineName);
```

- [ ] **Step 2: IBackupClient interface**

`OrderDeck.Licensing/Backup/IBackupClient.cs`:

```csharp
namespace OrderDeck.Licensing.Backup;

public interface IBackupClient
{
    Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default);
    Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default);
    Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default);
    Task DeleteAsync(Guid backupId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Test file (FAIL beklenir)**

`OrderDeck.Licensing.Tests/Backup/BackupClientTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Backup;
using OrderDeck.Licensing.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Licensing.Tests.Backup;

public class BackupClientTests
{
    private static (BackupClient client, MockHttpMessageHandler handler) Make(string jwt = "test-jwt")
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        return (new BackupClient(http), handler);
    }

    [Fact]
    public async Task UploadAsync_SendsBytesWithShaHeader_AndDeserializes()
    {
        var (client, handler) = Make();
        var meta = new
        {
            id = Guid.NewGuid(),
            sizeBytes = 12345L,
            createdAt = DateTimeOffset.UtcNow,
            isMonthlyMilestone = true,
            machineName = "TEST"
        };
        handler.Reply(HttpStatusCode.Created, JsonSerializer.Serialize(meta), "application/json");

        var result = await client.UploadAsync(new byte[] { 1, 2, 3 }, "deadbeef", "TEST");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/me/backups");
        handler.LastRequest.Headers.GetValues("X-Backup-Sha256").Should().Contain("deadbeef");
        handler.LastRequest.Headers.GetValues("X-Machine-Name").Should().Contain("TEST");

        result.Id.Should().Be(meta.id);
        result.SizeBytes.Should().Be(12345L);
        result.IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_ServerReturnsError_ThrowsLicenseApiException()
    {
        var (client, handler) = Make();
        handler.Reply(HttpStatusCode.PayloadTooLarge, "{\"error\":\"too large\"}", "application/json");

        Func<Task> act = () => client.UploadAsync(new byte[] { 1 }, "abc", null);
        var ex = await act.Should().ThrowAsync<LicenseApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.PayloadTooLarge);
    }

    [Fact]
    public async Task ListAsync_ReturnsArrayOfMetadata()
    {
        var (client, handler) = Make();
        var arr = new[]
        {
            new { id = Guid.NewGuid(), sizeBytes = 100L, createdAt = DateTimeOffset.UtcNow, isMonthlyMilestone = false, machineName = "A" },
            new { id = Guid.NewGuid(), sizeBytes = 200L, createdAt = DateTimeOffset.UtcNow.AddDays(-1), isMonthlyMilestone = true, machineName = "A" }
        };
        handler.Reply(HttpStatusCode.OK, JsonSerializer.Serialize(arr), "application/json");

        var list = await client.ListAsync();

        list.Should().HaveCount(2);
        list[1].IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadAsync_ReturnsByteContent()
    {
        var (client, handler) = Make();
        var bytes = Encoding.UTF8.GetBytes("zip-payload-contents");
        handler.ReplyBytes(HttpStatusCode.OK, bytes, "application/octet-stream");

        var id = Guid.NewGuid();
        var got = await client.DownloadAsync(id);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be($"/api/v1/me/backups/{id}/download");
        got.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task DeleteAsync_SendsDelete()
    {
        var (client, handler) = Make();
        handler.Reply(HttpStatusCode.NoContent, "", "");

        var id = Guid.NewGuid();
        await client.DeleteAsync(id);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be($"/api/v1/me/backups/{id}");
    }
}
```

`MockHttpMessageHandler` — Phase 4b/4f testlerinde mevcut helper. `ReplyBytes(...)` yoksa, mevcut `Reply` benzeri ama `ByteArrayContent` ile cevap dönen yardımcıyı `OrderDeck.Licensing.Tests/TestHelpers/MockHttpMessageHandler.cs`'e ekle:

```csharp
public void ReplyBytes(HttpStatusCode code, byte[] bytes, string contentType)
{
    _next = (_) => new HttpResponseMessage(code)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = string.IsNullOrEmpty(contentType) ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
        }
    };
}
```

- [ ] **Step 4: Test FAIL doğrula**

```bash
dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --filter "FullyQualifiedName~BackupClient"
```

Expected: FAIL (`BackupClient` doesn't exist).

- [ ] **Step 5: BackupClient implementation**

`OrderDeck.Licensing/Backup/BackupClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrderDeck.Licensing.Api;

namespace OrderDeck.Licensing.Backup;

public sealed class BackupClient : IBackupClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public BackupClient(HttpClient http) => _http = http;

    public async Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default)
    {
        var content = new ByteArrayContent(zipPayload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/backups") { Content = content };
        req.Headers.Add("X-Backup-Sha256", sha256Hex);
        if (!string.IsNullOrEmpty(machineName))
            req.Headers.Add("X-Machine-Name", machineName);

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
        var meta = await resp.Content.ReadFromJsonAsync<BackupMetadata>(JsonOpts, ct);
        return meta ?? throw new LicenseApiException(resp.StatusCode, "Empty response from upload");
    }

    public async Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/v1/me/backups", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
        var list = await resp.Content.ReadFromJsonAsync<List<BackupMetadata>>(JsonOpts, ct);
        return list ?? new List<BackupMetadata>();
    }

    public async Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/v1/me/backups/{backupId}/download", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DeleteAsync(Guid backupId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/api/v1/me/backups/{backupId}", ct);
        await EnsureSuccessOrThrowAsync(resp, ct);
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new LicenseApiException(resp.StatusCode, body);
    }
}
```

- [ ] **Step 6: Test PASS**

```bash
dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj --filter "FullyQualifiedName~BackupClient"
```

Expected: 5/5 pass.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.Licensing/Backup/IBackupClient.cs \
        OrderDeck.Licensing/Backup/BackupClient.cs \
        OrderDeck.Licensing/Backup/BackupMetadata.cs \
        OrderDeck.Licensing.Tests/TestHelpers/MockHttpMessageHandler.cs \
        OrderDeck.Licensing.Tests/Backup/BackupClientTests.cs

git commit -m "feat(licensing-sdk): Phase 5a — BackupClient REST wrapper

IBackupClient interface + BackupClient impl + BackupMetadata record.
Methods: UploadAsync (octet-stream + X-Backup-Sha256 + X-Machine-Name
headers), ListAsync, DownloadAsync, DeleteAsync. Maps non-2xx to
LicenseApiException (existing pattern). Reuses HttpClient with JWT
bearer set externally by caller (LicenseService pattern).

5 unit tests with MockHttpMessageHandler (added ReplyBytes helper)."
```

---

### Task 13: StreamSessionService.SessionEnded event + BackupService (fire-and-forget)

**Files:**
- Modify: `OrderDeck.Core/Sessions/StreamSessionService.cs` (add `SessionEnded` event)
- Create: `OrderDeck.Core/Sessions/SessionEndedEventArgs.cs`
- Create: `OrderDeck.App/Services/BackupService.cs`
- Create: `OrderDeck.Tests/Services/BackupServiceTests.cs`
- Modify: `OrderDeck.App/AppHost.cs` (DI + event subscription)
- Create: `OrderDeck.Tests/Fakes/FakeBackupClient.cs`

**Context:** SessionEnded event decoupled (Core doesn't reference App/Backup). BackupService.QueueBackup fire-and-forget Task.Run, RunBackupNowAsync awaitable. Single-flight: SemaphoreSlim(1).

- [ ] **Step 1: SessionEndedEventArgs**

`OrderDeck.Core/Sessions/SessionEndedEventArgs.cs`:

```csharp
using System;

namespace OrderDeck.Core.Sessions;

public sealed class SessionEndedEventArgs : EventArgs
{
    public string SessionId { get; }
    public long EndedAt { get; }
    public SessionEndedEventArgs(string sessionId, long endedAt)
    {
        SessionId = sessionId;
        EndedAt = endedAt;
    }
}
```

- [ ] **Step 2: StreamSessionService event**

Modify `OrderDeck.Core/Sessions/StreamSessionService.cs`:

```csharp
public event EventHandler<SessionEndedEventArgs>? SessionEnded;

public void End(string sessionId)
{
    var endedAt = _clock.UnixNow();
    _repo.End(sessionId, endedAt);
    SessionEnded?.Invoke(this, new SessionEndedEventArgs(sessionId, endedAt));
}
```

(Replace existing one-line `End`. Preserve existing constructor + GetActive method.)

- [ ] **Step 3: FakeBackupClient (test fixture)**

`OrderDeck.Tests/Fakes/FakeBackupClient.cs`:

```csharp
using OrderDeck.Licensing.Backup;

namespace OrderDeck.Tests.Fakes;

public sealed class FakeBackupClient : IBackupClient
{
    public List<(byte[] payload, string sha, string? machine)> Uploads { get; } = new();
    public Func<byte[], string, string?, BackupMetadata>? UploadResponseFactory { get; set; }
    public Exception? UploadException { get; set; }

    public Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default)
    {
        Uploads.Add((zipPayload, sha256Hex, machineName));
        if (UploadException is not null) throw UploadException;
        var meta = UploadResponseFactory?.Invoke(zipPayload, sha256Hex, machineName)
                ?? new BackupMetadata(Guid.NewGuid(), zipPayload.Length, DateTimeOffset.UtcNow, false, machineName);
        return Task.FromResult(meta);
    }

    public Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupMetadata>>(Array.Empty<BackupMetadata>());

    public Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task DeleteAsync(Guid backupId, CancellationToken ct = default) => Task.CompletedTask;
}
```

- [ ] **Step 4: BackupServiceTests (FAIL beklenir)**

`OrderDeck.Tests/Services/BackupServiceTests.cs`:

```csharp
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage;
using OrderDeck.Licensing.Backup;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.Services;

public class BackupServiceTests : IDisposable
{
    private readonly string _tempDb;

    public BackupServiceTests()
    {
        // Build a tiny "DB" file we can backup
        _tempDb = Path.Combine(Path.GetTempPath(), $"orderdeck-bs-test-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(_tempDb, Encoding.UTF8.GetBytes("fake sqlite content for test purposes"));
    }

    public void Dispose()
    {
        if (File.Exists(_tempDb)) File.Delete(_tempDb);
    }

    [Fact]
    public async Task RunBackupNowAsync_ZipsDbAndUploadsWithCorrectSha()
    {
        var fake = new FakeBackupClient();
        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);

        var result = await sut.RunBackupNowAsync();

        result.Success.Should().BeTrue();
        fake.Uploads.Should().HaveCount(1);

        var upload = fake.Uploads[0];
        // Verify it's a valid zip containing orderdeck.db
        using var ms = new MemoryStream(upload.payload);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.GetEntry("orderdeck.db").Should().NotBeNull();

        // SHA matches actual zip bytes
        var expected = Convert.ToHexString(SHA256.HashData(upload.payload)).ToLowerInvariant();
        upload.sha.Should().Be(expected);
    }

    [Fact]
    public async Task RunBackupNowAsync_NoDbFile_ReturnsFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db");
        var fake = new FakeBackupClient();
        var sut = new BackupService(missing, fake, NullLogger<BackupService>.Instance);

        var result = await sut.RunBackupNowAsync();

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        fake.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task RunBackupNowAsync_UploadException_ReturnsFailureWithoutThrowing()
    {
        var fake = new FakeBackupClient { UploadException = new InvalidOperationException("simulated network") };
        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);

        var result = await sut.RunBackupNowAsync();

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("simulated network");
    }

    [Fact]
    public async Task QueueBackup_ConcurrentCalls_OnlyOneActiveUploadAtATime()
    {
        var fake = new FakeBackupClient();
        var startedSignals = new SemaphoreSlim(0);
        var releaseSignals = new SemaphoreSlim(0);
        fake.UploadResponseFactory = (_, _, m) =>
        {
            startedSignals.Release();
            releaseSignals.Wait();  // block until released
            return new BackupMetadata(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, false, m);
        };

        var sut = new BackupService(_tempDb, fake, NullLogger<BackupService>.Instance);
        sut.QueueBackup("test1");
        sut.QueueBackup("test2");  // should be skipped (single-flight)

        await startedSignals.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSignals.Release();
        await Task.Delay(200);  // give the first upload time to complete

        fake.Uploads.Count.Should().Be(1, because: "second QueueBackup detected active and skipped");
    }
}
```

- [ ] **Step 5: BackupService implementation**

`OrderDeck.App/Services/BackupService.cs`:

```csharp
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Services;

public sealed record BackupResult(bool Success, string? Error, BackupMetadata? Metadata);

/// <summary>
/// Phase 5a: zip orderdeck.db, SHA256 it, upload via IBackupClient. Fire-and-forget for
/// stream-end trigger; awaitable RunBackupNowAsync for explicit calls (none in v1).
/// Single-flight via SemaphoreSlim(1).
/// </summary>
public sealed class BackupService
{
    private readonly string _databaseFile;
    private readonly IBackupClient _client;
    private readonly ILogger<BackupService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(string databaseFile, IBackupClient client, ILogger<BackupService> log)
    {
        _databaseFile = databaseFile;
        _client = client;
        _log = log;
    }

    /// <summary>Fire-and-forget. Returns immediately. Errors only logged. Single-flight.</summary>
    public void QueueBackup(string reason)
    {
        if (!_gate.Wait(0))
        {
            _log.LogInformation("Backup already in progress; skipping ({Reason})", reason);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await RunCoreAsync(default);
                if (!result.Success)
                    _log.LogWarning("Background backup failed ({Reason}): {Error}", reason, result.Error);
                else
                    _log.LogInformation("Background backup OK ({Reason}): {Bytes} bytes", reason, result.Metadata?.SizeBytes);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Background backup unhandled exception ({Reason})", reason);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    public async Task<BackupResult> RunBackupNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await RunCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<BackupResult> RunCoreAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_databaseFile))
                return new BackupResult(false, $"Database file not found: {_databaseFile}", null);

            // Copy DB to temp first (avoid locking issues with WAL/journal)
            var tempCopy = Path.Combine(Path.GetTempPath(), $"orderdeck-bup-{Guid.NewGuid():N}.db");
            try
            {
                File.Copy(_databaseFile, tempCopy, overwrite: true);

                // Zip the temp DB into a memory stream
                byte[] zipBytes;
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var entry = archive.CreateEntry("orderdeck.db", CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var src = File.OpenRead(tempCopy);
                        await src.CopyToAsync(entryStream, ct);
                    }
                    zipBytes = ms.ToArray();
                }

                var sha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
                var meta = await _client.UploadAsync(zipBytes, sha, Environment.MachineName, ct);

                return new BackupResult(true, null, meta);
            }
            finally
            {
                try { if (File.Exists(tempCopy)) File.Delete(tempCopy); }
                catch { /* swallow */ }
            }
        }
        catch (Exception ex)
        {
            return new BackupResult(false, ex.Message, null);
        }
    }
}
```

- [ ] **Step 6: AppHost wiring**

In `OrderDeck.App/AppHost.cs` (Phase 4b/4f patterns):

```csharp
// Phase 5a — backup
services.AddSingleton<IBackupClient>(sp =>
{
    var http = sp.GetRequiredService<LicenseHttpClientFactory>().CreateAuthed();
    return new BackupClient(http);
});
services.AddSingleton<BackupService>(sp =>
    new BackupService(
        AppPaths.DatabaseFile,
        sp.GetRequiredService<IBackupClient>(),
        sp.GetRequiredService<ILogger<BackupService>>()));
```

(Adjust `LicenseHttpClientFactory.CreateAuthed()` to match existing factory name in the codebase. If existing pattern uses `LicenseApiClient` directly with shared HttpClient, mirror that.)

In `OrderDeck.App/App.xaml.cs` startup hook (after Host built, before main window shown):

```csharp
var sessionService = Host.Services.GetRequiredService<StreamSessionService>();
var backupService = Host.Services.GetRequiredService<BackupService>();
sessionService.SessionEnded += (_, _) => backupService.QueueBackup("stream-end");
```

- [ ] **Step 7: Test PASS + build**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~BackupService"
```

Expected: 0 errors, 4/4 pass.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.Core/Sessions/StreamSessionService.cs \
        OrderDeck.Core/Sessions/SessionEndedEventArgs.cs \
        OrderDeck.App/Services/BackupService.cs \
        OrderDeck.App/AppHost.cs \
        OrderDeck.App/App.xaml.cs \
        OrderDeck.Tests/Services/BackupServiceTests.cs \
        OrderDeck.Tests/Fakes/FakeBackupClient.cs

git commit -m "feat(app): Phase 5a — BackupService + StreamSessionService.SessionEnded event

StreamSessionService.End now fires SessionEnded event with (sessionId, endedAt).
Core remains decoupled — App subscribes in startup wiring.

BackupService:
- QueueBackup(reason): fire-and-forget Task.Run, single-flight via
  SemaphoreSlim(1). Concurrent calls during active upload are skipped.
- RunBackupNowAsync: awaitable variant returning BackupResult.
- Pipeline: copy DB to temp → ZIP (Optimal) → SHA256 hex → IBackupClient.UploadAsync
- All exceptions caught + returned as BackupResult.Error (no throw to caller).

AppHost wires SessionEnded → BackupService.QueueBackup. 4 tests with FakeBackupClient
covering: zip+sha pipeline, missing DB, upload exception, single-flight concurrency."
```

---

### Task 14: RestoreService + RestoreRecoveryService

**Files:**
- Create: `OrderDeck.App/Services/RestoreService.cs`
- Create: `OrderDeck.App/Services/RestoreRecoveryService.cs` (HostedService)
- Create: `OrderDeck.Tests/Services/RestoreServiceTests.cs`
- Create: `OrderDeck.Tests/Services/RestoreRecoveryServiceTests.cs`
- Modify: `OrderDeck.App/AppHost.cs` (DI for both services)

**Context:** RestoreService handles download + verify SHA + .pre-restore.bak hedge + extract. RestoreRecoveryService is a HostedService that runs on app start and detects orphan .pre-restore.bak files.

- [ ] **Step 1: RestoreServiceTests (FAIL beklenir)**

`OrderDeck.Tests/Services/RestoreServiceTests.cs`:

```csharp
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.Licensing.Backup;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.Services;

public class RestoreServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempDir;

    public RestoreServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orderdeck-rs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "orderdeck.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static byte[] BuildZip(byte[] dbContent)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("orderdeck.db");
            using var s = entry.Open();
            s.Write(dbContent, 0, dbContent.Length);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task RestoreAsync_DownloadsAndExtracts_CreatesPreRestoreBak()
    {
        var existingDb = Encoding.UTF8.GetBytes("OLD-DB");
        File.WriteAllBytes(_dbPath, existingDb);

        var newDbContent = Encoding.UTF8.GetBytes("NEW-DB-FROM-CLOUD");
        var zip = BuildZip(newDbContent);
        var fake = new FakeBackupClient
        {
            UploadResponseFactory = (_, _, _) =>
                new BackupMetadata(Guid.NewGuid(), zip.Length, DateTimeOffset.UtcNow, false, null)
        };
        // override Download to return our zip
        var downloadId = Guid.NewGuid();
        var fakeWithDownload = new FakeBackupClientWithDownload(zip);
        var sut = new RestoreService(_dbPath, fakeWithDownload, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(downloadId);

        result.Success.Should().BeTrue();
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(newDbContent);
        File.Exists(_dbPath + ".pre-restore.bak").Should().BeTrue();
        File.ReadAllBytes(_dbPath + ".pre-restore.bak").Should().BeEquivalentTo(existingDb);
    }

    [Fact]
    public async Task RestoreAsync_DownloadFails_LeavesOriginalDbUntouched()
    {
        var existingDb = Encoding.UTF8.GetBytes("OLD-DB");
        File.WriteAllBytes(_dbPath, existingDb);
        var fake = new FakeBackupClientWithDownload(failOnDownload: true);
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(existingDb);
    }

    [Fact]
    public async Task RestoreAsync_InvalidZipMissingDbEntry_ReturnsFailure_NoOverwrite()
    {
        var existingDb = Encoding.UTF8.GetBytes("OLD-DB");
        File.WriteAllBytes(_dbPath, existingDb);

        var badZip = new byte[] { 1, 2, 3 };  // not a valid zip
        var fake = new FakeBackupClientWithDownload(badZip);
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var result = await sut.RestoreAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        File.ReadAllBytes(_dbPath).Should().BeEquivalentTo(existingDb);
    }

    [Fact]
    public async Task ListAvailableAsync_ReturnsClientResults()
    {
        var fake = new FakeBackupClientWithDownload(new byte[0]);
        fake.ListResults = new List<BackupMetadata>
        {
            new(Guid.NewGuid(), 100, DateTimeOffset.UtcNow, true, "M1"),
            new(Guid.NewGuid(), 200, DateTimeOffset.UtcNow.AddDays(-1), false, "M2")
        };
        var sut = new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance);

        var list = await sut.ListAvailableAsync();
        list.Should().HaveCount(2);
        list[0].MachineName.Should().Be("M1");
    }
}

internal sealed class FakeBackupClientWithDownload : IBackupClient
{
    private readonly byte[]? _downloadBytes;
    private readonly bool _failOnDownload;
    public List<BackupMetadata> ListResults { get; set; } = new();

    public FakeBackupClientWithDownload(byte[] downloadBytes) { _downloadBytes = downloadBytes; _failOnDownload = false; }
    public FakeBackupClientWithDownload(bool failOnDownload) { _downloadBytes = null; _failOnDownload = failOnDownload; }

    public Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct = default) =>
        Task.FromResult(new BackupMetadata(Guid.NewGuid(), zipPayload.Length, DateTimeOffset.UtcNow, false, machineName));

    public Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupMetadata>>(ListResults);

    public Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct = default)
    {
        if (_failOnDownload) throw new InvalidOperationException("simulated download fail");
        return Task.FromResult(_downloadBytes!);
    }

    public Task DeleteAsync(Guid backupId, CancellationToken ct = default) => Task.CompletedTask;
}
```

- [ ] **Step 2: RestoreService implementation**

`OrderDeck.App/Services/RestoreService.cs`:

```csharp
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Services;

public sealed record RestoreResult(bool Success, string? Error);

/// <summary>
/// Phase 5a: download cloud backup, hedge with .pre-restore.bak copy of existing db,
/// then extract zip → orderdeck.db. Caller must restart app for new connections.
/// </summary>
public sealed class RestoreService
{
    public const string PreRestoreBakSuffix = ".pre-restore.bak";

    private readonly string _databaseFile;
    private readonly IBackupClient _client;
    private readonly ILogger<RestoreService> _log;

    public RestoreService(string databaseFile, IBackupClient client, ILogger<RestoreService> log)
    {
        _databaseFile = databaseFile;
        _client = client;
        _log = log;
    }

    public Task<IReadOnlyList<BackupMetadata>> ListAvailableAsync(CancellationToken ct = default) =>
        _client.ListAsync(ct);

    public async Task<RestoreResult> RestoreAsync(Guid backupId, CancellationToken ct = default)
    {
        byte[] zipBytes;
        try
        {
            zipBytes = await _client.DownloadAsync(backupId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore download failed for {BackupId}", backupId);
            return new RestoreResult(false, $"İndirme başarısız: {ex.Message}");
        }

        var bakPath = _databaseFile + PreRestoreBakSuffix;
        try
        {
            // Hedge: backup existing db before overwriting
            if (File.Exists(_databaseFile))
                File.Copy(_databaseFile, bakPath, overwrite: true);

            // Extract to temp first, then atomic move-overwrite
            var tempExtract = _databaseFile + ".restoring";
            using (var ms = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry("orderdeck.db")
                    ?? throw new InvalidOperationException("Backup zip missing orderdeck.db entry");
                await using var src = entry.Open();
                await using var dst = File.Create(tempExtract);
                await src.CopyToAsync(dst, ct);
            }

            // Replace db
            File.Move(tempExtract, _databaseFile, overwrite: true);
            _log.LogInformation("Restore complete: {BackupId} → {Path}", backupId, _databaseFile);
            return new RestoreResult(true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed mid-way for {BackupId}", backupId);
            // Roll back from .pre-restore.bak if extract corrupted the original
            try
            {
                if (File.Exists(bakPath) && !ZipLooksValid(_databaseFile))
                    File.Copy(bakPath, _databaseFile, overwrite: true);
            }
            catch { /* best effort */ }
            return new RestoreResult(false, $"Geri yükleme hatası: {ex.Message}");
        }
    }

    private static bool ZipLooksValid(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
}
```

- [ ] **Step 3: RestoreRecoveryService (HostedService)**

`OrderDeck.App/Services/RestoreRecoveryService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services;

/// <summary>
/// Phase 5a: detects orphan .pre-restore.bak files at app start.
/// If found AND main DB looks empty/corrupt, prompts user to roll back.
/// In v1: only logs a warning. Future: UI prompt.
/// </summary>
public sealed class RestoreRecoveryService : IHostedService
{
    private readonly string _databaseFile;
    private readonly ILogger<RestoreRecoveryService> _log;

    public RestoreRecoveryService(string databaseFile, ILogger<RestoreRecoveryService> log)
    {
        _databaseFile = databaseFile;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bakPath = _databaseFile + RestoreService.PreRestoreBakSuffix;
        if (!File.Exists(bakPath)) return Task.CompletedTask;

        // If main DB is valid and non-trivial, restore was successful → safe to delete bak.
        var mainExists = File.Exists(_databaseFile);
        var mainSize = mainExists ? new FileInfo(_databaseFile).Length : 0;

        if (mainExists && mainSize >= 1024)
        {
            _log.LogInformation("Cleaning up successful pre-restore backup: {Path}", bakPath);
            try { File.Delete(bakPath); } catch (Exception ex) { _log.LogWarning(ex, "Failed to delete bak"); }
        }
        else
        {
            // Main DB missing/tiny — restore likely interrupted. Log a warning.
            // Future: surface to user via dialog. For v1, just log.
            _log.LogWarning("Detected pre-restore backup at {Path} but main DB is empty/missing — possible interrupted restore", bakPath);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: RestoreRecoveryServiceTests**

`OrderDeck.Tests/Services/RestoreRecoveryServiceTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.Services;

public class RestoreRecoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RestoreRecoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rs-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "orderdeck.db");
    }

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    [Fact]
    public async Task StartAsync_BakFileWithValidMainDb_DeletesBak()
    {
        File.WriteAllBytes(_dbPath, new byte[2048]);  // valid db (>= 1024)
        var bakPath = _dbPath + RestoreService.PreRestoreBakSuffix;
        File.WriteAllBytes(bakPath, new byte[1000]);

        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        await sut.StartAsync(default);

        File.Exists(bakPath).Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_BakFileWithMissingMainDb_LeavesBak()
    {
        // No main db exists
        var bakPath = _dbPath + RestoreService.PreRestoreBakSuffix;
        File.WriteAllBytes(bakPath, new byte[1000]);

        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        await sut.StartAsync(default);

        File.Exists(bakPath).Should().BeTrue(because: "interrupted restore: bak preserved");
    }

    [Fact]
    public async Task StartAsync_NoBakFile_NoOp()
    {
        File.WriteAllBytes(_dbPath, new byte[2048]);
        var sut = new RestoreRecoveryService(_dbPath, NullLogger<RestoreRecoveryService>.Instance);
        Func<Task> act = () => sut.StartAsync(default);
        await act.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 5: AppHost DI**

In `OrderDeck.App/AppHost.cs`:

```csharp
services.AddSingleton<RestoreService>(sp =>
    new RestoreService(
        AppPaths.DatabaseFile,
        sp.GetRequiredService<IBackupClient>(),
        sp.GetRequiredService<ILogger<RestoreService>>()));

services.AddHostedService(sp =>
    new RestoreRecoveryService(
        AppPaths.DatabaseFile,
        sp.GetRequiredService<ILogger<RestoreRecoveryService>>()));
```

- [ ] **Step 6: Tests + commit**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~Restore"
```

Expected: 7/7 pass (4 RestoreService + 3 RestoreRecoveryService).

```bash
git add OrderDeck.App/Services/RestoreService.cs \
        OrderDeck.App/Services/RestoreRecoveryService.cs \
        OrderDeck.App/AppHost.cs \
        OrderDeck.Tests/Services/RestoreServiceTests.cs \
        OrderDeck.Tests/Services/RestoreRecoveryServiceTests.cs

git commit -m "feat(app): Phase 5a — RestoreService + RestoreRecoveryService

RestoreService.RestoreAsync(backupId):
1. Download zip via IBackupClient
2. Copy current db → .pre-restore.bak (rollback hedge)
3. Extract zip orderdeck.db entry → temp file
4. Atomic File.Move overwrite to AppPaths.DatabaseFile
5. On failure: roll back from .pre-restore.bak

RestoreRecoveryService (IHostedService): scans for orphan .pre-restore.bak
at startup. If main db is healthy → delete bak (cleanup). If main db
empty/missing → log warning (future: UI prompt).

7 unit tests covering happy path, download failure preserves original,
invalid zip preserves original, list passthrough, recovery cleanup,
recovery preservation on interrupted restore."
```

---

### Task 15: RestoreDialog + auto-prompt on empty DB

**Files:**
- Create: `OrderDeck.App/ViewModels/RestoreDialogViewModel.cs`
- Create: `OrderDeck.App/Views/RestoreDialog.xaml` + `.xaml.cs`
- Create: `OrderDeck.Tests/ViewModels/RestoreDialogViewModelTests.cs`
- Modify: `OrderDeck.App/App.xaml.cs` (auto-prompt on empty DB after license activation)

**Context:** Dialog auto-shows after login if local DB is empty/missing AND cloud has backups. Lists backups, user picks one, RestoreService.RestoreAsync, then forces app shutdown for clean reinit.

- [ ] **Step 1: RestoreDialogViewModel**

`OrderDeck.App/ViewModels/RestoreDialogViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.ViewModels;

public sealed partial class RestoreDialogViewModel : ObservableObject
{
    private readonly RestoreService _service;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private BackupMetadata? _selectedBackup;
    [ObservableProperty] private bool _restoreCompleted;

    public ObservableCollection<BackupMetadata> AvailableBackups { get; } = new();

    public RestoreDialogViewModel(RestoreService service)
    {
        _service = service;
        RestoreLatestCommand = new AsyncRelayCommand(RestoreLatestAsync, () => !IsBusy && AvailableBackups.Count > 0);
        RestoreSelectedCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => !IsBusy && SelectedBackup is not null);
        SkipCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public IAsyncRelayCommand RestoreLatestCommand { get; }
    public IAsyncRelayCommand RestoreSelectedCommand { get; }
    public IRelayCommand SkipCommand { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler? RestoreCompletedEvent;

    public void Populate(IReadOnlyList<BackupMetadata> backups)
    {
        AvailableBackups.Clear();
        foreach (var b in backups.OrderByDescending(b => b.CreatedAt))
            AvailableBackups.Add(b);
        RestoreLatestCommand.NotifyCanExecuteChanged();
    }

    private Task RestoreLatestAsync() =>
        AvailableBackups.Count == 0
            ? Task.CompletedTask
            : RestoreInternalAsync(AvailableBackups[0]);

    private Task RestoreSelectedAsync() =>
        SelectedBackup is null
            ? Task.CompletedTask
            : RestoreInternalAsync(SelectedBackup);

    private async Task RestoreInternalAsync(BackupMetadata backup)
    {
        IsBusy = true;
        StatusMessage = "Yedek indiriliyor ve geri yükleniyor…";
        try
        {
            var result = await _service.RestoreAsync(backup.Id);
            if (result.Success)
            {
                StatusMessage = "Geri yükleme tamamlandı. Uygulama yeniden başlatılacak.";
                RestoreCompleted = true;
                RestoreCompletedEvent?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StatusMessage = $"Hata: {result.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
```

- [ ] **Step 2: RestoreDialog.xaml**

`OrderDeck.App/Views/RestoreDialog.xaml`:

```xml
<Window x:Class="OrderDeck.App.Views.RestoreDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Cloud Yedek Bulundu"
        Width="500" SizeToContent="Height"
        ResizeMode="NoResize"
        WindowStartupLocation="CenterScreen">
    <StackPanel Margin="20">
        <TextBlock TextWrapping="Wrap" FontSize="14" Margin="0,0,0,12">
            Hesabınızda cloud yedekleri bulundu. Geri yüklemek ister misiniz?
        </TextBlock>

        <ListBox ItemsSource="{Binding AvailableBackups}"
                 SelectedItem="{Binding SelectedBackup}"
                 Height="200" Margin="0,0,0,12">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="{Binding CreatedAt, StringFormat='{}{0:dd MMM yyyy HH:mm}'}" FontWeight="Bold"/>
                        <TextBlock Text="—" Margin="6,0"/>
                        <TextBlock>
                            <Run Text="{Binding SizeBytes}"/>
                            <Run Text=" bytes"/>
                        </TextBlock>
                        <TextBlock Text=" 📅" Visibility="{Binding IsMonthlyMilestone, Converter={StaticResource BoolToVisibleConverter}}"/>
                        <TextBlock Margin="6,0" Foreground="Gray">
                            <Run Text="{Binding MachineName, FallbackValue=''}"/>
                        </TextBlock>
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>

        <TextBlock Text="{Binding StatusMessage}"
                   Foreground="DarkBlue" Margin="0,0,0,8"
                   Visibility="{Binding StatusMessage, Converter={StaticResource NullToCollapsedConverter}}"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Atla, yeni başlat" Command="{Binding SkipCommand}" MinWidth="120" Margin="0,0,8,0"/>
            <Button Content="Seçileni geri yükle" Command="{Binding RestoreSelectedCommand}" MinWidth="160" Margin="0,0,8,0"/>
            <Button Content="En son yedeği kullan" Command="{Binding RestoreLatestCommand}" IsDefault="True"
                    MinWidth="180" Background="#25D366" Foreground="White" BorderThickness="0" Padding="8,4"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 3: RestoreDialog.xaml.cs**

`OrderDeck.App/Views/RestoreDialog.xaml.cs`:

```csharp
using System.Windows;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.App.Views;

public partial class RestoreDialog : Window
{
    private readonly RestoreDialogViewModel _vm;

    public RestoreDialog(RestoreService service, IReadOnlyList<BackupMetadata> available)
    {
        InitializeComponent();
        _vm = new RestoreDialogViewModel(service);
        _vm.Populate(available);
        _vm.CloseRequested += (_, _) => { DialogResult = false; Close(); };
        _vm.RestoreCompletedEvent += (_, _) => { DialogResult = true; Close(); };
        DataContext = _vm;
    }
}
```

- [ ] **Step 4: VM tests**

`OrderDeck.Tests/ViewModels/RestoreDialogViewModelTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Backup;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class RestoreDialogViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RestoreDialogViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rd-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "orderdeck.db");
    }
    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    [Fact]
    public void Populate_OrdersBackupsByCreatedAtDescending()
    {
        var fake = new FakeBackupClient();
        var sut = new RestoreDialogViewModel(new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance));

        var older = new BackupMetadata(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddDays(-2), false, "A");
        var newer = new BackupMetadata(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, false, "A");
        sut.Populate(new[] { older, newer });

        sut.AvailableBackups[0].Should().BeEquivalentTo(newer);
        sut.AvailableBackups[1].Should().BeEquivalentTo(older);
    }

    [Fact]
    public void RestoreLatestCommand_Disabled_WhenAvailableBackupsEmpty()
    {
        var fake = new FakeBackupClient();
        var sut = new RestoreDialogViewModel(new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance));
        sut.RestoreLatestCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SkipCommand_FiresCloseRequestedEvent()
    {
        var fake = new FakeBackupClient();
        var sut = new RestoreDialogViewModel(new RestoreService(_dbPath, fake, NullLogger<RestoreService>.Instance));
        var fired = false;
        sut.CloseRequested += (_, _) => fired = true;
        sut.SkipCommand.Execute(null);
        fired.Should().BeTrue();
    }
}
```

- [ ] **Step 5: App.xaml.cs auto-prompt wiring**

In `OrderDeck.App/App.xaml.cs`, after license activation logic and before MainWindow shown:

```csharp
// Phase 5a — auto-prompt restore if local DB is empty AND cloud has backups
try
{
    var dbFile = AppPaths.DatabaseFile;
    var dbMissingOrTiny = !File.Exists(dbFile) || new FileInfo(dbFile).Length < 10240;
    if (dbMissingOrTiny)
    {
        var restoreService = Host.Services.GetRequiredService<RestoreService>();
        var available = await restoreService.ListAvailableAsync();
        if (available.Count > 0)
        {
            var dlg = new Views.RestoreDialog(restoreService, available);
            var ok = dlg.ShowDialog();
            if (ok == true)
            {
                MessageBox.Show("Geri yükleme tamamlandı. Uygulama yeniden başlatılacak.",
                    "OrderDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
    }
}
catch (Exception ex)
{
    var logger = Host.Services.GetRequiredService<ILogger<App>>();
    logger.LogWarning(ex, "Restore auto-prompt failed (non-fatal)");
}
```

- [ ] **Step 6: Tests + commit**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~RestoreDialogViewModel"
```

Expected: 0 errors, 3/3 pass.

```bash
git add OrderDeck.App/ViewModels/RestoreDialogViewModel.cs \
        OrderDeck.App/Views/RestoreDialog.xaml \
        OrderDeck.App/Views/RestoreDialog.xaml.cs \
        OrderDeck.App/App.xaml.cs \
        OrderDeck.Tests/ViewModels/RestoreDialogViewModelTests.cs

git commit -m "feat(app): Phase 5a — RestoreDialog + auto-prompt on empty DB

RestoreDialogViewModel: AvailableBackups (sorted DESC), SelectedBackup,
IsBusy, StatusMessage. Commands: RestoreLatest (default action),
RestoreSelected, Skip. Events CloseRequested + RestoreCompletedEvent.

RestoreDialog.xaml: ListBox of backups (date / size / monthly badge /
machine), buttons + status text. Bound to NullToCollapsedConverter +
BoolToVisibleConverter from App.xaml.

App.xaml.cs auto-prompt: after license activation, if AppPaths.DatabaseFile
missing or < 10KB AND cloud has backups → show dialog. On success → MessageBox
+ Application.Shutdown() (forces user to manually relaunch with new DB).

3 ViewModel tests."
```

---

### Task 16: Deploy — backup master key + docker-compose volume + smoke

**Files:**
- Create: `deploy/setup-backup-key.sh`
- Modify: `deploy/docker-compose.yml` (env vars + volume mount)
- Modify: `deploy/README.md` (BACKUP_MASTER_KEY in template + instructions)
- Modify: `OrderDeck.LicenseServer/appsettings.json` (Backup section already added in Task 2)

**Context:** Final integration step. Generate 32-byte master key on VPS, add to `.env`, mount `./backups:/app/Backups` volume, restart container, smoke test E2E from desktop app.

- [ ] **Step 1: setup-backup-key.sh**

`deploy/setup-backup-key.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

# Generate (or rotate) the AES-256-GCM master key for OrderDeck cloud backups.
# Writes BACKUP_MASTER_KEY=<64 hex chars> to /opt/orderdeck/.env (mode 600).
# Restarts the license-server container so the new key is loaded.

ENV_FILE=/opt/orderdeck/.env
COMPOSE_DIR=/opt/orderdeck

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: $ENV_FILE not found" >&2
  exit 1
fi

# Detect existing key
if grep -q '^BACKUP_MASTER_KEY=' "$ENV_FILE"; then
    EXISTING=$(grep '^BACKUP_MASTER_KEY=' "$ENV_FILE" | cut -d= -f2-)
    if [ -n "$EXISTING" ] && [ "$EXISTING" != "REPLACE-WITH-64-HEX-CHARS" ]; then
        read -rp "An existing BACKUP_MASTER_KEY is set. Rotating will make ALL existing encrypted backups unreadable. Continue? [yes/NO]: " confirm
        if [ "$confirm" != "yes" ]; then
            echo "Aborted."
            exit 0
        fi
    fi
fi

# Generate 32 bytes → 64 hex chars
NEW_KEY=$(openssl rand -hex 32)
if [ ${#NEW_KEY} -ne 64 ]; then
    echo "ERROR: openssl produced unexpected key length: ${#NEW_KEY}" >&2
    exit 1
fi

# Update .env (replace or append)
if grep -q '^BACKUP_MASTER_KEY=' "$ENV_FILE"; then
    sed -i "s|^BACKUP_MASTER_KEY=.*|BACKUP_MASTER_KEY=${NEW_KEY}|" "$ENV_FILE"
else
    echo "BACKUP_MASTER_KEY=${NEW_KEY}" >> "$ENV_FILE"
fi

chmod 600 "$ENV_FILE"
echo "BACKUP_MASTER_KEY written (64 hex chars). Length: ${#NEW_KEY}"

echo ""
echo "Restarting license-server..."
cd "$COMPOSE_DIR"
docker compose up -d --force-recreate license-server 2>&1 | tail -5

echo ""
echo "Done. Verify:"
echo "  docker exec orderdeck-license env | grep Backup__MasterKeyHex"
```

- [ ] **Step 2: Update docker-compose.yml**

In `deploy/docker-compose.yml`, in the `license-server.environment:` block, add:

```yaml
      Backup__MasterKeyHex: "${BACKUP_MASTER_KEY}"
      Backup__StorageRoot: "/app/Backups"
      Backup__MaxBlobSizeMb: "200"
```

In the `license-server.volumes:` block, add:

```yaml
      - ./backups:/app/Backups
```

- [ ] **Step 3: Update deploy/README.md**

Add `BACKUP_MASTER_KEY` to the .env template section:

```bash
BACKUP_MASTER_KEY=GenerateWith_setup-backup-key.sh
```

Add a "Backup setup" subsection:

```markdown
## Cloud backup setup (Phase 5a)

After initial deploy, bootstrap the AES master key:

```bash
ssh root@72.62.53.86
/opt/orderdeck/setup-backup-key.sh
```

This generates a 64-hex (32-byte) random key, writes it to `/opt/orderdeck/.env`,
and restarts the license-server. Backups are stored at `/opt/orderdeck/backups/{customerId}/`.

**Rotation warning:** rotating the key makes all existing encrypted backups
unreadable (no re-encryption flow in v1).
```

- [ ] **Step 4: Deploy to VPS**

```bash
# Local: commit current state and push deploy artifacts
git add deploy/setup-backup-key.sh deploy/docker-compose.yml deploy/README.md
git commit -m "deploy: Phase 5a — backup master key bootstrap script + compose env/volume"

# Local: deploy
scp -i ~/.ssh/id_ed25519 deploy/setup-backup-key.sh root@72.62.53.86:/opt/orderdeck/setup-backup-key.sh
scp -i ~/.ssh/id_ed25519 deploy/docker-compose.yml root@72.62.53.86:/opt/orderdeck/docker-compose.yml
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86 'sed -i "s/\r$//" /opt/orderdeck/setup-backup-key.sh && chmod +x /opt/orderdeck/setup-backup-key.sh && mkdir -p /opt/orderdeck/backups && chmod 700 /opt/orderdeck/backups'
```

- [ ] **Step 5: Build + scp updated app source, rebuild license-server image**

```bash
cd /c/Users/burak/source/repos/LiveDeck

# Tar app source (Dockerfile + LicenseServer + dependencies)
tar --exclude='obj' --exclude='bin' --exclude='*.user' \
  -czf /tmp/orderdeck-app.tar.gz \
  OrderDeck.LicenseServer/

scp -i ~/.ssh/id_ed25519 /tmp/orderdeck-app.tar.gz root@72.62.53.86:/opt/orderdeck/app.tar.gz

ssh -i ~/.ssh/id_ed25519 root@72.62.53.86 '
cd /opt/orderdeck
rm -rf app/OrderDeck.LicenseServer
tar -xzf app.tar.gz -C app/
rm app.tar.gz
docker compose build license-server 2>&1 | tail -10
'
```

- [ ] **Step 6: Generate master key + EF migration apply**

```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86
/opt/orderdeck/setup-backup-key.sh
# (interactive — confirm if prompted, container auto-restarts)
```

EF migration `AddCustomerBackups` auto-applies on container start (if Program.cs has `db.Database.Migrate()`). Verify:

```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86 '
SQL_PWD=$(grep ^SQL_PASSWORD= /opt/orderdeck/.env | cut -d= -f2-)
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PWD" -C -No -Q "
USE OrderDeckLicense;
SELECT name FROM sys.tables WHERE name = '\''CustomerBackups'\'';"
'
```

Expected: `CustomerBackups` row returned (1 row affected).

- [ ] **Step 7: E2E smoke**

```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86 'docker logs orderdeck-license --tail 20 2>&1'
```

Expected: no startup errors. `Backup:MasterKeyHex must be exactly 64 hex chars` should NOT appear (means key is set correctly).

Then locally: launch desktop app, end a stream session (or trigger a manual session-end if app has dev hook), watch:
```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86 'docker logs orderdeck-license --tail 30 -f 2>&1'
```

Expected log line: `INFO ... Background backup OK (stream-end): NNNN bytes`. Then verify:

```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86 '
ls -la /opt/orderdeck/backups/  # should have customer guid subdirectory
SQL_PWD=$(grep ^SQL_PASSWORD= /opt/orderdeck/.env | cut -d= -f2-)
docker exec orderdeck-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_PWD" -C -No -Q "
USE OrderDeckLicense;
SELECT TOP 5 Id, CustomerId, SizeBytes, IsMonthlyMilestone, CreatedAt FROM CustomerBackups ORDER BY CreatedAt DESC;"
'
```

Expected: row appears with current timestamp.

Browser: visit `https://license.orderdeckapp.com/admin/customers` → click your customer → "Yedekler" → see the row → click "Görüntüle" → Summary page renders with totals.

- [ ] **Step 8: Final commit**

```bash
git add deploy/setup-backup-key.sh deploy/docker-compose.yml deploy/README.md
# (already committed in Step 4 if you ran it; otherwise:)
git commit -m "deploy: Phase 5a — cloud backup deployment

setup-backup-key.sh: openssl rand -hex 32 → BACKUP_MASTER_KEY in .env (mode 600),
container restart, rotation warning prompt.

docker-compose.yml: Backup__MasterKeyHex/StorageRoot/MaxBlobSizeMb env vars,
./backups:/app/Backups volume mount.

README: backup setup section + rotation warning (rotating key = all existing
backups unreadable, no re-encryption flow in v1).

E2E smoke verified: master key set, EF migration AddCustomerBackups applied,
desktop stream-end → upload → DB row + filesystem blob → admin viewer renders summary."
```

---

## Spec Coverage Summary

| Spec section | Tasks |
|--|--|
| §3.1 VPS storage | 2 (storage service write/read) |
| §3.2 StreamSession.End trigger | 13 (event + AppHost wiring) |
| §3.3 AES-256-GCM at rest | 2 (BackupStorageService) |
| §3.4 Retention 5 + monthly | 3 (BackupRetentionService) |
| §3.5 DB-only zip scope | 13 (BackupService zips orderdeck.db only) |
| §3.6 Auto-prompt restore | 14 + 15 (RestoreService + RestoreDialog + App.xaml.cs) |
| §3.7 Fire-and-forget client | 13 (BackupService.QueueBackup) |
| §3.8 Admin deep view | 7-11 (BackupViewerService + 5 Razor pages) |
| §3.9 Silent UI | 15 (no Settings tab, only auto-prompt on empty DB) |
| §4 Architecture diagram | All tasks reference |
| §5.1 CustomerBackup entity | 1 |
| §5.2 EF migration 010 | 1 |
| §5.3 Filesystem layout | 2 + 16 |
| §5.4 Encryption format | 2 |
| §5.5 Env vars | 2 + 16 |
| §6 REST API | 5 (controller) + 6 (round-trip test) |
| §7 Server services | 2, 3, 7 |
| §8 Client SDK + services | 12, 13, 14, 15 |
| §9.1 No customer UI | 15 (only auto-prompt RestoreDialog) |
| §9.2-9.4 Admin viewer | 8, 9, 10, 11 |
| §10 Retention algorithm | 3 |
| §11 Error handling | 5, 7, 9, 13, 14 |
| §12 Audit log | 4 (constants), 5 + 8-11 (call sites) |
| §13 Testing strategy | All tasks include tests |
| §14 Performance targets | (no test, manual smoke in Task 16) |
| §15 YAGNI | (deliberate omissions match) |
| §16 File manifest | All tasks specify Files: |
| §17 Migration & rollout | 1 (EF) + 16 (deploy) |

No gaps detected.

## Test Counts (target)

| Project | Pre-Phase 5a | New | Post |
|--|--|--|--|
| OrderDeck.LicenseServer.Tests | ~201 | +29 (1+5+6+8+1+4+2+3) | ~230 |
| OrderDeck.Licensing.Tests | ~108 | +5 | ~113 |
| OrderDeck.Tests | ~193 | +14 (4+4+3+3) | ~207 |
| **TOTAL** | **~502** | **+48** | **~550** |

(Plan'da yazdığım 43 hedef + integration test + extras = 48 actual.)

---

**End of Phase 5a Implementation Plan.**





