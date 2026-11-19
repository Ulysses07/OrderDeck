using System.Collections.Specialized;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Pages;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Shortcuts;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.App;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Ayarlar > Facebook sekmesindeki Instagram kartı (spec 2026-11-16 §8.6.1): bağlı her Sayfanın hesabı bir satırdır,
/// ek hesap tek başına kaldırılır, birincil kaldırılamaz; kart bağlantı değişince hemen tazelenir ve tek Sayfalı
/// kurulumun durum metni bugünküyle aynıdır.
///
/// Gerçek OAuth servisi (geçici klasörde gerçek şifreli depo) + sahte Graph; ağa çıkılmaz. Kimlik, token, hesap ve
/// Sayfa adları üretilir. Arayüz metinleri literal doğrulanır (üretim sabitine başvurulmaz): metin değişirse test düşer.
/// </summary>
public class SettingsViewModel_InstagramAccountsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"orderdeck-svm-ig-{Guid.NewGuid():N}.json");
    private readonly FacebookOAuthTestKit _kit = new();
    private IReadOnlyList<SeededPage> _pages = Array.Empty<SeededPage>();
    private FacebookOAuthService _service = null!;

    public void Dispose() { _kit.Dispose(); if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>Hep 404 dönen işleyici: kurucunun arkada başlattığı kayıt formu ve müşteri uygulaması okumaları ağa çıkmasın.</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    private SettingsViewModel CreateVm(FacebookOAuthService? oauth)
    {
        var store = new SettingsStore(_path);
        var shortcutsTab = new ShortcutsTabViewModel(new ShortcutRegistry(store), null!);   // bağlayıcı yalnız kısayol kaydında çağrılır
        var api = new LicenseApiClient(
            new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") }, new LicenseTokenStore());

        return new SettingsViewModel(_kit.Settings, store, shortcutsTab,
            new IntakeFormSettingsViewModel(api),
            new ShopperAppSettingsViewModel(api, NullLogger<ShopperAppSettingsViewModel>.Instance),
            facebookOAuth: oauth, httpFactory: _kit.HttpFactory);
    }

    private async Task SeedAsync(int extraPages = 0) => _pages = await _kit.SeedAsync(extraPages);

    /// <summary>Ayarlar'ı tohumlanmış bağlantıyla açar ve iki durum okumasını bekler. Tazelemeler sıraya girdiği için
    /// kurucunun başlattığı tazeleme de bitmiş olur.</summary>
    private async Task<SettingsViewModel> OpenAsync()
    {
        _service = _kit.CreateService();
        var vm = CreateVm(_service);
        await vm.RefreshFacebookConnectionStatusAsync();
        await vm.RefreshInstagramStatusAsync();
        return vm;
    }

    private void MakeBare(int index) => _kit.Graph.AddPage(_pages[index].Page.PageId, _pages[index].Page.PageName);   // o Sayfa Instagram'sız olur
    private static (string Text, bool Warning) RowAt(SettingsViewModel vm, int i) => (vm.InstagramPages[i].InstagramText, vm.InstagramPages[i].IsWarning);
    private static bool IsResolve(Uri u, string pageId) => u.AbsolutePath.EndsWith("/" + pageId, StringComparison.Ordinal)
        && u.Query.Contains("instagram_business_account", StringComparison.Ordinal);
    private static bool IsPermissions(Uri u) => u.AbsolutePath.EndsWith("/me/permissions", StringComparison.Ordinal);
    private static void OnSta(Action body) => ThemeTestHost.RunOnSta(body).Should().BeNull();

    /// <summary>Sayfayı 1200x900'e yerleştirir, "Facebook" sekmesini seçer (seçili olmayan sekmenin içeriği görsel
    /// ağaçta yoktur), bekleyen bağlamaları boşaltır ve yeniden yerleştirir.</summary>
    private static SettingsPage RenderFacebookTab(SettingsViewModel vm)
    {
        var pages = new PageStack();
        pages.ShowAsync("settings", "Ayarlar", p => SettingsPage.Create(p, vm));
        var page = Assert.IsType<SettingsPage>(pages.Top!.Content);
        Arrange(page);
        var tabs = Descendants(page).OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Facebook"));
        ThemeTestHost.Pump();
        Arrange(page);
        return page;
    }

    private static void Arrange(SettingsPage page)
    {
        var size = new Size(1200, 900);
        page.Measure(size);
        page.Arrange(new Rect(size));
        page.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)        // görsel ağaç
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deep in Descendants(child)) yield return deep;
        }
    }

    private static bool Shown(FrameworkElement e) => e.ActualWidth > 0 && e.ActualHeight > 0;
    private static IEnumerable<TextBlock> ShownTexts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Where(Shown);

    [Fact]
    public async Task Tek_Sayfada_durum_metni_bugunku_satirlar_gizli()
    {
        await SeedAsync();
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}");
        (vm.InstagramPages.Count, vm.ShowInstagramPageRows, vm.CanManageInstagram).Should().Be((1, false, true));
    }

    [Theory]                                                            // hesap yok: bugünkü metin; çözümleme düştü: "Durum okunamadı."
    [InlineData(false, "Bu Sayfa'ya bağlı Instagram profesyonel hesabı yok.", "Bu Sayfaya bağlı Instagram profesyonel hesabı yok")]
    [InlineData(true, "Durum okunamadı.", "Durum okunamadı")]
    public async Task Tek_Sayfada_hesap_yoksa_ya_da_cozumleme_duserse_metin_ayri_satir_notr(bool failed, string status, string row)
    {
        await SeedAsync();
        if (failed) _kit.Graph.Map(u => IsResolve(u, _pages[0].Page.PageId), _ => FakeGraph.Error(2, HttpStatusCode.InternalServerError));
        else MakeBare(0);
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be(status);
        RowAt(vm, 0).Should().Be((row, false));
    }

    [Fact]
    public async Task Iki_Sayfada_iki_satir_birincil_kaldirilamaz_izin_sondasi_bir_kez()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var probes = _kit.Graph.Requests.Count(IsPermissions);
        await vm.RefreshInstagramStatusAsync();
        _kit.Graph.Requests.Count(IsPermissions).Should().Be(probes + 1, "izin sondası tazeleme başına bir kez");
        (vm.InstagramAccountStatus, vm.ShowInstagramPageRows).Should().Be(("2 Instagram hesabı bağlı", true));
        vm.InstagramPages.Should().Equal(
            new InstagramPageRow(_pages[0].Page.PageId, _pages[0].Page.PageName, $"@{_pages[0].IgUsername}", true, false, false),
            new InstagramPageRow(_pages[1].Page.PageId, _pages[1].Page.PageName, $"@{_pages[1].IgUsername}", false, true, false));
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[0]);         // birincil kaldırılamaz
        vm.InstagramPages.Should().HaveCount(2);
        vm.InstagramNotice.Should().BeNull();
    }

    [Theory, InlineData(0), InlineData(1)]
    public async Task Iki_Sayfada_hesabi_olmayan_satir_uyarir_baslik_cozulen_hesabi_sayar(int bare)
    {
        await SeedAsync(extraPages: 1);
        MakeBare(bare);
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be("1 Instagram hesabı bağlı");
        RowAt(vm, bare).Should().Be(("Bu Sayfaya bağlı Instagram profesyonel hesabı yok", true));
        vm.InstagramPages[1 - bare].IsWarning.Should().BeFalse();
        if (bare == 0) return;                                          // hesapsız ek Sayfa kaldırılınca metin Sayfa adını söyler
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        vm.InstagramNotice.Should().Be($"{_pages[1].Page.PageName} kaldırıldı.");
    }

    [Fact]
    public async Task Kaldir_satiri_aninda_kaldirir_metni_yazar_karti_tazeler_Tamam_kapatir()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var before = _kit.Graph.Requests.Count;
        int? atRemoval = null;
        vm.InstagramPages.CollectionChanged += (_, e) =>
        { if (e.Action == NotifyCollectionChangedAction.Remove) atRemoval ??= _kit.Graph.Requests.Count; };
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        atRemoval.Should().Be(before, "satır tazelemenin Graph isteklerini beklemeden kalkar");
        vm.InstagramNotice.Should().Be($"@{_pages[1].IgUsername} kaldırıldı.");
        (await _service.GetConnectedPagesAsync()).Select(p => p.PageId).Should().Equal(_pages[0].Page.PageId);
        vm.InstagramAccountStatus.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}");
        vm.DismissInstagramNoticeCommand.Execute(null);
        vm.InstagramNotice.Should().BeNull();
    }

    [Fact]
    public async Task Baglanti_kaldirilinca_kart_hemen_tazelenir()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        await vm.DisconnectFacebookCommand.ExecuteAsync(null);
        (vm.InstagramPages.Count, vm.InstagramAccountStatus).Should().Be((0, "Önce Facebook'a bağlan."));
        (vm.ShowInstagramPageRows, vm.CanManageInstagram).Should().Be((false, false));
    }

    [Fact]
    public async Task Es_zamanli_tazelemeler_cift_satir_uretmez()
    {
        await SeedAsync(extraPages: 1);
        var vm = CreateVm(_kit.CreateService());                        // kurucu bir tazeleme başlattı
        await Task.WhenAll(vm.RefreshInstagramStatusAsync(), vm.RefreshInstagramStatusAsync());
        vm.InstagramPages.Should().HaveCount(2);
    }

    [Fact]                                                              // Review Focus 3
    public void Kart_satirlari_yalniz_iki_Sayfada_cizer_Kaldir_yalniz_ek_satirda() => OnSta(() =>
    {
        var primary = new InstagramPageRow(FacebookOAuthTestKit.NewId(), $"Sayfa {Guid.NewGuid():N}", $"@hesap-{Guid.NewGuid():N}", true, false, false);
        var extra = new InstagramPageRow(FacebookOAuthTestKit.NewId(), $"Sayfa {Guid.NewGuid():N}", "Bu Sayfaya bağlı Instagram profesyonel hesabı yok", false, true, true);
        var single = CreateVm(oauth: null);
        single.InstagramPages.Add(primary);
        ShownTexts(RenderFacebookTab(single)).Should().NotContain(t => t.Text == primary.InstagramText, "tek Sayfada satır yok");

        var vm = CreateVm(oauth: null);
        vm.InstagramPages.Add(primary);
        vm.InstagramPages.Add(extra);
        vm.InstagramNotice = $"sonuç {Guid.NewGuid():N}";
        var page = RenderFacebookTab(vm);
        foreach (var row in new[] { primary, extra })
            ShownTexts(page).Where(t => ReferenceEquals(t.DataContext, row)).Select(t => t.Text)
                .Should().Contain(new[] { row.InstagramText, $"Sayfa: {row.PageName}" });
        var remove = Descendants(page).OfType<Button>().Where(b => Equals(b.Content, "Kaldır") && Shown(b)).Should().ContainSingle().Subject;
        remove.DataContext.Should().BeSameAs(extra);
        remove.CommandParameter.Should().BeSameAs(extra);
        remove.Command.Should().BeSameAs(vm.RemoveInstagramAccountCommand);
        ShownTexts(page).Single(t => t.Text == extra.InstagramText).Foreground.Should().BeSameAs(page.FindResource("OD.Brush.Amber"));
        Descendants(page).OfType<FrameworkElement>().Should().Contain(e => ReferenceEquals(e.DataContext, primary)
            && Equals(e.ToolTip, "Birincil Sayfayı değiştirmek için \"Bağlantıyı Kaldır\" + \"Facebook'a Bağlan\""));
        ShownTexts(page).Select(t => t.Text).Should().Contain(new[] { vm.InstagramNotice!,
            "İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar." });
        Descendants(page).OfType<Button>().Should().Contain(b => Equals(b.Content, "Tamam") && Shown(b)
            && ReferenceEquals(b.Command, vm.DismissInstagramNoticeCommand));
    });
}
