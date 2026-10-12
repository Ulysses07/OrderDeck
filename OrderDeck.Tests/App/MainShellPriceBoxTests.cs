using System;
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
    public void Startup_price_is_not_flagged_as_carried_over()
    {
        // Açılıştaki "0" hiçbir ürüne ait değil; vurgu yanlış alarm olurdu (0 TL zaten sorulur).
        using var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "Z1";

        h.Vm.IsPriceCarriedOver.Should().BeFalse();
    }
}
