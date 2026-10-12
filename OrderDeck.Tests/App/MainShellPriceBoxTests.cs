using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// FİYAT kutusu (sahip kararı K8): yanlış fiyatlı etiketi baştan önleyen kurallar. Okuma tek
/// yardımcıdan (PriceInput), eksi fiyat ve geçersiz metin satır içi reddedilir, 0 TL satır içi
/// sorulur; kod değişince katalog fiyatı kutuyu doldurur, yoksa önceki ürünün fiyatı vurgulanır.
/// </summary>
public class MainShellPriceBoxTests
{
    [Theory]
    [InlineData("99,50", 99.50)]
    [InlineData("1.500", 1500)]
    [InlineData("1.500,50", 1500.50)]
    [InlineData("250 TL", 250)]
    public async Task Price_box_text_is_read_with_the_shared_rule(string text, double expected)
    {
        // Eski okuma: "99,50" → 9950 TL, "1.500" → 1,5 TL.
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = text;

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "aldım"));

        h.Vm.PrintQueue.Should().ContainSingle()
            .Which.Label.Price.Should().Be((decimal)expected);
    }

    [Fact]
    public async Task Negative_price_is_rejected_inline_without_a_popup()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "-50";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "aldım"));

        h.Vm.PrintQueue.Should().BeEmpty();
        h.Vm.PriceError.Should().Contain("eksi");
        h.Dialogs.Shown.Should().BeEmpty("hiçbir şey pop-up olarak açılmaz");

        h.Vm.ActivePriceText = "50";
        h.Vm.PriceError.Should().BeNull("kutu düzeltilince hata kalkar");
    }

    [Fact]
    public async Task Zero_price_asks_inline_and_writes_only_after_confirmation()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "0";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "aldım"));

        h.Vm.PrintQueue.Should().BeEmpty("0 TL onaysız yazılmaz");
        h.Vm.ZeroPricePrompt.Should().NotBeNull();
        h.Dialogs.Confirmations.Should().BeEmpty("onay satır içi, pop-up değil");

        await h.Vm.ConfirmZeroPriceCommand.ExecuteAsync(null);

        h.Vm.PrintQueue.Should().ContainSingle().Which.Label.Price.Should().Be(0m);
        h.Vm.ZeroPricePrompt.Should().BeNull();

        // Aynı kutu metniyle sonraki yorum yeniden sorulmaz.
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ikinci", "aldım"));
        h.Vm.PrintQueue.Should().HaveCount(2);
    }

    [Fact]
    public async Task Zero_price_confirmation_does_not_carry_over_to_another_product()
    {
        // Açılış "0"ı; A1 (katalogda fiyat yok) için 0 TL onaylandı. Kod B2'ye (fiyatsız) çevrildi, fiyat
        // yazılmadı: B2 yorumu sorusuz 0 TL yazılıyordu.
        using var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "A1";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@hediye", "aldım"));
        await h.Vm.ConfirmZeroPriceCommand.ExecuteAsync(null);
        h.Vm.PrintQueue.Should().ContainSingle();

        h.Vm.ActiveCode = "B2";
        h.Vm.IsPriceCarriedOver.Should().BeTrue("0 TL A1'in fiyatıydı — B2'ye önceki üründen kaldı");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b2", "aldım"));

        h.Vm.PrintQueue.Should().ContainSingle("onay ürüne bağlı: B2 için yeniden sorulur");
        h.Vm.ZeroPricePrompt.Should().Contain("@b2");
    }

    [Fact]
    public async Task Comments_clicked_while_the_zero_question_is_open_are_kept_in_click_order()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "0";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@bir", "aldım"));
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@iki", "aldım"));
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@uc", "aldım"));

        h.Vm.ZeroPricePrompt.Should().Contain("3 yorum").And.Contain("@bir").And.Contain("@iki").And.Contain("@uc");
        h.Vm.PrintQueue.Should().BeEmpty();

        await h.Vm.ConfirmZeroPriceCommand.ExecuteAsync(null);

        h.Vm.PrintQueue.Select(q => q.Username).Should().Equal(new[] { "@bir", "@iki", "@uc" },
            "ilk yazan alır — sonraki tıklama öncekini ezmez");
    }

    [Fact]
    public async Task Typing_a_price_instead_of_answering_names_the_unwritten_comments()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "0";
        var bir = MainShellTestHarness.ChatVm("@bir", "aldım");
        await h.Vm.AddChatToQueueAsync(bir);
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@iki", "aldım"));

        h.Vm.ActivePriceText = "2";
        h.Vm.ActivePriceText = "25";
        h.Vm.ActivePriceText = "250";

        h.Vm.ZeroPricePrompt.Should().BeNull();
        h.Vm.PrintQueue.Should().BeEmpty("soru 0 TL'yi soruyordu; yorumlar başka fiyatla sessizce yazılmaz");
        h.Vm.ZeroPriceDropped.Should().Contain("@bir").And.Contain("@iki");

        await h.Vm.AddChatToQueueAsync(bir);   // operatör yeniden tıkladı
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(250m);
        h.Vm.ZeroPriceDropped.Should().NotContain("@bir", "yazılan ad listeden düşer").And.Contain("@iki");

        h.Vm.DismissZeroPriceDroppedCommand.Execute(null);
        h.Vm.ZeroPriceDropped.Should().BeNull();
    }

    [Fact]
    public async Task Pending_zero_question_is_not_answered_for_another_product()
    {
        // Soru açıkken kod değişti: "Evet" A yorumunu B koduyla 0 TL yazardı.
        using var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "A1";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "aldım"));

        h.Vm.ActiveCode = "B2";
        await h.Vm.ConfirmZeroPriceCommand.ExecuteAsync(null);

        h.Vm.PrintQueue.Should().BeEmpty();
        h.Vm.ZeroPricePrompt.Should().BeNull();
        h.Vm.ZeroPriceDropped.Should().Contain("@a");
    }

    [Fact]
    public async Task Zero_price_cancel_writes_nothing()
    {
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "0";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "aldım"));

        h.Vm.CancelZeroPriceCommand.Execute(null);

        h.Vm.ZeroPricePrompt.Should().BeNull();
        h.Vm.PrintQueue.Should().BeEmpty();
    }

    private static void SeedCatalog(MainShellTestHarness.Harness h, params (string Id, string Code, decimal Price)[] products)
    {
        var rows = new CatalogProduct[products.Length];
        var codes = new CatalogBroadcastCode[products.Length];
        var variants = new CatalogVariant[products.Length];
        for (var i = 0; i < products.Length; i++)
        {
            var (id, code, price) = products[i];
            rows[i] = new CatalogProduct(id, null, "SK" + i, "SK" + i, "Ürün " + code, price, null,
                null, null, null, null, null, 0);
            codes[i] = new CatalogBroadcastCode(id, null, code, SearchNormalizer.Normalize(code), 0, 0);
            variants[i] = new CatalogVariant("v" + id, id, null, null, Barcode(i), true, 0);
        }
        new CatalogReplicaRepository(h.Db).Replace(rows, variants, Array.Empty<CatalogCategory>(), codes);
    }

    /// <summary><see cref="SeedCatalog"/>'daki <paramref name="index"/>. ürünün (tek varyantının) barkodu.</summary>
    private static string Barcode(int index) => (8690000000000L + index).ToString();

    [Fact]
    public void Code_change_fills_price_from_catalog_and_card_shows_it()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A12", 250m));
        h.Vm.ActivePriceText = "999";

        h.Vm.ActiveCode = "A12";

        h.Vm.ActivePriceText.Should().Be("250");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        h.Vm.ProductCard.CatalogPriceText.Should().Contain("250");
    }

    [Fact]
    public void Code_change_without_catalog_price_keeps_box_but_flags_carried_over()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A12", 250m), ("p2", "B7", 0m));
        h.Vm.ActiveCode = "A12";            // kutu 250
        h.Vm.ActivePriceText = "300";       // operatör A12 için 300 yazdı

        h.Vm.ActiveCode = "B7";             // katalogda fiyat yok

        h.Vm.ActivePriceText.Should().Be("300", "kutu korunur");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("önceki üründen kaldı");

        h.Vm.ActivePriceText = "120";
        h.Vm.IsPriceCarriedOver.Should().BeFalse("operatör fiyata dokununca vurgu kalkar");
    }

    [Fact]
    public async Task Price_typed_before_the_code_belongs_to_the_first_product_it_is_written_for()
    {
        // FİYAT önce yazıldı (kod boş), sonra katalogda fiyatı olmayan A12 — etiketler 250. Kod B7 (fiyatsız)
        // olunca kutu 250'de kalıyor; vurgu çıkmıyordu (sahip hiç atanmamıştı).
        using var h = MainShellTestHarness.Build();
        h.Vm.ActivePriceText = "250";
        h.Vm.ActiveCode = "A12";
        h.Vm.IsPriceCarriedOver.Should().BeFalse("ilk ürün: fiyat henüz hiçbir ürüne ait değil");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "aldım"));

        h.Vm.ActiveCode = "B7";

        h.Vm.ActivePriceText.Should().Be("250");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("250 A12'nin fiyatı olarak basıldı — B7'ye önceki üründen kaldı");
    }

    /// <summary>Kod kutusu her tuşta değişir: <paramref name="code"/>'u harf harf yazar.</summary>
    private static void TypeCode(MainShellTestHarness.Harness h, string code)
    {
        for (var i = 1; i <= code.Length; i++) h.Vm.ActiveCode = code[..i];
    }

    [Fact]
    public async Task Catalog_price_of_a_code_passed_while_typing_does_not_stick()
    {
        // "A12" yazılırken kod "A1"den geçer: A1'in katalog fiyatı kutuya yazılıp kalıyordu — katalogda fiyatı
        // olmayan A12'nin etiketleri A1'in fiyatıyla çıkardı.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "Z9";
        h.Vm.ActivePriceText = "300";        // operatör Z9 için 300 yazdı

        h.Vm.ActiveCode = "A";
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText.Should().Be("100", "A1'in katalog fiyatı (geçici dolum)");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        h.Vm.ActiveCode = "A12";

        h.Vm.ActivePriceText.Should().Be("300", "dolum A12'nin fiyatı değil: operatörün fiyatı geri gelir");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("300 Z9 için yazıldı — önceki üründen kaldı");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "A12 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(300m, "etiket kutuda görünen fiyatla yazılır");
    }

    [Fact]
    public void Typed_code_with_its_own_catalog_price_ends_with_that_price()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "A12", 250m));
        h.Vm.ActiveCode = "Z9";
        h.Vm.ActivePriceText = "300";

        TypeCode(h, "A12");

        h.Vm.ActivePriceText.Should().Be("250");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
    }

    [Fact]
    public void Operator_edit_after_a_catalog_fill_is_kept_as_the_operators_price()
    {
        // Operatör dolumun üstüne yazdı: o değer onun fiyatı (sahibi o anki kod) — sonraki geçici dolumdan
        // sonra geri gelen fiyat budur, eskisi (300) değil.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 80m));
        h.Vm.ActiveCode = "Z9";
        h.Vm.ActivePriceText = "300";
        h.Vm.ActiveCode = "A1";              // dolum 100
        h.Vm.ActivePriceText = "120";        // operatör A1 için 120 yazdı

        h.Vm.ActiveCode = "A12";
        h.Vm.ActivePriceText.Should().Be("120", "operatörün düzenlemesi korunur");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("120 A1 için yazıldı");

        h.Vm.ActiveCode = "";
        TypeCode(h, "B5");                    // "B"de geçici dolum 80

        h.Vm.ActivePriceText.Should().Be("120");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("120 A1'in fiyatı — B5'e önceki üründen kaldı");
    }

    [Fact]
    public async Task Catalog_price_used_for_a_label_stays_flagged_on_the_next_product()
    {
        // Dolumla etiket yazıldı: fiyat o ürün için KULLANILDI, artık geçici değil — sonraki fiyatsız üründe kutu
        // kalır ve vurgulanır (K8 "kutu kalır ama önceki üründen kaldı").
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "A1";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "A1 aldım"));

        h.Vm.ActiveCode = "B7";

        h.Vm.ActivePriceText.Should().Be("100");
        h.Vm.IsPriceCarriedOver.Should().BeTrue();
    }

    [Fact]
    public async Task Operators_price_for_a_code_beats_its_catalog_price_when_the_operator_comes_back()
    {
        // A1 için yayına özel 120 yazıldı, stoğa bakmak için B'ye geçildi, A1'e dönüldü: kutu katalog fiyatına
        // (100) dönüyor, vurgu çıkmıyordu — etiket sessizce 100 TL yazılıyordu.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";

        h.Vm.ActiveCode = "B";
        h.Vm.ActivePriceText.Should().Be("300", "operatörün B için fiyatı yok: katalog fiyatı");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("120", "operatörün A1 için yazdığı fiyat katalogdan önce gelir");
        h.Vm.IsPriceCarriedOver.Should().BeFalse("120 A1'in kendi fiyatı");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "A1 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(120m);
    }

    [Fact]
    public void Operators_price_comes_back_when_the_code_is_typed_again_letter_by_letter()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";
        h.Vm.ActiveCode = "B";

        TypeCode(h, "A1");

        h.Vm.ActivePriceText.Should().Be("120");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
    }

    [Fact]
    public async Task Operators_price_survives_labels_written_with_another_products_catalog_price()
    {
        // B'de katalog fiyatıyla etiket yazılması (dolum "kullanıldı") operatörün A1 için yazdığı fiyatı silmez.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";
        h.Vm.ActiveCode = "B";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "B aldım"));

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("120");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(300m, 120m);
    }

    [Theory]
    [InlineData("A1 ")]
    [InlineData("a1")]
    [InlineData("barcode")]
    public async Task Same_product_entered_again_keeps_the_operators_price(string sameProduct)
    {
        // Kod kutusuna sonda boşluk, küçük harf ya da aynı ürünün barkodu: etkin kod aynı (A1) ama katalog fiyatı
        // yeniden uygulanıp operatörün 120'si sessizce 100'e dönüyordu.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";

        h.Vm.ActiveCode = sameProduct == "barcode" ? Barcode(0) : sameProduct;

        h.Vm.ProductCard.Code.Should().Be("A1");
        h.Vm.ActivePriceText.Should().Be("120");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "A1 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(120m);
    }

    [Fact]
    public void Same_effective_code_reapplies_nothing()
    {
        // Etkin kod değişmediyse kutuya hiçbir şey yeniden uygulanmaz — arada katalog fiyatı değişmiş olsa da.
        // Kutu yalnız ürün değişince değişir; etiket kutuda görünenle yazılır.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText.Should().Be("100");
        SeedCatalog(h, ("p1", "A1", 110m));    // katalog senkronu

        h.Vm.ActiveCode = Barcode(0);

        h.Vm.ActivePriceText.Should().Be("100");
    }

    [Fact]
    public void Startup_price_is_not_flagged_as_carried_over()
    {
        // Açılıştaki "0" hiçbir ürüne ait değil; vurgu yanlış alarm olurdu (0 TL zaten sorulur).
        using var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "Z1";

        h.Vm.IsPriceCarriedOver.Should().BeFalse();
    }
}
