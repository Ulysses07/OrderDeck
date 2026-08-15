using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Arşivleme sözleşmesi: <b>yayın kodu olan ürün = yayında satılabilen ürün.</b>
/// Arşive girmek ürünü satılabilir kümeden çıkarır, o yüzden kodları da siler;
/// arşivden çıkmak kodları GERİ GETİRMEZ.
///
/// <para>Bu dosyanın asıl işi o silmenin gerçekten olduğunu ve yan etkilerinin
/// (silme kapısının açılması, eksen kilidinin gevşemesi) bilinçli olduğunu
/// kayıt altına almak — yani ileride biri "kod satırları neden duruyor değil de
/// neden gidiyor?" diye sorduğunda cevabın testte durması.</para>
/// </summary>
public class ProductArchiveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ProductArchiveTests(ApiFactory factory) => _factory = factory;

    private sealed record Seed(HttpClient Client, Guid LicenseId, Guid ProductId);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    /// <summary>
    /// Satıcı eksenli ("Renk") tek varyantlı ürün + istenirse ona verilmiş bir
    /// yayın kodu. Kod HTTP ucundan veriliyor, doğrudan DB'ye yazılmıyor:
    /// normalize kolonu (<c>CodeNormalized</c>) SaveChanges zincirinde
    /// doldurulduğu için elle kurulan satır gerçek satırdan farklı davranırdı.
    /// </summary>
    private async Task<Seed> SeedAsync(bool withCode)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(), CustomerId = customerId,
                LicenseKey = "LDK-ARSV-" + Guid.NewGuid().ToString("N"),
                SkuCode = "STD", ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }

        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Arşiv denemesi " + Guid.NewGuid().ToString("N")[..6],
            defaultPrice = 100m,
            axis1Name = "Renk",
            axis1Role = (int)AxisRole.Seller,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var productId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var variant = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", isActive = true });
        variant.StatusCode.Should().Be(HttpStatusCode.Created);

        if (withCode)
        {
            var code = await client.PutAsJsonAsync(
                $"/api/panel/products/{productId}/broadcast-codes",
                new { sellerAxisValue = "Siyah", code = "ATEŞ" + Guid.NewGuid().ToString("N")[..6] });
            code.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return new Seed(client, licenseId, productId);
    }

    private async Task StartBroadcastAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.StreamSessions.Add(new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Akşam mezatı",
            StartedAt = DateTimeOffset.UtcNow,
            // EndedAt null = yayın sürüyor.
            Platforms = "youtube",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> CodeCountAsync(Guid productId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.ProductBroadcastCodes.CountAsync(x => x.ProductId == productId);
    }

    [Fact]
    public async Task Archiving_deletes_the_broadcast_codes()
    {
        var s = await SeedAsync(withCode: true);
        (await CodeCountAsync(s.ProductId)).Should().Be(1);

        var resp = await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isArchived").GetBoolean().Should().BeTrue();
        (await CodeCountAsync(s.ProductId)).Should().Be(0);

        // Panel ucu da boş görmeli — DB'ye bakmak yetmez, kod listesi ayrı bir
        // sorgudan besleniyor.
        var codes = await s.Client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{s.ProductId}/broadcast-codes");
        codes.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Archiving_stamps_ArchivedAt_and_bumps_UpdatedAt()
    {
        var s = await SeedAsync(withCode: false);

        DateTimeOffset before;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            before = (await db.Products.AsNoTracking()
                .FirstAsync(p => p.Id == s.ProductId)).UpdatedAt;
        }

        (await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var after = _factory.Services.CreateScope();
        var db2 = after.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var product = await db2.Products.AsNoTracking().FirstAsync(p => p.Id == s.ProductId);

        product.ArchivedAt.Should().NotBeNull();
        // UpdatedAt'ın ilerlemesi şart: panel listesi ve WPF çekmesi bu kolonun
        // indeksinden besleniyor, dokunulmazsa arşivleme sıralamada görünmez.
        product.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Archiving_is_rejected_while_a_broadcast_is_live()
    {
        var s = await SeedAsync(withCode: true);
        await StartBroadcastAsync(s.LicenseId);

        var resp = await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("broadcast-in-progress");
        // Reddedilen istek kodlara DOKUNMAMALI: silme SaveChanges'ten önce
        // yapılıyor, bekçi yanlış yere konursa satırlar 409'a rağmen giderdi.
        (await CodeCountAsync(s.ProductId)).Should().Be(1);
    }

    [Fact]
    public async Task Archiving_is_allowed_once_the_broadcast_has_ended()
    {
        var s = await SeedAsync(withCode: true);
        await StartBroadcastAsync(s.LicenseId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var session = await db.StreamSessions.FirstAsync(x => x.LicenseId == s.LicenseId);
            session.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var resp = await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CodeCountAsync(s.ProductId)).Should().Be(0);
    }

    /// <summary>
    /// Tekrar arşivleme, yayın bekçisinden ÖNCE dönmeli. Aksi hâlde yayın
    /// başladıktan sonra panelin yeniden gönderdiği istek 409 alır ve zaten
    /// arşivde olan ürün için hiç yakınsamayan bir hata döngüsü kurulurdu.
    /// </summary>
    [Fact]
    public async Task Archiving_an_already_archived_product_succeeds_even_mid_broadcast()
    {
        var s = await SeedAsync(withCode: true);
        (await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await StartBroadcastAsync(s.LicenseId);

        var again = await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isArchived").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Unarchiving_brings_the_product_back_without_its_codes()
    {
        var s = await SeedAsync(withCode: true);
        await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        var resp = await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/unarchive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("isArchived").GetBoolean().Should().BeFalse();
        // Sözleşmenin çekirdeği: kod GERİ GELMEZ. Ürün kodsuz dönüyor, yeni kod
        // istemek panelin işi.
        (await CodeCountAsync(s.ProductId)).Should().Be(0);
    }

    [Fact]
    public async Task Unarchiving_a_live_product_is_a_no_op()
    {
        var s = await SeedAsync(withCode: true);

        var resp = await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/unarchive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Arşivde olmayan ürüne gelen "arşivden çıkar" kodlara dokunmamalı.
        (await CodeCountAsync(s.ProductId)).Should().Be(1);
    }

    /// <summary>
    /// Kuralın öteki yarısı: arşivdeki ürüne kod YAZILAMAZ. Bekçi olmasa
    /// arşivleme sızdırırdı — arşivden sonra yazılan kod kalıcı olarak rezerve
    /// olur, üstelik tekrar arşivleme erken döndüğü için bir daha silinmez ve
    /// kod sonsuza dek arşivde asılı kalırdı.
    /// </summary>
    [Fact]
    public async Task Archived_product_rejects_new_broadcast_codes()
    {
        var s = await SeedAsync(withCode: false);
        (await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await s.Client.PutAsJsonAsync(
            $"/api/panel/products/{s.ProductId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" + Guid.NewGuid().ToString("N")[..6] });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("product-archived");
        (await CodeCountAsync(s.ProductId)).Should().Be(0);
    }

    /// <summary>
    /// Yasak arşive özgü, kalıcı değil: arşivden çıkan ürün yeniden kod alabilir.
    /// Zaten "arşivden çıkarınca yeni kod ver" sözleşmesinin çalıştığı yer burası.
    /// </summary>
    [Fact]
    public async Task Unarchived_product_accepts_broadcast_codes_again()
    {
        var s = await SeedAsync(withCode: false);
        await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);
        await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/unarchive", null);

        var resp = await s.Client.PutAsJsonAsync(
            $"/api/panel/products/{s.ProductId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" + Guid.NewGuid().ToString("N")[..6] });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CodeCountAsync(s.ProductId)).Should().Be(1);
    }

    [Fact]
    public async Task Archived_product_leaves_the_default_list_but_stays_behind_the_flag()
    {
        var s = await SeedAsync(withCode: false);
        await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        var visible = await s.Client.GetFromJsonAsync<JsonElement>("/api/panel/products");
        Ids(visible).Should().NotContain(s.ProductId);

        var all = await s.Client.GetFromJsonAsync<JsonElement>(
            "/api/panel/products?includeArchived=true");
        Ids(all).Should().Contain(s.ProductId);

        static IEnumerable<Guid> Ids(JsonElement page)
            => page.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Arşiv, kodu olan ürün için gerçek bir çıkış kapısı: kodlar gidince eski
    /// <c>product-has-broadcast-codes</c> yasağının konusu kalmıyor ve hareketi
    /// olmayan ürün silinebiliyor. Silme kapısı artık YALNIZ stok hareketine
    /// bakıyor.
    /// </summary>
    [Fact]
    public async Task Archived_product_without_movements_can_be_deleted()
    {
        var s = await SeedAsync(withCode: true);
        await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        var resp = await s.Client.DeleteAsync($"/api/panel/products/{s.ProductId}");

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
    }

    /// <summary>
    /// Arşivden geçmeden de silinebilmeli: kod satırları ürünle birlikte
    /// gidiyor. Eski davranış burada 409 <c>product-has-broadcast-codes</c>
    /// veriyordu ve operatörün elinde hiçbir çıkış kalmıyordu (arşivleme ucu
    /// yoktu).
    /// </summary>
    [Fact]
    public async Task Product_with_codes_but_no_movements_can_be_deleted_directly()
    {
        var s = await SeedAsync(withCode: true);

        var resp = await s.Client.DeleteAsync($"/api/panel/products/{s.ProductId}");

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        (await CodeCountAsync(s.ProductId)).Should().Be(0);
    }

    /// <summary>
    /// Eksen kilidi ("kod varsa eksen değişmez") arşivde meşru şekilde açılıyor:
    /// kod satırı ortada olmadığı için eski bir kodun yeni eksende sessizce
    /// başka bir kırılıma bağlanması imkânsız.
    ///
    /// <para>Varyant bilerek siliniyor: <c>axis-in-use</c> (dolu varyant varken
    /// eksen değişmez) daha ÖNCE tetikleniyor ve kod bekçisi hiç sınanmazdı.
    /// Kilidin kapattığı delik zaten tam olarak bu şekilde — varyantlar
    /// silinmiş, kod satırı yerinde kalmış — açılıyor.</para>
    /// </summary>
    [Fact]
    public async Task Axis_lock_opens_after_archiving()
    {
        var s = await SeedAsync(withCode: true);
        await DeleteVariantsAsync(s);

        var blocked = await PutAxisAsync(s, "Renk", AxisRole.Viewer);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(blocked)).Should().Be("axis-in-use-broadcast-codes");

        await s.Client.PostAsync($"/api/panel/products/{s.ProductId}/archive", null);

        var allowed = await PutAxisAsync(s, "Beden", AxisRole.Seller);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task DeleteVariantsAsync(Seed s)
    {
        var product = await s.Client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{s.ProductId}");

        foreach (var id in product.GetProperty("variants").EnumerateArray()
                     .Select(v => v.GetProperty("id").GetGuid()).ToList())
        {
            var resp = await s.Client.DeleteAsync(
                $"/api/panel/products/{s.ProductId}/variants/{id}");
            resp.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        }
    }

    private static Task<HttpResponseMessage> PutAxisAsync(Seed s, string axisName, AxisRole role)
        => s.Client.PutAsJsonAsync($"/api/panel/products/{s.ProductId}", new
        {
            name = "Arşiv denemesi",
            categoryId = (Guid?)null,
            defaultPrice = 100m,
            cost = (decimal?)null,
            shelfLocation = (string?)null,
            axis1Name = axisName,
            axis1Role = (int)role,
            axis2Name = (string?)null,
            axis2Role = (int?)null,
        });

    [Fact]
    public async Task Archiving_another_tenants_product_is_404()
    {
        var mine = await SeedAsync(withCode: false);
        var other = await SeedAsync(withCode: false);

        var resp = await other.Client.PostAsync(
            $"/api/panel/products/{mine.ProductId}/archive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Sızıntı kontrolü: başka kiracının ürünü arşive girmemiş olmalı.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Products.AsNoTracking().FirstAsync(p => p.Id == mine.ProductId))
            .IsArchived.Should().BeFalse();
    }

    /// <summary>
    /// Yayın bekçisi kiracıya bağlı olmalı: BAŞKA bir lisansın açık yayını
    /// benim ürünümü arşivlemeyi engellememeli.
    /// </summary>
    [Fact]
    public async Task Another_tenants_live_broadcast_does_not_block_archiving()
    {
        var mine = await SeedAsync(withCode: true);
        var other = await SeedAsync(withCode: false);
        await StartBroadcastAsync(other.LicenseId);

        var resp = await mine.Client.PostAsync(
            $"/api/panel/products/{mine.ProductId}/archive", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
