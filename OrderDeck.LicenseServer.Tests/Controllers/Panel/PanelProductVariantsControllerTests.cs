using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductVariantsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductVariantsControllerTests(ApiFactory f) => _factory = f;

    private sealed record VariantDto(
        Guid Id, string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode, bool IsActive);

    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        bool IsArchived, List<VariantDto> Variants);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
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

    private async Task<(HttpClient Client, Guid CustomerId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-VARI-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return (client, customerId);
    }

    private static async Task<ProductDto> CreateProductAsync(
        HttpClient client,
        string name = "Deneme ürünü", string? code = null,
        string? axis1Name = "Renk", int? axis1Role = 2,
        string? axis2Name = null, int? axis2Role = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name, code, categoryId = (Guid?)null,
            defaultPrice = 100m, cost = (decimal?)null,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> PostVariantAsync(
        HttpClient client, Guid productId,
        string? axis1Value = null, string? axis1Code = null,
        string? axis2Value = null, string? axis2Code = null)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value, axis1Code, axis2Value, axis2Code, isActive = true });

    [Fact]
    public async Task Create_derives_the_code_from_the_display_value()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Yeşil");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Value.Should().Be("Yeşil");
        dto.Axis1Code.Should().Be("YESI");
        dto.VariantCode.Should().Be($"{product.Code}-YESI");
    }

    [Fact]
    public async Task Create_prefers_the_manually_supplied_code()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Yeşil", axis1Code: "yes");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Code.Should().Be("YES");
        dto.VariantCode.Should().Be($"{product.Code}-YES");
    }

    [Fact]
    public async Task Create_builds_a_two_segment_code_for_a_two_axis_product()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis2Code.Should().Be("38");
        dto.VariantCode.Should().Be($"{product.Code}-SIYA-38");
    }

    [Fact]
    public async Task Create_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var (clientB, _) = await SeedAsync();

        var resp = await PostVariantAsync(clientB, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_400_when_the_product_has_no_axis()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, axis1Name: null, axis1Role: null);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("product-has-no-axis");
    }

    [Fact]
    public async Task Create_400_when_the_first_axis_value_is_missing()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "  ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_the_second_axis_value_is_missing()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_a_second_value_is_sent_to_a_single_axis_product()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unexpected-axis-value");
    }

    [Fact]
    public async Task Create_400_when_no_ascii_code_can_be_derived()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "•••");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-axis-code");
    }

    /// <summary>
    /// Görünen eksen değeri serbest metin ama kolon <c>nvarchar(60)</c>;
    /// sınır DTO'da duyurulmazsa taşan girdi prod'da 500 oluyor.
    /// (Kod parçası ayrı bir konu: onu <c>AxisCodeDeriver</c> zaten 4 karaktere
    /// kısaltıyor, yani 8'lik kolon yapı gereği taşmıyor.)
    /// </summary>
    [Theory]
    [InlineData("Axis1Value")]
    [InlineData("Axis2Value")]
    public async Task Create_400_when_an_axis_value_exceeds_the_column_limit(string field)
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: field == "Axis1Value"
                ? new string('A', CatalogLimits.AxisValue + 1) : "Siyah",
            axis2Value: field == "Axis2Value"
                ? new string('B', CatalogLimits.AxisValue + 1) : "M");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, field)).Should().BeTrue();
    }

    /// <summary>
    /// Aynı değerin farklı yazımı GERÇEK tekrardır: kullanıcı açısından
    /// "kırmızı" ile "Kırmızı" aynı varyant. Mesaj da kodu değil DEĞERİ anmalı —
    /// kullanıcı kartta kodu değil değeri görüyor.
    /// </summary>
    [Fact]
    public async Task Create_409_duplicate_variant_when_the_same_value_is_re_added_in_another_casing()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "kırmızı");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
        (await DetailAsync(resp)).Should().Contain("kırmızı");
    }

    /// <summary>
    /// Değerler farklı ama türetilen 4 karakterlik kod aynı: kullanıcı iki AYRI
    /// varyant istiyor ve hakkı da var. "Zaten var" demek onu yanlış yönlendirir
    /// — kartta öyle bir değer yok. Ayrı slug + çareyi söyleyen mesaj şart.
    /// </summary>
    [Fact]
    public async Task Create_409_variant_code_collision_when_a_different_value_derives_the_same_code()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Kırmızılı");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("variant-code-collision");
        var detail = await DetailAsync(resp);
        detail.Should().Contain("Kırmızılı");
        detail.Should().Contain("'Kırmızı'");
        detail.Should().Contain("eksen kodunu elle");
    }

    /// <summary>Çakışmanın çaresi işlemeli: eksen kodu elle verilince iki satır yan yana yaşar.</summary>
    [Fact]
    public async Task Create_201_when_the_colliding_value_carries_a_manual_axis_code()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Kırmızılı", axis1Code: "KRMZ");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Select(v => v.VariantCode).Should().BeEquivalentTo(
            new[] { $"{product.Code}-KIRM", $"{product.Code}-KRMZ" });
    }

    /// <summary>İkinci eksende doğan çakışma da aynı sınıflandırmadan geçmeli.</summary>
    [Fact]
    public async Task Create_409_variant_code_collision_on_the_second_axis()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah", axis2Value: "Küçük"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "Küçükçe");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("variant-code-collision");
        var detail = await DetailAsync(resp);
        detail.Should().Contain("Siyah / Küçükçe");
        detail.Should().Contain("'Siyah / Küçük'");
    }

    /// <summary>İki eksenli üründe gerçek tekrar da doğru slug'ı almalı.</summary>
    [Fact]
    public async Task Create_409_duplicate_variant_on_a_two_axis_product()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah", axis2Value: "Küçük"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "siyah", axis2Value: "küçük");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    /// <summary>
    /// Ürün kodu değişince eski varyant kodu bayatlarsa, aynı eksen değeri
    /// ikinci kez eklendiğinde çakışma yakalanamaz ve tek üründe iki özdeş
    /// Axis1Value oluşurdu. Kod türetildiği için çakışma yakalanmalı.
    /// </summary>
    [Fact]
    public async Task Create_409_when_the_same_value_is_re_added_after_a_product_code_change()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = "D3", categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = "Renk", axis1Role = 2,
            axis2Name = (string?)null, axis2Role = (int?)null,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");

        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().ContainSingle();
        after.Variants[0].VariantCode.Should().Be("D3-SIYA");
    }

    [Fact]
    public async Task Update_recomputes_the_variant_code()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}",
            new
            {
                axis1Value = "Beyaz", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Code.Should().Be("BEYA");
        dto.VariantCode.Should().Be($"{product.Code}-BEYA");
        dto.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_409_when_it_collides_with_a_sibling()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");
        var beyaz = (await (await PostVariantAsync(client, product.Id, axis1Value: "Beyaz"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{beyaz.Id}",
            new
            {
                axis1Value = "Siyah", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    /// <summary>
    /// Güncelleme yolu da ayrımı yapmalı: kardeşin koduna DÜŞEN ama değeri
    /// farklı olan satır "zaten var" değil, çakışmadır.
    /// </summary>
    [Fact]
    public async Task Update_409_variant_code_collision_when_the_new_value_derives_a_siblings_code()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı");
        var mavi = (await (await PostVariantAsync(client, product.Id, axis1Value: "Mavi"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{mavi.Id}",
            new
            {
                axis1Value = "Kırmızılı", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("variant-code-collision");
        (await DetailAsync(resp)).Should().Contain("eksen kodunu elle");
    }

    /// <summary>
    /// Satır kendi değerleriyle kaydedilince kendi kendiyle çakışmamalı —
    /// yoksa sadece <c>IsActive</c>'i değiştiren kaydetme 409 yerdi.
    /// </summary>
    [Fact]
    public async Task Update_200_when_the_row_is_saved_with_its_own_values()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}",
            new
            {
                axis1Value = "Kırmızı", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Kiracı sınırı oluşturmada olduğu gibi GÜNCELLEMEDE de geçerli. Kural 404
    /// (403 değil): başka kiracının ürünü bizim için var olmamalı.
    /// </summary>
    [Fact]
    public async Task Update_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var variant = (await (await PostVariantAsync(clientA, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}",
            new
            {
                axis1Value = "Beyaz", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 404 dönüp yine de yazmış olmak yalnız duruma bakan bir testten kaçar.
        var after = await clientA.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Variants.Single().Axis1Value.Should().Be("Siyah");
        after.Variants.Single().IsActive.Should().BeTrue();
    }

    /// <summary>
    /// Silmede 404 tek başına yetmez: satır sahibinin gözünden HÂLÂ DURUYOR
    /// olmalı. 404 dönüp yine de silen bir uç, yalnız durum kodu kontrol eden
    /// bir testin gözünden kaçar.
    /// </summary>
    [Fact]
    public async Task Delete_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var variant = (await (await PostVariantAsync(clientA, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.DeleteAsync(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var after = await clientA.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Variants.Should().ContainSingle(v => v.Id == variant.Id);
    }

    [Fact]
    public async Task Delete_removes_the_variant()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.DeleteAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task Bulk_writes_every_row_in_one_go()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört", "T1",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 2);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new
            {
                items = new[]
                {
                    new { axis1Value = "Siyah", axis2Value = "M", isActive = true },
                    new { axis1Value = "Siyah", axis2Value = "L", isActive = true },
                    new { axis1Value = "Beyaz", axis2Value = "M", isActive = true },
                },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BulkResult>();
        body!.Variants.Should().HaveCount(3);
        body.Variants.Select(v => v.VariantCode).Should()
            .BeEquivalentTo(new[] { "T1-SIYA-M", "T1-SIYA-L", "T1-BEYA-M" });
    }

    /// <summary>
    /// Kritik güvence: parti bölünmez. Ortadaki tek geçersiz satır bile yazımı
    /// tamamen iptal etmeli — yoksa kullanıcı yarım kurulmuş ürünle kalır ve
    /// tekrar denediğinde ilk yazılanlar çakışır.
    /// </summary>
    [Fact]
    public async Task Bulk_writes_zero_rows_when_any_item_is_invalid()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört", "T2",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 2);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new
            {
                items = new[]
                {
                    new { axis1Value = "Siyah", axis2Value = "M", isActive = true },
                    new { axis1Value = "Beyaz", axis2Value = (string?)null, isActive = true },
                    new { axis1Value = "Mavi", axis2Value = "L", isActive = true },
                },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");

        var after = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Variants.Should().BeEmpty("geçersiz parti hiç satır bırakmamalı");
    }

    [Fact]
    public async Task Bulk_rejects_a_duplicate_inside_the_batch()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört", "T3",
            axis1Name: "Renk", axis1Role: 1);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new
            {
                items = new[]
                {
                    new { axis1Value = "Siyah", isActive = true },
                    new { axis1Value = "siyah", isActive = true },
                },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-in-batch");
    }

    [Fact]
    public async Task Bulk_rejects_a_row_that_already_exists()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört", "T4",
            axis1Name: "Renk", axis1Role: 1);

        (await client.PostAsJsonAsync($"/api/panel/products/{product.Id}/variants",
            new { axis1Value = "Siyah", isActive = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new { items = new[] { new { axis1Value = "Siyah", isActive = true } } });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    private sealed record BulkResult(List<VariantDto> Variants);
}
