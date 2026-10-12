using FluentAssertions;
using OrderDeck.App.Views.Drawers;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>Yedek onayı kartının fiyat okuması (K8): FİYAT kutusuyla aynı kural — eksi reddedilir, 0 TL satır
/// içi ikinci tıklamayla onaylanır.</summary>
public sealed class BackupRowViewModelTests
{
    private static BackupRowViewModel Row()
        => new(new Label("b1", "s1", "c1", "instagram", "@yedek", "ben de", "A12", 250m, 1000L, null,
            IsBackupPromoted: true, ParentLabelId: "p1", IsTentativeBackup: true));

    [Fact]
    public void Zero_price_needs_a_second_click_before_it_is_confirmed()
    {
        var row = Row();
        row.PriceText = "0";

        row.ReadPrice().Should().BeNull("ilk tıklama sorar");
        row.ErrorText.Should().Contain("0 TL");
        row.ActionButtonText.Should().Be("Evet, 0 TL");

        row.ReadPrice().Should().Be(0m, "ikinci tıklama onaylar");
    }

    [Fact]
    public void Editing_the_price_drops_a_pending_zero_confirmation()
    {
        var row = Row();
        row.PriceText = "0";
        row.ReadPrice();

        row.PriceText = "00";

        row.ActionButtonText.Should().Be("Onayla");
        row.ErrorText.Should().BeNull();
        row.ReadPrice().Should().BeNull("yeni metin yeniden sorulur");
    }

    [Theory]
    [InlineData("1.500", 1500)]
    [InlineData("99,50", 99.50)]
    public void Price_is_read_with_the_shared_rule(string text, double expected)
    {
        var row = Row();
        row.PriceText = text;

        row.ReadPrice().Should().Be((decimal)expected);
    }

    [Fact]
    public void Negative_price_is_rejected_inline()
    {
        var row = Row();
        row.PriceText = "-5";

        row.ReadPrice().Should().BeNull();
        row.ErrorText.Should().Contain("eksi");
    }
}
