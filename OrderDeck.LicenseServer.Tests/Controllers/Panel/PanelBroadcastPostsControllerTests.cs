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

    private static async Task<string?> ReadTitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

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
        _factory.BroadcastMedia.UploadCalls
            .Should().Contain(c => c.Key.StartsWith($"{licenseId}/") && c.ContentType == "image/jpeg" && c.Size == 500_000);
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

    [Fact]
    public async Task Create_text_post_succeeds()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts",
            new { type = "text", textBody = "Hello world" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().Should().Be("text");
        var createdAt = doc.RootElement.GetProperty("createdAt").GetDateTimeOffset();
        var expiresAt = doc.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        expiresAt.Should().BeAfter(createdAt);
    }

    [Fact]
    public async Task Create_text_post_400_when_body_empty()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/posts",
            new { type = "text", textBody = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadTitleAsync(resp)).Should().Be("text-required");
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
        (await ReadTitleAsync(resp)).Should().Be("media-not-uploaded");
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
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"type\":\"photo\"");
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
        (await ReadTitleAsync(resp)).Should().Be("video-duration-out-of-range");
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
        (await ReadTitleAsync(resp)).Should().Be("invalid-object-key");
    }

    [Fact]
    public async Task Create_photo_post_400_when_head_content_type_mismatches_allowlist()
    {
        var (client, licenseId) = await SeedAsync();
        var objectKey = $"{licenseId}/gif-post/media.bin";
        _factory.BroadcastMedia.Seed(objectKey, 1024, "image/gif");

        var resp = await client.PostAsJsonAsync("/api/panel/posts",
            new
            {
                type = "photo",
                textBody = (string?)null,
                media = new
                {
                    objectKey, contentType = "image/jpeg", sizeBytes = 1024L,
                    durationSec = (int?)null, width = 100, height = 100
                }
            });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadTitleAsync(resp)).Should().Be("invalid-content-type");
    }

    [Fact]
    public async Task List_returns_empty_for_new_customer()
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

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(postId);
        doc.RootElement.GetProperty("textBody").GetString().Should().Be("x");
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

    [Fact]
    public async Task List_pagination_does_not_duplicate_pinned()
    {
        var (client, licenseId) = await SeedAsync();

        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            // 2 pinned (older CreatedAt) + 5 unpinned (newer CreatedAt) — limit=3 means
            // page 1 returns [pinned1, pinned2, unpinned-newest], page 2 must NOT show
            // the pinned posts again (the cursor-filter bug would re-include them).
            for (int i = 0; i < 2; i++)
            {
                db.BroadcastPosts.Add(new BroadcastPost
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId,
                    Type = BroadcastPostType.Text, TextBody = $"pinned-{i}",
                    CreatedAt = now.AddDays(-10 - i),
                    ExpiresAt = DateTimeOffset.MaxValue,
                    IsPinned = true
                });
            }
            for (int i = 0; i < 5; i++)
            {
                db.BroadcastPosts.Add(new BroadcastPost
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId,
                    Type = BroadcastPostType.Text, TextBody = $"unpinned-{i}",
                    CreatedAt = now.AddDays(-i),
                    ExpiresAt = now.AddDays(20 - i),
                    IsPinned = false
                });
            }
            await db.SaveChangesAsync();
        }

        var resp1 = await client.GetAsync("/api/panel/posts?limit=3");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var body1 = await resp1.Content.ReadAsStringAsync();
        using var doc1 = System.Text.Json.JsonDocument.Parse(body1);
        var cursor = doc1.RootElement.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var resp2 = await client.GetAsync($"/api/panel/posts?limit=3&cursor={Uri.EscapeDataString(cursor!)}");
        var body2 = await resp2.Content.ReadAsStringAsync();
        using var doc2 = System.Text.Json.JsonDocument.Parse(body2);
        // Parse JSON instead of substring-match (textBody "pinned-N" collides
        // with "unpinned-N" as a substring). Any pinned=true on page 2 = bug.
        foreach (var post in doc2.RootElement.GetProperty("posts").EnumerateArray())
        {
            post.GetProperty("isPinned").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task List_pagination_tie_breaker_on_same_timestamp()
    {
        var (client, licenseId) = await SeedAsync();

        var sharedTime = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            for (int i = 0; i < 3; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.BroadcastPosts.Add(new BroadcastPost
                {
                    Id = id, LicenseId = licenseId,
                    Type = BroadcastPostType.Text, TextBody = $"same-tick-{i}",
                    CreatedAt = sharedTime,
                    ExpiresAt = sharedTime.AddDays(30),
                    IsPinned = false
                });
            }
            await db.SaveChangesAsync();
        }

        var seenIds = new HashSet<Guid>();
        string? cursor = null;
        for (int page = 0; page < 5; page++)
        {
            var url = cursor is null
                ? "/api/panel/posts?limit=2"
                : $"/api/panel/posts?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            foreach (var post in doc.RootElement.GetProperty("posts").EnumerateArray())
            {
                seenIds.Add(post.GetProperty("id").GetGuid());
            }
            cursor = doc.RootElement.GetProperty("nextCursor").GetString();
            if (cursor is null) break;
        }
        seenIds.Should().Contain(ids);
    }

    [Fact]
    public async Task GetMediaUrl_returns_download_url_for_photo_post()
    {
        var (client, licenseId) = await SeedAsync();
        var objectKey = $"{licenseId}/media-url-post/media.bin";
        _factory.BroadcastMedia.Seed(objectKey, 1024, "image/jpeg");

        Guid postId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var p = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Photo,
                MediaObjectKey = objectKey,
                MediaContentType = "image/jpeg",
                MediaSizeBytes = 1024,
                MediaWidth = 800, MediaHeight = 600,
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
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("new");
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
                Type = BroadcastPostType.Text, TextBody = "secret",
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
                Type = BroadcastPostType.Text, TextBody = "no media here",
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
        (await ReadTitleAsync(resp)).Should().Be("no-media");
    }

    [Fact]
    public async Task Update_400_when_text_too_long()
    {
        var (client, licenseId) = await SeedAsync();
        Guid postId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var p = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Text, TextBody = "ok",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                IsPinned = false
            };
            db.BroadcastPosts.Add(p);
            await db.SaveChangesAsync();
            postId = p.Id;
        }

        var oversized = new string('x', 2001); // MaxTextLength = 2000
        var resp = await client.PutAsJsonAsync($"/api/panel/posts/{postId}",
            new { textBody = oversized });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadTitleAsync(resp)).Should().Be("text-too-long");
    }

    [Fact]
    public async Task Update_clears_caption_on_photo_post()
    {
        var (client, licenseId) = await SeedAsync();
        Guid postId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var p = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Photo,
                TextBody = "original caption",
                MediaObjectKey = $"{licenseId}/photo-update/media.bin",
                MediaContentType = "image/jpeg",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                IsPinned = false
            };
            db.BroadcastPosts.Add(p);
            await db.SaveChangesAsync();
            postId = p.Id;
        }

        var resp = await client.PutAsJsonAsync($"/api/panel/posts/{postId}",
            new { textBody = (string?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify caption normalized to null in DB (consistent with Create's whitespace-to-null rule)
        using var verifyScope = _factory.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var reloaded = await db2.BroadcastPosts.FindAsync(postId);
        reloaded!.TextBody.Should().BeNull();
    }

    [Fact]
    public async Task Update_400_when_text_required_and_body_empty()
    {
        var (client, licenseId) = await SeedAsync();
        Guid postId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var p = new BroadcastPost
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                Type = BroadcastPostType.Text, TextBody = "before",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                IsPinned = false
            };
            db.BroadcastPosts.Add(p);
            await db.SaveChangesAsync();
            postId = p.Id;
        }

        var resp = await client.PutAsJsonAsync($"/api/panel/posts/{postId}",
            new { textBody = "   " });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadTitleAsync(resp)).Should().Be("text-required");
    }
}
