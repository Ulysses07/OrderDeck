using FluentAssertions;
using OrderDeck.App.Services.Sync;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>Satış akışının durum satırı (spec 2026-10-13 §5.7 "Akış"; W2-03): yalnız mod On'da; "güncel ✓ (SS:DD)",
/// "yetişiyor", "N öğe bekliyor", "M öğe atlandı".</summary>
public sealed class SalesFeedStatusFormatterTests
{
    private static readonly DateTimeOffset CaughtUp = new(2026, 10, 17, 18, 14, 0, TimeSpan.Zero);

    [Fact]
    public void Mod_Offta_satir_yok()
    {
        SalesFeedStatusFormatter.Format(SalesFeedStatus.Off, skipped: 3).Should().BeNull("mod Off'ta satır gösterilmez");
        SalesFeedStatusFormatter.Format(null, skipped: 0).Should().BeNull();
    }

    [Fact]
    public void Mod_On_acilisi_beklerken_satir_senkron_aciliyor_der()
    {
        // W2-10 (§8.4-1): sunucu modu On, yerel kip bir giden kuyruk boşalmadığı için henüz Off.
        var s = SalesFeedStatusFormatter.Format(SalesFeedStatus.Opening, skipped: 2)!.Value;

        s.Text.Should().Be("Senkron açılıyor; bekleyen gönderim var");
        s.Healthy.Should().BeFalse();
        SalesFeedStatusFormatter.Tooltip(s, 0).Split('\n').Should().Equal(s.Text, SalesCommandMessages.OpeningPendingAdvice);
        SalesFeedStatusFormatter.Format(SalesFeedStatus.Opening, 0, handingOver: true, [])!.Value.Text
            .Should().Be(s.Text, "devir sorgusu yalnız mod On'da");
    }

    [Fact]
    public void Guncel_son_tam_yetismenin_saatiyle_saglikli()
    {
        var s = SalesFeedStatusFormatter.Format(new SalesFeedStatus(true, CaughtUp, false, 0), 0)!.Value;

        s.Text.Should().Be($"Satış: güncel ✓ ({CaughtUp.ToLocalTime():HH:mm})");
        s.Healthy.Should().BeTrue();
        SalesFeedStatusFormatter.Tooltip(s, 0).Should().Be(s.Text);
    }

    [Fact]
    public void Yetisiyor_sayfa_sinirinda_ya_da_hic_yetisilmediyse()
    {
        SalesFeedStatusFormatter.Format(new SalesFeedStatus(true, CaughtUp, true, 0), 0)!.Value.Text
            .Should().Be("Satış: yetişiyor");
        SalesFeedStatusFormatter.Format(new SalesFeedStatus(true, null, false, 0), 0)!.Value
            .Should().Be(new SyncStatusFormatter.Status("Satış: yetişiyor", false));
    }

    [Fact]
    public void Bekleyen_oge_sayisi_ve_ne_yapilacagi()
    {
        var s = SalesFeedStatusFormatter.Format(new SalesFeedStatus(true, CaughtUp, false, 4), 0)!.Value;

        s.Text.Should().Be("Satış: 4 öğe bekliyor");
        s.Healthy.Should().BeFalse();
        SalesFeedStatusFormatter.Tooltip(s, 0).Split('\n').Should().Equal(s.Text, SalesFeedStatusFormatter.WaitingAdvice);
    }

    [Fact]
    public void Atlanan_oge_arkaya_eklenir_satir_sari_ipucu_destek()
    {
        var s = SalesFeedStatusFormatter.Format(new SalesFeedStatus(true, CaughtUp, false, 0), 2)!.Value;

        s.Text.Should().Be($"Satış: güncel ✓ ({CaughtUp.ToLocalTime():HH:mm}) · 2 öğe atlandı");
        s.Healthy.Should().BeFalse();
        SalesFeedStatusFormatter.Tooltip(s, 2).Split('\n').Should().Equal(s.Text, SalesFeedStatusFormatter.SkippedAdvice);
    }
}
