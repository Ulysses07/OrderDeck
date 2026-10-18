using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using OrderDeck.App.Views.Shell;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kabuk şeritleri: geçici şeritler aynı hücreyi paylaşır (sonra gelen öncekini örter), ama zeminleri yarı saydam
/// (<c>OD.Brush.AmberTint</c> ≈ %8 opak) — üst üste binen iki şeritte iki metin aynı satıra çizilir, ikisi de okunmaz.
/// "Kendi yayını uzaktan bitirildi" bandı (W2-09, §5.7) yayının geri kalanı boyunca kalıcıdır; spec §10.4 -10/-11'de
/// tam o sırada "kâğıtları imha et" şeridi gelir. Band bu yüzden geçici şeritlerle aynı hücrede durmaz: ikisi birlikte
/// görünürken dikdörtgenleri kesişmez.
/// </summary>
public class ShellBannersLayoutTests
{
    /// <summary>Şeritlerin bağlandığı yüzey (bağlamanın okuduğu adlarla); komutlar yok — bağlama hatası yalnız iz.</summary>
    public sealed class RemoteSessionsStub
    {
        public string? OwnSessionBand { get; init; }
        public string? ForceEndProblem { get; init; }
        public string? ForceEndPrompt { get; init; }
        public bool CanRetryForceEnd { get; init; }
    }

    public sealed class ShellStub
    {
        public RemoteSessionsStub RemoteSessions { get; init; } = new();
        public string? ServerOfflineBanner { get; init; }
        public string? LicenseExpiryBanner { get; init; }
        public string? SalesModeOffBanner { get; init; }
        public string? SalesPushWarning { get; init; }
        public string? BackupProblem { get; init; }
        public bool CanRetryBackup { get; init; }
        public bool IsInBackupSelectionMode { get; init; }
        public bool IsGiveawayActive { get; init; }
    }

    private const string BandPath = "RemoteSessions.OwnSessionBand";
    private const string BandText = "Bu yayın PC-B'den bitirildi; yayın sürer, etiket ve baskı kesilmez.";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deep in Descendants(child)) yield return deep;
        }
    }

    private static Border BannerBoundTo(DependencyObject root, string path) =>
        Descendants(root).OfType<Border>().Single(b =>
            BindingOperations.GetBinding(b, UIElement.VisibilityProperty)?.Path.Path == path);

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        new(element.TransformToAncestor(root).Transform(new Point(0, 0)),
            new Size(element.ActualWidth, element.ActualHeight));

    private static ShellBanners Layout(ShellStub context)
    {
        var banners = new ShellBanners { DataContext = context, Width = 900 };
        banners.Measure(new Size(900, double.PositiveInfinity));
        banners.Arrange(new Rect(banners.DesiredSize));
        ThemeTestHost.Pump();                                   // bağlamalar etkinleşir
        banners.UpdateLayout();
        banners.Measure(new Size(900, double.PositiveInfinity));
        banners.Arrange(new Rect(banners.DesiredSize));
        banners.UpdateLayout();
        return banners;
    }

    [Theory]
    [InlineData("SalesPushWarning")]
    [InlineData("LicenseExpiryBanner")]
    [InlineData("ServerOfflineBanner")]
    public void Kalici_band_gecici_seritle_birlikte_gorunurken_ust_uste_binmez(string transientPath)
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            const string text = "1 etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.";
            var context = new ShellStub
            {
                RemoteSessions = new RemoteSessionsStub { OwnSessionBand = BandText },
                SalesPushWarning = transientPath == "SalesPushWarning" ? text : null,
                LicenseExpiryBanner = transientPath == "LicenseExpiryBanner" ? text : null,
                ServerOfflineBanner = transientPath == "ServerOfflineBanner" ? text : null,
            };
            var banners = Layout(context);

            var band = BannerBoundTo(banners, BandPath);
            var transient = BannerBoundTo(banners, transientPath);
            band.ActualHeight.Should().BeGreaterThan(0, "ön koşul: band görünür");
            transient.ActualHeight.Should().BeGreaterThan(0, "ön koşul: geçici şerit görünür");

            var a = Bounds(band, banners);
            var b = Bounds(transient, banners);
            Rect.Intersect(a, b).IsEmpty.Should().BeTrue(
                $"yarı saydam zeminde iki metin üst üste okunmaz (band {a}, şerit {b})");
        });

        Assert.Null(error);
    }

    [Fact]
    public void Yalniz_band_gorunurken_ustunde_bosluk_kalmaz()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var banners = Layout(new ShellStub { RemoteSessions = new RemoteSessionsStub { OwnSessionBand = BandText } });

            var band = BannerBoundTo(banners, BandPath);
            band.ActualHeight.Should().BeGreaterThan(0);
            Bounds(band, banners).Top.Should().Be(0, "geçici şerit yokken araya boşluk girmez");
        });

        Assert.Null(error);
    }
}
