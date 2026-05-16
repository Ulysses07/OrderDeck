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
}
