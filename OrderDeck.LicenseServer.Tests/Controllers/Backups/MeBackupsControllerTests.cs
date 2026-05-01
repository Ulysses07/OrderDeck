using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Backups;

public class MeBackupsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public MeBackupsControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, string jwt)> AuthedAsync()
        => await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

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
