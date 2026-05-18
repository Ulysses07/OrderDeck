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
}
