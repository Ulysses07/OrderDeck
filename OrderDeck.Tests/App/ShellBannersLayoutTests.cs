using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using OrderDeck.App.Views.Shell;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kabuk şeritleri aynı hücreyi paylaşır; sonra gelen öncekini örter. "Kendi yayını uzaktan bitirildi" bandı
/// (W2-09, §5.7) yayının geri kalanı boyunca kalıcıdır: üstte dursaydı "Lisans sunucusuna erişilemiyor" ve "Lisans
/// süresi doluyor" uyarılarını yayın bitene kadar gizlerdi.
/// </summary>
public class ShellBannersLayoutTests
{
    private static int IndexOfBannerBoundTo(Grid grid, string path) =>
        grid.Children.OfType<Border>().ToList().FindIndex(b =>
            BindingOperations.GetBinding(b, UIElement.VisibilityProperty)?.Path.Path == path);

    [Fact]
    public void Kalici_uzaktan_bitirildi_bandi_sunucu_ve_lisans_uyarilarinin_altinda_durur()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var grid = (Grid)new ShellBanners().Content;
            var band = IndexOfBannerBoundTo(grid, "RemoteSessions.OwnSessionBand");
            var server = IndexOfBannerBoundTo(grid, "ServerOfflineBanner");
            var license = IndexOfBannerBoundTo(grid, "LicenseExpiryBanner");

            band.Should().BeGreaterThanOrEqualTo(0, "ön koşul: band şeritlerde");
            server.Should().BeGreaterThanOrEqualTo(0);
            license.Should().BeGreaterThanOrEqualTo(0);
            band.Should().BeLessThan(server, "sunucu uyarısı kalıcı bandın üstünde görünmeli");
            band.Should().BeLessThan(license, "lisans uyarısı kalıcı bandın üstünde görünmeli");
        });

        Assert.Null(error);
    }
}
