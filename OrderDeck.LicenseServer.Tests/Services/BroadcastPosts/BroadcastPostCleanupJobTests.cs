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
