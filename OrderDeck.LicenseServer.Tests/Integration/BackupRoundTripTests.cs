using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
