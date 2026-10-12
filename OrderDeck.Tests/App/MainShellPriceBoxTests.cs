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
            variants[i] = new CatalogVariant("v" + id, id, null, null, null, true, 0);
        }
        new CatalogReplicaRepository(h.Db).Replace(rows, variants, Array.Empty<CatalogCategory>(), codes);
    }

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

    [Fact]
    public void Startup_price_is_not_flagged_as_carried_over()
    {
        // Açılıştaki "0" hiçbir ürüne ait değil; vurgu yanlış alarm olurdu (0 TL zaten sorulur).
        using var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "Z1";

        h.Vm.IsPriceCarriedOver.Should().BeFalse();
    }
}
