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
        Guid Id, string ObjectKey, string ContentType, long SizeBytes,
        int? Width, int? Height, int SortOrder, string Url);
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

    /// <summary>
    /// <c>[MaxLength]</c> ihlalini [ApiController] standart
    /// <c>ValidationProblemDetails</c> ile döner — <c>Problem(title: "…")</c>
    /// slug'ıyla değil. Bu yüzden başlık yerine <c>errors</c> sözlüğüne bakılır.
    /// </summary>
    private static async Task<bool> HasValidationErrorAsync(
        HttpResponseMessage resp, string field)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("errors", out var errors)
               && errors.TryGetProperty(field, out _);
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
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/photos/upload-url",
            new { contentType, sizeBytes });

    private static Task<HttpResponseMessage> AttachAsync(
        HttpClient client, Guid productId, string objectKey,
        int? width = 800, int? height = 800)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/photos",
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

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<PhotoDto>())!;
        dto.ObjectKey.Should().Be(key);
        dto.SizeBytes.Should().Be(99_000);
        dto.ContentType.Should().Be("image/png");
        dto.Width.Should().Be(800);
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

    /// <summary>
    /// Anahtarı sunucu üretiyor (~111 karakter) ama Attach İSTEMCİDEN gelen
    /// anahtarı yazıyor; önek kontrolünden sonrası serbest. Kolon
    /// <c>nvarchar(512)</c> olduğu için uzun anahtar prod'da kesme hatası
    /// demek — sınır DTO'da kapatılmalı.
    /// </summary>
    [Fact]
    public async Task Attach_400_when_the_object_key_exceeds_the_column_limit()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var upload = await UploadUrlAsync(client, product.Id);
        var prefix = (await upload.Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        var longKey = prefix + new string('x', CatalogLimits.PhotoObjectKey);
        SimulateUpload(longKey);

        var resp = await AttachAsync(client, product.Id, longKey);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, "ObjectKey")).Should().BeTrue();
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
    public async Task Attach_404_for_another_tenants_product()
    {
        var clientA = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var key = (await (await UploadUrlAsync(clientA, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key);
        var clientB = await SeedAsync();

        var resp = await AttachAsync(clientB, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Galeri uçları: aynı ürüne birden fazla fotoğraf eklenebilir ve limit
    /// koruma altında.
    /// </summary>
    private async Task<PhotoDto> AddPhotoAsync(HttpClient client, Guid productId)
    {
        var urlResp = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/photos/upload-url",
            new { contentType = "image/jpeg", sizeBytes = 1024 });
        urlResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var uploaded = (await urlResp.Content.ReadFromJsonAsync<UploadUrlDto>())!;

        SimulateUpload(uploaded.ObjectKey, size: 1024);

        var attach = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/photos",
            new { objectKey = uploaded.ObjectKey, width = 800, height = 600 });
        attach.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await attach.Content.ReadFromJsonAsync<PhotoDto>())!;
    }

    [Fact]
    public async Task Photos_are_appended_in_order_and_the_fifth_is_refused()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var added = new List<PhotoDto>();
        for (var i = 0; i < CatalogLimits.MaxProductPhotos; i++)
            added.Add(await AddPhotoAsync(client, product.Id));

        added.Select(p => p.SortOrder).Should().Equal(0, 1, 2, 3);

        var fifth = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/photos/upload-url",
            new { contentType = "image/jpeg", sizeBytes = 1024 });

        fifth.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(fifth)).Should().Be("photo-limit-reached");
    }

    [Fact]
    public async Task Reorder_rewrites_sort_order_and_makes_the_first_id_the_cover()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var first = await AddPhotoAsync(client, product.Id);
        var second = await AddPhotoAsync(client, product.Id);

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/photos/order",
            new { ids = new[] { second.Id, first.Id } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await resp.Content.ReadFromJsonAsync<List<PhotoDto>>())!;
        list.Select(p => p.Id).Should().Equal(second.Id, first.Id);
        list[0].SortOrder.Should().Be(0);
    }

    /// <summary>
    /// Eksik/fazla id ile sıralama reddedilir. Kabul edilseydi listede olmayan
    /// fotoğrafın sırası belirsiz kalır, kapak sessizce değişirdi.
    /// </summary>
    [Fact]
    public async Task Reorder_refuses_an_incomplete_id_list()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var only = await AddPhotoAsync(client, product.Id);
        await AddPhotoAsync(client, product.Id);

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/photos/order",
            new { ids = new[] { only.Id } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("photo-order-mismatch");
    }

    [Fact]
    public async Task Deleting_a_photo_closes_the_gap_in_sort_order()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var first = await AddPhotoAsync(client, product.Id);
        var second = await AddPhotoAsync(client, product.Id);

        (await client.DeleteAsync(
            $"/api/panel/products/{product.Id}/photos/{first.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await client.GetFromJsonAsync<List<PhotoDto>>(
            $"/api/panel/products/{product.Id}/photos");

        list!.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        list[0].SortOrder.Should().Be(0, "silinen kapağın yerini bir sonraki almalı");
    }
}
