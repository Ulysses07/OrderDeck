using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Hero şeridindeki dört sayaç + ciro maskesi. Harness gerçek repo'lar
/// kullanıyor, bu yüzden sayaçlar SQL'in kendisini de doğruluyor.
/// </summary>
public class MainShellHeroStatsTests
{
    [Fact]
    public void Queue_count_tracks_the_print_queue()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.QueueCount.Should().Be(0);
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 100m);

        h.Vm.QueueCount.Should().Be(1);
    }

    [Fact]
    public void Product_count_counts_only_the_active_code()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "A100";
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "fatma", 100m);
        h.Vm.ActiveCode = "B200";
        MainShellTestHarness.EnqueueLabel(h.Vm, "zeynep", 100m);

        h.Vm.ProductOrderCount.Should().Be(1);

        h.Vm.ActiveCode = "A100";
        h.Vm.ProductOrderCount.Should().Be(2);
    }

    [Fact]
    public void Product_count_is_zero_when_no_code_is_active()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 100m);

        h.Vm.ActiveCode = "";

        // Kod yokken "BU ÜRÜNDEN" kutusu anlamsız — sayaç sıfırlanır, view
        // kutuyu soluklaştırır.
        h.Vm.ProductOrderCount.Should().Be(0);
    }

    [Fact]
    public async Task Session_totals_count_printed_labels_only()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 150m);

        // Henüz basılmadı → GetSessionTotals PrintedAt IS NOT NULL istiyor.
        h.Vm.SessionLabelCount.Should().Be(0);
        h.Vm.SessionRevenue.Should().Be(0m);

        // PrintCommand async (UI freeze fix 2026-05-13) — ExecuteAsync await edilir.
        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Vm.SessionLabelCount.Should().Be(1);
        h.Vm.SessionRevenue.Should().Be(150m);
    }

    [Fact]
    public async Task Revenue_mask_hides_the_amount_but_not_the_value()
    {
        var h = MainShellTestHarness.Build();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ayse", 150m);
        await h.Vm.PrintCommand.ExecuteAsync(null);

        h.Vm.SessionRevenueText.Should().Contain("150");

        h.Vm.ToggleRevenueMaskCommand.Execute(null);

        // Yayında ekran paylaşılıyor olabilir; metin gizlenir ama sayı durur.
        h.Vm.IsRevenueMasked.Should().BeTrue();
        h.Vm.SessionRevenueText.Should().Be("₺ ••••");
        h.Vm.SessionRevenue.Should().Be(150m);
    }

    [Fact]
    public void Active_code_change_loads_the_product_card()
    {
        var h = MainShellTestHarness.Build();

        h.Vm.ActiveCode = "A100";

        h.Vm.ProductCard.Code.Should().Be("A100");
        // Kod tanınmıyor → kart satır-içi tanımlama moduna düşer (pop-up yok).
        h.Vm.ProductCard.IsEditing.Should().BeTrue();
    }

    [Fact]
    public void Stream_duration_text_is_empty_without_an_active_session()
    {
        var h = MainShellTestHarness.Build();
        // EndStreamCommand yerine servisi doğrudan çağırıyoruz: komut async ve
        // onay MessageBox'ı açabiliyor — test sürecinde diyalog istemiyoruz.
        h.Sessions.End(h.Sessions.GetActive()!.Id);

        h.Vm.RefreshHeroStats();

        h.Vm.StreamDurationText.Should().BeEmpty();
    }

    [Fact]
    public void Stream_duration_text_is_hh_mm_ss()
    {
        var h = MainShellTestHarness.Build();       // session StartedAt = 1000
        h.Clock.Setup(c => c.UnixNow()).Returns(1000L + 3661L);

        h.Vm.RefreshHeroStats();

        h.Vm.StreamDurationText.Should().Be("01:01:01");
    }
}
