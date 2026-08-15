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

public class PanelBroadcastCodesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelBroadcastCodesControllerTests(ApiFactory f) => _factory = f;

    [Fact]
    public async Task Code_is_saved_and_returned_for_the_seller_axis_value()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var put = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = " ateş " });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        get.GetArrayLength().Should().Be(1);
        get[0].GetProperty("code").GetString().Should().Be("ateş");
        get[0].GetProperty("sellerAxisValue").GetString().Should().Be("Siyah");
        get[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Kod bir daha ASLA devredilmez: başka bir ürüne verilmiş kod reddedilir.
    /// Devredilseydi, eski yayın videosundaki kodu bugün yazan izleyicinin
    /// siparişi yanlış ürüne düşerdi.
    ///
    /// <para>409 <b>nerede</b> kullanıldığını söylemek zorunda: kod hiç serbest
    /// bırakılmadığı için çakışma çoğu zaman aylar önceki başka bir ürünle olur
    /// ve operatörün onu bulmak için tarayabileceği bir ekran yok. Mesaj sadece
    /// "kullanılmış" deseydi elinde teşhis edilemez bir "olmaz" kalırdı.</para>
    /// </summary>
    [Fact]
    public async Task Code_used_by_another_product_is_rejected_and_says_where()
    {
        var client = await NewPanelClientAsync();
        var first = await NewProductWithSellerAxisAsync(client);
        var second = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{first}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        // Denenen yazım BİLEREK farklı: çakışma normalize üstünden ("ates" ile
        // "ATEŞ" aynı sayılıyor). Mesaj kayıtlı yazımı da söylemezse operatör
        // ürün kartında kendi yazdığı metni arar ve bulamaz.
        var clash = await client.PutAsJsonAsync(
            $"/api/panel/products/{second}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ates" });

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var owner = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{first}");
        var detail = await DetailAsync(clash);

        detail.Should().Contain("'ates'").And.Contain("'ATEŞ' ile aynı sayılıyor");
        detail.Should().Contain(owner.GetProperty("code").GetString()!);
        detail.Should().Contain(owner.GetProperty("name").GetString()!);
        detail.Should().Contain("'Siyah'");
    }

    /// <summary>
    /// Ekseni olmayan üründe kod ürünün tamamına ait; mesaj olmayan bir eksen
    /// değeri uydurmamalı.
    /// </summary>
    [Fact]
    public async Task Conflict_on_an_axisless_product_names_only_the_product()
    {
        var client = await NewPanelClientAsync();
        var first = await NewAxislessProductAsync(client);
        var second = await NewProductWithSellerAxisAsync(client);

        var taken = await client.PutAsJsonAsync(
            $"/api/panel/products/{first}/broadcast-codes",
            new { code = "ATEŞ" });
        taken.StatusCode.Should().Be(HttpStatusCode.OK);

        var clash = await client.PutAsJsonAsync(
            $"/api/panel/products/{second}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var owner = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{first}");
        var detail = await DetailAsync(clash);

        detail.Should().Contain(owner.GetProperty("name").GetString()!);
        detail.Should().NotContain("değerine");
        // Kayıtlı yazım denenenle aynı: "aynı sayılıyor" eki takılmamalı.
        detail.Should().NotContain("aynı sayılıyor");
    }

    /// <summary>
    /// ProblemDetails'in <c>detail</c> alanını çözülmüş metin olarak verir.
    /// Ham gövdeye bakmak kırılgan: JSON yazıcısı ASCII dışı harfleri kaçış
    /// dizisine çevirirse ("ş" → <c>\u015F</c>) düz metin araması yanlış
    /// kırmızı üretir.
    /// </summary>
    private static async Task<string> DetailAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("detail").GetString()!;
    }

    /// <summary>
    /// Aynı hedefe aynı kodu yeniden yazmak çakışma değil; satır tazelenir.
    /// Panel kaydete iki kez basınca 409 görmemeli.
    /// </summary>
    [Fact]
    public async Task Rewriting_the_same_code_to_the_same_target_succeeds()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var again = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);

        // Asıl iddia bu: YENİ SATIR AÇILMADI. Yalnız 200'e bakmak yetmez —
        // EF InMemory benzersiz indeksi zorlamadığı için ikinci satır eklense
        // de test yeşil kalırdı; prod'da (SQL Server) aynı yol 500 olurdu.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.ProductBroadcastCodes.CountAsync(x => x.ProductId == productId);
        rows.Should().Be(1);
    }

    /// <summary>
    /// Benzersizlik LİSANS BAŞINA, global değil: iki farklı yayıncı aynı yayın
    /// kodunu kullanabilmeli. Ön kontroldeki <c>LicenseId</c> filtresi düşerse
    /// kırmızıya dönen tek test bu.
    /// </summary>
    [Fact]
    public async Task Same_code_under_another_license_is_allowed()
    {
        // NewPanelClientAsync her çağrıda yeni müşteri + yeni lisans üretir,
        // yani iki istemci iki ayrı kiracıdır.
        var first = await NewPanelClientAsync();
        var firstProduct = await NewProductWithSellerAxisAsync(first);
        var taken = await first.PutAsJsonAsync(
            $"/api/panel/products/{firstProduct}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });
        taken.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await NewPanelClientAsync();
        var secondProduct = await NewProductWithSellerAxisAsync(second);

        var res = await second.PutAsJsonAsync(
            $"/api/panel/products/{secondProduct}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Kod değişince eski satır SİLİNMEZ — kodu rezerve tutmaya devam eder.
    /// "Güncel" olan yalnız en yeni satır ama GET emekliyi de döndürür:
    /// aşağıdaki 409'un sebebini operatörün görebileceği tek ekran orası.
    /// </summary>
    [Fact]
    public async Task Changing_the_code_keeps_the_old_one_reserved()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);
        var other = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ESKI" });
        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "YENI" });
        await StampCreatedAtAsync(productId, ("ESKI", 10), ("YENI", 5));

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");
        codes.GetArrayLength().Should().Be(2);
        codes[0].GetProperty("code").GetString().Should().Be("YENI");
        codes[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        codes[1].GetProperty("code").GetString().Should().Be("ESKI");
        codes[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();

        var stealOld = await client.PutAsJsonAsync(
            $"/api/panel/products/{other}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ESKI" });

        stealOld.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// GET tüm geçmişi döndürür: en yeni satır <c>isCurrent: true</c>, emekli
    /// satır <c>false</c> ve sıra en yeni başta. Emekli satır listeden düşerse
    /// operatör, o kodu başka bir yere yazmayı denediğinde aldığı 409'un
    /// kaynağını hiçbir ekranda göremez.
    /// </summary>
    [Fact]
    public async Task Get_returns_the_retired_code_alongside_the_current_one()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });
        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "SU" });
        await StampCreatedAtAsync(productId, ("ATEŞ", 10), ("SU", 5));

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(2);

        codes[0].GetProperty("code").GetString().Should().Be("SU");
        codes[0].GetProperty("sellerAxisValue").GetString().Should().Be("Siyah");
        codes[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();

        codes[1].GetProperty("code").GetString().Should().Be("ATEŞ");
        codes[1].GetProperty("sellerAxisValue").GetString().Should().Be("Siyah");
        codes[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// <c>IsCurrent</c> satıcı ekseni değeri BAŞINA hesaplanır: "Gri"nin en
    /// yeni kodu, listede ondan sonra gelse bile güncel kalır. Bayrak tüm
    /// listenin ilk satırına verilseydi bu test kırmızıya dönerdi.
    /// </summary>
    [Fact]
    public async Task Each_seller_axis_value_keeps_its_own_current_code()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var gri = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Gri", isActive = true });
        gri.StatusCode.Should().Be(HttpStatusCode.Created);

        foreach (var (value, code) in new[]
                 {
                     ("Siyah", "ATEŞ"), ("Siyah", "SU"),
                     ("Gri", "DUMAN"), ("Gri", "BUZ"),
                 })
        {
            var put = await client.PutAsJsonAsync(
                $"/api/panel/products/{productId}/broadcast-codes",
                new { sellerAxisValue = value, code });
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Sıra BİLEREK "Siyah'ın güncel kodu en başta değil" olacak şekilde
        // kuruluyor: BUZ (Gri, güncel) listenin ilk satırı.
        await StampCreatedAtAsync(productId,
            ("ATEŞ", 40), ("DUMAN", 30), ("SU", 20), ("BUZ", 10));

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(4);

        var seen = codes.EnumerateArray()
            .Select(x => (
                Code: x.GetProperty("code").GetString(),
                Value: x.GetProperty("sellerAxisValue").GetString(),
                IsCurrent: x.GetProperty("isCurrent").GetBoolean()))
            .ToList();

        // En yeni başta.
        seen.Select(x => x.Code).Should().Equal("BUZ", "SU", "DUMAN", "ATEŞ");

        seen.Should().Contain(("BUZ", "Gri", true));
        seen.Should().Contain(("SU", "Siyah", true));
        seen.Should().Contain(("DUMAN", "Gri", false));
        seen.Should().Contain(("ATEŞ", "Siyah", false));
    }

    /// <summary>
    /// Yazılan satırların <c>CreatedAt</c>'ini DB üstünden ayrıştırır.
    /// Gerekçesi kararlılık: GET sıralaması <c>CreatedAt</c> üstünde ve
    /// eşitlikte tie-break rastgele bir Guid'e düşüyor. Arka arkaya atılan iki
    /// PUT, saatin çözünürlüğü yüzünden aynı tick'e düşebilir; o zaman sıra
    /// kura ile belirlenir ve test kararsız olur. Damgalar mutlak değil, birbirine
    /// göre okunmalı — <paramref name="stamps"/> "kaç dakika önce" verir.
    /// </summary>
    private async Task StampCreatedAtAsync(
        Guid productId, params (string Code, int MinutesAgo)[] stamps)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (code, minutesAgo) in stamps)
        {
            var row = await db.ProductBroadcastCodes
                .SingleAsync(x => x.ProductId == productId && x.Code == code);
            row.CreatedAt = now.AddMinutes(-minutesAgo);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Unknown_seller_axis_value_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Turuncu", code = "ATEŞ" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Başlık da doğrulanıyor: [ApiController] model doğrulaması da 400
        // üretir, yalnız durum koduna bakan test bambaşka bir sebeple geçerdi.
        (await res.Content.ReadAsStringAsync())
            .Should().Contain("unknown-seller-axis-value");
    }

    [Fact]
    public async Task Empty_code_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("missing-code");
    }

    /// <summary>
    /// Yalnız noktalamadan oluşan kod reddedilir: canlı yorumda hiçbir zaman
    /// eşleşmez ama kabul edilseydi satır silinmediği için kodu kalıcı olarak
    /// rezerve ederdi.
    /// </summary>
    [Fact]
    public async Task Code_without_letters_or_digits_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "---" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("invalid-code");
    }

    /// <summary>
    /// Bu test eskiden TERSİNİ savunuyordu ("kodu olan ürün silinemez"): kod
    /// kalıcı rezerve sayıldığı için satırın durması şarttı. Kural bilerek
    /// gevşetildi — kodu olup HİÇ stok hareketi olmayan ürün silinebiliyor ve
    /// kodunu serbest bırakıyor.
    ///
    /// <para>Gerekçe: hareketler <c>OrderId</c> taşıyor (StockLedgerWriter),
    /// yani "hareket yok" = "sipariş yok" = o kodla geçmişte tek bir satış bile
    /// yapılmamış; serbest kalan kodun bozacağı bir geçmiş yok. Eski kural ise
    /// yanlışlıkla açılmış bir ürün kartını kalıcı hâle getiriyordu, üstelik
    /// hata mesajının işaret ettiği arşivleme ucu o sırada hiç yoktu.</para>
    ///
    /// <para>Serbest bırakmanın tüm sözleşmesi <c>ProductArchiveTests</c>'te;
    /// burada yalnız bu dosyanın eski iddiasının yerine geçen hâli duruyor.</para>
    /// </summary>
    [Fact]
    public async Task Product_with_broadcast_code_can_be_deleted_when_it_has_no_movements()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var del = await client.DeleteAsync($"/api/panel/products/{productId}");

        del.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);

        // "Serbest kaldı" demek, kodun BAŞKA bir ürüne verilebilmesi demek.
        // Satır sayısına bakmak yetmez — asıl iddia, çakışma bekçisinin (ve
        // gerçek sağlayıcıda UNIQUE indeksin) artık takılmaması.
        var other = await NewProductWithSellerAxisAsync(client);
        var reuse = await client.PutAsJsonAsync(
            $"/api/panel/products/{other}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        reuse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Kimliği doğrulanmış panel istemcisi + altında aktif bir lisans.
    /// <c>PanelProductsControllerTests.SeedAsync</c> ile aynı kalıp.
    /// </summary>
    private async Task<HttpClient> NewPanelClientAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PROD-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    /// <summary>
    /// Ürün "Renk" ekseni satıcı rolünde, altında Siyah varyantı olacak şekilde
    /// kurulur ve Id'si döner.
    /// </summary>
    private static async Task<Guid> NewProductWithSellerAxisAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Yayın Kodlu " + Guid.NewGuid().ToString("N")[..6],
            defaultPrice = 100m,
            axis1Name = "Renk",
            axis1Role = 1,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = dto.GetProperty("id").GetGuid();

        var variant = await client.PostAsJsonAsync(
            $"/api/panel/products/{id}/variants",
            new { axis1Value = "Siyah", isActive = true });
        variant.StatusCode.Should().Be(HttpStatusCode.Created);

        return id;
    }

    /// <summary>
    /// Eksensiz ürün: yayın kodu ürünün tamamına verilir, satıcı ekseni değeri
    /// yoktur. Otomatik varyantı sunucu açar.
    /// </summary>
    private static async Task<Guid> NewAxislessProductAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Eksensiz " + Guid.NewGuid().ToString("N")[..6],
            defaultPrice = 100m,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Ürün iki eksenli kurulur: "Renk" satıcı ekseni (rol 1), "Beden" izleyici
    /// ekseni (rol 2). Altında <c>Siyah/M</c> ve <c>Siyah/L</c> — yani AYNI
    /// satıcı ekseni değerini paylaşan iki satır. Ürün Id'si döner.
    ///
    /// <para>Tek eksenli ürünle bu kurulum mümkün değil: benzersizlik
    /// <c>(ProductId, Axis1ValueNorm, Axis2ValueNorm)</c> üstünde ve ikinci
    /// eksen yokken <c>Axis2ValueNorm</c> her satırda boş dize kalır, dolayısıyla
    /// iki varyant aynı satıcı değerini taşıyamaz.</para>
    /// </summary>
    private static async Task<Guid> NewTwoAxisProductWithSharedSellerValueAsync(
        HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "İki Eksenli " + Guid.NewGuid().ToString("N")[..6],
            defaultPrice = 100m,
            axis1Name = "Renk",
            axis1Role = 1,
            axis2Name = "Beden",
            axis2Role = 2,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = dto.GetProperty("id").GetGuid();

        foreach (var beden in new[] { "M", "L" })
        {
            var variant = await client.PostAsJsonAsync(
                $"/api/panel/products/{id}/variants",
                new { axis1Value = "Siyah", axis2Value = beden, isActive = true });
            variant.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return id;
    }

    /// <summary>
    /// Satıcı ekseni değeri yeniden adlandırılınca kod da taşınır. Taşınmasaydı
    /// kod sahipsiz kalır ve canlı yorumda hiçbir kırılıma çözülemezdi —
    /// operatör de bunu ancak yayın ortasında fark ederdi.
    /// </summary>
    [Fact]
    public async Task Renaming_the_seller_axis_value_carries_the_code()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var variants = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}");
        var variantId = variants.GetProperty("variants")[0].GetProperty("id").GetGuid();

        var renamed = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/variants/{variantId}",
            new { axis1Value = "Antrasit", isActive = true });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(1);
        codes[0].GetProperty("sellerAxisValue").GetString().Should().Be("Antrasit");
        codes[0].GetProperty("code").GetString().Should().Be("ATEŞ");
    }

    /// <summary>
    /// Eski değeri taşıyan BAŞKA varyant kalmışsa bu bir yeniden adlandırma
    /// değil, tek satırın başka bir değere geçirilmesi — kod eski değerde kalır.
    /// </summary>
    [Fact]
    public async Task Moving_one_row_does_not_carry_the_code()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        // İkinci bir "Siyah" satırı yaratmak için ürüne ikinci eksen gerekir;
        // bunun yerine ikinci varyantı farklı değerle açıp ONU Siyah'a taşıyoruz,
        // sonra ilk satırı yeniden adlandırıyoruz: eski değer hâlâ kullanımda.
        var second = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Gri", isActive = true });
        second.StatusCode.Should().Be(HttpStatusCode.Created);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Gri", code = "DUMAN" });

        var product = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}");
        var siyahId = product.GetProperty("variants").EnumerateArray()
            .First(v => v.GetProperty("axis1Value").GetString() == "Siyah")
            .GetProperty("id").GetGuid();

        // "Siyah" → "Gri" olamaz (tekrar), o yüzden Siyah'ı yeni bir değere al:
        // "Gri" kodunun taşınmadığını görmek istiyoruz.
        var moved = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/variants/{siyahId}",
            new { axis1Value = "Lacivert", isActive = true });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(1);
        codes[0].GetProperty("sellerAxisValue").GetString().Should().Be("Gri",
            "kodun bağlı olduğu değer değişmedi");
    }

    /// <summary>
    /// <c>stillUsed</c> korumasının GERÇEK sınavı. Ürün iki eksenli:
    /// <c>Siyah/M</c> ve <c>Siyah/L</c> iki ayrı satır ama aynı satıcı ekseni
    /// değerini paylaşıyor. <c>Siyah/M</c>'nin rengi "Antrasit" yapılınca bu bir
    /// yeniden adlandırma değil — "Siyah" hâlâ <c>Siyah/L</c>'de yaşıyor, yani
    /// kod hâlâ geçerli bir kırılımı gösteriyor ve taşınmamalı.
    ///
    /// <para><b>Neden ayrı bir test:</b> yukarıdaki
    /// <see cref="Moving_one_row_does_not_carry_the_code"/> tek eksenli ürünle
    /// kurulu ve orada iki varyant aynı satıcı değerini paylaşamıyor
    /// (benzersizlik <c>(ProductId, Axis1ValueNorm, Axis2ValueNorm)</c> üstünde,
    /// ikinci eksen yokken <c>Axis2ValueNorm</c> hep boş dize). Orada eski değerde
    /// zaten kod olmadığı için <c>stillUsed</c> kaldırılsa da test yeşil kalıyor —
    /// ölçüldü. Bu senaryo, koruma düştüğünde kırmızıya dönen tek testtir.</para>
    /// </summary>
    [Fact]
    public async Task Renaming_a_row_that_shares_its_seller_value_does_not_carry_the_code()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewTwoAxisProductWithSharedSellerValueAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var product = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}");
        var siyahMId = product.GetProperty("variants").EnumerateArray()
            .First(v => v.GetProperty("axis2Value").GetString() == "M")
            .GetProperty("id").GetGuid();

        var renamed = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/variants/{siyahMId}",
            new { axis1Value = "Antrasit", axis2Value = "M", isActive = true });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(1);
        codes[0].GetProperty("sellerAxisValue").GetString().Should().Be("Siyah",
            "'Siyah' hâlâ Siyah/L satırında kullanımda; kod orada kalmalı");
        codes[0].GetProperty("code").GetString().Should().Be("ATEŞ");
    }
}
