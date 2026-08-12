using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductPhotoControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductPhotoControllerTests(ApiFactory f) => _factory = f;

    private sealed record UploadUrlDto(string ObjectKey, string UploadUrl);
    private sealed record PhotoDto(
        string ObjectKey, string ContentType, long SizeBytes, int? Width, int? Height);
    private sealed record PhotoUrlDto(string Url);
    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? Axis1Name, int? Axis1Role, string? Axis2Name, int? Axis2Role,
        string? PhotoObjectKey, bool IsArchived);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<HttpClient> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PHOT-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ProductDto> CreateProductAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Fotoğraflı ürün", code = (string?)null, categoryId = (Guid?)null,
            defaultPrice = 100m, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> UploadUrlAsync(
        HttpClient client, Guid productId,
        string contentType = "image/jpeg", long sizeBytes = 120_000)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/photo/upload-url",
            new { contentType, sizeBytes });

    private static Task<HttpResponseMessage> AttachAsync(
        HttpClient client, Guid productId, string objectKey,
        int? width = 800, int? height = 800)
        => client.PutAsJsonAsync($"/api/panel/products/{productId}/photo",
            new { objectKey, width, height });

    /// <summary>Panelin R2'ye yaptığı PUT'un yerine geçer.</summary>
    private void SimulateUpload(string objectKey, long size = 120_000,
        string contentType = "image/jpeg")
        => _factory.BroadcastMedia.Seed(objectKey, size, contentType);

    [Fact]
    public async Task Upload_url_is_scoped_to_the_license_and_product()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await UploadUrlAsync(client, product.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<UploadUrlDto>())!;
        dto.ObjectKey.Should().Contain($"/products/{product.Id:N}/");
        dto.UploadUrl.Should().NotBeNullOrWhiteSpace();
        _factory.BroadcastMedia.UploadCalls
            .Should().Contain(c => c.Key == dto.ObjectKey && c.ContentType == "image/jpeg");
    }

    [Fact]
    public async Task Upload_url_400_on_an_unsupported_content_type()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await UploadUrlAsync(client, product.Id, contentType: "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unsupported-media-type");
    }

    [Fact]
    public async Task Upload_url_400_when_the_file_is_too_large()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await UploadUrlAsync(client, product.Id, sizeBytes: 6 * 1024 * 1024);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("file-too-large");
    }

    [Fact]
    public async Task Upload_url_404_for_another_tenants_product()
    {
        var clientA = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var clientB = await SeedAsync();

        var resp = await UploadUrlAsync(clientB, product.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Attach_records_the_size_and_type_reported_by_storage()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key, size: 99_000, contentType: "image/png");

        var resp = await AttachAsync(client, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<PhotoDto>())!;
        dto.ObjectKey.Should().Be(key);
        dto.SizeBytes.Should().Be(99_000);
        dto.ContentType.Should().Be("image/png");
        dto.Width.Should().Be(800);

        var product2 = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        product2!.PhotoObjectKey.Should().Be(key);
    }

    [Fact]
    public async Task Attach_400_when_the_object_is_not_in_storage()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;

        var resp = await AttachAsync(client, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("object-not-found");
    }

    [Fact]
    public async Task Attach_400_on_a_key_outside_the_products_prefix()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        const string foreignKey = "00000000000000000000000000000000/products/x/evil.img";
        SimulateUpload(foreignKey);

        var resp = await AttachAsync(client, product.Id, foreignKey);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-object-key");
    }

    [Fact]
    public async Task Attach_400_when_storage_reports_an_unsupported_type()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key, contentType: "application/zip");

        var resp = await AttachAsync(client, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unsupported-media-type");
    }

    [Fact]
    public async Task Attaching_a_second_photo_replaces_the_first()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var first = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(first);
        (await AttachAsync(client, product.Id, first)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(second);
        var resp = await AttachAsync(client, product.Id, second);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Should().NotBe(first);
        var dto = (await resp.Content.ReadFromJsonAsync<PhotoDto>())!;
        dto.ObjectKey.Should().Be(second);
    }

    [Fact]
    public async Task Photo_url_returns_404_when_there_is_no_photo()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await client.GetAsync($"/api/panel/products/{product.Id}/photo/url");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Photo_url_returns_a_download_url_when_a_photo_exists()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key);
        await AttachAsync(client, product.Id, key);

        var dto = await client.GetFromJsonAsync<PhotoUrlDto>(
            $"/api/panel/products/{product.Id}/photo/url");

        dto!.Url.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Delete_clears_the_photo_fields()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key);
        await AttachAsync(client, product.Id, key);

        var resp = await client.DeleteAsync($"/api/panel/products/{product.Id}/photo");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.PhotoObjectKey.Should().BeNull();
    }
}
