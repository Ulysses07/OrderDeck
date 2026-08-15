using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class CatalogReplicaRepositoryTests
{
    private static CatalogReplicaRepository Make(out IDbConnectionFactory db)
    {
        db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CatalogReplicaRepository(db);
    }

    private static CatalogProduct Product(string id, string code, string name = "Elbise")
        => new(id, null, code, OrderDeck.Shared.Text.SearchNormalizer.Normalize(code),
               name, 199.90m, null, null, null, null, null, null, 1_700_000_000);

    [Fact]
    public void FindByCode_matches_case_and_turkish_letters_insensitively()
    {
        var repo = Make(out _);
        repo.Replace([Product("p1", "GUZEL ELBISE")], [], [], []);

        repo.FindByCode("güzel elbise")!.Id.Should().Be("p1");
        repo.FindByCode("  Güzel   Elbise ")!.Id.Should().Be("p1");
        repo.FindByCode("ISIK 1").Should().BeNull();
    }

    [Fact]
    public void Replace_wipes_rows_that_the_server_no_longer_reports()
    {
        var repo = Make(out _);
        repo.Replace([Product("p1", "A1"), Product("p2", "A2")], [], [], []);

        // Sunucu artık yalnız A2'yi bildiriyor: A1 panelden silinmiş demektir.
        repo.Replace([Product("p2", "A2")], [], [], []);

        repo.FindByCode("A1").Should().BeNull();
        repo.FindByCode("A2").Should().NotBeNull();
    }

    [Fact]
    public void Replace_wipes_variants_and_categories_that_the_server_no_longer_reports()
    {
        var repo = Make(out _);

        // İlk tur: ürün + varyant + kategori hepsi dolu.
        repo.Replace(
            [Product("p1", "A1")],
            [new CatalogVariant("v1", "p1", null, null, null, true, 0)],
            [new CatalogCategory("c1", null, "Erkek", "/c1/", 0, true)],
            []);

        // İkinci tur: sunucu ürünü bildiriyor ama varyant ve kategori listesi boş.
        repo.Replace([Product("p1", "A1")], [], [], []);

        repo.GetVariants("p1").Should().BeEmpty();
        repo.GetCategories().Should().BeEmpty();
    }

    [Fact]
    public void GetVariants_returns_only_that_products_variants_in_sort_order()
    {
        var repo = Make(out _);
        repo.Replace(
            [Product("p1", "A1"), Product("p2", "A2")],
            [
                // "z-first" alfabetik olarak "a-second"'dan sonra gelir ama
                // SortOrder 0 ile önde olmalı — ORDER BY SortOrder'ı test eder.
                new CatalogVariant("z-first",  "p1", "Kırmızı", "S", null, true,  0),
                new CatalogVariant("a-second", "p1", "Kırmızı", "M", null, false, 1),
                new CatalogVariant("v9",       "p2", null, null, null, true, 0),
            ],
            [], []);

        var variants = repo.GetVariants("p1");

        // Sıra SortOrder'a göre: z-first önce, a-second sonra.
        variants.Select(v => v.Id).Should().Equal("z-first", "a-second");
        // IsActive doğru yuvarlak-turlanmalı.
        variants[0].IsActive.Should().BeTrue();
        variants[1].IsActive.Should().BeFalse();
    }

    [Fact]
    public void Replace_round_trips_categories()
    {
        var repo = Make(out _);
        // INSERT sırası PATH sırasının TERSİ — ORDER BY Path yoksa c2 önce gelir.
        repo.Replace(
            [],
            [],
            [
                new CatalogCategory("c2",   null, "Kadın",        "/c2/",     2, false),
                new CatalogCategory("c1a",  "c1", "Gömlek",      "/c1/c1a/", 1, true),
                new CatalogCategory("c1",   null, "Erkek",       "/c1/",     0, true),
            ], []);

        var cats = repo.GetCategories();

        // ORDER BY Path: /c1/ < /c1/c1a/ < /c2/
        cats.Select(c => c.Id).Should().Equal("c1", "c1a", "c2");

        // Tüm alanlar yuvarlak-turlanmalı.
        var erkek = cats.Single(c => c.Id == "c1");
        erkek.Name.Should().Be("Erkek");
        erkek.Path.Should().Be("/c1/");
        erkek.SortOrder.Should().Be(0);
        erkek.ParentCategoryId.Should().BeNull();
        erkek.IsActive.Should().BeTrue();

        var gomlek = cats.Single(c => c.Id == "c1a");
        gomlek.ParentCategoryId.Should().Be("c1");
        gomlek.IsActive.Should().BeTrue();

        // IsActive false olan kategori doğru okunmalı.
        var kadin = cats.Single(c => c.Id == "c2");
        kadin.IsActive.Should().BeFalse();
        kadin.SortOrder.Should().Be(2);
    }

    [Fact]
    public void CoverPhotoKeys_lists_every_live_key_once()
    {
        var repo = Make(out _);
        repo.Replace(
            [
                // Aynı anahtar iki ayrı üründe — DISTINCT olmadan iki kez dönerdi.
                Product("p1", "A1") with { CoverPhotoKey = "lic/products/shared/k.img" },
                Product("p2", "A2") with { CoverPhotoKey = "lic/products/shared/k.img" },
                // Fotoğrafsız ürün listede olmamalı.
                Product("p3", "A3"),
            ],
            [], [], []);

        var keys = repo.CoverPhotoKeys();

        // Aynı anahtar tam olarak bir kez dönmeli.
        keys.Should().Equal("lic/products/shared/k.img");
    }

    [Fact]
    public void FindByCode_picks_the_same_row_every_time_when_the_code_repeats()
    {
        var repo = Make(out _);
        // Replika indeksi bilerek UNIQUE değil (bkz. göç 025): beklenmedik bir
        // çakışma bütün senkron transaction'ını düşürmesin. Bedeli, aynı kodu
        // taşıyan iki satırın mümkün olması — arama yine de KARARLI dönmeli,
        // yoksa aynı yorum yayının iki anında iki farklı ürüne eşleşir.
        // "z-dup" alfabetik olarak sonra gelir ama Code eşit olduğu için sırayı
        // Id belirler: ORDER BY Code, Id → "a-dup".
        repo.Replace(
            [Product("z-dup", "A1"), Product("a-dup", "A1")],
            [], [], []);

        repo.FindByCode("A1")!.Id.Should().Be("a-dup");
    }

    [Fact]
    public void FindByCode_round_trips_default_price()
    {
        var repo = Make(out _);
        var product = Product("p1", "A1") with { DefaultPrice = 199.95m };
        repo.Replace([product], [], [], []);

        var found = repo.FindByCode("A1");

        found!.DefaultPrice.Should().Be(199.95m);
    }

    [Fact]
    public void FindBroadcastCode_normalize_edilmis_iğneyle_bulur()
    {
        var repo = Make(out _);
        var product = new CatalogProduct(
            "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
            "Renk", 1, "Beden", 2, null, 0);

        repo.Replace(
            new[] { product },
            Array.Empty<CatalogVariant>(),
            Array.Empty<CatalogCategory>(),
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        // Operatör küçük harf + Türkçe karakterle yazıyor; iğne aynı
        // normalizasyondan geçtiği için bulunmalı.
        var hit = repo.FindBroadcastCode("ateş");

        hit.Should().NotBeNull();
        hit!.ProductId.Should().Be("p1");
        hit.SellerAxisValue.Should().Be("Siyah");
        hit.Code.Should().Be("Ateş");
    }

    [Fact]
    public void Replace_eski_yayin_kodlarini_siler()
    {
        var repo = Make(out _);
        var product = new CatalogProduct(
            "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
            null, null, null, null, null, 0);

        repo.Replace(new[] { product }, Array.Empty<CatalogVariant>(), Array.Empty<CatalogCategory>(),
            new[] { new CatalogBroadcastCode("p1", null, "Ateş", "ATES", 0, 0) });
        repo.Replace(new[] { product }, Array.Empty<CatalogVariant>(), Array.Empty<CatalogCategory>(),
            new[] { new CatalogBroadcastCode("p1", null, "Buz", "BUZ", 0, 0) });

        // Sunucuda silinen kod yerelde hayalet kalırsa yayında YANLIŞ ÜRÜNE
        // eşleşir — bu testin tek amacı o hayaleti yakalamak.
        repo.FindBroadcastCode("ateş").Should().BeNull();
        repo.FindBroadcastCode("buz").Should().NotBeNull();
    }

    [Fact]
    public void GetProductById_bulamayinca_null_doner()
    {
        var repo = Make(out _);
        repo.GetProductById("yok").Should().BeNull();
    }
}
