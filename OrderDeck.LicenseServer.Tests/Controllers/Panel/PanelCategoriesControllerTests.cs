using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelCategoriesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelCategoriesControllerTests(ApiFactory f) => _factory = f;

    private sealed record CategoryDto(
        Guid Id, Guid? ParentCategoryId, string Name, string Path,
        int Depth, int SortOrder, bool IsActive);

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

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-CATC-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<CategoryDto> CreateAsync(
        HttpClient client, string name, Guid? parentId = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name, parentCategoryId = parentId, sortOrder = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    [Fact]
    public async Task Creates_a_three_level_tree_with_correct_paths_and_depths()
    {
        var (client, _) = await SeedAsync();

        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);
        var tisort = await CreateAsync(client, "Tişört", ust.Id);

        erkek.Path.Should().Be($"/{erkek.Id:N}/");
        erkek.Depth.Should().Be(0);
        ust.Path.Should().Be($"/{erkek.Id:N}/{ust.Id:N}/");
        ust.Depth.Should().Be(1);
        tisort.Path.Should().Be($"/{erkek.Id:N}/{ust.Id:N}/{tisort.Id:N}/");
        tisort.Depth.Should().Be(2);
    }

    [Fact]
    public async Task List_returns_the_tree_ordered_by_path()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        await CreateAsync(client, "Üst Giyim", erkek.Id);

        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");

        rows!.Select(r => r.Name).Should().ContainInOrder("Erkek", "Üst Giyim");
    }

    [Fact]
    public async Task List_is_scoped_to_the_callers_license()
    {
        var (clientA, _) = await SeedAsync();
        await CreateAsync(clientA, "A tarafı");

        var (clientB, _) = await SeedAsync();
        var rows = await clientB.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");

        rows!.Should().NotContain(r => r.Name == "A tarafı");
    }

    [Fact]
    public async Task Create_400_on_unknown_parent()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name = "Yetim", parentCategoryId = Guid.NewGuid(), sortOrder = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("parent-not-found");
    }

    [Fact]
    public async Task Create_400_on_empty_name()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name = "   ", parentCategoryId = (Guid?)null, sortOrder = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-name");
    }

    /// <summary>
    /// Kolon <c>nvarchar(120)</c>. Sınır DTO'da <c>[MaxLength]</c> ile
    /// duyurulmazsa istek prod'da kesme hatasına, yani 500'e düşer.
    /// </summary>
    [Fact]
    public async Task Create_400_when_the_name_exceeds_the_column_limit()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new
            {
                name = new string('A', CatalogLimits.CategoryName + 1),
                parentCategoryId = (Guid?)null,
                sortOrder = 0,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, "Name")).Should().BeTrue();
    }

    [Fact]
    public async Task Update_400_when_the_name_exceeds_the_column_limit()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{erkek.Id}",
            new
            {
                name = new string('A', CatalogLimits.CategoryName + 1),
                sortOrder = 0,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, "Name")).Should().BeTrue();
    }

    /// <summary>
    /// <c>Path</c> seviye başına 33 karakter büyüyor ve kolon 512 —
    /// yani derinlik kapatılmazsa ağaç sessizce kolonu taşırır.
    /// </summary>
    [Fact]
    public async Task Create_400_when_the_tree_would_get_too_deep()
    {
        var (client, _) = await SeedAsync();

        CategoryDto? parent = null;
        for (var level = 1; level <= CatalogLimits.CategoryMaxDepth; level++)
            parent = await CreateAsync(client, $"Seviye {level}", parent?.Id);

        parent!.Path.Length.Should().BeLessThanOrEqualTo(CatalogLimits.CategoryPath);

        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name = "Bir fazla", parentCategoryId = parent.Id, sortOrder = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("category-too-deep");
    }

    /// <summary>
    /// Taşıma tek düğümü değil ALT AĞACIN tamamını derinleştirir; kontrol
    /// taşınan düğümün değil, en derin torununun yoluna bakmalı.
    /// </summary>
    [Fact]
    public async Task Move_400_when_the_deepest_descendant_would_get_too_deep()
    {
        var (client, _) = await SeedAsync();

        // Kökte 3 seviyelik küçük bir alt ağaç…
        var dal = await CreateAsync(client, "Dal");
        var orta = await CreateAsync(client, "Orta", dal.Id);
        await CreateAsync(client, "Yaprak", orta.Id);

        // …ve tavanın bir eksiğine kadar giden ayrı bir zincir. "Dal" oraya
        // taşınınca TAM tavana oturur; taşan yalnız torunlarıdır.
        CategoryDto? derin = null;
        for (var level = 1; level <= CatalogLimits.CategoryMaxDepth - 1; level++)
            derin = await CreateAsync(client, $"Zincir {level}", derin?.Id);

        // Yalnız taşınan düğüme bakan bir kontrol bunu KAÇIRIR.
        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{dal.Id}/parent",
            new { parentCategoryId = derin!.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("category-too-deep");
    }

    /// <summary>Tavanın tam sınırındaki taşıma reddedilmemeli.</summary>
    [Fact]
    public async Task Move_is_allowed_when_the_subtree_lands_exactly_on_the_limit()
    {
        var (client, _) = await SeedAsync();

        var dal = await CreateAsync(client, "Dal");
        var yaprak = await CreateAsync(client, "Yaprak", dal.Id);

        // Zincir öyle seçiliyor ki taşınan 2 seviyelik alt ağaç tam tavana otursun.
        CategoryDto? hedef = null;
        for (var level = 1; level <= CatalogLimits.CategoryMaxDepth - 2; level++)
            hedef = await CreateAsync(client, $"Zincir {level}", hedef?.Id);

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{dal.Id}/parent",
            new { parentCategoryId = hedef!.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");
        var movedYaprak = rows!.Single(r => r.Id == yaprak.Id);
        movedYaprak.Depth.Should().Be(CatalogLimits.CategoryMaxDepth - 1);
        movedYaprak.Path.Length.Should().BeLessThanOrEqualTo(CatalogLimits.CategoryPath);
    }

    [Fact]
    public async Task Rename_updates_the_name_but_not_the_path()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{erkek.Id}",
            new { name = "Bay", sortOrder = 3, isActive = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
        dto.Name.Should().Be("Bay");
        dto.SortOrder.Should().Be(3);
        dto.IsActive.Should().BeFalse();
        dto.Path.Should().Be(erkek.Path);
    }

    [Fact]
    public async Task Move_rewrites_the_whole_subtree()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);
        var tisort = await CreateAsync(client, "Tişört", ust.Id);
        var kadin = await CreateAsync(client, "Kadın");

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{ust.Id}/parent",
            new { parentCategoryId = kadin.Id });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");
        var movedUst = rows!.Single(r => r.Id == ust.Id);
        var movedTisort = rows!.Single(r => r.Id == tisort.Id);

        movedUst.Path.Should().Be($"/{kadin.Id:N}/{ust.Id:N}/");
        movedUst.ParentCategoryId.Should().Be(kadin.Id);
        movedTisort.Path.Should().Be($"/{kadin.Id:N}/{ust.Id:N}/{tisort.Id:N}/");
        movedTisort.Depth.Should().Be(2);
    }

    [Fact]
    public async Task Move_to_root_is_allowed()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{ust.Id}/parent",
            new { parentCategoryId = (Guid?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
        dto.ParentCategoryId.Should().BeNull();
        dto.Path.Should().Be($"/{ust.Id:N}/");
        dto.Depth.Should().Be(0);
    }

    [Fact]
    public async Task Move_into_own_subtree_is_rejected()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{erkek.Id}/parent",
            new { parentCategoryId = ust.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("category-cycle");
    }

    [Fact]
    public async Task Delete_409_when_it_has_children()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        await CreateAsync(client, "Üst Giyim", erkek.Id);

        var resp = await client.DeleteAsync($"/api/panel/categories/{erkek.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("category-has-children");
    }

    [Fact]
    public async Task Delete_409_when_it_has_products()
    {
        var (client, licenseId) = await SeedAsync();
        var tisort = await CreateAsync(client, "Tişört");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Products.Add(new Product
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, CategoryId = tisort.Id,
                Code = "A1", Name = "Basic", DefaultPrice = 100m,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.DeleteAsync($"/api/panel/categories/{tisort.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("category-has-products");
    }

    [Fact]
    public async Task Delete_removes_an_empty_leaf()
    {
        var (client, _) = await SeedAsync();
        var bos = await CreateAsync(client, "Boş");

        var resp = await client.DeleteAsync($"/api/panel/categories/{bos.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");
        rows!.Should().NotContain(r => r.Id == bos.Id);
    }

    [Fact]
    public async Task Another_tenants_category_is_invisible_not_forbidden()
    {
        var (clientA, _) = await SeedAsync();
        var erkek = await CreateAsync(clientA, "Erkek");

        var (clientB, _) = await SeedAsync();
        var resp = await clientB.DeleteAsync($"/api/panel/categories/{erkek.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
