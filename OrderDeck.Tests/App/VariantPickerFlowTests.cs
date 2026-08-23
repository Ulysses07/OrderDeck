using FluentAssertions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Çift tıklamadan sipariş satırına kadar olan akış: yorumdaki beden bulunur,
/// belirsizlik varsa çekmece açılır. Tasarımın beş kuralı burada kilitleniyor —
/// çekmece yalnız gerektiğinde açılır, Esc hiçbir şey yazmaz, her işaretli
/// değer ayrı satır olur, bilinmeyen kod ve eksensiz ürün eski davranışta kalır.
/// </summary>
public class VariantPickerFlowTests
{
    /// <summary>
    /// Sahte çekmece servisi. İçerik fabrikasını <b>hiç çağırmaz</b>: çağırsaydı
    /// gerçek bir WPF UserControl kurulur ve test STA thread isterdi. Akış
    /// içeriğe <see cref="MainShellViewModel.ActiveVariantPicker"/> üzerinden
    /// erişiliyor, tam da üretimdeki gibi.
    /// </summary>
    private sealed class FakeDrawerService : IDrawerService
    {
        private readonly Func<VariantPickerViewModel, bool> _decide;

        public FakeDrawerService(Func<VariantPickerViewModel, bool> decide) => _decide = decide;

        /// <summary>Testte Build()'dan SONRA doldurulur (kabuk ctor'da servisi ister).</summary>
        public MainShellViewModel? Vm { get; set; }

        public int ShowCount { get; private set; }

        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            ShowCount++;
            var picker = Vm!.ActiveVariantPicker!;
            return Task.FromResult(_decide(picker));
        }

        // DrawerStack.CloseTop() de top.Close(false) diyor — ESC iptal demek.
        public bool CloseTop() => false;
    }

    // Renk = satıcı ekseni (1), Beden = izleyici ekseni (2).
    private static readonly CatalogProduct Elbise = new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    // Kolye'nin hiç ekseni yok: stok ürün düzeyinden düşer.
    private static readonly CatalogProduct Kolye = new(
        "p2", null, "SK00002", "SK00002", "Kolye", 50m, null,
        null, null, null, null, null, 0);

    private static MainShellTestHarness.Harness Seed(FakeDrawerService drawers)
    {
        // Harness'i ÇAĞIRAN sahipleniyor (using ile) — burada yıkma, döndürüyoruz.
        var h = MainShellTestHarness.Build(drawers);
        drawers.Vm = h.Vm;

        var variants = new[]
        {
            new CatalogVariant("v1", "p1", "Siyah", "S", null, true, 0),
            new CatalogVariant("v2", "p1", "Siyah", "M", null, true, 0),
            new CatalogVariant("v3", "p1", "Siyah", "L", null, true, 0),
            new CatalogVariant("v4", "p2", null, null, null, true, 0),
        };

        // SortOrder'ı dizideki konumdan veriyoruz — GetVariants "ORDER BY
        // SortOrder" diyor, hepsine 0 verseydik sıra SQLite'ın eşitlik
        // durumundaki davranışına kalırdı.
        new CatalogReplicaRepository(h.Db).Replace(
            new[] { Elbise, Kolye },
            variants.Select((v, i) => v with { SortOrder = i }).ToList(),
            Array.Empty<CatalogCategory>(),
            new[]
            {
                new CatalogBroadcastCode("p1", "Siyah", "Ateş", SearchNormalizer.Normalize("Ateş"), 0, 0),
                new CatalogBroadcastCode("p2", null, "Buz", SearchNormalizer.Normalize("Buz"), 0, 1),
            });

        h.Vm.ActivePriceText = "100";
        return h;
    }

    private static OrderDeck.Core.Sales.Label LastLabel(MainShellTestHarness.Harness h) =>
        h.Vm.PrintQueue[^1].Label;

    [Fact]
    public async Task Tek_tam_eslesmede_cekmece_ACILMAZ()
    {
        var drawers = new FakeDrawerService(_ => throw new Xunit.Sdk.XunitException(
            "tek ve tam eşleşmede çekmece açılmamalıydı"));
        using var h = Seed(drawers);
        h.Vm.ActiveCode = "ateş";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "ateş m"));

        // Kural 1: operatör hiçbir şey tıklamaz, akış kesilmez.
        drawers.ShowCount.Should().Be(0);
        h.Vm.PrintQueue.Should().ContainSingle();
        LastLabel(h).ProductVariantId.Should().Be("v2");
    }

    [Fact]
    public async Task Esc_hicbir_siparis_yazmaz()
    {
        var drawers = new FakeDrawerService(_ => false);
        using var h = Seed(drawers);
        h.Vm.ActiveCode = "ateş";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "bana da"));

        drawers.ShowCount.Should().Be(1);
        // Kabul kriteri 8 — vazgeçilen çekmeceden tek satır bile sızmamalı.
        h.Vm.PrintQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task Iki_isaretli_deger_IKI_ayri_satir_yazar()
    {
        var drawers = new FakeDrawerService(picker =>
        {
            // İkisi de yorumda geçiyor, ikisi de önceden işaretli gelmeli.
            picker.SelectedValues.Should().Equal("M", "L");
            return true;
        });
        using var h = Seed(drawers);
        h.Vm.ActiveCode = "ateş";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "ateş m l"));

        drawers.ShowCount.Should().Be(1);
        // Kabul kriteri 9 — her değer AYRI sipariş satırı.
        h.Vm.PrintQueue.Should().HaveCount(2);
        h.Vm.PrintQueue.Select(l => l.Label.ProductVariantId).Should().Equal("v2", "v3");
    }

    [Fact]
    public async Task Bilinmeyen_kodda_cekmece_acilmaz_satir_yazilir()
    {
        var drawers = new FakeDrawerService(_ => throw new Xunit.Sdk.XunitException(
            "bilinmeyen kodda seçilecek bir şey yok"));
        using var h = Seed(drawers);
        h.Vm.ActiveCode = "zzz";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "zzz m"));

        // Kabul kriteri 10 — satır bugünkü gibi yazılır, katalog kimlikleri boş.
        drawers.ShowCount.Should().Be(0);
        h.Vm.PrintQueue.Should().ContainSingle();
        LastLabel(h).ProductId.Should().BeNull();
        LastLabel(h).ProductVariantId.Should().BeNull();
    }

    [Fact]
    public async Task Eksensiz_urunde_cekmece_acilmaz_varyant_null()
    {
        var drawers = new FakeDrawerService(_ => throw new Xunit.Sdk.XunitException(
            "eksensiz üründe çekmece açılmamalıydı"));
        using var h = Seed(drawers);
        h.Vm.ActiveCode = "buz";

        await h.Vm.AddChatToQueueAsync(MainShellTestHarness.ChatVm("@ali", "buz"));

        // Kabul kriteri 11 — stok ÜRÜNDEN düşer, varyanttan değil.
        drawers.ShowCount.Should().Be(0);
        h.Vm.PrintQueue.Should().ContainSingle();
        LastLabel(h).ProductId.Should().Be("p2");
        LastLabel(h).ProductVariantId.Should().BeNull();
    }
}
