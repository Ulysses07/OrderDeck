using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Backup;
using OrderDeck.Licensing.Tests.TestHelpers;

namespace OrderDeck.Licensing.Tests.Backup;

public class BackupClientTests
{
    private static (BackupClient client, FakeHttpMessageHandler handler) Make(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string jwt = "test-jwt")
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (new BackupClient(http), handler);
    }

    [Fact]
    public async Task UploadAsync_SendsBytesWithShaHeader_AndDeserializes()
    {
        var meta = new
        {
            id = Guid.NewGuid(),
            sizeBytes = 12345L,
            createdAt = DateTimeOffset.UtcNow,
            isMonthlyMilestone = true,
            machineName = "TEST"
        };

        var (client, handler) = Make(_ =>
            FakeHttpMessageHandler.Json(201, JsonSerializer.Serialize(meta)));

        var result = await client.UploadAsync(new byte[] { 1, 2, 3 }, "deadbeef", "TEST");

        var lastReq = handler.Requests.Last();
        lastReq.Method.Should().Be(HttpMethod.Post);
        lastReq.RequestUri!.AbsolutePath.Should().Be("/api/v1/me/backups");
        lastReq.Headers.GetValues("X-Backup-Sha256").Should().Contain("deadbeef");
        lastReq.Headers.GetValues("X-Machine-Name").Should().Contain("TEST");

        result.Id.Should().Be(meta.id);
        result.SizeBytes.Should().Be(12345L);
        result.IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_ServerReturnsError_ThrowsLicenseApiException()
    {
        var (client, _) = Make(_ =>
            FakeHttpMessageHandler.Problem(413, "too large"));

        Func<Task> act = () => client.UploadAsync(new byte[] { 1 }, "abc", null);
        var ex = await act.Should().ThrowAsync<LicenseApiException>();
        ex.Which.Should().BeOfType<LicenseApiUnknownException>();
        ((LicenseApiUnknownException)ex.Which).StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task ListAsync_ReturnsArrayOfMetadata()
    {
        var arr = new[]
        {
            new { id = Guid.NewGuid(), sizeBytes = 100L, createdAt = DateTimeOffset.UtcNow, isMonthlyMilestone = false, machineName = "A" },
            new { id = Guid.NewGuid(), sizeBytes = 200L, createdAt = DateTimeOffset.UtcNow.AddDays(-1), isMonthlyMilestone = true, machineName = "A" }
        };
        var (client, _) = Make(_ =>
            FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(arr)));

        var list = await client.ListAsync();

        list.Should().HaveCount(2);
        list[1].IsMonthlyMilestone.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadAsync_ReturnsByteContent()
    {
        var bytes = Encoding.UTF8.GetBytes("zip-payload-contents");
        var (client, handler) = Make(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return resp;
        });

        var id = Guid.NewGuid();
        var got = await client.DownloadAsync(id);

        handler.Requests.Last().RequestUri!.AbsolutePath.Should().Be($"/api/v1/me/backups/{id}/download");
        got.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task DeleteAsync_SendsDelete()
    {
        var (client, handler) = Make(_ => FakeHttpMessageHandler.Empty(204));

        var id = Guid.NewGuid();
        await client.DeleteAsync(id);

        handler.Requests.Last().Method.Should().Be(HttpMethod.Delete);
        handler.Requests.Last().RequestUri!.AbsolutePath.Should().Be($"/api/v1/me/backups/{id}");
    }
}
