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
/// FİYAT kutusu (sahip kararları K8, F1/F2/F4): yanlış fiyatlı etiketi baştan önleyen kurallar. Okuma tek
/// yardımcıdan (PriceInput), eksi fiyat ve geçersiz metin satır içi reddedilir, 0 TL satır içi sorulur. Bir
/// yayında bir kodun TEK fiyatı var, kaynağı o yayındaki etiketleri (ayrı bellek yok): kod değişince kodun bu
/// yayındaki fiyatı; yoksa katalog fiyatı; o da yoksa kutu kalır ve başka ürünün fiyatıysa vurgulanır. Etiketi
/// olan kodun fiyatı kutuda değişince satır içi şerit sorar; "Evet" kodun bütün etiketlerini yeni fiyata geçirir.
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
        h.Vm.PriceSourceText.Should().Be("katalog fiyatı");
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
    public async Task Catalog_price_of_a_code_passed_while_typing_does_not_stick_silently()
    {
        // "A12" yazılırken kod "A1"den geçer: A1'in katalog fiyatı kutuya yazılıp SESSİZCE kalıyordu — katalogda
        // fiyatı olmayan A12'nin etiketleri A1'in fiyatıyla çıkardı. Kural: A12'nin ne belleği ne katalog fiyatı
        // var → kutu kalır, değer A1'in olduğu için "önceki üründen kaldı" vurgulanır.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "Z9";
        h.Vm.ActivePriceText = "300";        // operatör Z9 için 300 yazdı

        h.Vm.ActiveCode = "A";
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText.Should().Be("100", "A1'in katalog fiyatı (geçici dolum)");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        h.Vm.ActiveCode = "A12";

        h.Vm.ActivePriceText.Should().Be("100");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("100 A1'in fiyatı — A12'ye önceki üründen kaldı, sessiz değil");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "A12 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(100m, "etiket kutuda görünen fiyatla yazılır");
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
    public void Price_typed_for_a_code_without_labels_is_not_remembered_after_leaving_it()
    {
        // F1: RAM'de ayrı fiyat belleği YOK. A1 için 120 yazıldı ama etiket yazılmadı: A1'e dönüşte kutu kodun
        // kaynağından dolar — etiketi yok, katalog fiyatı (100). Yazılan değer gittiği yerde "önceki üründen
        // kaldı" diye vurgulanır.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 80m));
        h.Vm.ActiveCode = "Z9";
        h.Vm.ActivePriceText = "300";
        h.Vm.ActiveCode = "A1";              // katalog 100
        h.Vm.ActivePriceText = "120";        // A1'in etiketi yok: soru yok, değer operatörün

        h.Vm.CodePriceQuestion.Should().BeNull("kodun bu yayında etiketi yok — değiştirilecek bir şey yok");
        h.Vm.ActiveCode = "A12";
        h.Vm.ActivePriceText.Should().Be("120", "fiyatsız koda geçişte kutu kalır");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("120 A1 için yazıldı");

        h.Vm.ActiveCode = "";
        TypeCode(h, "B5");                    // "B"de katalog dolumu 80
        h.Vm.ActivePriceText.Should().Be("80");
        h.Vm.IsPriceCarriedOver.Should().BeTrue("80 B'nin katalog fiyatı — B5'e önceki üründen kaldı");

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("100", "yazılan ama kullanılmayan fiyat bellekte tutulmaz");
        h.Vm.PriceSourceText.Should().Be("katalog fiyatı");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_price_used_for_a_label_stays_flagged_on_the_next_product()
    {
        // Dolumla etiket yazıldı: fiyat o ürün için KULLANILDI — sonraki fiyatsız üründe kutu kalır ve vurgulanır
        // (K8 "kutu kalır ama önceki üründen kaldı").
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "A1";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@alici", "A1 aldım"));

        h.Vm.ActiveCode = "B7";

        h.Vm.ActivePriceText.Should().Be("100");
        h.Vm.IsPriceCarriedOver.Should().BeTrue();
    }

    [Fact]
    public async Task Code_with_labels_in_this_stream_shows_their_price_before_the_catalog_price()
    {
        // A1'in bu yayındaki fiyatı 120 (etiketleri 120). Stoğa bakmak için B'ye geçildi, A1'e dönüldü: kutu
        // katalog fiyatına (100) dönerse etiket sessizce 100 TL yazılırdı.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ilk", "A1 aldım"));
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı", "etiket yazıldı: fiyat artık kodun bu yayındaki fiyatı");

        h.Vm.ActiveCode = "B";
        h.Vm.ActivePriceText.Should().Be("300", "B'nin bu yayında etiketi yok: katalog fiyatı");
        h.Vm.PriceSourceText.Should().Be("katalog fiyatı");

        TypeCode(h, "A1");

        h.Vm.ActivePriceText.Should().Be("120", "kodun bu yayındaki fiyatı katalogdan önce gelir");
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ikinci", "A1 aldım"));
        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(120m, 120m);
    }

    [Fact]
    public void Code_price_is_read_from_the_streams_labels_not_from_memory()
    {
        // "Uygulama yeniden açılınca da aynı sonuç": kabuk bu etiketleri hiç görmedi (başka yoldan, ör. önceki
        // açılışta yazıldılar). Kutu veritabanından okur — katalog fiyatı (100) değil, yayındaki fiyat (120).
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        var sessionId = h.Sessions.GetActive()!.Id;
        h.Labels.Add(sessionId, MainShellTestHarness.ChatVm("@a", "A1 aldım").Message, 120m, "A1");
        h.Labels.Add(sessionId, MainShellTestHarness.ChatVm("@b", "A1 aldım").Message, 120m, "A1");

        h.Vm.ActiveCode = "a1";

        h.Vm.ActivePriceText.Should().Be("120");
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
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

    [Fact]
    public async Task Each_code_shows_its_own_stream_price()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m), ("p2", "B", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        h.Vm.ActiveCode = "B";
        h.Vm.ActivePriceText = "150";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "B aldım"));

        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText.Should().Be("120");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a2", "A1 aldım"));
        h.Vm.ActiveCode = "B";
        h.Vm.ActivePriceText.Should().Be("150");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b2", "B aldım"));

        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(120m, 150m, 120m, 150m);
    }

    [Fact]
    public async Task Stream_price_of_a_code_beats_a_changed_catalog_price()
    {
        // Bir yayında bir kodun TEK fiyatı var: A1 bu yayında 100'den satıldı; katalog arada 110 oldu. A1'e dönüşte
        // kutu 110 gösterseydi aynı yayında iki fiyat olurdu.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "A1";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        h.Vm.ActiveCode = "B7";
        SeedCatalog(h, ("p1", "A1", 110m));    // katalog senkronu

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("100");
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
    }

    [Fact]
    public async Task A_new_stream_starts_from_the_catalog_price()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 100m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "120";            // yayına özel fiyat
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        h.Vm.ActiveCode = "B7";
        h.Sessions.End(h.Sessions.GetActive()!.Id);
        h.Sessions.Start("Yeni Yayın", new[] { "instagram" });

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("100", "önceki yayının fiyatı bu yayına taşınmaz: katalog fiyatı");
    }

    [Fact]
    public async Task Box_kept_into_a_new_stream_is_flagged_as_the_previous_streams_price()
    {
        // Yayın bitti, uygulama açık, yeni yayın başladı; kod kutusunda hâlâ A1. Kutu dünkü 250'yi gösteriyor,
        // kaynak ipucu boş, vurgu yoktu: ilk yorum 250'den yazılıp bu yayının fiyatı oluyordu. Koddan çıkıp dönen
        // operatör ise 300 (katalog) görürdü — aynı kod, gezinmeye göre iki fiyat.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "250";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmResult = _ => true;     // "Yayını bitirmek istediğinden emin misin?"
        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Vm.StartStreamCommand.Execute(null);

        h.Sessions.GetActive().Should().NotBeNull();
        h.Vm.ActivePriceText.Should().Be("250", "kutu sessizce değişmez");
        h.Vm.PriceSourceText.Should().BeNull();
        h.Vm.IsPriceCarriedOver.Should().BeTrue();
        h.Vm.PriceCarriedOverText.Should().Be("önceki yayından kaldı — bu yayının fiyatı mı?");

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        h.Vm.IsPriceCarriedOver.Should().BeFalse("etiket yazıldı: artık bu yayının fiyatı");
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
    }

    [Fact]
    public async Task Stream_price_equal_to_the_catalog_is_named_as_the_catalog_price_in_the_next_stream()
    {
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 300m));
        h.Vm.ActiveCode = "A1";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        h.Vm.StartStreamCommand.Execute(null);

        h.Vm.ActivePriceText.Should().Be("300");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        h.Vm.PriceSourceText.Should().Be("katalog fiyatı");
    }

    [Fact]
    public async Task Cancelling_every_label_of_the_code_flags_the_box_instead_of_a_stale_stream_source()
    {
        // A1'in bu yayındaki bütün etiketleri iptal edildi: kutu "bu yayındaki fiyatı" demeye devam ediyordu, oysa
        // kodun bu yayında artık fiyatı yok — koddan çıkıp dönen operatör katalog fiyatını (300) görürdü.
        using var h = MainShellTestHarness.Build();
        SeedCatalog(h, ("p1", "A1", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "250";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
        foreach (var row in h.Vm.PrintQueue) h.Vm.SelectedQueueItems.Add(row);

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.ActivePriceText.Should().Be("250", "kutu sessizce değişmez");
        h.Vm.PriceSourceText.Should().BeNull();
        h.Vm.IsPriceCarriedOver.Should().BeTrue();
        h.Vm.PriceCarriedOverText.Should().Be("bu yayında artık etiketi yok — bu fiyat doğru mu?");
    }

    [Fact]
    public async Task Cancelling_in_the_customer_detail_rechecks_the_box_source()
    {
        // Müşteri detayında iptal kabuğun dışında olur; detay kapanınca kutunun kaynağı yeniden okunur.
        using var h = MainShellTestHarness.Build(new CountingDrawers());
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        h.Vm.CustomerDetailSeam = _ =>
        {
            h.Labels.Cancel(new[] { row.Id }, OrderDeck.Core.Sales.CancelReasonCodes.Customer);
            return Task.CompletedTask;
        };

        await h.Vm.OpenCustomerDetailFromQueueCommand.ExecuteAsync(row);

        h.Vm.PriceSourceText.Should().BeNull();
        h.Vm.IsPriceCarriedOver.Should().BeTrue();
    }

    [Fact]
    public async Task Bulk_cancel_of_the_printed_labels_rechecks_the_box_source()
    {
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        var id = h.Vm.PrintQueue[0].Id;
        await h.Vm.PrintCommand.ExecuteAsync(null);

        await h.Vm.CancelLabelsAsync(new[] { id }, OrderDeck.Core.Sales.CancelReasonCodes.Customer);

        h.Vm.PriceSourceText.Should().BeNull();
        h.Vm.IsPriceCarriedOver.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_the_labels_behind_the_boxs_price_hints_the_other_price_without_blocking()
    {
        // Kodda başka fiyatlı etiket kaldı (230): kutudaki 250 artık kodun fiyatı değil. Şerit kendiliğinden
        // açılıyor ve yanıtlanana kadar o kod için etiket yazılmıyordu — operatör fiyatı değiştirmedi; sahip:
        // yayında operatörün başlatmadığı hiçbir şey etiketlemeyi durdurmaz. Kutunun altında engellemeyen ipucu,
        // yazım kutudaki fiyatla sürer, kutu sessizce değişmez.
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a", "@b");
        h.Labels.Add(h.Sessions.GetActive()!.Id, MainShellTestHarness.ChatVm("@yedek", "A1").Message, 230m, "A1");
        foreach (var row in h.Vm.PrintQueue.Where(r => r.Price == 250m).ToList()) h.Vm.SelectedQueueItems.Add(row);

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.ActivePriceText.Should().Be("250", "kutu sessizce değişmez");
        h.Vm.PriceSourceText.Should().BeNull("250 artık kodun bu yayındaki fiyatı değil");
        h.Vm.CodePriceQuestion.Should().BeNull("operatör fiyatı değiştirmedi — şerit kendiliğinden açılmaz");
        h.Vm.CodePriceHint.Should().Be(
            "A1 kodunda farklı fiyatlı 1 etiket var — Basılanlar'dan ya da kuyruktan \"Fiyatı düzelt\"le düzeltebilirsin.");

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@c", "A1 aldım"));

        h.Vm.PrintQueue.Should().ContainSingle(r => r.Username == "@c").Which.Price.Should().Be(250m);
        h.Vm.CodePriceBlocked.Should().BeNull();
        h.Dialogs.Shown.Should().BeEmpty("pop-up yok");
    }

    [Fact]
    public void Mixed_prices_from_old_data_hint_without_a_strip_and_labelling_goes_on()
    {
        // Eski veriden (ya da pazarlıklı yedekten) kalan karışık fiyat: kod girilince kutu en çok tekrarlanan fiyatı
        // gösterir. Operatör hiçbir şey değiştirmedi — şerit açılmaz, yazım durmaz; farklı fiyatlı etiketler
        // kutunun altında söylenir. Operatör fiyatı kendisi değiştirince şerit sorar; "Vazgeç" ipucunu geri getirir.
        using var h = MainShellTestHarness.Build();
        var sessionId = h.Sessions.GetActive()!.Id;
        foreach (var p in new[] { 250m, 250m, 230m, 240m })
            h.Labels.Add(sessionId, MainShellTestHarness.ChatVm("@x", "A1").Message, p, "A1");

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("250");
        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.CodePriceHint.Should().StartWith("A1 kodunda farklı fiyatlı 2 etiket var — ");
        MainShellTestHarness.EnqueueLabel(h.Vm, "@yeni", 250m);
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(250m, "yazım kutudaki fiyatla sürer");

        h.Vm.ActivePriceText = "200";
        h.Vm.CodePriceQuestion.Should().NotBeNull("operatör fiyatı değiştirdi: şerit sorar");
        h.Vm.CodePriceHint.Should().BeNull("şerit açıkken ipucu yazılmaz — şerit söylüyor");

        h.Vm.CancelCodePriceCommand.Execute(null);

        h.Vm.ActivePriceText.Should().Be("250");
        h.Vm.CodePriceHint.Should().StartWith("A1 kodunda farklı fiyatlı 2 etiket var — ");
    }

    [Fact]
    public async Task Strip_closes_by_itself_when_every_label_of_the_code_is_cancelled_and_keeps_the_typed_price()
    {
        // Şerit beklerken kodun bütün etiketleri iptal edildi: soru bayattı ("A1 kodunun 2 etiketi 250,00 TL…"), kodun
        // etiketi yokken o kod için yazım kilitli kalıyordu; tek çıkış "Vazgeç"ti. Şerit kendiliğinden kapanır, yazım
        // açılır; kutu operatörün yazdığı değeri korur (sessiz değişiklik yok).
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a", "@b");
        h.Vm.ActivePriceText = "200";
        var late = MainShellTestHarness.ChatVm("@c", "A1 aldım");
        await h.Vm.AddChatToQueueAsync(late);
        h.Vm.CodePriceBlocked.Should().Be("Önce fiyat değişikliğini onayla — yazılmadı: @c");
        foreach (var row in h.Vm.PrintQueue) h.Vm.SelectedQueueItems.Add(row);

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Vm.CodePriceQuestion.Should().BeNull("kodun düzeltilebilir etiketi kalmadı — soru bayat");
        h.Vm.ActivePriceText.Should().Be("200", "kutu operatörün yazdığı değeri korur");
        h.Vm.CodePriceBlocked.Should().Contain("yazılmadı: @c").And.Contain("yeniden tıkla");
        await h.Vm.AddChatToQueueAsync(late);
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(200m);
        h.Vm.CodePriceBlocked.Should().BeNull();
    }

    /// <summary>A1: @a, @b, @c 250 TL'den kuyrukta; @d, @e 200 TL'den (eski veri / pazarlıklı yedek). Operatör kutuya
    /// 200 yazdı, şerit soruyor.</summary>
    private static async Task<string> StripOverMixedA1(MainShellTestHarness.Harness h)
    {
        await WithCodeLabels(h, 250m, "@a", "@b", "@c");
        var sessionId = h.Sessions.GetActive()!.Id;
        foreach (var u in new[] { "@d", "@e" })
            h.Labels.Add(sessionId, MainShellTestHarness.ChatVm(u, "A1").Message, 200m, "A1");
        h.Vm.ActivePriceText = "200";
        h.Vm.CodePriceQuestion.Should().Be(
            "A1 kodunun 5 etiketi: 250,00 TL × 3, 200,00 TL × 2. Hepsi 200,00 TL olsun mu?");
        return sessionId;
    }

    [Fact]
    public async Task Strip_stays_when_a_cancel_makes_the_typed_price_the_codes_majority()
    {
        // Operatör cevap vermeden @a'yı kuyruktan çıkardı: 250 × 2, 200 × 2 — eşitlikte en son yazılan, kodun fiyatı
        // artık 200. Şerit "yazılan fiyat kodun fiyatı" diye kendiliğinden kapanıyordu: @b ve @c 250'de kalıyor,
        // sonraki yorumlar 200'den yazılıyordu. Operatörün başlattığı değişiklik onun cevabını bekler; şerit yalnız
        // kodun değişecek etiketi kalmayınca kendiliğinden kapanır.
        using var h = MainShellTestHarness.Build();
        var sessionId = await StripOverMixedA1(h);
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue.Single(r => r.Username == "@a"));

        h.Vm.RemoveSelectedFromQueueCommand.Execute(null);

        h.Labels.GetCodePrice(sessionId, "A1")!.Price.Should().Be(200m, "eşitlikte en son yazılanın fiyatı");
        h.Vm.CodePriceQuestion.Should().Be(
            "A1 kodunun 4 etiketi: 200,00 TL × 2, 250,00 TL × 2. Hepsi 200,00 TL olsun mu?");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@f", "A1 aldım"));
        h.Vm.CodePriceBlocked.Should().Be("Önce fiyat değişikliğini onayla — yazılmadı: @f");

        h.Vm.ConfirmCodePriceCommand.Execute(null);

        new[] { "@b", "@c" }.Select(u => DbPrice(h, h.Vm.PrintQueue.Single(r => r.Username == u).Id))
            .Should().OnlyContain(p => p == 200m);
        h.Vm.CodePriceQuestion.Should().BeNull();
    }

    [Fact]
    public async Task Yes_applies_when_a_change_outside_the_shell_made_the_typed_price_the_codes_majority()
    {
        // Aynı durum kabuğun görmediği bir yoldan: "Evet" hiçbir şey uygulamadan, hata yazmadan şeridi kapatıyordu;
        // 250'lik etiketler 250'de kalıyordu.
        using var h = MainShellTestHarness.Build();
        await StripOverMixedA1(h);
        h.Labels.Cancel(new[] { h.Vm.PrintQueue.Single(r => r.Username == "@a").Id },
            OrderDeck.Core.Sales.CancelReasonCodes.Customer);

        h.Vm.ConfirmCodePriceCommand.Execute(null);

        new[] { "@b", "@c" }.Select(u => DbPrice(h, h.Vm.PrintQueue.Single(r => r.Username == u).Id))
            .Should().OnlyContain(p => p == 200m);
        h.Vm.CodePriceError.Should().BeNull();
        h.Vm.CodePriceCorrection.Should().NotBeNull();
    }

    [Fact]
    public async Task Mixed_price_hint_does_not_survive_into_a_new_stream_where_the_code_has_no_labels()
    {
        // Kodun fiyatı yayına özel: biten yayının "A1 kodunda farklı fiyatlı 1 etiket var" ipucu, yayın bitince ve yeni
        // yayında A1'in hiç etiketi yokken kutunun altında kalıyordu.
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a", "@b");
        await h.Vm.PrintCommand.ExecuteAsync(null);
        var other = h.Labels.Add(h.Sessions.GetActive()!.Id, MainShellTestHarness.ChatVm("@c", "A1").Message, 230m, "A1");
        h.Labels.MarkPrintedAndRecord(new[] { other.Id });
        h.Vm.ActiveCode = "B7";
        h.Vm.ActiveCode = "A1";
        h.Vm.CodePriceHint.Should().StartWith("A1 kodunda farklı fiyatlı 1 etiket var — ");
        h.Dialogs.ConfirmResult = _ => true;     // "Yayını bitirmek istediğinden emin misin?" (mevcut akış)

        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Sessions.GetActive().Should().BeNull();
        h.Vm.CodePriceHint.Should().BeNull("yayın bitti");
        h.Vm.StartStreamCommand.Execute(null);
        h.Sessions.GetActive().Should().NotBeNull();
        h.Vm.CodePriceHint.Should().BeNull("yeni yayında A1'in etiketi yok");
    }

    // ── F2: etiketi olan kodun fiyatı kutuda değişince satır içi şerit ─────

    /// <summary>A1 için <paramref name="count"/> etiket <paramref name="price"/> TL ile kuyrukta.</summary>
    private static async Task<MainShellTestHarness.Harness> WithCodeLabels(
        MainShellTestHarness.Harness h, decimal price, params string[] users)
    {
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = PriceText(price);
        foreach (var u in users) await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm(u, "A1 aldım"));
        return h;
    }

    private static string PriceText(decimal price) => price.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    private static decimal DbPrice(MainShellTestHarness.Harness h, string labelId)
        => new LabelRepository(h.Db).GetById(labelId)!.Price;

    [Fact]
    public async Task Changing_the_price_of_a_code_with_labels_asks_once_and_yes_moves_every_label()
    {
        // Sahip: "A1 kodlu ürünün fiyatı 250 diyelim, ben bunu 200 yaparsam zaten tüm etiketler 200 olarak
        // çıkmalı." Kuyruktaki, basılmış etiketler hepsi; iptal edilmiş değişmez. Tuş vuruşunda değil, onayla.
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a", "@b", "@c", "@iptal");
        var rows = h.Vm.PrintQueue.ToList();
        h.Labels.Cancel(new[] { rows[3].Id }, OrderDeck.Core.Sales.CancelReasonCodes.Customer);
        h.Vm.SelectedQueueItems.Add(rows[0]);
        await h.Vm.PrintCommand.ExecuteAsync(null);         // @a basıldı (ciro 250)
        var queueRow = h.Vm.PrintQueue.Single(r => r.Username == "@b");

        h.Vm.ActivePriceText = "2";
        h.Vm.ActivePriceText = "20";
        h.Vm.ActivePriceText = "200";

        h.Vm.CodePriceQuestion.Should().Be("A1 kodunun 3 etiketi 250,00 TL. Hepsi 200,00 TL olsun mu?");
        rows.Take(3).Select(r => DbPrice(h, r.Id)).Should().OnlyContain(p => p == 250m, "tuş vuruşunda uygulanmaz");

        h.Vm.ConfirmCodePriceCommand.Execute(null);

        rows.Take(3).Select(r => DbPrice(h, r.Id)).Should().OnlyContain(p => p == 200m);
        DbPrice(h, rows[3].Id).Should().Be(250m, "iptal edilmiş etiket değişmez");
        h.Vm.PrintQueue.Should().Contain(queueRow, "kuyruk satırı yerinde");
        queueRow.Price.Should().Be(200m, "kâğıda yeni fiyat basılır");
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(200m,
            "basılmış etiketin müşteri toplamı fark kadar düzelir");
        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.ActivePriceText.Should().Be("200");
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
        var result = h.Vm.CodePriceCorrection!;
        result.Should().NotBeNull("sonuç kutunun altında satır içi");
        result.ResultText.Should().StartWith("A1: 3 etiket 200,00 TL'ye düzeltildi");
        result.IsReprintAsked.Should().BeTrue("basılmış etiket düzeldi — yeniden baskı sorulur");
        h.Dialogs.Shown.Should().BeEmpty("pop-up yok");

        result.DoneCommand.Execute(null);
        h.Vm.CodePriceCorrection.Should().BeNull("Kapat");
    }

    [Fact]
    public async Task Pending_price_change_writes_no_label_for_that_code_until_answered()
    {
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        h.Vm.ActivePriceText = "200";
        var late = MainShellTestHarness.ChatVm("@gec", "A1 aldım");

        await h.Vm.AddChatToQueueAsync(late);

        h.Vm.PrintQueue.Should().ContainSingle("şerit beklerken o kod için etiket yazılmaz");
        h.Vm.CodePriceBlocked.Should().Be("Önce fiyat değişikliğini onayla — yazılmadı: @gec");

        h.Vm.ConfirmCodePriceCommand.Execute(null);
        h.Vm.CodePriceBlocked.Should().Contain("yazılmadı: @gec").And.Contain("yeniden tıkla",
            "onaydan sonra da adı kalır — sessizce kaybolmaz");
        await h.Vm.AddChatToQueueAsync(late);              // operatör yeniden tıkladı

        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(200m, 200m);
        h.Vm.CodePriceBlocked.Should().BeNull("yazılan ad listeden düşer");
    }

    [Fact]
    public async Task Cancel_returns_the_box_to_the_codes_price_and_changes_nothing()
    {
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        h.Vm.ActivePriceText = "200";

        h.Vm.CancelCodePriceCommand.Execute(null);

        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.ActivePriceText.Should().Be("250");
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
        DbPrice(h, h.Vm.PrintQueue[0].Id).Should().Be(250m);
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(250m, 250m);
    }

    [Fact]
    public async Task Switching_to_another_code_closes_the_strip_without_applying()
    {
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        h.Vm.ActivePriceText = "200";
        h.Vm.CodePriceQuestion.Should().NotBeNull();

        h.Vm.ActiveCode = "B7";

        h.Vm.CodePriceQuestion.Should().BeNull("başka koda geçilince şerit kapanır");
        DbPrice(h, h.Vm.PrintQueue[0].Id).Should().Be(250m, "değişiklik uygulanmaz");
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText.Should().Be("250", "kod kendi fiyatıyla döner");
    }

    [Fact]
    public async Task Typing_the_codes_own_price_asks_nothing()
    {
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");

        h.Vm.ActivePriceText = "25";
        h.Vm.CodePriceQuestion.Should().NotBeNull();
        h.Vm.ActivePriceText = "250";

        h.Vm.CodePriceQuestion.Should().BeNull();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        h.Vm.PrintQueue.Should().HaveCount(2);
    }

    [Fact]
    public void Mixed_prices_of_a_code_are_named_in_the_question_and_all_move()
    {
        using var h = MainShellTestHarness.Build();
        var sessionId = h.Sessions.GetActive()!.Id;
        var ids = new[] { 250m, 250m, 230m }
            .Select(p => h.Labels.Add(sessionId, MainShellTestHarness.ChatVm("@x", "A1").Message, p, "A1").Id)
            .ToList();
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText.Should().Be("250", "en çok tekrarlanan fiyat");

        h.Vm.ActivePriceText = "200";
        h.Vm.CodePriceQuestion.Should().Be(
            "A1 kodunun 3 etiketi: 250,00 TL × 2, 230,00 TL × 1. Hepsi 200,00 TL olsun mu?");
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        ids.Select(id => DbPrice(h, id)).Should().OnlyContain(p => p == 200m);
    }

    [Fact]
    public async Task Ending_the_stream_closes_the_pending_question_and_the_stale_source()
    {
        // Kodun fiyatı yayına özel: biten yayının "250 → 200 olsun mu?" sorusu ve "bu yayındaki fiyatı" kaynağı
        // kalmamalı. Kutu sessizce değişmez.
        using var h = MainShellTestHarness.Build();
        h.Labels.Add(h.Sessions.GetActive()!.Id, MainShellTestHarness.ChatVm("@a", "A1").Message, 250m, "A1");
        h.Vm.ActiveCode = "A1";
        h.Vm.PriceSourceText.Should().Be("bu yayındaki fiyatı");
        h.Vm.ActivePriceText = "200";
        h.Vm.CodePriceQuestion.Should().NotBeNull();
        h.Dialogs.ConfirmResult = _ => true;      // "Yayını bitirmek istediğinden emin misin?" (mevcut akış)

        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Sessions.GetActive().Should().BeNull();
        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.PriceSourceText.Should().BeNull();
        h.Vm.ActivePriceText.Should().Be("200");
    }

    [Fact]
    public async Task Turkish_letter_case_is_one_code_for_the_box_the_question_and_the_correction()
    {
        // Katalogda olmayan "çanta1" yazılıp 250'den satıldı, kod kutusu "Çanta1" yapıldı. Kabuk (harf duyarsız)
        // ikisini aynı kod sayıyor, fiyat kaynağı (SQLite NOCASE yalnız ASCII katlar) ayrı sayıyordu: 200 yazılınca
        // şerit açılmıyor, aynı ürüne iki fiyat yazılıyordu.
        using var h = MainShellTestHarness.Build();
        h.Vm.ActiveCode = "çanta1";
        h.Vm.ActivePriceText = "250";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "aldım"));
        h.Vm.ActiveCode = "Çanta1";
        h.Vm.ActivePriceText.Should().Be("250");

        h.Vm.ActivePriceText = "200";

        h.Vm.CodePriceQuestion.Should().Be("Çanta1 kodunun 1 etiketi 250,00 TL. Hepsi 200,00 TL olsun mu?");
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "aldım"));
        h.Vm.PrintQueue.Should().ContainSingle("şerit beklerken o kod için etiket yazılmaz");
        h.Vm.ConfirmCodePriceCommand.Execute(null);
        DbPrice(h, h.Vm.PrintQueue[0].Id).Should().Be(200m, "düzeltme öbür yazımı da kapsar");
        h.Vm.RefreshHeroStats();
        h.Vm.ProductOrderCount.Should().Be(1, "BU ÜRÜNDEN sayacı da aynı kural");
    }

    [Fact]
    public async Task Yes_to_zero_applies_and_the_next_comment_is_not_asked_again()
    {
        // Şeridin sorusu yeni fiyatı adıyla sordu ("Hepsi 0,00 TL olsun mu?"): "Evet" ikinci bir 0 TL onayı
        // istemeden uygulanır, sonraki yorum da 0 TL sorusuna takılmaz.
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        var id = h.Vm.PrintQueue[0].Id;
        h.Vm.ActivePriceText = "0";
        h.Vm.CodePriceQuestion.Should().Be("A1 kodunun 1 etiketi 250,00 TL. Hepsi 0,00 TL olsun mu?");

        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.CodePriceError.Should().BeNull();
        h.Vm.CodePriceQuestion.Should().BeNull();
        DbPrice(h, id).Should().Be(0m);
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        h.Vm.ZeroPricePrompt.Should().BeNull("0 TL şeritte onaylandı");
        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(0m, 0m);
    }

    /// <summary>A1 için <paramref name="user"/> 250 TL'den yazılıp basılır, kargo dosyası kargoya verilir.</summary>
    private static async Task<string> ShippedLabel(MainShellTestHarness.Harness h, string user)
    {
        await WithCodeLabels(h, 250m, user);
        var row = h.Vm.PrintQueue.Single(r => r.Username == user);
        h.Vm.SelectedQueueItems.Clear();
        h.Vm.SelectedQueueItems.Add(row);
        await h.Vm.PrintCommand.ExecuteAsync(null);
        ShipNow(h, row);
        return row.Id;
    }

    /// <summary>Basılmış etiketin kargo dosyası kargoya verilir — kabuğun görmediği bir yoldan (ör. müşteri
    /// uygulaması/panel akışı, başka bir ekran).</summary>
    private static void ShipNow(MainShellTestHarness.Harness h, OrderDeck.App.ViewModels.LabelViewModel row)
    {
        var shipments = new OrderDeck.Core.Sales.ShipmentService(new ShipmentRepository(h.Db), new LabelRepository(h.Db),
            OrderDeck.Tests.TestHelpers.LocalShipping.Off());
        var file = shipments.GetOrCreateOpenShipment(row.Label.CustomerId);
        shipments.AttachLabels(file.Id, new[] { row.Id });
        shipments.ApplyDecision(file.Id, OrderDeck.Core.Sales.ShipmentDecision.ShipNow);
    }

    [Fact]
    public async Task Strip_whose_labels_were_shipped_meanwhile_does_not_block_the_next_comment()
    {
        // Şerit beklerken kodun tek etiketi kabuğun görmediği bir yoldan kargoya verildi: değiştirilecek etiket
        // kalmadı. Bayat şerit yorumu kilitlememeli — kapanır, yorum kutudaki (operatörün yazdığı) fiyatla yazılır.
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Vm.ActivePriceText = "200";
        h.Vm.CodePriceQuestion.Should().NotBeNull();
        ShipNow(h, row);

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@yeni", "A1 aldım"));

        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(200m);
        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.CodePriceBlocked.Should().BeNull();
        DbPrice(h, row.Id).Should().Be(250m, "kargoya verilmiş etiket değişmez");
    }

    [Fact]
    public async Task Yes_on_a_strip_gone_stale_closes_it_without_an_error()
    {
        // Aynı bayat şeritte "Evet": "Düzeltilecek etiket yok." deyip şerit açık kalıyor, o kodun yazımı kilitli
        // kalıyordu. Şerit hatasız kapanır; kutu operatörün yazdığı değeri korur.
        using var h = MainShellTestHarness.Build();
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Vm.ActivePriceText = "200";
        ShipNow(h, row);

        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.CodePriceError.Should().BeNull();
        h.Vm.CodePriceCorrection.Should().BeNull("uygulanacak bir şey yoktu");
        h.Vm.ActivePriceText.Should().Be("200");
        DbPrice(h, row.Id).Should().Be(250m);
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@yeni", "A1 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(200m);
    }

    [Fact]
    public async Task Code_whose_only_labels_are_shipped_asks_nothing()
    {
        // Kargoya verilmiş etiket değişmez: değiştirilecek etiket yokken şerit "A1 kodunun 0 etiketi…" diye
        // açılmamalı.
        using var h = MainShellTestHarness.Build();
        var shipped = await ShippedLabel(h, "@gitti");

        h.Vm.ActivePriceText = "200";

        h.Vm.CodePriceQuestion.Should().BeNull();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@yeni", "A1 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(200m);
        DbPrice(h, shipped).Should().Be(250m);
    }

    [Fact]
    public async Task Question_says_shipped_labels_do_not_change()
    {
        using var h = MainShellTestHarness.Build();
        await ShippedLabel(h, "@gitti");
        await WithCodeLabels(h, 250m, "@a");

        h.Vm.ActivePriceText = "200";

        h.Vm.CodePriceQuestion.Should().Be(
            "A1 kodunun 1 etiketi 250,00 TL. Hepsi 200,00 TL olsun mu? Kargoya verilmiş 1 etiket değişmez.");
    }

    [Fact]
    public async Task Correction_of_another_streams_code_leaves_the_box_and_the_pending_question()
    {
        // Rapordan (K6) önceki yayının A1'i düzeltildi; bu yayında da A1 var, kutuda 280 yazılmış, şerit bekliyor.
        // Başka yayının düzeltmesi bu yayının kutusuna ve şeridine dokunmaz.
        using var h = MainShellTestHarness.Build();
        var oldSession = h.Sessions.GetActive()!.Id;
        var old = h.Labels.Add(oldSession, MainShellTestHarness.ChatVm("@eski", "A1").Message, 250m, "A1");
        h.Sessions.End(oldSession);
        h.Sessions.Start("Yeni Yayın", new[] { "instagram" });
        await WithCodeLabels(h, 300m, "@a");
        h.Vm.ActivePriceText = "280";
        h.Vm.CodePriceQuestion.Should().NotBeNull();

        h.Vm.ApplyPriceCorrection(h.Labels.CorrectPrices(new[] { old.Id }, 200m));

        DbPrice(h, old.Id).Should().Be(200m);
        h.Vm.CodePriceQuestion.Should().NotBeNull("başka yayının düzeltmesi bu yayının sorusunu kapatmaz");
        h.Vm.ActivePriceText.Should().Be("280");
    }

    [Fact]
    public void Yes_follows_the_license_state()
    {
        using var h = MainShellTestHarness.Build();
        var raised = 0;
        h.Vm.ConfirmCodePriceCommand.CanExecuteChanged += (_, _) => raised++;

        h.Vm.IsLicenseWritable = false;

        raised.Should().BeGreaterThan(0, "lisans yazılamaz olunca \"Evet\" devre dışı kalır");
        h.Vm.ConfirmCodePriceCommand.CanExecute(null).Should().BeFalse();
    }

    private sealed class GatedPrinter : OrderDeck.Labeling.ILabelPrinter
    {
        public System.Threading.ManualResetEventSlim Release { get; } = new(false);

        public void Print(System.Collections.Generic.IReadOnlyList<OrderDeck.Core.Sales.Label> labels,
            System.Collections.Generic.IReadOnlySet<string>? recipientPaysLabelIds = null,
            System.Collections.Generic.IReadOnlySet<string>? correctionLabelIds = null)
            => Release.Wait(TimeSpan.FromSeconds(10));

        public void PrintGiftLabels(System.Collections.Generic.IReadOnlyList<OrderDeck.Core.Sales.Label> labels) { }
    }

    [Fact]
    public async Task Yes_is_refused_while_a_label_of_the_code_is_on_the_printer()
    {
        var printer = new GatedPrinter();
        using var h = MainShellTestHarness.Build(printerOverride: printer);
        await WithCodeLabels(h, 250m, "@a");
        var id = h.Vm.PrintQueue[0].Id;
        var printing = h.Vm.PrintCommand.ExecuteAsync(null);   // baskı uçuşta
        try
        {
            h.Vm.ActivePriceText = "200";

            h.Vm.ConfirmCodePriceCommand.Execute(null);

            h.Vm.CodePriceError.Should().Contain("Baskı bitince tekrar deneyin");
            h.Vm.CodePriceQuestion.Should().NotBeNull("şerit açık kalır");
            DbPrice(h, id).Should().Be(250m);
        }
        finally
        {
            printer.Release.Set();
            await printing;
        }
        h.Vm.ConfirmCodePriceCommand.Execute(null);
        DbPrice(h, id).Should().Be(200m, "baskı bitince Evet tutar");
    }

    [Fact]
    public async Task Strip_error_stays_when_a_comment_rechecks_the_strip_and_goes_on_a_price_edit()
    {
        // "Evet" yazıcıdaki etiket yüzünden reddedildi, sebep şeritte. Şerit beklerken tıklanan yorum şeridi yeniden
        // denetler — sebep hâlâ geçerli, hata satırı kalmalı. Operatör fiyata dokununca yeni soru: eski hata kalkar.
        var printer = new GatedPrinter();
        using var h = MainShellTestHarness.Build(printerOverride: printer);
        await WithCodeLabels(h, 250m, "@a");
        var printing = h.Vm.PrintCommand.ExecuteAsync(null);   // baskı uçuşta
        try
        {
            h.Vm.ActivePriceText = "200";
            h.Vm.ConfirmCodePriceCommand.Execute(null);
            h.Vm.CodePriceError.Should().Contain("Baskı bitince tekrar deneyin");

            await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));

            h.Vm.CodePriceBlocked.Should().Be("Önce fiyat değişikliğini onayla — yazılmadı: @b");
            h.Vm.CodePriceError.Should().Contain("Baskı bitince tekrar deneyin", "yorum tıklaması hatayı silmez");

            h.Vm.ActivePriceText = "190";

            h.Vm.CodePriceQuestion.Should().NotBeNull();
            h.Vm.CodePriceError.Should().BeNull("operatör fiyatı değiştirdi");
        }
        finally
        {
            printer.Release.Set();
            await printing;
        }
    }

    [Fact]
    public async Task Yes_lists_people_who_were_asked_the_old_amount_inline()
    {
        // K5 paketinin ödeme tarafı şeritten de çalışır: 250 TL istenmiş kişi "düzeltilmiş isteği gönder"
        // listesinde, kutunun altında (çekmece açılmaz).
        var drawers = new CountingDrawers();
        using var h = MainShellTestHarness.Build(drawers, paymentFollowUp: (db, customers, labels) =>
            new OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests(customers, labels,
                new PaymentJobRepository(db), OrderDeck.Tests.TestHelpers.LocalShipping.Off(),
                (_, _, _, _, _) => Task.FromResult(new OrderDeck.App.Services.PaymentRequestOutcome(
                    OrderDeck.App.Services.PaymentRequestResult.Sent))));
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        var jobs = new PaymentJobRepository(h.Db);
        var job = jobs.FindOrCreate(row.Label.CustomerId,
            OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests.SessionScope(h.Sessions.GetActive()!.Id), 250m);
        jobs.MarkNoBalance(job.Id, null, job.Revision).Should().BeTrue();
        jobs.MarkDelivered(job.Id, 250m);

        h.Vm.ActivePriceText = "200";
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.CodePriceCorrection!.ResendRows.Should().ContainSingle().Which.Candidate.NewTotal.Should().Be(200m);
        drawers.Count.Should().Be(0, "çekmece açılmaz — kod/fiyat kutusu ve kuyruk örtülmez");
    }

    private static MainShellTestHarness.Harness BuildWithPayments(OrderDeck.App.Services.Drawers.IDrawerService? drawers = null)
        => MainShellTestHarness.Build(drawers, paymentFollowUp: (db, customers, labels) =>
            new OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests(customers, labels,
                new PaymentJobRepository(db), OrderDeck.Tests.TestHelpers.LocalShipping.Off(),
                (_, _, _, _, _) => Task.FromResult(new OrderDeck.App.Services.PaymentRequestOutcome(
                    OrderDeck.App.Services.PaymentRequestResult.Sent))));

    private static string StreamScope(MainShellTestHarness.Harness h)
        => OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests.SessionScope(h.Sessions.GetActive()!.Id);

    /// <summary>Kişiye bu yayında <paramref name="net"/> TL'lik istek teslim edilmiş (bakiye yok).</summary>
    private static void DeliveredRequest(MainShellTestHarness.Harness h, string customerId, decimal net)
    {
        var jobs = new PaymentJobRepository(h.Db);
        var job = jobs.FindOrCreate(customerId, StreamScope(h), net);
        jobs.MarkNoBalance(job.Id, null, job.Revision).Should().BeTrue();
        jobs.MarkDelivered(job.Id, net);
    }

    /// <summary>Kişinin bu yayındaki isteğinin tamamı bakiyeden düşülmüş — ödendi.</summary>
    private static void PaidByBalance(MainShellTestHarness.Harness h, string customerId, decimal total)
    {
        var jobs = new PaymentJobRepository(h.Db);
        var job = jobs.FindOrCreate(customerId, StreamScope(h), total);
        var key = Guid.NewGuid();
        jobs.BeginApply(job.Id, key).Should().BeTrue();
        jobs.MarkApplied(job.Id, key, job.Revision, total).Should().BeTrue();
        jobs.MarkDelivered(job.Id, 0m);
    }

    [Fact]
    public async Task Another_codes_yes_moves_the_unanswered_result_to_the_printed_outcome_strip()
    {
        // FİYAT kutusunun altındaki sonuç tek yuva. A1 250 → 200 dendi, yeniden baskı sorusu ve "düzeltilmiş isteği
        // gönder" cevaplanmadan operatör B2'ye geçip onu da düzeltti: A1'in sonucu iz bırakmadan eziliyordu —
        // kâğıtlarda 250 kalıyor, @a'ya 200'lük düzeltilmiş istek hiç gitmiyor, @odendi'nin farkı unutuluyordu.
        using var h = BuildWithPayments();
        await WithCodeLabels(h, 250m, "@a", "@odendi");
        var rows = h.Vm.PrintQueue.ToList();
        await h.Vm.PrintCommand.ExecuteAsync(null);
        DeliveredRequest(h, rows[0].Label.CustomerId, 250m);
        PaidByBalance(h, rows[1].Label.CustomerId, 250m);
        h.Vm.ActivePriceText = "200";
        h.Vm.ConfirmCodePriceCommand.Execute(null);
        var first = h.Vm.CodePriceCorrection!;
        first.IsReprintAsked.Should().BeTrue();
        first.ResendRows.Should().ContainSingle();
        first.PaidRows.Should().ContainSingle();

        h.Vm.ActiveCode = "B2";
        h.Vm.ActivePriceText = "100";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "B2 aldım"));
        h.Vm.ActivePriceText = "90";
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.CodePriceCorrection.Should().NotBeNull().And.NotBeSameAs(first);
        h.Vm.PrintedLabels.Outcomes.Should().ContainSingle().Which.Should()
            .Contain("(A1)")
            .And.Contain("2 basılmış etiket yeniden basılmadı")
            .And.Contain("düzeltilmiş istek gönderilmedi").And.Contain("• @a")
            .And.Contain("Ödemesi alınmış").And.Contain("• @odendi");
        h.Vm.PrintedLabels.HasUnreadOutcomes.Should().BeTrue("sekme kapalı: başlıkta okunmamış işareti");
    }

    [Fact]
    public async Task Answered_result_replaced_by_another_code_leaves_no_outcome()
    {
        using var h = BuildWithPayments();
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        DeliveredRequest(h, row.Label.CustomerId, 250m);
        h.Vm.ActivePriceText = "200";
        h.Vm.ConfirmCodePriceCommand.Execute(null);
        var first = h.Vm.CodePriceCorrection!;
        first.SkipReprintCommand.Execute(null);   // "Hayır" — operatörün kararı
        await first.ResendCommand.ExecuteAsync(null);
        h.Vm.PrintedLabels.Outcomes.Should().ContainSingle("gönderimin kendi sonucu");

        h.Vm.ActiveCode = "B2";
        h.Vm.ActivePriceText = "100";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "B2 aldım"));
        h.Vm.ActivePriceText = "90";
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.PrintedLabels.Outcomes.Should().ContainSingle("cevaplanmamış iş yok — yeni kayıt düşmez");
    }

    [Fact]
    public async Task Same_codes_second_yes_rebuilds_the_result_with_current_amounts()
    {
        // Aynı kodda ikinci "Evet": yeni sonuç aynı basılmış etiketler ve kişiler için soruyu ve listeyi güncel
        // tutarla yeniden kurar — şeride ayrıca bayat bir kayıt düşmez.
        using var h = BuildWithPayments();
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        DeliveredRequest(h, row.Label.CustomerId, 250m);
        h.Vm.ActivePriceText = "200";
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.ActivePriceText = "190";
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.PrintedLabels.Outcomes.Should().BeEmpty();
        h.Vm.CodePriceCorrection!.IsReprintAsked.Should().BeTrue();
        h.Vm.CodePriceCorrection.ResendRows.Should().ContainSingle().Which.Candidate.NewTotal.Should().Be(190m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_persons_difference_after_a_second_yes_is_against_what_was_paid(bool firstResultClosed)
    {
        // A1 250'den basıldı, @odendi'nin isteği tamamen bakiyeden düşüldü. 200'e "Evet": "fark −50". Operatör yanlış
        // girdiğini fark edip 190'a yine "Evet" dedi (ilk sonucu kapatarak ya da açıkken): ikinci sonuç "fark −10"
        // diyordu, −50 iz bırakmadan gidiyordu. Müşteri 250 ödedi, yeni fiyat 190: fark −60; bu düzeltmenin kendi
        // farkı (−10) yanında.
        using var h = BuildWithPayments();
        await WithCodeLabels(h, 250m, "@odendi");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        PaidByBalance(h, row.Label.CustomerId, 250m);
        h.Vm.ActivePriceText = "200";
        h.Vm.ConfirmCodePriceCommand.Execute(null);
        h.Vm.CodePriceCorrection!.PaidRows.Should().Equal("@odendi — ödendi (bakiye), fark −50,00 TL");
        if (firstResultClosed) h.Vm.CodePriceCorrection.DoneCommand.Execute(null);

        h.Vm.ActivePriceText = "190";
        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.CodePriceCorrection!.PaidRows.Should().Equal(
            "@odendi — ödendi (bakiye), fark −60,00 TL (bu düzeltmede −10,00 TL)");
        DbPrice(h, row.Id).Should().Be(190m);
    }

    /// <summary>A1 250'den yazılıp basıldı, @a'ya 250 TL istek teslim edildi, şeritte 200'e "Evet" dendi: satır
    /// içi sonuçta yanıtlanmamış yeniden baskı sorusu ve @a için düzeltilmiş istek satırı var.</summary>
    private static async Task<(OrderDeck.App.ViewModels.PriceCorrectionViewModel Result, string LabelId)> UnansweredA1Result(
        MainShellTestHarness.Harness h)
    {
        await WithCodeLabels(h, 250m, "@a");
        var row = h.Vm.PrintQueue[0];
        await h.Vm.PrintCommand.ExecuteAsync(null);
        DeliveredRequest(h, row.Label.CustomerId, 250m);
        h.Vm.ActivePriceText = "200";
        h.Vm.ConfirmCodePriceCommand.Execute(null);
        var result = h.Vm.CodePriceCorrection!;
        result.IsReprintAsked.Should().BeTrue();
        result.ResendRows.Should().ContainSingle();
        return (result, row.Id);
    }

    [Fact]
    public async Task Ending_the_stream_moves_the_unanswered_inline_result_to_the_printed_outcome_strip()
    {
        // Sonuç cevaplanmadan yayın bitti: sonuç yeni yayında kuyruğun üstünde kalıyor, sonra başka bir sonuçla iz
        // bırakmadan eziliyordu. Yayın değişince cevaplanmamış işi kalıcı sonuç şeridine düşer.
        using var h = BuildWithPayments();
        await UnansweredA1Result(h);
        h.Dialogs.ConfirmResult = _ => true;     // "Yayını bitirmek istediğinden emin misin?" (mevcut akış)

        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Sessions.GetActive().Should().BeNull();
        h.Vm.CodePriceCorrection.Should().BeNull("önceki yayının sonucu kutunun altında kalmaz");
        h.Vm.PrintedLabels.Outcomes.Should().ContainSingle().Which.Should()
            .Contain("(A1)")
            .And.Contain("yayın değişti")
            .And.Contain("1 basılmış etiket yeniden basılmadı")
            .And.Contain("düzeltilmiş istek gönderilmedi").And.Contain("• @a");
        h.Vm.PrintedLabels.HasUnreadOutcomes.Should().BeTrue("sekme kapalı: başlıkta okunmamış işareti");
    }

    [Fact]
    public async Task Same_code_in_another_stream_does_not_swallow_the_previous_streams_result()
    {
        // Kodlar her yayında yeniden kullanılıyor. Önceki yayının A1 sonucu açıkken (yayın kabuğun dışından
        // değişti) bu yayının A1'i düzeltildi: "aynı kod" sayılıp sonuç iz bırakmadan siliniyordu — @a'ya 200'lük
        // düzeltilmiş istek gitmiyor, yeniden basılmamış kâğıt unutuluyordu. Yeni sonuç önceki yayının etiketlerini ve
        // kişilerini kapsamaz: karşılaştırma (yayın, kod).
        using var h = BuildWithPayments();
        var first = await UnansweredA1Result(h);
        h.Sessions.End(h.Sessions.GetActive()!.Id);
        h.Sessions.Start("Yeni Yayın", new[] { "instagram" });
        await WithCodeLabels(h, 300m, "@b");
        h.Vm.ActivePriceText = "280";
        h.Vm.CodePriceQuestion.Should().NotBeNull();

        h.Vm.ConfirmCodePriceCommand.Execute(null);

        h.Vm.CodePriceCorrection.Should().NotBeNull().And.NotBeSameAs(first.Result);
        DbPrice(h, first.LabelId).Should().Be(200m, "önceki yayının etiketi bu düzeltmeye girmez");
        h.Vm.PrintedLabels.Outcomes.Should().ContainSingle().Which.Should()
            .Contain("(A1)")
            .And.Contain("yayın değişti")
            .And.Contain("1 basılmış etiket yeniden basılmadı")
            .And.Contain("• @a");
        h.Vm.PrintedLabels.HasUnreadOutcomes.Should().BeTrue();
    }

    [Fact]
    public async Task Unanswered_work_on_the_printed_outcome_strip_is_not_dropped_by_the_cap()
    {
        // Kalıcı sonuç şeridi en çok MaxOutcomes kayıt tutuyor, fazlası en eskiden düşüyordu: okunup "Tamam" denmeden
        // açık bırakılan şeritte sonraki düzeltmelerin sonuçları biriktikçe A1'in cevaplanmamış işi (kâğıt yeniden
        // basılmadı, @a'ya düzeltilmiş istek gitmedi) iz bırakmadan siliniyordu. Yalnız bilgi kayıtları düşer.
        using var h = BuildWithPayments();
        await UnansweredA1Result(h);
        h.Dialogs.ConfirmResult = _ => true;     // "Yayını bitirmek istediğinden emin misin?" (mevcut akış)
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        var unanswered = h.Vm.PrintedLabels.Outcomes.Should().ContainSingle().Subject;

        for (var i = 1; i <= OrderDeck.App.ViewModels.PrintedLabelsViewModel.MaxOutcomes + 2; i++)
            h.Vm.PrintedLabels.AddOutcome($"Fiyat düzeltmesi: {i} etiket \"DÜZELTME\" işaretiyle yeniden basıldı.");

        h.Vm.PrintedLabels.Outcomes.Should().HaveCount(OrderDeck.App.ViewModels.PrintedLabelsViewModel.MaxOutcomes);
        h.Vm.PrintedLabels.Outcomes[0].Should().Be(unanswered);
    }

    private sealed class CountingDrawers : OrderDeck.App.Services.Drawers.IDrawerService
    {
        public int Count { get; private set; }

        public Task<bool> ShowAsync(string title, Func<OrderDeck.App.Services.Drawers.Drawer, object> buildContent)
        {
            Count++;
            return Task.FromResult(false);
        }

        public bool CloseTop() => false;
    }

    // ── F4: düzeltmeden sonra kutu ───────────────────────────────────────

    /// <summary>Fiyat düzeltme çekmecesi: yeni fiyatı yazar, onaylar, yeniden baskıyı atlar.</summary>
    private sealed class CorrectingDrawers(string newPrice) : OrderDeck.App.Services.Drawers.IDrawerService
    {
        public MainShellTestHarness.Harness? H { get; set; }

        public Task<bool> ShowAsync(string title, Func<OrderDeck.App.Services.Drawers.Drawer, object> buildContent)
        {
            if (H?.Vm.ActivePriceCorrection is not { } pc) return Task.FromResult(false);
            pc.NewPriceText = newPrice;
            pc.ConfirmCommand.Execute(null);
            pc.SkipReprintCommand.Execute(null);
            return Task.FromResult(true);
        }

        public bool CloseTop() => false;
    }

    private static MainShellTestHarness.Harness BuildCorrecting(string newPrice)
    {
        var drawers = new CorrectingDrawers(newPrice);
        var h = MainShellTestHarness.Build(drawers);
        drawers.H = h;
        return h;
    }

    [Fact]
    public async Task Price_declared_wrong_by_a_correction_does_not_come_back_for_that_code()
    {
        // A1 için yanlışlıkla 250 yazıldı, etiketler basıldı, "Fiyatı düzelt" ile 205 yapıldı. Başka ürüne bakıp
        // A1'e dönünce kutu yine 250 gösterseydi yeni etiket 250 yazılırdı.
        using var h = BuildCorrecting("205");
        SeedCatalog(h, ("p1", "A1", 199m), ("p2", "B", 300m));
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "250";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.SelectBatchCommand.Execute(h.Vm.PrintedLabels.VisibleRows[0].Batch);
        h.Vm.ActiveCode = "B";

        await h.Vm.FixPrintedPriceCommand.ExecuteAsync(null);
        h.Vm.ActivePriceText.Should().Be("300", "kutu B'yi gösteriyor; düzeltme ona dokunmaz");

        h.Vm.ActiveCode = "A1";

        h.Vm.ActivePriceText.Should().Be("205", "kodun bu yayındaki fiyatı — ne yanlış 250 ne katalogdaki 199");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@c", "A1 aldım"));
        h.Vm.PrintQueue.Should().ContainSingle().Which.Price.Should().Be(205m);
    }

    [Fact]
    public async Task Correction_of_the_active_code_updates_the_box_showing_the_old_price()
    {
        // Kod hâlâ A1, kutu yanlış 250'yi gösteriyor; kuyrukta düzeltildi: sonraki yorum yine 250 yazılırdı.
        using var h = BuildCorrecting("205");
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "250";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);

        await h.Vm.FixQueuePriceCommand.ExecuteAsync(null);

        h.Vm.ActivePriceText.Should().Be("205");
        h.Vm.IsPriceCarriedOver.Should().BeFalse();
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@b", "A1 aldım"));
        h.Vm.PrintQueue.Select(q => q.Price).Should().Equal(205m, 205m);
    }

    [Fact]
    public async Task Correction_of_the_active_code_closes_a_pending_change_and_shows_the_corrected_price()
    {
        // F4: kutuda A1 için 260 yazılmış, şerit bekliyor; çekmeceden A1 205'e düzeltildi. Kod artık 205 — şeridin
        // "250 → 260" sorusu bayat, kutu kodun fiyatını gösterir.
        using var h = BuildCorrecting("205");
        h.Vm.ActiveCode = "A1";
        h.Vm.ActivePriceText = "250";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@a", "A1 aldım"));
        h.Vm.SelectedQueueItems.Add(h.Vm.PrintQueue[0]);
        h.Vm.ActivePriceText = "260";
        h.Vm.CodePriceQuestion.Should().NotBeNull();

        await h.Vm.FixQueuePriceCommand.ExecuteAsync(null);

        h.Vm.CodePriceQuestion.Should().BeNull();
        h.Vm.ActivePriceText.Should().Be("205");
    }
}
