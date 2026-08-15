using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Catalog;

public class BroadcastCodeResolverTests
{
    // Renk = satıcı ekseni (1), Beden = izleyici ekseni (2).
    private static CatalogProduct Elbise() => new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private static CatalogVariant V(string id, string renk, string beden, bool active = true) =>
        new(id, "p1", renk, beden, null, active, 0);

    private static BroadcastCodeResolver Build(
        CatalogProduct product,
        IReadOnlyList<CatalogVariant> variants,
        IReadOnlyList<CatalogBroadcastCode> codes)
    {
        // Şema önce göç ettirilmeli: tablolar olmadan her sorgu patlar.
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);

        // SortOrder'ı dizideki konumdan veriyoruz — CatalogSyncService de tam
        // olarak bunu yapıyor. Testlerin çoğu sırayı doğruluyor ve GetVariants
        // "ORDER BY SortOrder" diyor; hepsine 0 verseydik sıra SQLite'ın eşitlik
        // durumundaki davranışına kalırdı, ki bu bir sözleşme değil.
        repo.Replace(
            new[] { product },
            variants.Select((v, i) => v with { SortOrder = i }).ToList(),
            Array.Empty<CatalogCategory>(),
            codes);
        return new BroadcastCodeResolver(repo);
    }

    [Fact]
    public void Kod_urunu_ve_satici_ekseni_degerini_cozer()
    {
        var resolver = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "M"), V("v2", "Beyaz", "M") },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        // Operatör küçük harf + Türkçe karakterle yazıyor.
        var result = resolver.Resolve("ateş");

        result.Should().NotBeNull();
        result!.Product.Name.Should().Be("Elbise");
        result.SellerAxisValue.Should().Be("Siyah");
        result.ViewerAxisName.Should().Be("Beden");
    }

    [Fact]
    public void Varyantlar_satici_ekseni_degerine_gore_suzulur()
    {
        var resolver = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "S"), V("v2", "Siyah", "M"), V("v3", "Beyaz", "S") },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var result = resolver.Resolve("Ateş");

        // "ATEŞ" siyah demek; beyaz varyant bu kodun altında hiç görünmemeli,
        // yoksa operatör kartta olmayan bir bedeni seçebilir.
        result!.Variants.Select(v => v.Id).Should().Equal("v1", "v2");
        result.ViewerAxisValues.Should().Equal("S", "M");
    }

    [Fact]
    public void Pasif_varyant_aday_degildir()
    {
        var resolver = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "S"), V("v2", "Siyah", "M", active: false) },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var result = resolver.Resolve("Ateş");

        // Panelde kapatılmış varyant yayında seçilebilir olmamalı.
        result!.ViewerAxisValues.Should().Equal("S");
    }

    [Fact]
    public void Eksensiz_urunde_varyant_kimligi_null()
    {
        var kolye = new CatalogProduct(
            "p1", null, "SK00002", "SK00002", "Kolye", 50m, null,
            null, null, null, null, null, 0);

        var resolver = Build(
            kolye,
            new[] { new CatalogVariant("v1", "p1", null, null, null, true, 0) },
            new[] { new CatalogBroadcastCode("p1", null, "Buz", "BUZ", 0, 0) });

        var result = resolver.Resolve("buz");

        result.Should().NotBeNull();
        result!.HasViewerAxis.Should().BeFalse();
        // Kabul kriteri 11 — eksensiz üründe stok ÜRÜNDEN düşer, varyanttan değil.
        result.ResolveVariantId(null).Should().BeNull();
    }

    [Fact]
    public void Yalniz_satici_ekseni_varsa_varyant_kimligi_belirlenir()
    {
        var canta = new CatalogProduct(
            "p1", null, "SK00003", "SK00003", "Çanta", 250m, null,
            "Renk", 1, null, null, null, 0);

        var resolver = Build(
            canta,
            new[]
            {
                new CatalogVariant("v1", "p1", "Siyah", null, null, true, 0),
                new CatalogVariant("v2", "p1", "Beyaz", null, null, true, 1),
            },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var result = resolver.Resolve("Ateş");

        result!.HasViewerAxis.Should().BeFalse();
        // Satıcı ekseni süzmesi tek varyant bıraktığı için kimlik belirlidir.
        result.ResolveVariantId(null).Should().Be("v1");
    }

    [Fact]
    public void Izleyici_ekseni_degeri_varyanta_cevrilir()
    {
        var resolver = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "S"), V("v2", "Siyah", "M") },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var result = resolver.Resolve("Ateş");

        // Yorumdan gelen değer küçük harfli olabilir; normalize karşılaştırılır.
        result!.ResolveVariantId("m").Should().Be("v2");
        // Katalogda olmayan beden varyanta çevrilemez.
        result.ResolveVariantId("XXL").Should().BeNull();
    }

    [Fact]
    public void Bilinmeyen_kod_null_doner()
    {
        var resolver = Build(
            Elbise(),
            Array.Empty<CatalogVariant>(),
            Array.Empty<CatalogBroadcastCode>());

        resolver.Resolve("yok").Should().BeNull();
        resolver.Resolve(null).Should().BeNull();
    }
}
