# Broadcast Posts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Yayıncı tarafı broadcast posts — Mobile Panel'den foto/video/text duyuru oluşturma, sabitleme, 30 gün auto-delete, Cloudflare R2 storage.

**Architecture:** Server'da `BroadcastPost` entity + Cloudflare R2 media storage (pre-signed PUT/GET URL flow). Mobile Panel'de 4 yeni ekran. Hangfire daily job 30 gün geçmişleri siler (pinned hariç). Mevcut `S3BackupSink` pattern'i baz alınır.

**Tech Stack:** .NET 10, EF Core, AWSSDK.S3 (R2 S3-compatible), Hangfire, React + Vite + Capacitor 6, @capacitor/camera, TanStack Query, Tailwind.

**Spec:** `docs/superpowers/specs/2026-05-15-broadcast-posts-design.md` (PR #62)

---

## File Structure

### Server

**Create:**
- `OrderDeck.LicenseServer/Domain/BroadcastPost.cs`
- `OrderDeck.LicenseServer/Services/BroadcastPosts/IBroadcastMediaStorage.cs`
- `OrderDeck.LicenseServer/Services/BroadcastPosts/StubBroadcastMediaStorage.cs`
- `OrderDeck.LicenseServer/Services/BroadcastPosts/R2Options.cs`
- `OrderDeck.LicenseServer/Services/BroadcastPosts/R2BroadcastMediaStorage.cs`
- `OrderDeck.LicenseServer/Services/BroadcastPosts/BroadcastPostCleanupJob.cs`
- `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- `OrderDeck.LicenseServer/Data/Migrations/*_AddBroadcastPosts.cs` (generated)
- `OrderDeck.LicenseServer.Tests/TestHelpers/FakeBroadcastMediaStorage.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`
- `OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/StubStorageTests.cs`
- `OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/BroadcastPostCleanupJobTests.cs`
- `OrderDeck.LicenseServer.Tests/Storage/BroadcastPostMigrationTests.cs`
- `deploy/setup-r2.md`

**Modify:**
- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- `OrderDeck.LicenseServer/Program.cs`
- `OrderDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`

### Mobile Panel

**Create:**
- `apps/panel/src/screens/DuyurularScreen.tsx`
- `apps/panel/src/screens/YeniDuyuruScreen.tsx`
- `apps/panel/src/screens/DuyuruDetayScreen.tsx`
- `apps/panel/src/lib/broadcastUpload.ts`

**Modify:**
- `apps/panel/src/api/queries.ts`
- `apps/panel/src/App.tsx`
- `apps/panel/src/screens/DahaFazlaScreen.tsx`
- `apps/panel/src/screens/AnaScreen.tsx`
- `apps/panel/package.json`

---

## Task Summary

1. Entity + DbContext + EF migration
2. `IBroadcastMediaStorage` + Stub
3. `R2Options` + `R2BroadcastMediaStorage`
4. Controller iskeleti + `upload-url`
5. POST create endpoint
6. GET list + detail endpoint'leri
7. `media-url` endpoint
8. PUT caption edit endpoint
9. POST/DELETE pin endpoint'leri
10. DELETE endpoint
11. Cleanup job + Hangfire registration
12. R2 deploy doc + Program.cs prod wiring
13. Mobile API hooks
14. DuyurularScreen liste + route
15. YeniDuyuruScreen text mode
16. YeniDuyuruScreen photo mode
17. YeniDuyuruScreen video mode
18. DuyuruDetayScreen + actions
19. Ana ekran teaser + DahaFazla navrow
20. PR + R2 manuel smoke checklist

---

## Implementation Notes

Detaylı task adımları aşağıda. Her task ayrı commit. Sunucu (1-12) + mobile (13-19) + PR (20).

Engineer'lar için: **bir önceki PR'lardaki pattern'lere bak**:
- Controller pattern: `PanelOperatorsController.cs` veya `PanelCustomersController.cs`
- S3 client pattern: `Services/Backup/S3BackupSink.cs`
- Hangfire job pattern: `Services/Audit/AuditRetentionJobs.cs`
- ApiFactory override pattern: mevcut `Push` ve `Email` örnekleri
- Mobile screen pattern: `EkipScreen.tsx` (CRUD + form), `DekontlarScreen.tsx` (liste)
- Mobile upload + Capacitor: bu plan'da ilk kez — Task 16'da Camera plugin detayları var

Her endpoint için tenant izolasyonu **mecburi**: `User.GetTenantCustomerId()` → owned licenses filter. Mevcut `LicenseApiClient` pattern'leri bu konuda örnek.

---

### Task 1: BroadcastPost entity + DbContext + EF migration

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/BroadcastPost.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Test: `OrderDeck.LicenseServer.Tests/Storage/BroadcastPostMigrationTests.cs`

- [ ] **Step 1: Entity dosyası**

`OrderDeck.LicenseServer/Domain/BroadcastPost.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

public enum BroadcastPostType
{
    Text = 0,
    Photo = 1,
    Video = 2
}

public sealed class BroadcastPost
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public BroadcastPostType Type { get; set; }
    public string? TextBody { get; set; }

    public string? MediaObjectKey { get; set; }
    public string? MediaContentType { get; set; }
    public long? MediaSizeBytes { get; set; }
    public int? MediaDurationSec { get; set; }
    public int? MediaWidth { get; set; }
    public int? MediaHeight { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
```

- [ ] **Step 2: DbContext'e DbSet ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` içinde `WhatsAppTemplateSettings` DbSet satırından sonra:

```csharp
public DbSet<BroadcastPost> BroadcastPosts => Set<BroadcastPost>();
```

- [ ] **Step 3: OnModelCreating config'i ekle**

Aynı dosyada `WhatsAppTemplateSettings` entity config'inden sonra:

```csharp
mb.Entity<BroadcastPost>(b =>
{
    b.HasKey(p => p.Id);
    b.Property(p => p.Type).HasConversion<int>();
    b.Property(p => p.TextBody).HasMaxLength(2000);
    b.Property(p => p.MediaObjectKey).HasMaxLength(512);
    b.Property(p => p.MediaContentType).HasMaxLength(64);
    b.HasOne(p => p.License).WithMany()
        .HasForeignKey(p => p.LicenseId).OnDelete(DeleteBehavior.Cascade);
    b.HasIndex(p => new { p.LicenseId, p.CreatedAt })
        .IsDescending(false, true);
    b.HasIndex(p => new { p.ExpiresAt, p.IsPinned });
});
```

- [ ] **Step 4: EF migration üret**

```bash
cd C:/Users/burak/source/repos/LiveDeck
dotnet ef migrations add AddBroadcastPosts \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
  --context LicenseDbContext
```

Beklenen: `Data/Migrations/{ts}_AddBroadcastPosts.cs` + Designer + ModelSnapshot güncellenir.

- [ ] **Step 5: Migration smoke test**

`OrderDeck.LicenseServer.Tests/Storage/BroadcastPostMigrationTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Storage;

public class BroadcastPostMigrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BroadcastPostMigrationTests(ApiFactory f) => _factory = f;

    [Fact]
    public async Task BroadcastPosts_table_can_round_trip_entity()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"bp-{Guid.NewGuid():N}@test.com",
            Name = "X", PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id,
            LicenseKey = "LDK-BP-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var post = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Type = BroadcastPostType.Text, TextBody = "hello",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(post);
        await db.SaveChangesAsync();

        var fetched = await db.BroadcastPosts.FirstAsync(p => p.Id == post.Id);
        fetched.TextBody.Should().Be("hello");
        fetched.Type.Should().Be(BroadcastPostType.Text);
    }
}
```

- [ ] **Step 6: Build + test**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj --nologo -v q
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~BroadcastPostMigration" --nologo
```

Beklenen: build temiz + 1 test pass.

- [ ] **Step 7: Commit**

```bash
git checkout -b feat/broadcast-posts-server
git add OrderDeck.LicenseServer/Domain/BroadcastPost.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations/ \
        OrderDeck.LicenseServer.Tests/Storage/BroadcastPostMigrationTests.cs
git commit -m "feat(posts): BroadcastPost entity + EF migration"
```

---

### Task 2: IBroadcastMediaStorage + Stub implementation

**Files:**
- Create: `OrderDeck.LicenseServer/Services/BroadcastPosts/IBroadcastMediaStorage.cs`
- Create: `OrderDeck.LicenseServer/Services/BroadcastPosts/StubBroadcastMediaStorage.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/StubStorageTests.cs`

- [ ] **Step 1: Interface**

`OrderDeck.LicenseServer/Services/BroadcastPosts/IBroadcastMediaStorage.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public interface IBroadcastMediaStorage
{
    Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default);
    Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}

public sealed record MediaObjectInfo(long SizeBytes, string ContentType);
```

- [ ] **Step 2: Stub implementation**

`OrderDeck.LicenseServer/Services/BroadcastPosts/StubBroadcastMediaStorage.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class StubBroadcastMediaStorage : IBroadcastMediaStorage
{
    private readonly ConcurrentDictionary<string, MediaObjectInfo> _objects = new();
    private readonly ILogger<StubBroadcastMediaStorage> _log;

    public StubBroadcastMediaStorage(ILogger<StubBroadcastMediaStorage> log) => _log = log;

    public void Seed(string objectKey, long sizeBytes, string contentType)
        => _objects[objectKey] = new MediaObjectInfo(sizeBytes, contentType);

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        _log.LogDebug("Stub upload-url: {Key} ({Size} bytes, {Mime})", objectKey, sizeBytes, contentType);
        return Task.FromResult($"https://stub.local/{objectKey}?upload=1");
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
        => Task.FromResult($"https://stub.local/{objectKey}?get=1");

    public Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
        => Task.FromResult(_objects.TryGetValue(objectKey, out var info) ? info : null);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Unit test**

`OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/StubStorageTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.BroadcastPosts;

public class StubStorageTests
{
    private static StubBroadcastMediaStorage New() =>
        new(NullLogger<StubBroadcastMediaStorage>.Instance);

    [Fact]
    public async Task Head_returns_null_for_missing_key()
        => (await New().HeadAsync("nope")).Should().BeNull();

    [Fact]
    public async Task Seed_then_Head_returns_info()
    {
        var s = New();
        s.Seed("k1", 1024, "image/jpeg");
        var info = await s.HeadAsync("k1");
        info.Should().NotBeNull();
        info!.SizeBytes.Should().Be(1024);
        info.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Delete_removes_seeded_object()
    {
        var s = New();
        s.Seed("k1", 1, "x");
        await s.DeleteAsync("k1");
        (await s.HeadAsync("k1")).Should().BeNull();
    }

    [Fact]
    public async Task CreateUploadUrl_returns_stub_url()
    {
        var url = await New().CreateUploadUrlAsync("k", "image/jpeg", 1024);
        url.Should().Contain("stub.local").And.Contain("upload=1");
    }
}
```

- [ ] **Step 4: Test**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~StubStorageTests" --nologo
```

Beklenen: 4 test pass.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/BroadcastPosts/ \
        OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/
git commit -m "feat(posts): IBroadcastMediaStorage abstraction + stub"
```

---

### Task 3: R2Options + R2BroadcastMediaStorage

**Files:**
- Create: `OrderDeck.LicenseServer/Services/BroadcastPosts/R2Options.cs`
- Create: `OrderDeck.LicenseServer/Services/BroadcastPosts/R2BroadcastMediaStorage.cs`

- [ ] **Step 1: R2Options**

`OrderDeck.LicenseServer/Services/BroadcastPosts/R2Options.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class R2Options
{
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string BucketName { get; set; } = "";

    public string ServiceUrl =>
        string.IsNullOrWhiteSpace(AccountId)
            ? ""
            : $"https://{AccountId}.r2.cloudflarestorage.com";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && !string.IsNullOrWhiteSpace(BucketName);
}
```

- [ ] **Step 2: R2 implementation**

`OrderDeck.LicenseServer/Services/BroadcastPosts/R2BroadcastMediaStorage.cs`:

```csharp
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class R2BroadcastMediaStorage : IBroadcastMediaStorage, IDisposable
{
    private readonly R2Options _opt;
    private readonly AmazonS3Client _client;
    private readonly ILogger<R2BroadcastMediaStorage> _log;

    public R2BroadcastMediaStorage(R2Options opt, ILogger<R2BroadcastMediaStorage> log)
    {
        _opt = opt;
        _log = log;
        if (!_opt.IsConfigured)
            throw new InvalidOperationException(
                "R2 options not configured (AccountId/AccessKeyId/SecretAccessKey/BucketName all required).");

        _client = new AmazonS3Client(
            _opt.AccessKeyId, _opt.SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = _opt.ServiceUrl,
                ForcePathStyle = true
            });
    }

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(10),
            ContentType = contentType
        };
        return Task.FromResult(_client.GetPreSignedURL(req));
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5)
        };
        return Task.FromResult(_client.GetPreSignedURL(req));
    }

    public async Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.GetObjectMetadataAsync(_opt.BucketName, objectKey, ct);
            return new MediaObjectInfo(resp.ContentLength, resp.Headers.ContentType ?? "");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_opt.BucketName, objectKey, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "R2 delete failed for {Key} (swallowed)", objectKey);
        }
    }

    public void Dispose() => _client.Dispose();
}
```

- [ ] **Step 3: Build**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj --nologo -v q
```

Beklenen: build temiz. (Unit test yok — gerçek R2 HTTP gerekir, manuel smoke'a bırakıldı.)

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Services/BroadcastPosts/R2Options.cs \
        OrderDeck.LicenseServer/Services/BroadcastPosts/R2BroadcastMediaStorage.cs
git commit -m "feat(posts): R2 media storage implementation"
```

---

### Task 4: Controller iskeleti + upload-url endpoint

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`
- Create: `OrderDeck.LicenseServer.Tests/TestHelpers/FakeBroadcastMediaStorage.cs`
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: FakeBroadcastMediaStorage**

`OrderDeck.LicenseServer.Tests/TestHelpers/FakeBroadcastMediaStorage.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public sealed class FakeBroadcastMediaStorage : IBroadcastMediaStorage
{
    public sealed record UploadCall(string Key, string ContentType, long Size);

    private readonly StubBroadcastMediaStorage _inner =
        new(NullLogger<StubBroadcastMediaStorage>.Instance);

    public List<UploadCall> UploadCalls { get; } = new();

    public void Seed(string key, long size, string contentType)
        => _inner.Seed(key, size, contentType);

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        UploadCalls.Add(new UploadCall(objectKey, contentType, sizeBytes));
        return _inner.CreateUploadUrlAsync(objectKey, contentType, sizeBytes, ct);
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
        => _inner.CreateDownloadUrlAsync(objectKey, ct);

    public Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
        => _inner.HeadAsync(objectKey, ct);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        => _inner.DeleteAsync(objectKey, ct);
}
```

- [ ] **Step 2: ApiFactory override**

`OrderDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` — `Push` property yanına:

```csharp
public FakeBroadcastMediaStorage BroadcastMedia { get; } = new();
```

`ConfigureServices` içinde `Push` override'ından sonra:

```csharp
services.RemoveAll<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>();
services.AddSingleton<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>(BroadcastMedia);
```

- [ ] **Step 3: Controller (sadece upload-url ilk version)**

`OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`:

```csharp
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

[ApiController]
[Route("api/panel/posts")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelBroadcastPostsController : ControllerBase
{
    private const long MaxPhotoBytes = 10 * 1024 * 1024;
    private const long MaxVideoBytes = 60 * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoMime = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/heic", "image/heif", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedVideoMime = new(StringComparer.OrdinalIgnoreCase)
        { "video/mp4", "video/quicktime", "video/x-m4v" };

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;

    public PanelBroadcastPostsController(LicenseDbContext db, IBroadcastMediaStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public sealed record UploadUrlRequest(string Type, long SizeBytes, string ContentType);
    public sealed record UploadUrlResponse(string UploadUrl, string ObjectKey, DateTimeOffset ExpiresAt);

    [HttpPost("upload-url")]
    public async Task<IActionResult> CreateUploadUrl([FromBody] UploadUrlRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "missing-body", statusCode: 400);

        var (allowedMime, maxBytes) = req.Type?.ToLowerInvariant() switch
        {
            "photo" => (AllowedPhotoMime, MaxPhotoBytes),
            "video" => (AllowedVideoMime, MaxVideoBytes),
            _ => (null!, 0L)
        };
        if (allowedMime is null)
            return Problem(title: "invalid-type", detail: "Type must be 'photo' or 'video'.", statusCode: 400);
        if (!allowedMime.Contains(req.ContentType ?? ""))
            return Problem(title: "invalid-content-type", statusCode: 400);
        if (req.SizeBytes <= 0 || req.SizeBytes > maxBytes)
            return Problem(title: "size-out-of-range", detail: $"Max {maxBytes} bytes.", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var licenseId = await ResolveActiveLicenseAsync(customerId, ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var postId = Guid.NewGuid();
        var objectKey = $"{licenseId.Value}/{postId}/media.bin";
        var url = await _storage.CreateUploadUrlAsync(objectKey, req.ContentType!, req.SizeBytes, ct);

        return Ok(new UploadUrlResponse(url, objectKey, DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    private Task<Guid?> ResolveActiveLicenseAsync(Guid customerId, CancellationToken ct)
        => _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null
                && l.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
}
```

- [ ] **Step 4: Test dosyası (5 test)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelBroadcastPostsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelBroadcastPostsControllerTests(ApiFactory f) => _factory = f;

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-BPC-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    [Fact]
    public async Task UploadUrl_returns_url_for_valid_photo()
    {
        var (client, licenseId) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts/upload-url",
            new { type = "photo", sizeBytes = 500_000, contentType = "image/jpeg" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("uploadUrl").And.Contain($"{licenseId}");
        _factory.BroadcastMedia.UploadCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task UploadUrl_400_on_oversize_photo()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts/upload-url",
            new { type = "photo", sizeBytes = 11 * 1024 * 1024, contentType = "image/jpeg" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadUrl_400_on_invalid_mime()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts/upload-url",
            new { type = "photo", sizeBytes = 1024, contentType = "image/gif" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadUrl_400_on_invalid_type()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts/upload-url",
            new { type = "audio", sizeBytes = 1024, contentType = "audio/mp3" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadUrl_accepts_video_mp4_under_60mb()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts/upload-url",
            new { type = "video", sizeBytes = 59 * 1024 * 1024, contentType = "video/mp4" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 5: Build + test**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj --nologo -v q
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
```

Beklenen: 5 test pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/FakeBroadcastMediaStorage.cs \
        OrderDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): upload-url endpoint + storage abstraction"
```

---

### Task 5: POST create endpoint

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: Using ekle**

Controller dosyasının başına:

```csharp
using OrderDeck.LicenseServer.Domain;
```

- [ ] **Step 2: Sabitler + DTO'lar + Create endpoint**

Controller içine (upload-url'den sonra) ekle:

```csharp
private const int MaxTextLength = 2000;
private const int MaxVideoDurationSec = 45;

public sealed record CreatePostMediaDto(
    string ObjectKey, string ContentType, long SizeBytes,
    int? DurationSec, int Width, int Height);

public sealed record CreatePostRequest(
    string Type, string? TextBody, CreatePostMediaDto? Media);

public sealed record PostDto(
    Guid Id, string Type, string? TextBody,
    string? MediaContentType, int? MediaWidth, int? MediaHeight,
    int? MediaDurationSec, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, bool IsPinned);

[HttpPost]
public async Task<IActionResult> Create([FromBody] CreatePostRequest req, CancellationToken ct)
{
    if (req is null) return Problem(title: "missing-body", statusCode: 400);

    var customerId = User.GetTenantCustomerId();
    var licenseId = await ResolveActiveLicenseAsync(customerId, ct);
    if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

    var type = req.Type?.ToLowerInvariant() switch
    {
        "text" => BroadcastPostType.Text,
        "photo" => BroadcastPostType.Photo,
        "video" => BroadcastPostType.Video,
        _ => (BroadcastPostType?)null
    };
    if (type is null) return Problem(title: "invalid-type", statusCode: 400);

    var text = req.TextBody?.Trim();
    if (type == BroadcastPostType.Text && string.IsNullOrWhiteSpace(text))
        return Problem(title: "text-required", statusCode: 400);
    if (text is { Length: > MaxTextLength })
        return Problem(title: "text-too-long", statusCode: 400);

    BroadcastPost post;
    if (type == BroadcastPostType.Text)
    {
        post = NewPost(licenseId.Value, type.Value, text, null);
    }
    else
    {
        if (req.Media is null) return Problem(title: "media-required", statusCode: 400);

        if (!req.Media.ObjectKey.StartsWith($"{licenseId.Value}/"))
            return Problem(title: "invalid-object-key", statusCode: 400);

        if (type == BroadcastPostType.Video &&
            (req.Media.DurationSec is null or <= 0 or > MaxVideoDurationSec))
            return Problem(title: "video-duration-out-of-range",
                detail: $"Max {MaxVideoDurationSec} seconds.", statusCode: 400);

        var head = await _storage.HeadAsync(req.Media.ObjectKey, ct);
        if (head is null) return Problem(title: "media-not-uploaded", statusCode: 400);

        post = NewPost(licenseId.Value, type.Value, text, req.Media);
    }

    _db.BroadcastPosts.Add(post);
    await _db.SaveChangesAsync(ct);

    return Created($"/api/panel/posts/{post.Id}", ToDto(post));
}

private static BroadcastPost NewPost(Guid licenseId, BroadcastPostType type, string? text, CreatePostMediaDto? media)
{
    var now = DateTimeOffset.UtcNow;
    return new BroadcastPost
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        Type = type,
        TextBody = text,
        MediaObjectKey = media?.ObjectKey,
        MediaContentType = media?.ContentType,
        MediaSizeBytes = media?.SizeBytes,
        MediaDurationSec = media?.DurationSec,
        MediaWidth = media?.Width,
        MediaHeight = media?.Height,
        CreatedAt = now,
        ExpiresAt = now.AddDays(30),
        IsPinned = false
    };
}

private static PostDto ToDto(BroadcastPost p) =>
    new(p.Id, p.Type.ToString().ToLowerInvariant(), p.TextBody,
        p.MediaContentType, p.MediaWidth, p.MediaHeight, p.MediaDurationSec,
        p.CreatedAt, p.ExpiresAt, p.IsPinned);
```

- [ ] **Step 3: 6 yeni test ekle**

Test dosyasının sonuna:

```csharp
[Fact]
public async Task Create_text_post_succeeds()
{
    var (client, _) = await SeedAsync();
    var resp = await client.PostAsJsonAsync("/api/panel/posts",
        new { type = "text", textBody = "Hello world" });
    resp.StatusCode.Should().Be(HttpStatusCode.Created);
}

[Fact]
public async Task Create_text_post_400_when_body_empty()
{
    var (client, _) = await SeedAsync();
    var resp = await client.PostAsJsonAsync("/api/panel/posts",
        new { type = "text", textBody = "" });
    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
}

[Fact]
public async Task Create_photo_post_400_when_media_not_uploaded()
{
    var (client, licenseId) = await SeedAsync();
    var resp = await client.PostAsJsonAsync("/api/panel/posts",
        new
        {
            type = "photo",
            textBody = (string?)null,
            media = new
            {
                objectKey = $"{licenseId}/dead-beef/media.bin",
                contentType = "image/jpeg",
                sizeBytes = 1024L,
                durationSec = (int?)null,
                width = 100, height = 100
            }
        });
    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
}

[Fact]
public async Task Create_photo_post_succeeds_after_seeded_upload()
{
    var (client, licenseId) = await SeedAsync();
    var objectKey = $"{licenseId}/seeded-post/media.bin";
    _factory.BroadcastMedia.Seed(objectKey, 1024, "image/jpeg");

    var resp = await client.PostAsJsonAsync("/api/panel/posts",
        new
        {
            type = "photo",
            textBody = "caption",
            media = new
            {
                objectKey, contentType = "image/jpeg", sizeBytes = 1024L,
                durationSec = (int?)null, width = 800, height = 600
            }
        });
    resp.StatusCode.Should().Be(HttpStatusCode.Created);
}

[Fact]
public async Task Create_video_post_400_when_duration_over_limit()
{
    var (client, licenseId) = await SeedAsync();
    var objectKey = $"{licenseId}/vid/media.bin";
    _factory.BroadcastMedia.Seed(objectKey, 1024, "video/mp4");

    var resp = await client.PostAsJsonAsync("/api/panel/posts",
        new
        {
            type = "video",
            textBody = (string?)null,
            media = new
            {
                objectKey, contentType = "video/mp4", sizeBytes = 1024L,
                durationSec = 60, width = 1080, height = 1920
            }
        });
    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
}

[Fact]
public async Task Create_400_when_object_key_belongs_to_other_license()
{
    var (client, _) = await SeedAsync();
    var foreignKey = $"{Guid.NewGuid()}/post/media.bin";
    _factory.BroadcastMedia.Seed(foreignKey, 1024, "image/jpeg");

    var resp = await client.PostAsJsonAsync("/api/panel/posts",
        new
        {
            type = "photo",
            textBody = (string?)null,
            media = new
            {
                objectKey = foreignKey, contentType = "image/jpeg", sizeBytes = 1024L,
                durationSec = (int?)null, width = 100, height = 100
            }
        });
    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
}
```

- [ ] **Step 4: Test**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
```

Beklenen: 11 test pass.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): POST create endpoint + media validation"
```

---

### Task 6: GET list + detail endpoint'leri

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: List + Detail endpoint'leri**

Controller'a ekle:

```csharp
public sealed record ListResponse(List<PostDto> Posts, string? NextCursor);

[HttpGet]
public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int limit = 20, CancellationToken ct = default)
{
    if (limit < 1 || limit > 100) limit = 20;

    var customerId = User.GetTenantCustomerId();
    var licenseIds = await _db.Licenses
        .Where(l => l.CustomerId == customerId)
        .Select(l => l.Id)
        .ToListAsync(ct);

    if (licenseIds.Count == 0) return Ok(new ListResponse(new(), null));

    var query = _db.BroadcastPosts
        .Where(p => licenseIds.Contains(p.LicenseId) && p.DeletedAt == null);

    if (!string.IsNullOrWhiteSpace(cursor) && DateTimeOffset.TryParse(cursor, out var cursorAt))
    {
        // Cursor-based: createdAt < cursorAt (next page)
        query = query.Where(p => p.CreatedAt < cursorAt);
    }

    // Pinned önce, sonra CreatedAt DESC
    var rows = await query
        .OrderByDescending(p => p.IsPinned)
        .ThenByDescending(p => p.CreatedAt)
        .Take(limit + 1)  // peek next-page cursor
        .ToListAsync(ct);

    string? nextCursor = null;
    if (rows.Count > limit)
    {
        nextCursor = rows[limit - 1].CreatedAt.ToString("O");
        rows = rows.Take(limit).ToList();
    }

    return Ok(new ListResponse(rows.Select(ToDto).ToList(), nextCursor));
}

[HttpGet("{id:guid}")]
public async Task<IActionResult> Get(Guid id, CancellationToken ct)
{
    var customerId = User.GetTenantCustomerId();
    var post = await _db.BroadcastPosts
        .Include(p => p.License)
        .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    if (post is null) return NotFound();
    if (post.License.CustomerId != customerId) return NotFound();

    return Ok(ToDto(post));
}
```

- [ ] **Step 2: Testler ekle**

Test dosyasının sonuna:

```csharp
[Fact]
public async Task List_returns_empty_when_no_posts()
{
    var (client, _) = await SeedAsync();
    var resp = await client.GetAsync("/api/panel/posts");
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await resp.Content.ReadAsStringAsync();
    body.Should().Contain("\"posts\":[]");
}

[Fact]
public async Task List_returns_pinned_before_recent()
{
    var (client, licenseId) = await SeedAsync();

    // 1 normal + 1 pinned (manuel insert)
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.BroadcastPosts.Add(new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "old normal",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(25),
            IsPinned = false
        });
        db.BroadcastPosts.Add(new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "old pinned",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresAt = DateTimeOffset.MaxValue,
            IsPinned = true
        });
        await db.SaveChangesAsync();
    }

    var resp = await client.GetAsync("/api/panel/posts");
    var body = await resp.Content.ReadAsStringAsync();
    var pinnedIdx = body.IndexOf("old pinned");
    var normalIdx = body.IndexOf("old normal");
    pinnedIdx.Should().BeGreaterThan(0).And.BeLessThan(normalIdx);
}

[Fact]
public async Task Get_returns_post_for_owner()
{
    var (client, licenseId) = await SeedAsync();
    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.GetAsync($"/api/panel/posts/{postId}");
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
}

[Fact]
public async Task Get_404_for_cross_tenant_post()
{
    var (clientA, licenseA) = await SeedAsync();
    var (clientB, _) = await SeedAsync();

    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseA,
            Type = BroadcastPostType.Text, TextBody = "secret",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await clientB.GetAsync($"/api/panel/posts/{postId}");
    resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
}
```

- [ ] **Step 3: Test**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
```

Beklenen: 15 test pass (11 + 4 yeni).

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): GET list + detail endpoint"
```

---

### Task 7: media-url endpoint

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: Endpoint**

Controller'a ekle:

```csharp
public sealed record MediaUrlResponse(string Url, DateTimeOffset ExpiresAt);

[HttpGet("{id:guid}/media-url")]
public async Task<IActionResult> GetMediaUrl(Guid id, CancellationToken ct)
{
    var customerId = User.GetTenantCustomerId();
    var post = await _db.BroadcastPosts
        .Include(p => p.License)
        .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    if (post is null) return NotFound();
    if (post.License.CustomerId != customerId) return NotFound();
    if (string.IsNullOrWhiteSpace(post.MediaObjectKey))
        return Problem(title: "no-media", statusCode: 400);

    var url = await _storage.CreateDownloadUrlAsync(post.MediaObjectKey, ct);
    return Ok(new MediaUrlResponse(url, DateTimeOffset.UtcNow.AddMinutes(5)));
}
```

- [ ] **Step 2: Test**

```csharp
[Fact]
public async Task GetMediaUrl_returns_download_url_for_photo_post()
{
    var (client, licenseId) = await SeedAsync();
    var objectKey = $"{licenseId}/media-test/media.bin";
    _factory.BroadcastMedia.Seed(objectKey, 1024, "image/jpeg");

    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Photo, MediaObjectKey = objectKey,
            MediaContentType = "image/jpeg",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.GetAsync($"/api/panel/posts/{postId}/media-url");
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await resp.Content.ReadAsStringAsync();
    body.Should().Contain("stub.local").And.Contain("get=1");
}

[Fact]
public async Task GetMediaUrl_400_for_text_only_post()
{
    var (client, licenseId) = await SeedAsync();
    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.GetAsync($"/api/panel/posts/{postId}/media-url");
    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
}
```

- [ ] **Step 3: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): media-url endpoint"
```

Beklenen: 17 test pass.

---

### Task 8: PUT caption edit endpoint

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: Endpoint**

Controller'a ekle:

```csharp
public sealed record UpdatePostRequest(string? TextBody);

[HttpPut("{id:guid}")]
public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePostRequest req, CancellationToken ct)
{
    if (req is null) return Problem(title: "missing-body", statusCode: 400);

    var customerId = User.GetTenantCustomerId();
    var post = await _db.BroadcastPosts
        .Include(p => p.License)
        .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    if (post is null) return NotFound();
    if (post.License.CustomerId != customerId) return NotFound();

    var text = req.TextBody?.Trim();
    if (post.Type == BroadcastPostType.Text && string.IsNullOrWhiteSpace(text))
        return Problem(title: "text-required", statusCode: 400);
    if (text is { Length: > MaxTextLength })
        return Problem(title: "text-too-long", statusCode: 400);

    post.TextBody = text;
    await _db.SaveChangesAsync(ct);

    return Ok(ToDto(post));
}
```

- [ ] **Step 2: 2 test**

```csharp
[Fact]
public async Task Update_changes_text_body()
{
    var (client, licenseId) = await SeedAsync();
    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "old",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.PutAsJsonAsync($"/api/panel/posts/{postId}",
        new { textBody = "new" });
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    (await resp.Content.ReadAsStringAsync()).Should().Contain("new");
}

[Fact]
public async Task Update_404_for_cross_tenant()
{
    var (clientA, licenseA) = await SeedAsync();
    var (clientB, _) = await SeedAsync();
    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseA,
            Type = BroadcastPostType.Text, TextBody = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await clientB.PutAsJsonAsync($"/api/panel/posts/{postId}",
        new { textBody = "hijack" });
    resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
}
```

- [ ] **Step 3: Commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): PUT caption edit endpoint"
```

Beklenen: 19 test pass.

---

### Task 9: Pin/Unpin endpoint'leri

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: Sabit + Pin endpoint'leri**

Controller'a ekle:

```csharp
private const int MaxPinnedPerLicense = 5;
private static readonly DateTimeOffset PinnedExpiresAt = new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

[HttpPost("{id:guid}/pin")]
public async Task<IActionResult> Pin(Guid id, CancellationToken ct)
{
    var customerId = User.GetTenantCustomerId();
    var post = await _db.BroadcastPosts
        .Include(p => p.License)
        .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    if (post is null) return NotFound();
    if (post.License.CustomerId != customerId) return NotFound();

    if (post.IsPinned) return Ok(ToDto(post));

    var pinnedCount = await _db.BroadcastPosts
        .CountAsync(p => p.LicenseId == post.LicenseId
            && p.IsPinned && p.DeletedAt == null, ct);
    if (pinnedCount >= MaxPinnedPerLicense)
        return Problem(title: "pin-limit-exceeded",
            detail: $"En fazla {MaxPinnedPerLicense} sabit duyuru.", statusCode: 409);

    post.IsPinned = true;
    post.ExpiresAt = PinnedExpiresAt;
    await _db.SaveChangesAsync(ct);
    return Ok(ToDto(post));
}

[HttpDelete("{id:guid}/pin")]
public async Task<IActionResult> Unpin(Guid id, CancellationToken ct)
{
    var customerId = User.GetTenantCustomerId();
    var post = await _db.BroadcastPosts
        .Include(p => p.License)
        .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    if (post is null) return NotFound();
    if (post.License.CustomerId != customerId) return NotFound();

    if (!post.IsPinned) return Ok(ToDto(post));

    post.IsPinned = false;
    post.ExpiresAt = post.CreatedAt.AddDays(30);
    await _db.SaveChangesAsync(ct);
    return Ok(ToDto(post));
}
```

- [ ] **Step 2: 3 test**

```csharp
[Fact]
public async Task Pin_sets_IsPinned_and_far_future_expires()
{
    var (client, licenseId) = await SeedAsync();
    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "pin-me",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.PostAsync($"/api/panel/posts/{postId}/pin", null);
    resp.StatusCode.Should().Be(HttpStatusCode.OK);

    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
    var fetched = await db2.BroadcastPosts.FirstAsync(p => p.Id == postId);
    fetched.IsPinned.Should().BeTrue();
    fetched.ExpiresAt.Year.Should().Be(9999);
}

[Fact]
public async Task Pin_409_when_limit_exceeded()
{
    var (client, licenseId) = await SeedAsync();
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        for (int i = 0; i < 5; i++)
        {
            db.BroadcastPosts.Add(new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Text, TextBody = $"pinned {i}",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                ExpiresAt = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero),
                IsPinned = true
            });
        }
        await db.SaveChangesAsync();
    }

    Guid newPostId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "extra",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        newPostId = p.Id;
    }

    var resp = await client.PostAsync($"/api/panel/posts/{newPostId}/pin", null);
    resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
}

[Fact]
public async Task Unpin_restores_30day_expires()
{
    var (client, licenseId) = await SeedAsync();
    Guid postId;
    var createdAt = DateTimeOffset.UtcNow.AddDays(-5);
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Text, TextBody = "x",
            CreatedAt = createdAt,
            ExpiresAt = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero),
            IsPinned = true
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.DeleteAsync($"/api/panel/posts/{postId}/pin");
    resp.StatusCode.Should().Be(HttpStatusCode.OK);

    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
    var fetched = await db2.BroadcastPosts.FirstAsync(p => p.Id == postId);
    fetched.IsPinned.Should().BeFalse();
    fetched.ExpiresAt.Should().BeCloseTo(createdAt.AddDays(30), TimeSpan.FromSeconds(1));
}
```

- [ ] **Step 3: Commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): pin/unpin endpoint'leri (max 5 sabit)"
```

Beklenen: 22 test pass.

---

### Task 10: DELETE post endpoint

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs`
- Modify: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs`

- [ ] **Step 1: Endpoint**

Controller'a ekle:

```csharp
[HttpDelete("{id:guid}")]
public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
{
    var customerId = User.GetTenantCustomerId();
    var post = await _db.BroadcastPosts
        .Include(p => p.License)
        .FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct);
    if (post is null) return NotFound();
    if (post.License.CustomerId != customerId) return NotFound();

    post.DeletedAt = DateTimeOffset.UtcNow;
    await _db.SaveChangesAsync(ct);

    // R2 obj sil — best-effort
    if (!string.IsNullOrWhiteSpace(post.MediaObjectKey))
    {
        try { await _storage.DeleteAsync(post.MediaObjectKey, ct); }
        catch { /* swallow */ }
    }

    return NoContent();
}
```

- [ ] **Step 2: Test**

```csharp
[Fact]
public async Task Delete_soft_deletes_and_removes_media()
{
    var (client, licenseId) = await SeedAsync();
    var objectKey = $"{licenseId}/del-me/media.bin";
    _factory.BroadcastMedia.Seed(objectKey, 1024, "image/jpeg");

    Guid postId;
    using (var scope = _factory.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var p = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Type = BroadcastPostType.Photo,
            MediaObjectKey = objectKey, MediaContentType = "image/jpeg",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(p);
        await db.SaveChangesAsync();
        postId = p.Id;
    }

    var resp = await client.DeleteAsync($"/api/panel/posts/{postId}");
    resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
    var fetched = await db2.BroadcastPosts.FirstAsync(p => p.Id == postId);
    fetched.DeletedAt.Should().NotBeNull();

    (await _factory.BroadcastMedia.HeadAsync(objectKey)).Should().BeNull();
}
```

- [ ] **Step 3: Commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~PanelBroadcastPosts" --nologo
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastPostsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastPostsControllerTests.cs
git commit -m "feat(posts): DELETE endpoint (soft delete + R2 cleanup)"
```

Beklenen: 23 test pass.

---

### Task 11: BroadcastPostCleanupJob + Hangfire registration

**Files:**
- Create: `OrderDeck.LicenseServer/Services/BroadcastPosts/BroadcastPostCleanupJob.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/BroadcastPostCleanupJobTests.cs`

- [ ] **Step 1: Job class**

`OrderDeck.LicenseServer/Services/BroadcastPosts/BroadcastPostCleanupJob.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

/// <summary>
/// Hangfire daily job: 30 gün geçmiş ve sabitlenmemiş post'ları soft-delete eder
/// + R2 medyalarını temizler. Pin'lenmiş post'lar Expires=9999 olduğu için
/// query'de hiç bulunmaz.
/// </summary>
public sealed class BroadcastPostCleanupJob
{
    private const int BatchSize = 500;

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;
    private readonly ILogger<BroadcastPostCleanupJob> _log;

    public BroadcastPostCleanupJob(
        LicenseDbContext db, IBroadcastMediaStorage storage,
        ILogger<BroadcastPostCleanupJob> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await _db.BroadcastPosts
            .Where(p => p.ExpiresAt < now && !p.IsPinned && p.DeletedAt == null)
            .OrderBy(p => p.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            _log.LogDebug("BroadcastPost cleanup: no expired rows");
            return;
        }

        foreach (var p in expired)
        {
            if (!string.IsNullOrWhiteSpace(p.MediaObjectKey))
            {
                try { await _storage.DeleteAsync(p.MediaObjectKey, ct); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cleanup: R2 delete failed for {Key}", p.MediaObjectKey);
                }
            }
            p.DeletedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("BroadcastPost cleanup: soft-deleted {Count} expired rows", expired.Count);
    }
}
```

- [ ] **Step 2: Job test**

`OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/BroadcastPostCleanupJobTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.BroadcastPosts;

public class BroadcastPostCleanupJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BroadcastPostCleanupJobTests(ApiFactory f) => _factory = f;

    private async Task<Guid> CreateLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(), Email = $"cu-{Guid.NewGuid():N}@t.com",
            Name = "X", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        var l = new License
        {
            Id = Guid.NewGuid(), CustomerId = c.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(l);
        await db.SaveChangesAsync();
        return l.Id;
    }

    [Fact]
    public async Task RunAsync_soft_deletes_expired_non_pinned_only()
    {
        var licenseId = await CreateLicenseAsync();

        Guid expiredId, freshId, pinnedExpiredId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            var expired = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Text, TextBody = "expired",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
                IsPinned = false
            };
            var fresh = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Text, TextBody = "fresh",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(29),
                IsPinned = false
            };
            var pinnedExpired = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Text, TextBody = "pinned but expired",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-50),
                IsPinned = true
            };
            db.BroadcastPosts.AddRange(expired, fresh, pinnedExpired);
            await db.SaveChangesAsync();
            expiredId = expired.Id; freshId = fresh.Id; pinnedExpiredId = pinnedExpired.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var storage = scope.ServiceProvider
                .GetRequiredService<IBroadcastMediaStorage>();
            var job = new BroadcastPostCleanupJob(db, storage,
                NullLogger<BroadcastPostCleanupJob>.Instance);
            await job.RunAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var expired = await db.BroadcastPosts.FirstAsync(p => p.Id == expiredId);
            var fresh = await db.BroadcastPosts.FirstAsync(p => p.Id == freshId);
            var pinnedExpired = await db.BroadcastPosts.FirstAsync(p => p.Id == pinnedExpiredId);

            expired.DeletedAt.Should().NotBeNull("expired non-pinned should be soft-deleted");
            fresh.DeletedAt.Should().BeNull("fresh should be untouched");
            pinnedExpired.DeletedAt.Should().BeNull("pinned should be untouched even if past expires");
        }
    }

    [Fact]
    public async Task RunAsync_calls_storage_delete_for_media_posts()
    {
        var licenseId = await CreateLicenseAsync();
        var objectKey = $"{licenseId}/cleanup/media.bin";
        _factory.BroadcastMedia.Seed(objectKey, 1024, "image/jpeg");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BroadcastPosts.Add(new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Photo,
                MediaObjectKey = objectKey, MediaContentType = "image/jpeg",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
                IsPinned = false
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var storage = scope.ServiceProvider
                .GetRequiredService<IBroadcastMediaStorage>();
            var job = new BroadcastPostCleanupJob(db, storage,
                NullLogger<BroadcastPostCleanupJob>.Instance);
            await job.RunAsync();
        }

        (await _factory.BroadcastMedia.HeadAsync(objectKey)).Should().BeNull();
    }
}
```

- [ ] **Step 3: Hangfire register**

`OrderDeck.LicenseServer/Program.cs` — `audit-retention` job kaydından sonra ekle:

```csharp
// Broadcast posts cleanup — 30 gün geçenleri soft-delete (Spec 2)
manager.AddOrUpdate<OrderDeck.LicenseServer.Services.BroadcastPosts.BroadcastPostCleanupJob>(
    "broadcast-posts-cleanup",
    j => j.RunAsync(CancellationToken.None),
    "0 3 * * *");  // 03:00 UTC daily
```

DI register de eklemen lazım, ServiceCollection register kısmına:

```csharp
builder.Services.AddScoped<OrderDeck.LicenseServer.Services.BroadcastPosts.BroadcastPostCleanupJob>();
```

(Backup retention job'ın yanına koy — pattern aynı.)

- [ ] **Step 4: Test + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~BroadcastPostCleanupJob" --nologo
git add OrderDeck.LicenseServer/Services/BroadcastPosts/BroadcastPostCleanupJob.cs \
        OrderDeck.LicenseServer.Tests/Services/BroadcastPosts/BroadcastPostCleanupJobTests.cs \
        OrderDeck.LicenseServer/Program.cs
git commit -m "feat(posts): daily cleanup job + Hangfire registration"
```

Beklenen: 2 yeni test pass.

---

### Task 12: R2 DI wiring + Stub registration + deploy doc

**Files:**
- Modify: `OrderDeck.LicenseServer/Program.cs`
- Create: `deploy/setup-r2.md`

- [ ] **Step 1: Program.cs storage provider wiring**

`Program.cs` içinde, push provider switch'inin yanına yeni bir blok ekle:

```csharp
// Broadcast media storage — provider seçimi (stub | r2)
// Provider: appsettings.json "OrderDeck:BroadcastMedia:Provider" = "stub" | "r2"
var bmProvider = builder.Configuration["OrderDeck:BroadcastMedia:Provider"] ?? "stub";
if (bmProvider.Equals("stub", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<
        OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage,
        OrderDeck.LicenseServer.Services.BroadcastPosts.StubBroadcastMediaStorage>();
}
else if (bmProvider.Equals("r2", StringComparison.OrdinalIgnoreCase))
{
    var r2Opt = new OrderDeck.LicenseServer.Services.BroadcastPosts.R2Options();
    builder.Configuration.GetSection("R2").Bind(r2Opt);
    builder.Services.AddSingleton(r2Opt);
    builder.Services.AddSingleton<
        OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage,
        OrderDeck.LicenseServer.Services.BroadcastPosts.R2BroadcastMediaStorage>();
}
else
{
    throw new InvalidOperationException(
        $"Unsupported broadcast media provider: {bmProvider}. Valid values: 'stub', 'r2'.");
}
```

- [ ] **Step 2: Build (regression check)**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj --nologo -v q
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --nologo
```

Beklenen: build temiz, tüm test'ler yeşil (ApiFactory zaten override etti, default `stub` provider'a kayar).

- [ ] **Step 3: Deploy doc**

`deploy/setup-r2.md`:

```markdown
# Cloudflare R2 — Broadcast Posts Media Storage

OrderDeck broadcast post media (foto/video) Cloudflare R2'de tutulur.
Mevcut FCM kurulumu gibi tek seferlik bir setup, sonra otomatik.

## 1. Cloudflare hesabı + R2

1. https://dash.cloudflare.com → R2 sekmesi → **Enable R2**
2. Bucket oluştur: **orderdeck-broadcast-posts** (region: auto, EU)
3. Bucket → **Settings → CORS**:
   ```json
   [{
     "AllowedOrigins": ["https://localhost", "capacitor://localhost",
                        "https://license.orderdeckapp.com"],
     "AllowedMethods": ["GET", "PUT", "HEAD"],
     "AllowedHeaders": ["*"],
     "ExposeHeaders": ["ETag"],
     "MaxAgeSeconds": 3600
   }]
   ```

## 2. API token

1. R2 → **Manage R2 API Tokens** → **Create API Token**
2. Permissions: **Object Read & Write**
3. Specify bucket: `orderdeck-broadcast-posts`
4. TTL: opsiyonel (boş bırak = no expiry)
5. **Create** → çıkan **Access Key ID** + **Secret Access Key**'i kopyala

## 3. Account ID

Cloudflare dashboard → sağ üst → Account ID kopyala.

## 4. VPS .env güncelle

```bash
ssh -i ~/.ssh/id_ed25519 root@72.62.53.86
nano /opt/orderdeck/.env
```

Aşağıdakini ekle:

```env
OrderDeck__BroadcastMedia__Provider=r2
R2__AccountId=<account-id>
R2__AccessKeyId=<access-key>
R2__SecretAccessKey=<secret>
R2__BucketName=orderdeck-broadcast-posts
```

## 5. docker-compose.yml environment

`/opt/orderdeck/docker-compose.yml` license-server service environment'ına ekle:

```yaml
OrderDeck__BroadcastMedia__Provider: "${OrderDeck__BroadcastMedia__Provider:-stub}"
R2__AccountId: "${R2__AccountId:-}"
R2__AccessKeyId: "${R2__AccessKeyId:-}"
R2__SecretAccessKey: "${R2__SecretAccessKey:-}"
R2__BucketName: "${R2__BucketName:-}"
```

## 6. Restart + verify

```bash
cd /opt/orderdeck
docker compose up -d license-server
docker compose logs -f license-server | grep -iE "broadcast|r2"
```

Boot başarılıysa log'da "BroadcastMedia provider=r2" görünür (yoksa eski log seviyesinde olabilir, INFO veya DEBUG'a çek).

## 7. Smoke

Mobile Panel'den text post → POST 201. Photo post için:
1. POST /api/panel/posts/upload-url → URL al
2. Direkt R2'ye PUT (mobile in-app)
3. POST /api/panel/posts media bilgisiyle → 201
4. Cloudflare dashboard → bucket → objeyi gör.
```

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Program.cs deploy/setup-r2.md
git commit -m "feat(posts): R2 provider DI wiring + deploy guide"
```

**Branch checkpoint**: Server tarafı bitti. Bu noktada `git push -u origin feat/broadcast-posts-server` + PR aç → review için bırak. Mobile ayrı branch'te devam.

---

### Task 13: Mobile API hooks

**Files (OrderDeck-Mobile repo):**
- Modify: `apps/panel/src/api/queries.ts`

- [ ] **Step 1: Mobile repo'ya geç + branch aç**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git checkout main
git pull origin main --ff-only
git checkout -b feat/broadcast-posts
```

- [ ] **Step 2: Hook'lar ve type'lar**

`apps/panel/src/api/queries.ts` sonuna ekle:

```typescript
// ─── Broadcast Posts (Spec 2, 2026-05-15) ─────────────────────────

export type BroadcastPostType = "text" | "photo" | "video";

export type BroadcastPost = {
  id: string;
  type: BroadcastPostType;
  textBody: string | null;
  mediaContentType: string | null;
  mediaWidth: number | null;
  mediaHeight: number | null;
  mediaDurationSec: number | null;
  createdAt: string;
  expiresAt: string;
  isPinned: boolean;
};

export type BroadcastListResponse = {
  posts: BroadcastPost[];
  nextCursor: string | null;
};

export type UploadUrlResponse = {
  uploadUrl: string;
  objectKey: string;
  expiresAt: string;
};

export type MediaUrlResponse = {
  url: string;
  expiresAt: string;
};

export function useBroadcastPosts() {
  return useQuery({
    queryKey: ["broadcast-posts"],
    queryFn: async () => {
      const resp = await apiClient.get<BroadcastListResponse>(
        "/api/panel/posts",
      );
      return resp.data;
    },
    staleTime: 15_000,
  });
}

export function useBroadcastPost(id: string | null) {
  return useQuery({
    queryKey: ["broadcast-post", id],
    queryFn: async () => {
      if (!id) return null;
      const resp = await apiClient.get<BroadcastPost>(`/api/panel/posts/${id}`);
      return resp.data;
    },
    enabled: !!id,
  });
}

export function useBroadcastMediaUrl(id: string | null, hasMedia: boolean) {
  return useQuery({
    queryKey: ["broadcast-media-url", id],
    queryFn: async () => {
      if (!id) return null;
      const resp = await apiClient.get<MediaUrlResponse>(
        `/api/panel/posts/${id}/media-url`,
      );
      return resp.data.url;
    },
    enabled: !!id && hasMedia,
    staleTime: 4 * 60_000,  // server 5 dk, ufak emniyet payı
  });
}

export function useCreateBroadcastPost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (args: {
      type: BroadcastPostType;
      textBody?: string;
      media?: {
        objectKey: string;
        contentType: string;
        sizeBytes: number;
        durationSec?: number;
        width: number;
        height: number;
      };
    }) => {
      const resp = await apiClient.post<BroadcastPost>("/api/panel/posts", args);
      return resp.data;
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["broadcast-posts"] });
    },
  });
}

export function useUpdateBroadcastPost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (args: { id: string; textBody: string }) => {
      const resp = await apiClient.put<BroadcastPost>(
        `/api/panel/posts/${args.id}`,
        { textBody: args.textBody },
      );
      return resp.data;
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["broadcast-posts"] });
    },
  });
}

export function usePinBroadcastPost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const resp = await apiClient.post<BroadcastPost>(
        `/api/panel/posts/${id}/pin`,
      );
      return resp.data;
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["broadcast-posts"] });
    },
  });
}

export function useUnpinBroadcastPost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const resp = await apiClient.delete<BroadcastPost>(
        `/api/panel/posts/${id}/pin`,
      );
      return resp.data;
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["broadcast-posts"] });
    },
  });
}

export function useDeleteBroadcastPost() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/api/panel/posts/${id}`);
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["broadcast-posts"] });
    },
  });
}

export async function requestBroadcastUploadUrl(args: {
  type: "photo" | "video";
  sizeBytes: number;
  contentType: string;
}): Promise<UploadUrlResponse> {
  const resp = await apiClient.post<UploadUrlResponse>(
    "/api/panel/posts/upload-url",
    args,
  );
  return resp.data;
}
```

- [ ] **Step 3: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/src/api/queries.ts
git commit -m "feat(panel): broadcast post API hooks + types"
```

Beklenen: typecheck temiz.

---

### Task 14: DuyurularScreen liste + route

**Files:**
- Create: `apps/panel/src/screens/DuyurularScreen.tsx`
- Modify: `apps/panel/src/App.tsx`
- Modify: `apps/panel/src/screens/DahaFazlaScreen.tsx`

- [ ] **Step 1: Liste ekranı**

`apps/panel/src/screens/DuyurularScreen.tsx`:

```typescript
import { Link } from "react-router-dom";
import {
  BroadcastPost,
  useBroadcastPosts,
  useBroadcastMediaUrl,
} from "../api/queries";
import { formatRelative } from "../lib/format";

export function DuyurularScreen() {
  const { data, isLoading, isError, refetch } = useBroadcastPosts();
  const posts = data?.posts ?? [];

  return (
    <main className="px-5 pt-6 pb-24">
      <header className="mb-4 flex justify-between items-center">
        <div>
          <Link to="/daha-fazla" className="text-text-muted text-xs hover:text-text">
            ← Geri
          </Link>
          <h1 className="text-2xl font-bold mt-1">Duyurular</h1>
          <p className="text-text-muted text-sm mt-0.5">{posts.length} duyuru</p>
        </div>
        <Link
          to="/duyurular/yeni"
          className="px-3 py-2 text-sm rounded-lg bg-accent hover:bg-accent-hover text-white font-medium"
        >
          + Yeni
        </Link>
      </header>

      {isLoading ? (
        <p className="text-center text-text-muted text-sm py-12">Yükleniyor...</p>
      ) : isError ? (
        <div className="bg-danger/10 border border-danger/30 rounded-xl p-4 text-danger text-sm">
          Yüklenemedi.
          <button
            onClick={() => void refetch()}
            className="ml-2 underline"
          >
            Tekrar dene
          </button>
        </div>
      ) : posts.length === 0 ? (
        <div className="text-center py-16">
          <p className="text-5xl mb-3">📣</p>
          <p className="text-text font-medium">Henüz duyuru yok</p>
          <p className="text-text-muted text-xs mt-1">
            Müşterilerine bir şeyler söylemek için "+ Yeni" butonuna bas.
          </p>
        </div>
      ) : (
        <ul className="space-y-2">
          {posts.map((p) => (
            <PostRow key={p.id} post={p} />
          ))}
        </ul>
      )}
    </main>
  );
}

function PostRow({ post }: { post: BroadcastPost }) {
  const hasMedia = post.type !== "text";
  const { data: mediaUrl } = useBroadcastMediaUrl(post.id, hasMedia);

  return (
    <li>
      <Link
        to={`/duyurular/${post.id}`}
        className="block bg-bg-surface rounded-xl border border-bg-elevated p-3 hover:bg-bg-elevated transition-colors"
      >
        <div className="flex gap-3">
          {hasMedia && mediaUrl ? (
            <img
              src={mediaUrl}
              alt=""
              className="w-16 h-16 rounded-lg object-cover bg-bg-elevated"
            />
          ) : hasMedia ? (
            <div className="w-16 h-16 rounded-lg bg-bg-elevated animate-pulse" />
          ) : null}
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 mb-1">
              {post.isPinned && (
                <span className="text-warning text-xs">📌</span>
              )}
              <TypeBadge type={post.type} />
              <span className="text-text-muted text-[10px] ml-auto">
                {formatRelative(post.createdAt)}
              </span>
            </div>
            <p className="text-text text-sm line-clamp-2">
              {post.textBody ?? "(metin yok)"}
            </p>
          </div>
        </div>
      </Link>
    </li>
  );
}

function TypeBadge({ type }: { type: BroadcastPost["type"] }) {
  const label = type === "text" ? "Metin" : type === "photo" ? "Foto" : "Video";
  return (
    <span className="px-1.5 py-0.5 rounded text-[10px] font-medium bg-bg-elevated text-text-muted">
      {label}
    </span>
  );
}
```

- [ ] **Step 2: Route + DahaFazla navrow**

`apps/panel/src/App.tsx` import block'una ekle:

```typescript
import { DuyurularScreen } from "./screens/DuyurularScreen";
```

router children içine ekle (Ekip'in yanına):

```typescript
{ path: "/duyurular", element: <DuyurularScreen /> },
```

`apps/panel/src/screens/DahaFazlaScreen.tsx` — "Hesap" section'ından önce yeni bölüm:

```typescript
<p className="text-text-muted text-xs uppercase tracking-wider px-1 pt-4">
  İçerik
</p>

<NavRow
  to="/duyurular"
  label="Duyurular"
  hint="Müşterilere foto, video veya mesaj paylaş"
/>
```

- [ ] **Step 3: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/src/screens/DuyurularScreen.tsx \
        apps/panel/src/App.tsx \
        apps/panel/src/screens/DahaFazlaScreen.tsx
git commit -m "feat(panel): DuyurularScreen liste + route + DahaFazla link"
```

---

### Task 15: YeniDuyuruScreen text mode + upload helper

**Files:**
- Create: `apps/panel/src/lib/broadcastUpload.ts`
- Create: `apps/panel/src/screens/YeniDuyuruScreen.tsx`
- Modify: `apps/panel/src/App.tsx`

- [ ] **Step 1: Upload helper**

`apps/panel/src/lib/broadcastUpload.ts`:

```typescript
import { requestBroadcastUploadUrl } from "../api/queries";

/**
 * Pre-signed URL al + direkt R2'ye PUT et.
 * Server'ın CreatePost endpoint'ine eklenecek objectKey + meta döner.
 */
export async function uploadBroadcastMedia(args: {
  type: "photo" | "video";
  blob: Blob;
  contentType: string;
}): Promise<{ objectKey: string; sizeBytes: number }> {
  const sizeBytes = args.blob.size;

  const { uploadUrl, objectKey } = await requestBroadcastUploadUrl({
    type: args.type,
    sizeBytes,
    contentType: args.contentType,
  });

  const putResp = await fetch(uploadUrl, {
    method: "PUT",
    headers: { "Content-Type": args.contentType },
    body: args.blob,
  });
  if (!putResp.ok) {
    throw new Error(`Upload failed: ${putResp.status} ${putResp.statusText}`);
  }

  return { objectKey, sizeBytes };
}
```

- [ ] **Step 2: YeniDuyuruScreen — sadece text mode tab + form**

`apps/panel/src/screens/YeniDuyuruScreen.tsx`:

```typescript
import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useCreateBroadcastPost } from "../api/queries";

type Tab = "text" | "photo" | "video";

const MAX_TEXT = 2000;

export function YeniDuyuruScreen() {
  const nav = useNavigate();
  const [tab, setTab] = useState<Tab>("text");
  const [text, setText] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const create = useCreateBroadcastPost();

  async function submit() {
    setError(null);
    if (tab === "text") {
      if (!text.trim()) {
        setError("Metin boş olamaz.");
        return;
      }
      setSubmitting(true);
      try {
        await create.mutateAsync({ type: "text", textBody: text.trim() });
        nav("/duyurular");
      } catch (err) {
        setError("Gönderilemedi: " + ((err as Error).message ?? "bilinmeyen hata"));
      } finally {
        setSubmitting(false);
      }
    } else {
      // Photo/video mode Task 16/17'de implement edilir.
      setError("Foto/video desteği henüz hazır değil.");
    }
  }

  return (
    <main className="px-5 pt-6 pb-12">
      <header className="mb-4">
        <Link to="/duyurular" className="text-text-muted text-xs hover:text-text">
          ← Geri
        </Link>
        <h1 className="text-2xl font-bold mt-1">Yeni Duyuru</h1>
      </header>

      {/* Tip seçici tabs */}
      <div className="flex gap-2 mb-4">
        {(["text", "photo", "video"] as Tab[]).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`flex-1 py-2 rounded-lg text-sm font-medium ${
              tab === t
                ? "bg-accent text-white"
                : "bg-bg-surface text-text-muted border border-bg-elevated"
            }`}
          >
            {t === "text" ? "Metin" : t === "photo" ? "Foto" : "Video"}
          </button>
        ))}
      </div>

      {tab === "text" && (
        <div>
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value.slice(0, MAX_TEXT))}
            placeholder="Müşterilerine söylemek istediğin..."
            rows={8}
            className="w-full px-4 py-3 rounded-xl bg-bg-surface border border-bg-elevated focus:border-accent focus:outline-none text-sm"
          />
          <p className="text-text-muted text-xs text-right mt-1">
            {text.length} / {MAX_TEXT}
          </p>
        </div>
      )}

      {tab !== "text" && (
        <div className="bg-bg-surface border border-bg-elevated rounded-xl p-8 text-center text-text-muted">
          Foto/video yükleme yakında
        </div>
      )}

      {error && (
        <p className="text-danger text-sm mt-3">{error}</p>
      )}

      <button
        onClick={() => void submit()}
        disabled={submitting || (tab === "text" && !text.trim())}
        className="w-full mt-6 py-3 rounded-xl bg-accent text-white font-semibold disabled:opacity-50"
      >
        {submitting ? "Yayınlanıyor..." : "Yayınla"}
      </button>
    </main>
  );
}
```

- [ ] **Step 3: Route ekle**

`apps/panel/src/App.tsx` import:

```typescript
import { YeniDuyuruScreen } from "./screens/YeniDuyuruScreen";
```

router children:

```typescript
{ path: "/duyurular/yeni", element: <YeniDuyuruScreen /> },
```

- [ ] **Step 4: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/src/lib/broadcastUpload.ts \
        apps/panel/src/screens/YeniDuyuruScreen.tsx \
        apps/panel/src/App.tsx
git commit -m "feat(panel): YeniDuyuruScreen text mode + upload helper"
```

---

### Task 16: Photo mode + Capacitor Camera plugin

**Files:**
- Modify: `apps/panel/package.json`
- Modify: `apps/panel/src/screens/YeniDuyuruScreen.tsx`

- [ ] **Step 1: @capacitor/camera plugin ekle**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile/apps/panel
npm install @capacitor/camera
npx cap sync android
```

Beklenen: package.json'a `@capacitor/camera: ^6.x` eklenir, android sync OK.

- [ ] **Step 2: AndroidManifest izinleri**

`apps/panel/android/app/src/main/AndroidManifest.xml` `<manifest>` etiketi içine ekle (yoksa):

```xml
<uses-permission android:name="android.permission.CAMERA" />
<uses-permission android:name="android.permission.READ_MEDIA_IMAGES" />
<uses-permission android:name="android.permission.READ_MEDIA_VIDEO" />
```

- [ ] **Step 3: YeniDuyuruScreen photo mode genişletmesi**

`apps/panel/src/screens/YeniDuyuruScreen.tsx` — üste import ekle:

```typescript
import { Camera, CameraResultType, CameraSource } from "@capacitor/camera";
import { uploadBroadcastMedia } from "../lib/broadcastUpload";
```

State'lere ekle (mevcut `useState` block'larının yanına):

```typescript
const [photoDataUrl, setPhotoDataUrl] = useState<string | null>(null);
const [photoBlob, setPhotoBlob] = useState<Blob | null>(null);
const [photoDimensions, setPhotoDimensions] = useState<{ width: number; height: number } | null>(null);
```

Yeni handler — component içine ekle:

```typescript
async function pickPhoto() {
  setError(null);
  try {
    const photo = await Camera.getPhoto({
      resultType: CameraResultType.DataUrl,
      source: CameraSource.Prompt,  // user seç: çek veya galeriden
      quality: 85,
      width: 2048,
      allowEditing: false,
    });
    if (!photo.dataUrl) return;

    // dataUrl → blob
    const fetchResp = await fetch(photo.dataUrl);
    const blob = await fetchResp.blob();

    // 10 MB limit (server'la aynı)
    if (blob.size > 10 * 1024 * 1024) {
      setError("Foto 10 MB'tan büyük. Daha küçük çek/seç.");
      return;
    }

    // Dimensions hesapla
    const img = new Image();
    await new Promise<void>((resolve, reject) => {
      img.onload = () => resolve();
      img.onerror = () => reject(new Error("Image decode failed"));
      img.src = photo.dataUrl!;
    });

    setPhotoDataUrl(photo.dataUrl);
    setPhotoBlob(blob);
    setPhotoDimensions({ width: img.width, height: img.height });
  } catch (err) {
    setError("Foto seçilemedi: " + ((err as Error).message ?? "iptal edildi"));
  }
}
```

Submit fonksiyonunu güncelle — `tab === "photo"` branch'i ekle:

```typescript
if (tab === "photo") {
  if (!photoBlob || !photoDimensions) {
    setError("Foto seç önce.");
    return;
  }
  setSubmitting(true);
  try {
    const { objectKey, sizeBytes } = await uploadBroadcastMedia({
      type: "photo",
      blob: photoBlob,
      contentType: "image/jpeg",
    });
    await create.mutateAsync({
      type: "photo",
      textBody: text.trim() || undefined,
      media: {
        objectKey,
        contentType: "image/jpeg",
        sizeBytes,
        width: photoDimensions.width,
        height: photoDimensions.height,
      },
    });
    nav("/duyurular");
  } catch (err) {
    setError("Gönderilemedi: " + ((err as Error).message ?? ""));
  } finally {
    setSubmitting(false);
  }
  return;
}
```

Photo tab UI'ı güncelle (`tab !== "text"` branch'ini ikiye böl):

```typescript
{tab === "photo" && (
  <div className="space-y-3">
    {photoDataUrl ? (
      <div className="relative">
        <img
          src={photoDataUrl}
          alt=""
          className="w-full rounded-xl"
        />
        <button
          onClick={() => {
            setPhotoDataUrl(null);
            setPhotoBlob(null);
            setPhotoDimensions(null);
          }}
          className="absolute top-2 right-2 px-2 py-1 rounded-lg bg-bg/80 text-xs text-text"
        >
          Değiştir
        </button>
      </div>
    ) : (
      <button
        onClick={() => void pickPhoto()}
        className="w-full py-12 rounded-xl bg-bg-surface border-2 border-dashed border-bg-elevated text-text-muted"
      >
        📷 Foto seç veya çek
      </button>
    )}
    <textarea
      value={text}
      onChange={(e) => setText(e.target.value.slice(0, MAX_TEXT))}
      placeholder="Açıklama (opsiyonel)"
      rows={3}
      className="w-full px-4 py-3 rounded-xl bg-bg-surface border border-bg-elevated focus:border-accent focus:outline-none text-sm"
    />
  </div>
)}

{tab === "video" && (
  <div className="bg-bg-surface border border-bg-elevated rounded-xl p-8 text-center text-text-muted">
    Video yükleme yakında
  </div>
)}
```

(`tab !== "text"` placeholder'ı sil — yerine yukarıdaki iki branch geldi.)

- [ ] **Step 4: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/package.json apps/panel/package-lock.json \
        apps/panel/android/app/src/main/AndroidManifest.xml \
        apps/panel/src/screens/YeniDuyuruScreen.tsx
git commit -m "feat(panel): YeniDuyuruScreen photo mode + @capacitor/camera"
```

---

### Task 17: Video mode

**Files:**
- Modify: `apps/panel/src/screens/YeniDuyuruScreen.tsx`

Web'de native video chooser için `<input type="file" accept="video/*">` kullanırız. Capacitor 6 Camera plugin'i video çekmeyi resmi desteklemiyor (sadece foto), ama galeriden seçim için `<input>` hem web hem native webview'da çalışır.

- [ ] **Step 1: State ekle**

YeniDuyuruScreen'in state block'una:

```typescript
const [videoBlob, setVideoBlob] = useState<Blob | null>(null);
const [videoMeta, setVideoMeta] = useState<{
  width: number;
  height: number;
  durationSec: number;
  contentType: string;
} | null>(null);
const [videoPreviewUrl, setVideoPreviewUrl] = useState<string | null>(null);
```

- [ ] **Step 2: Video pick handler**

```typescript
async function pickVideo(file: File) {
  setError(null);

  if (file.size > 60 * 1024 * 1024) {
    setError("Video 60 MB'tan büyük. Kısalt veya kalitesini düşür.");
    return;
  }

  const validTypes = ["video/mp4", "video/quicktime", "video/x-m4v"];
  if (!validTypes.includes(file.type)) {
    setError("Sadece MP4 ve MOV destekleniyor.");
    return;
  }

  // Video metadata yükle (duration, dimensions)
  const url = URL.createObjectURL(file);
  const video = document.createElement("video");
  await new Promise<void>((resolve, reject) => {
    video.preload = "metadata";
    video.onloadedmetadata = () => resolve();
    video.onerror = () => reject(new Error("Video decode failed"));
    video.src = url;
  });

  const durationSec = Math.ceil(video.duration);
  if (durationSec > 45) {
    URL.revokeObjectURL(url);
    setError(`Video ${durationSec} sn — max 45 sn. Kısalt.`);
    return;
  }

  setVideoBlob(file);
  setVideoMeta({
    width: video.videoWidth,
    height: video.videoHeight,
    durationSec,
    contentType: file.type,
  });
  setVideoPreviewUrl(url);
}
```

- [ ] **Step 3: Submit branch'i**

`submit()` içine `tab === "video"` ekle:

```typescript
if (tab === "video") {
  if (!videoBlob || !videoMeta) {
    setError("Video seç önce.");
    return;
  }
  setSubmitting(true);
  try {
    const { objectKey, sizeBytes } = await uploadBroadcastMedia({
      type: "video",
      blob: videoBlob,
      contentType: videoMeta.contentType,
    });
    await create.mutateAsync({
      type: "video",
      textBody: text.trim() || undefined,
      media: {
        objectKey,
        contentType: videoMeta.contentType,
        sizeBytes,
        durationSec: videoMeta.durationSec,
        width: videoMeta.width,
        height: videoMeta.height,
      },
    });
    nav("/duyurular");
  } catch (err) {
    setError("Gönderilemedi: " + ((err as Error).message ?? ""));
  } finally {
    setSubmitting(false);
  }
  return;
}
```

- [ ] **Step 4: Video tab UI**

`tab === "video"` placeholder'ını şununla değiştir:

```typescript
{tab === "video" && (
  <div className="space-y-3">
    {videoPreviewUrl ? (
      <div className="relative">
        <video
          src={videoPreviewUrl}
          controls
          className="w-full rounded-xl bg-black"
        />
        <button
          onClick={() => {
            if (videoPreviewUrl) URL.revokeObjectURL(videoPreviewUrl);
            setVideoBlob(null);
            setVideoMeta(null);
            setVideoPreviewUrl(null);
          }}
          className="absolute top-2 right-2 px-2 py-1 rounded-lg bg-bg/80 text-xs text-text"
        >
          Değiştir
        </button>
        {videoMeta && (
          <p className="text-text-muted text-xs mt-1">
            {videoMeta.durationSec} sn · {videoMeta.width}×{videoMeta.height}
          </p>
        )}
      </div>
    ) : (
      <label className="block w-full py-12 rounded-xl bg-bg-surface border-2 border-dashed border-bg-elevated text-center text-text-muted cursor-pointer">
        🎬 Video seç (MP4/MOV, max 45 sn, 60 MB)
        <input
          type="file"
          accept="video/mp4,video/quicktime,video/x-m4v"
          onChange={(e) => {
            const f = e.target.files?.[0];
            if (f) void pickVideo(f);
          }}
          className="hidden"
        />
      </label>
    )}
    <textarea
      value={text}
      onChange={(e) => setText(e.target.value.slice(0, MAX_TEXT))}
      placeholder="Açıklama (opsiyonel)"
      rows={3}
      className="w-full px-4 py-3 rounded-xl bg-bg-surface border border-bg-elevated focus:border-accent focus:outline-none text-sm"
    />
  </div>
)}
```

- [ ] **Step 5: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/src/screens/YeniDuyuruScreen.tsx
git commit -m "feat(panel): YeniDuyuruScreen video mode + duration/size validation"
```

---

### Task 18: DuyuruDetayScreen + actions

**Files:**
- Create: `apps/panel/src/screens/DuyuruDetayScreen.tsx`
- Modify: `apps/panel/src/App.tsx`

- [ ] **Step 1: Detay ekranı**

`apps/panel/src/screens/DuyuruDetayScreen.tsx`:

```typescript
import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  useBroadcastPost,
  useBroadcastMediaUrl,
  useDeleteBroadcastPost,
  usePinBroadcastPost,
  useUnpinBroadcastPost,
  useUpdateBroadcastPost,
} from "../api/queries";
import { formatRelative } from "../lib/format";

export function DuyuruDetayScreen() {
  const { id } = useParams<{ id: string }>();
  const nav = useNavigate();
  const { data: post, isLoading } = useBroadcastPost(id ?? null);
  const hasMedia = post?.type !== "text";
  const { data: mediaUrl } = useBroadcastMediaUrl(id ?? null, hasMedia);

  const [editing, setEditing] = useState(false);
  const [editText, setEditText] = useState("");

  const update = useUpdateBroadcastPost();
  const pin = usePinBroadcastPost();
  const unpin = useUnpinBroadcastPost();
  const del = useDeleteBroadcastPost();

  if (isLoading) {
    return (
      <main className="px-5 pt-6">
        <p className="text-text-muted text-sm py-12 text-center">Yükleniyor...</p>
      </main>
    );
  }

  if (!post) {
    return (
      <main className="px-5 pt-6">
        <Link to="/duyurular" className="text-text-muted text-xs">← Geri</Link>
        <p className="text-text-muted text-sm py-12 text-center">Bulunamadı.</p>
      </main>
    );
  }

  async function startEdit() {
    setEditText(post!.textBody ?? "");
    setEditing(true);
  }

  async function saveEdit() {
    await update.mutateAsync({ id: post!.id, textBody: editText.trim() });
    setEditing(false);
  }

  async function togglePin() {
    if (post!.isPinned) await unpin.mutateAsync(post!.id);
    else await pin.mutateAsync(post!.id);
  }

  async function confirmDelete() {
    if (!confirm("Bu duyuruyu sil?")) return;
    await del.mutateAsync(post!.id);
    nav("/duyurular");
  }

  return (
    <main className="px-5 pt-6 pb-12">
      <header className="mb-4">
        <Link to="/duyurular" className="text-text-muted text-xs hover:text-text">
          ← Geri
        </Link>
      </header>

      <p className="text-text-muted text-xs mb-2">
        {post.isPinned && "📌 Sabitlenmiş · "}
        {formatRelative(post.createdAt)}
      </p>

      {hasMedia && mediaUrl && (
        post.type === "photo" ? (
          <img src={mediaUrl} alt="" className="w-full rounded-xl mb-3" />
        ) : (
          <video src={mediaUrl} controls className="w-full rounded-xl mb-3 bg-black" />
        )
      )}

      {editing ? (
        <div className="space-y-2">
          <textarea
            value={editText}
            onChange={(e) => setEditText(e.target.value.slice(0, 2000))}
            rows={6}
            className="w-full px-4 py-3 rounded-xl bg-bg-surface border border-bg-elevated text-sm"
          />
          <div className="flex gap-2">
            <button
              onClick={() => setEditing(false)}
              className="flex-1 py-2 rounded-lg bg-bg-elevated text-text-muted text-sm"
            >
              İptal
            </button>
            <button
              onClick={() => void saveEdit()}
              disabled={update.isPending}
              className="flex-1 py-2 rounded-lg bg-accent text-white text-sm font-medium disabled:opacity-50"
            >
              Kaydet
            </button>
          </div>
        </div>
      ) : (
        <p className="text-text whitespace-pre-wrap mb-4">
          {post.textBody ?? "(metin yok)"}
        </p>
      )}

      <p className="text-text-muted text-xs italic mb-4">
        Müşterilere bu şekilde görünür.
      </p>

      {!editing && (
        <div className="flex flex-col gap-2">
          <button
            onClick={() => void startEdit()}
            className="w-full py-2 rounded-lg bg-bg-surface border border-bg-elevated text-sm"
          >
            Düzenle
          </button>
          <button
            onClick={() => void togglePin()}
            disabled={pin.isPending || unpin.isPending}
            className="w-full py-2 rounded-lg bg-bg-surface border border-bg-elevated text-sm disabled:opacity-50"
          >
            {post.isPinned ? "📌 Sabiti Kaldır" : "📌 Sabitle"}
          </button>
          <button
            onClick={() => void confirmDelete()}
            disabled={del.isPending}
            className="w-full py-2 rounded-lg bg-danger/15 text-danger border border-danger/40 text-sm disabled:opacity-50"
          >
            Sil
          </button>
        </div>
      )}
    </main>
  );
}
```

- [ ] **Step 2: Route**

`apps/panel/src/App.tsx`:

```typescript
import { DuyuruDetayScreen } from "./screens/DuyuruDetayScreen";
```

```typescript
{ path: "/duyurular/:id", element: <DuyuruDetayScreen /> },
```

- [ ] **Step 3: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/src/screens/DuyuruDetayScreen.tsx apps/panel/src/App.tsx
git commit -m "feat(panel): DuyuruDetayScreen + actions (edit/pin/delete)"
```

---

### Task 19: Ana ekran teaser kartı

**Files:**
- Modify: `apps/panel/src/screens/AnaScreen.tsx`

- [ ] **Step 1: AnaScreen'i incele + teaser kart ekle**

`apps/panel/src/screens/AnaScreen.tsx` — başta `useBroadcastPosts` ve `useBroadcastMediaUrl` import et:

```typescript
import { useBroadcastPosts, useBroadcastMediaUrl } from "../api/queries";
```

Component içinde mevcut "Aktif yayın" / "Bekleyen dekont" kartlarının yanına ekle (kartların hemen altına):

```typescript
const { data: postsData } = useBroadcastPosts();
const latestPost = postsData?.posts?.[0];
const hasMedia = latestPost?.type !== "text";
const { data: latestMediaUrl } = useBroadcastMediaUrl(
  latestPost?.id ?? null,
  !!latestPost && hasMedia,
);
```

JSX'te (mevcut kartların yanına yerleştir):

```typescript
{latestPost && (
  <Link
    to="/duyurular"
    className="block bg-bg-surface rounded-2xl border border-bg-elevated p-4 hover:bg-bg-elevated transition-colors"
  >
    <p className="text-text-muted text-xs uppercase tracking-wider mb-2">
      Son duyuru
    </p>
    <div className="flex gap-3">
      {hasMedia && latestMediaUrl ? (
        <img
          src={latestMediaUrl}
          alt=""
          className="w-14 h-14 rounded-lg object-cover bg-bg-elevated"
        />
      ) : null}
      <div className="min-w-0 flex-1">
        <p className="text-text text-sm line-clamp-2">
          {latestPost.textBody ?? "(medya)"}
        </p>
        <p className="text-text-muted text-xs mt-1">
          Tümünü gör →
        </p>
      </div>
    </div>
  </Link>
)}
```

- [ ] **Step 2: Typecheck + commit**

```bash
cd apps/panel
npm run typecheck
cd ../..
git add apps/panel/src/screens/AnaScreen.tsx
git commit -m "feat(panel): AnaScreen 'Son duyuru' teaser kart"
```

---

### Task 20: PR'lar + manuel R2 smoke checklist

**Files:** PR description'ları + smoke test çıktıları (manuel).

- [ ] **Step 1: Server PR aç (LiveDeck)**

```bash
cd C:/Users/burak/source/repos/LiveDeck
git push -u origin feat/broadcast-posts-server
gh pr create --title "feat(posts): broadcast posts server (Spec 2)" \
  --body "$(cat <<'EOF'
## Summary

Spec 2 (LiveDeck PR #62) implementation. Yayıncı tarafı broadcast post:
foto/video/text duyuru oluştur, sabitle, sil, 30 gün auto-delete.

- BroadcastPost entity + EF migration
- IBroadcastMediaStorage abstraction (Stub + R2)
- PanelBroadcastPostsController (8 endpoint)
- BroadcastPostCleanupJob (Hangfire daily)
- deploy/setup-r2.md
- 23 endpoint testi + 4 stub test + 2 cleanup test + 1 migration test

## Test plan
- [x] dotnet test temiz
- [x] Build temiz
- [ ] Manuel: deploy/setup-r2.md uygulanıp R2 bucket'a gerçek upload
EOF
)"
```

- [ ] **Step 2: Mobile PR aç (OrderDeck-Mobile)**

```bash
cd C:/Users/burak/source/repos/OrderDeck-Mobile
git push -u origin feat/broadcast-posts
gh pr create --title "feat(panel): broadcast posts UI (Spec 2)" \
  --body "$(cat <<'EOF'
## Summary

LiveDeck server PR ile birlikte mobile compose + listeleme.

- DuyurularScreen (liste, pinned önce)
- YeniDuyuruScreen (text + photo + video, client-side validation)
- DuyuruDetayScreen (edit caption, pin/unpin, sil)
- AnaScreen teaser
- DahaFazla → Duyurular navrow
- @capacitor/camera + AndroidManifest izinleri
- broadcastUpload.ts helper (pre-signed PUT)

## Test plan
- [x] npm run typecheck temiz
- [ ] Manuel: emulator'da text post → liste'de görünür
- [ ] Manuel: photo seç → R2'ye yüklenir → liste'de thumbnail görünür
- [ ] Manuel: video seç (≤45 sn) → upload + post
- [ ] Manuel: pin/unpin + edit caption + sil round-trip
EOF
)"
```

- [ ] **Step 3: VPS R2 smoke checklist (server PR merge sonrası)**

Sen yapacaklar (deploy/setup-r2.md adımları):
1. Cloudflare R2 hesabı + bucket
2. API token
3. .env güncelle
4. docker-compose pull + restart
5. Log'da provider=r2 görmek
6. Mobile'dan text post → 201
7. Mobile'dan photo post → bucket'ta obje + liste'de thumbnail
8. 30 gün test: bir post'un ExpiresAt'ini manuel geriye al (SQL UPDATE), Hangfire job dashboard'undan tetikle (`/hangfire`) → soft-delete edildi mi?

- [ ] **Step 4: PR'lar merge edilince Spec 1 (müşteri app) için sıra**

Müşteri app implementation'a geçince bu altyapı `/api/customer/feed` endpoint'i eklenerek tüketilir. Bu plan kapsam dışında.

---

## Self-review

**Spec coverage:**

| Spec madde | Plan task |
|------------|-----------|
| BroadcastPost entity | Task 1 |
| R2 storage abstraction | Task 2-3 |
| Pre-signed upload flow | Task 4 (upload-url) + Task 16-17 (mobile PUT) |
| 8 endpoint | Task 4 (upload-url), 5 (create), 6 (list+detail), 7 (media-url), 8 (PUT), 9 (pin), 10 (DELETE) — toplam 8 ✓ |
| Auto-delete job | Task 11 |
| Mobile compose UI (text/photo/video) | Task 15, 16, 17 |
| Pin/unpin UI | Task 18 (action buttons) |
| Liste ekranı | Task 14 |
| Ana ekran teaser | Task 19 |
| DahaFazla navrow | Task 14 (combined) |
| Tenant izolasyonu | Tüm endpoint testleri cross-tenant 404 doğruluyor |
| 30 gün ExpiresAt + pin → 9999 | Task 5 (create), 9 (pin), 11 (cleanup) |
| Max 5 pinned | Task 9 |
| Soft delete | Task 10 + 11 |
| Cloudflare R2 setup | Task 12 (deploy doc) |
| WPF değişiklik yok | ✓ (hiçbir task WPF'e dokunmaz) |
| Multi-media yok | ✓ (entity tek media slot) |
| Public erişim yok | ✓ (tüm endpoint Bearer-Customer) |

Boşluk yok.

**Type consistency check:**
- `BroadcastPostType` enum hem entity hem DTO'da aynı string ("text"/"photo"/"video") — ✓
- `objectKey` formatı: `{licenseId}/{postId}/media.bin` — Task 4 (upload-url) ve Task 5 (create validation startsWith) aynı — ✓
- `MaxPhotoBytes` / `MaxVideoBytes` Task 4'te tanımlı, Task 5 kullanmıyor (sadece duration kontrol var) — ✓ (size kontrol upload-url'de yapılır, create'te tekrar gerekmiyor)
- Mobile DTO field isimleri (objectKey, contentType, sizeBytes vs.) server DTO ile birebir aynı — ✓

**Placeholder check:**
- Hiç "TODO", "TBD", "implement later" yok ✓
- Tüm step'lerde kod gösterildi, "appropriate validation" gibi vague step yok ✓

---

## Execution Handoff

**"Plan complete and saved to `docs/superpowers/plans/2026-05-15-broadcast-posts.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — Her task için fresh subagent dispatch, task'lar arasında review, hızlı iteration.

**2. Inline Execution** — Bu session'da `executing-plans` ile batch çalıştır, checkpoint'lerde review.

**Which approach?"**

