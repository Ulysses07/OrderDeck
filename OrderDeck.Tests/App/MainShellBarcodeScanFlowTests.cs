using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kod kutusuna <b>barkod okutulduktan sonra</b> kabuğun geri kalanı.
///
/// <para>Barkod yalnızca bir keşif yolu: kartı açar ve orada biter. Kutuda
/// duran metin barkod olsa bile aşağıya —siparişe, sohbet süzgecine,
/// sayaçlara— akması gereken şey ürünün <b>yayın kodu</b>dur, çünkü
/// izleyiciler yoruma o kodu yazıyor ve bütün sipariş akışı onun üzerine
/// kurulu. Bu ayrım kopsaydı hata yayının tam ortasında, en pahalı anda
/// ortaya çıkardı: sohbet süzgeci bomboş kalır, siparişlere okunaksız bir
/// sayı yazılır ve ürün sayacı sıfırlanmış görünürdü.</para>
/// </summary>
public class MainShellBarcodeScanFlowTests
{
    // Renk = satıcı ekseni (1), Beden = izleyici ekseni (2).
    private static readonly CatalogProduct Elbise = new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private const string BarkodM = "0000000042";

    private static MainShellTestHarness.Harness Seed()
    {
        // Harness'i ÇAĞIRAN sahipleniyor (using ile) — burada yıkma, döndürüyoruz.
        var h = MainShellTestHarness.Build();

        new CatalogReplicaRepository(h.Db).Replace(
            new[] { Elbise },
            new[]
            {
                new CatalogVariant("v1", "p1", "Siyah", "M", BarkodM, true, 0),
                new CatalogVariant("v2", "p1", "Siyah", "L", "0000000043", true, 1),
            },
            Array.Empty<CatalogCategory>(),
            new[]
            {
                new CatalogBroadcastCode(
                    "p1", "Siyah", "Ateş", SearchNormalizer.Normalize("Ateş"), 0, 0),
            });

        h.Vm.ActivePriceText = "100";
        return h;
    }

    [Fact]
    public async Task Barkod_okutulunca_siparise_YAYIN_KODU_yazilir()
    {
        // Sipariş satırındaki kod müşteri etiketine basılıyor ve ürün sayacı
        // da onun üzerinden dönüyor. Barkod sızsaydı etikette 10 haneli
        // anlamsız bir sayı görünürdü.
        using var h = Seed();
        h.Vm.ActiveCode = BarkodM;

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "ateş m"));

        h.Vm.PrintQueue.Should().ContainSingle();
        h.Vm.PrintQueue[0].Label.Code.Should().Be("Ateş");
    }

    [Fact]
    public void Barkod_okutulunca_sohbet_suzgeci_YAYIN_KODUNU_arar()
    {
        // İzleyici yoruma "ateş" yazıyor, barkodu değil. Süzgeç kutudaki ham
        // metni arasaydı "yalnız aktif kod" açıkken sohbet bomboş kalır ve
        // operatör siparişleri göremezdi.
        using var h = Seed();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "ateş alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "başka bir şey"));
        h.Vm.ActiveCode = BarkodM;

        h.Vm.OnlyActiveCode = true;

        h.Vm.ChatView.Cast<ChatMessageViewModel>()
            .Should().ContainSingle().Which.Username.Should().Be("ayse");
    }

    [Fact]
    public async Task Barkod_okutmak_urun_sayacini_sifirlamaz()
    {
        // Operatör önce kodu yazıp sipariş alıyor, sonra aynı ürünün etiketini
        // okutuyor. Sayaç ham metinle sorgulansaydı okutmadan sonra "0 sipariş"
        // gösterir ve operatör siparişlerin kaybolduğunu sanırdı.
        using var h = Seed();
        h.Vm.ActiveCode = "ateş";
        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "ateş m"));
        h.Vm.RefreshHeroStats();
        h.Vm.ProductOrderCount.Should().Be(1);

        h.Vm.ActiveCode = BarkodM;
        h.Vm.RefreshHeroStats();

        h.Vm.ProductOrderCount.Should().Be(1);
    }

    [Fact]
    public void Cozulmeyen_metin_hala_kutudaki_ham_haliyle_kullanilir()
    {
        // Katalogda olmayan bir kod da süzgeci beslemeye devam etmeli: operatör
        // henüz senkronlanmamış bir ürünü kodla takip edebiliyor. Çözüm yoksa
        // düşülecek yer kutudaki metindir.
        using var h = Seed();
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("ayse", "Z999 alıyorum"));
        h.Vm.ChatMessages.Add(MainShellTestHarness.ChatVm("fatma", "başka"));
        h.Vm.ActiveCode = "Z999";

        h.Vm.OnlyActiveCode = true;

        h.Vm.ChatView.Cast<ChatMessageViewModel>()
            .Should().ContainSingle().Which.Username.Should().Be("ayse");
    }
}
