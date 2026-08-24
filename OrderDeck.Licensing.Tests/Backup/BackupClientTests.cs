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

    /// <summary>
    /// Sunucu, aynı müşterinin eşzamanlı iki yüklemesinden birini kota hakemiyle
    /// 409'a düşürüyor. Kaybeden istek yeniden denendiğinde güncel toplamı görüp
    /// geçmeli — aksi hâlde tamamen geçerli bir yedek, yalnızca zamanlama
    /// yüzünden kullanıcıya hata olarak görünürdü.
    /// </summary>
    [Fact]
    public async Task UploadAsync_409_alinca_bir_kez_yeniden_dener()
    {
        var meta = new
        {
            id = Guid.NewGuid(),
            sizeBytes = 10L,
            createdAt = DateTimeOffset.UtcNow,
            isMonthlyMilestone = false,
            machineName = "TEST"
        };
        var first = true;
        var (client, handler) = Make(_ =>
        {
            if (first) { first = false; return FakeHttpMessageHandler.Problem(409, "backup-quota-busy"); }
            return FakeHttpMessageHandler.Json(201, JsonSerializer.Serialize(meta));
        });

        var result = await client.UploadAsync(new byte[] { 1, 2, 3 }, "deadbeef", "TEST");

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Last().Headers.GetValues("X-Backup-Sha256").Should().Contain("deadbeef",
            "yeniden denemede başlıklar birebir aynı olmalı");
        result.Id.Should().Be(meta.id);
    }

    /// <summary>İkinci 409 gerçek bir hata: sonsuz döngü yerine yüzeye çıkmalı.</summary>
    [Fact]
    public async Task UploadAsync_ikinci_409u_hata_olarak_yukseltir()
    {
        var (client, handler) = Make(_ => FakeHttpMessageHandler.Problem(409, "backup-quota-busy"));

        Func<Task> act = () => client.UploadAsync(new byte[] { 1 }, "abc", null);

        var ex = await act.Should().ThrowAsync<LicenseApiException>();
        ((LicenseApiUnknownException)ex.Which).StatusCode.Should().Be(409);
        handler.Requests.Should().HaveCount(2, "yalnız bir kez yeniden denenmeli");
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
