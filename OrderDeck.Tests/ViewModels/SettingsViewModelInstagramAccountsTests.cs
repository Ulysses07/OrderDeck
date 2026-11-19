using System.Collections.Specialized;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    /// <param name="oauth">Null: OAuth servisi yapılandırılmamış kurulum.</param>
    /// <param name="httpFactory">Verilmezse kitin sahte Graph'ına bağlı fabrika.</param>
    /// <param name="log">Verilmezse ViewModel'in varsayılanı (günlük yazılmaz).</param>
    private SettingsViewModel CreateVm(
        FacebookOAuthService? oauth, IHttpClientFactory? httpFactory = null, ILogger<SettingsViewModel>? log = null)
    {
        var store = new SettingsStore(_path);
        var shortcutsTab = new ShortcutsTabViewModel(new ShortcutRegistry(store), null!);   // bağlayıcı yalnız kısayol kaydında çağrılır
        var api = new LicenseApiClient(
            new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") }, new LicenseTokenStore());

        return new SettingsViewModel(_kit.Settings, store, shortcutsTab,
            new IntakeFormSettingsViewModel(api),
            new ShopperAppSettingsViewModel(api, NullLogger<ShopperAppSettingsViewModel>.Instance),
            facebookOAuth: oauth, httpFactory: httpFactory ?? _kit.HttpFactory, log: log);
    }

    private async Task SeedAsync(int extraPages = 0) => _pages = await _kit.SeedAsync(extraPages);

    /// <summary>Ayarlar'ı o ana dek tohumlanan bağlantıyla (tohumlanmadıysa bağlantısız) açar ve iki durum okumasını
    /// bekler. Tazelemeler sıraya girdiği için kurucunun başlattığı tazeleme de bitmiş olur.</summary>
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

    // ── Ek sabitlemeler: kuralların, yukarıdaki testlerin doğrulamadığı cümleleri ──

    /// <summary>Üretilmiş, hesabı çözülmüş bir satır.</summary>
    private static InstagramPageRow NewRow(bool primary) => new(
        FacebookOAuthTestKit.NewId(), $"Sayfa {Guid.NewGuid():N}", $"@hesap-{Guid.NewGuid():N}", primary, !primary, false);

    /// <summary><c>/me/permissions</c> yanıtı: yorum okumak için gereken iki Instagram izni, verilen durumla.</summary>
    private static HttpResponseMessage Permissions(string status) => FakeGraph.Json(
        $$"""{"data":[{"permission":"instagram_basic","status":"{{status}}"},{"permission":"instagram_manage_comments","status":"{{status}}"}]}""");

    [Fact]
    public async Task Servis_yokken_ya_da_bagli_degilken_satirlar_bosalir_durum_once_baglan_der()
    {
        var unconfigured = CreateVm(oauth: null);                       // OAuth servisi yok
        unconfigured.InstagramPages.Add(NewRow(primary: true));
        await unconfigured.RefreshInstagramStatusAsync();
        (unconfigured.InstagramPages.Count, unconfigured.InstagramAccountStatus, unconfigured.InstagramNeedsReconnect)
            .Should().Be((0, "Önce Facebook'a bağlan.", false));

        var vm = await OpenAsync();                                     // servis var, bağlantı tohumlanmadı
        (vm.InstagramPages.Count, vm.InstagramAccountStatus, vm.InstagramNeedsReconnect, vm.CanManageInstagram)
            .Should().Be((0, "Önce Facebook'a bağlan.", false, false));
        _kit.Graph.Requests.Should().BeEmpty("bağlı değilken Graph'a gidilmez");
    }

    /// <summary>İzin uyarısı çoklu hesapta da çalışır (tek kullanıcı token'ı bütün Sayfaları kapsar) ve bağlantı
    /// kalkınca söner: bağlı olmayan kurulumda "izinleri güncelle" uyarısının anlamı yoktur.</summary>
    [Fact]
    public async Task Izin_eksikse_uyari_iki_Sayfada_da_cikar_baglanti_kaldirilinca_soner()
    {
        await SeedAsync(extraPages: 1);
        _kit.Graph.Map(IsPermissions, _ => Permissions("declined"));
        var vm = await OpenAsync();
        vm.InstagramNeedsReconnect.Should().BeTrue();
        await vm.DisconnectFacebookCommand.ExecuteAsync(null);
        vm.InstagramNeedsReconnect.Should().BeFalse();
    }

    /// <summary>Kitin fabrikası; <see cref="FailWith"/> kurulunca istemci vermek yerine o mesajla fırlatır.</summary>
    private sealed class FailableHttpFactory(IHttpClientFactory inner) : IHttpClientFactory
    {
        public string? FailWith { get; set; }

        public HttpClient CreateClient(string name) =>
            FailWith is { } message ? throw new InvalidOperationException(message) : inner.CreateClient(name);
    }

    /// <summary>Genel hata dalı çoklu hesaptan önceki gibidir: durum satırı sebebi yazar ve izin uyarısı söner (durum
    /// bilinmiyorken uyarı gösterilmez).</summary>
    [Fact]
    public async Task Beklenmeyen_hatada_durum_satiri_sebebi_yazar_izin_uyarisi_soner()
    {
        await SeedAsync(extraPages: 1);
        _kit.Graph.Map(IsPermissions, _ => Permissions("declined"));
        var factory = new FailableHttpFactory(_kit.HttpFactory);
        var vm = CreateVm(_kit.CreateService(), factory);
        await vm.RefreshInstagramStatusAsync();
        vm.InstagramNeedsReconnect.Should().BeTrue();                   // ön koşul: uyarı yanıyor

        var reason = $"sebep {Guid.NewGuid():N}";
        factory.FailWith = reason;
        await vm.RefreshInstagramStatusAsync();
        (vm.InstagramAccountStatus, vm.InstagramNeedsReconnect).Should().Be(($"Durum okunamadı: {reason}", false));
    }

    [Fact]
    public async Task Kullanici_adi_donmeyen_hesabin_etiketi_Instagram_kimligidir()
    {
        await SeedAsync();
        _kit.Graph.AddPage(_pages[0].Page.PageId, _pages[0].Page.PageName, _pages[0].IgUserId);    // Graph kullanıcı adını döndürmüyor
        var vm = await OpenAsync();
        (vm.InstagramAccountStatus, vm.InstagramPages.Single().InstagramText)
            .Should().Be(($"Bağlı Instagram hesabı: @{_pages[0].IgUserId}", $"@{_pages[0].IgUserId}"));
    }

    /// <summary>Çağrısı düşen Sayfada hesap var mı yok mu bilinmez: satır iki Sayfada da uyarmaz, başlık onu saymaz.</summary>
    [Theory, InlineData(0), InlineData(1)]
    public async Task Iki_Sayfada_cozumlemesi_dusen_satir_notrdur_baslikta_sayilmaz(int failing)
    {
        await SeedAsync(extraPages: 1);
        _kit.Graph.Map(u => IsResolve(u, _pages[failing].Page.PageId), _ => FakeGraph.Error(2, HttpStatusCode.InternalServerError));
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be("1 Instagram hesabı bağlı");
        RowAt(vm, failing).Should().Be(("Durum okunamadı", false));
        RowAt(vm, 1 - failing).Should().Be(($"@{_pages[1 - failing].IgUsername}", false));
    }

    [Fact]
    public async Task Iki_Sayfada_hic_hesap_cozulmezse_baslik_sifir_der_birincil_dahil_iki_satir_da_uyarir()
    {
        await SeedAsync(extraPages: 1);
        MakeBare(0);
        MakeBare(1);
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be("0 Instagram hesabı bağlı");
        vm.InstagramPages.Select(r => (r.InstagramText, r.IsWarning)).Should().Equal(
            ("Bu Sayfaya bağlı Instagram profesyonel hesabı yok", true), ("Bu Sayfaya bağlı Instagram profesyonel hesabı yok", true));
    }

    /// <summary>
    /// Sıra: ilki sürerken istenen tazeleme, ilki bitmeden Graph'a gitmez. İç içe koşsalardı bağlantı değişmeden önce
    /// başlamış bir okuma, sonrakinin yazdığı satırların üstüne eski hâli yazabilirdi. Kurucunun tazelemesi izin
    /// sondasında tutulur; o an bağlantı bellektedir, yani sıraya girmeyen bir tazeleme kendi izin sondasını
    /// çağrıldığı anda atardı.
    /// </summary>
    [Fact]
    public async Task Tazelemeler_siraya_girer_suren_tazeleme_bitmeden_sonraki_Grapha_gitmez()
    {
        await SeedAsync(extraPages: 1);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = 0;
        _kit.Graph.Map(IsPermissions, _ =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0)                 // yalnız İLK izin sondası tutulur
            {
                reached.SetResult();
                release.Wait(TestPatience.Upper);
            }
            return Permissions("granted");
        });

        Task second;
        try
        {
            var vm = CreateVm(_kit.CreateService());                    // kurucunun tazelemesi izin sondasında tutuluyor
            await reached.Task.WaitAsync(TestPatience.Upper);
            second = vm.RefreshInstagramStatusAsync();
            _kit.Graph.Requests.Should().ContainSingle("ikinci tazeleme sırada bekler");
        }
        finally
        {
            release.Set();
        }
        await second.WaitAsync(TestPatience.Upper);

        string Kind(Uri u) => IsPermissions(u) ? "izin"
            : IsResolve(u, _pages[0].Page.PageId) ? "sayfa-0"
            : IsResolve(u, _pages[1].Page.PageId) ? "sayfa-1" : "başka";
        _kit.Graph.Requests.Select(Kind).Should().Equal("izin", "sayfa-0", "sayfa-1", "izin", "sayfa-0", "sayfa-1");
    }

    /// <summary>Graph isteklerinin türü: izin sondası ya da tohumlanan Sayfalardan birinin çözümlemesi.</summary>
    private string RequestKind(Uri u) => IsPermissions(u) ? "izin"
        : IsResolve(u, _pages[0].Page.PageId) ? "sayfa-0"
        : _pages.Count > 1 && IsResolve(u, _pages[1].Page.PageId) ? "sayfa-1" : "başka";

    /// <summary>
    /// Ayarlar'ı açar; kurucunun tazelemesi bağlı Sayfaları okumuş ve İLK izin sondasında tutulurken
    /// <paramref name="change"/> bağlantıyı aynı servis üstünden değiştirir; sonra tazeleme bırakılır ve sonucunu
    /// karta yazana dek (durum satırı ilk değerinden ayrılana dek) beklenir. İkinci bir tazeleme İSTENMEZ: o, bayat
    /// sonucu kendiliğinden düzeltip hatayı gizlerdi.
    /// </summary>
    private async Task<SettingsViewModel> ChangeDuringHeldRefreshAsync(Func<FacebookOAuthService, Task> change)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = 0;
        _kit.Graph.Map(IsPermissions, _ =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0)                 // yalnız İLK izin sondası tutulur
            {
                reached.SetResult();
                release.Wait(TestPatience.Upper);
            }
            return Permissions("granted");
        });

        var service = _kit.CreateService();
        SettingsViewModel vm;
        try
        {
            vm = CreateVm(service);                                     // kurucunun tazelemesi izin sondasında tutuluyor
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.InstagramAccountStatus)
                    && vm.InstagramAccountStatus != "Kontrol ediliyor...")
                    finished.TrySetResult();
            };
            await reached.Task.WaitAsync(TestPatience.Upper);
            await change(service).WaitAsync(TestPatience.Upper);
        }
        finally
        {
            release.Set();
        }
        await finished.Task.WaitAsync(TestPatience.Upper);
        return vm;
    }

    /// <summary>
    /// Süren tazeleme, o sırada kaldırılan ek Sayfanın eski hâlini karta yazmaz: sonuç yazılmadan önce bağlı Sayfalar
    /// yeniden okunur, liste değiştiyse sonuç atılır ve okuma güncel listeyle yinelenir. NEDEN: aynı servisi kullanan
    /// başka bir çağıran bağlantıyı değiştirdiğinde arkadan tazeleme gelmez; kart kaldırılmış satırla kalırdı.
    /// </summary>
    [Fact]
    public async Task Suren_tazeleme_sirasinda_kaldirilan_ek_Sayfa_karta_geri_yazilmaz()
    {
        await SeedAsync(extraPages: 1);

        var vm = await ChangeDuringHeldRefreshAsync(async service =>
            (await service.RemoveExtraPageAsync(_pages[1].Page.PageId)).Should().BeTrue());

        vm.InstagramPages.Select(r => r.PageId).Should().Equal(_pages[0].Page.PageId);
        vm.ShowInstagramPageRows.Should().BeFalse();
        vm.InstagramAccountStatus.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}");
        // ilk turun sonucu atıldı; ikinci tur güncel listeyle koştu
        _kit.Graph.Requests.Select(RequestKind).Should().Equal("izin", "sayfa-0", "sayfa-1", "izin", "sayfa-0");
    }

    /// <summary>Süren tazeleme sırasında bağlantı kaldırılırsa kart "bağlı değil" görünür; boş listede Graph'a gidilmez.</summary>
    [Fact]
    public async Task Suren_tazeleme_sirasinda_baglanti_kaldirilirsa_kart_bagli_degil_gorunur()
    {
        await SeedAsync(extraPages: 1);

        var vm = await ChangeDuringHeldRefreshAsync(service => service.DisconnectAsync());

        (vm.InstagramPages.Count, vm.InstagramAccountStatus, vm.InstagramNeedsReconnect)
            .Should().Be((0, "Önce Facebook'a bağlan.", false));
        _kit.Graph.Requests.Select(RequestKind).Should().Equal("izin", "sayfa-0", "sayfa-1");
    }

    [Fact]
    public async Task Baglaninca_kart_hemen_tazelenir()
    {
        var (pageId, igUsername) = (FacebookOAuthTestKit.NewId(), $"hesap-{Guid.NewGuid():N}");
        _kit.Graph.AddPage(pageId, $"sayfa-{Guid.NewGuid():N}", FacebookOAuthTestKit.NewId(), igUsername);   // kullanıcının tek Sayfası: seçici açılmaz
        var vm = await OpenAsync();
        vm.InstagramAccountStatus.Should().Be("Önce Facebook'a bağlan.");   // ön koşul: bağlı değil

        var connect = vm.ConnectFacebookCommand.ExecuteAsync(null);
        await _kit.Browser.WaitUntilOpenedAsync();
        _kit.Browser.Complete();
        await connect.WaitAsync(TestPatience.Upper);

        (vm.InstagramAccountStatus, vm.InstagramPages.Select(r => r.PageId).SingleOrDefault(), vm.CanManageInstagram)
            .Should().Be(($"Bağlı Instagram hesabı: @{igUsername}", pageId, true));
    }

    /// <summary>Yazım düşerse bağlantı değişmemiştir: satır kalır, sonuç satırı yeniden denemeyi söyler; engel kalkınca
    /// aynı satır kaldırılabilir.</summary>
    [Fact]
    public async Task Kaldir_yazimi_duserse_satir_kalir_metin_yeniden_denemeyi_soyler_sonraki_deneme_kaldirir()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var rows = vm.InstagramPages.ToArray();
        File.SetAttributes(_kit.TokenFilePath, FileAttributes.ReadOnly);        // hedef salt okunur: deponun yazımı düşer
        try
        {
            await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        }
        finally
        {
            File.SetAttributes(_kit.TokenFilePath, FileAttributes.Normal);
        }

        vm.InstagramNotice.Should().Be("Hesap kaldırılamadı, yeniden dene.");
        vm.InstagramPages.Should().Equal(rows);
        (await _service.GetConnectedPagesAsync()).Should().HaveCount(2);

        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        vm.InstagramNotice.Should().Be($"@{_pages[1].IgUsername} kaldırıldı.");
        vm.InstagramPages.Should().Equal(rows[0]);
        (await _service.GetConnectedPagesAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// Düşen "Kaldır"ın sebebi (erişim reddi, dolu disk, kilitli dosya) günlüğe Warning olarak düşer: operatör sabit
    /// metni görür, destek sebebi günlükten okur. Satır yalnız Sayfa kimliğini taşır; başarılı "Kaldır" satır yazmaz.
    /// </summary>
    [Fact]
    public async Task Kaldir_yazimi_duserse_sebep_gunluge_uyari_olarak_duser()
    {
        await SeedAsync(extraPages: 1);
        var log = new CapturingLogger<SettingsViewModel>();
        _service = _kit.CreateService();
        var vm = CreateVm(_service, log: log);
        await vm.RefreshInstagramStatusAsync();                         // sıra: kurucunun tazelemesi de bitti
        var removed = _pages[1].Page;
        File.SetAttributes(_kit.TokenFilePath, FileAttributes.ReadOnly);        // hedef salt okunur: deponun yazımı düşer
        try
        {
            await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        }
        finally
        {
            File.SetAttributes(_kit.TokenFilePath, FileAttributes.Normal);
        }

        vm.InstagramNotice.Should().Be("Hesap kaldırılamadı, yeniden dene.");
        var warning = log.Warnings.Should().ContainSingle().Which;
        (warning.Level, warning.Message).Should().Be((LogLevel.Warning, $"Instagram account removal failed for Page {removed.PageId}"));
        warning.Exception.Should().NotBeNull("sebep istisnadadır");
        var bundle = (await _kit.Store.LoadAsync())!;
        var secrets = new[] { removed.PageName, bundle.UserAccessToken }
            .Concat((await _service.GetConnectedPagesAsync()).Select(p => p.PageAccessToken));
        foreach (var secret in secrets) warning.Full.Should().NotContain(secret);

        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);   // engel kalktı
        vm.InstagramNotice.Should().Be($"@{_pages[1].IgUsername} kaldırıldı.");
        log.Warnings.Should().ContainSingle("başarılı Kaldır günlük yazmaz");
    }

    /// <summary>Servisin dönüş değeri akışı değiştirmez: Sayfa başka yoldan çoktan kaldırıldıysa (servis false döner)
    /// satır yine kalkar ve sonuç yazılır. Satırsız çağrı hiçbir şey yapmaz.</summary>
    [Fact]
    public async Task Kaldir_Sayfa_coktan_bagli_degilse_de_satiri_kaldirir_satirsiz_cagri_hicbir_sey_yapmaz()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(null);
        (vm.InstagramPages.Count, vm.InstagramNotice).Should().Be((2, (string?)null));

        (await _service.RemoveExtraPageAsync(_pages[1].Page.PageId)).Should().BeTrue();     // ön koşul: Sayfa başka yoldan kaldırıldı
        await vm.RemoveInstagramAccountCommand.ExecuteAsync(vm.InstagramPages[1]);
        (vm.InstagramPages.Count, vm.InstagramNotice).Should().Be((1, $"@{_pages[1].IgUsername} kaldırıldı."));
    }

    [Fact]
    public void Satir_sayisi_ve_baglanti_durumu_degisince_turetilen_ozellikler_bildirilir()
    {
        var vm = CreateVm(oauth: null);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        int Raised(string name) { var count = raised.Count(n => n == name); raised.Clear(); return count; }

        vm.InstagramPages.Add(NewRow(primary: true));
        (Raised(nameof(vm.ShowInstagramPageRows)), vm.ShowInstagramPageRows).Should().Be((1, false));
        vm.InstagramPages.Add(NewRow(primary: false));
        (Raised(nameof(vm.ShowInstagramPageRows)), vm.ShowInstagramPageRows).Should().Be((1, true));
        vm.InstagramPages.RemoveAt(1);
        (Raised(nameof(vm.ShowInstagramPageRows)), vm.ShowInstagramPageRows).Should().Be((1, false));

        vm.IsFacebookConnected = true;
        (Raised(nameof(vm.CanManageInstagram)), vm.CanManageInstagram).Should().Be((1, true));
        vm.IsFacebookBusy = true;
        (Raised(nameof(vm.CanManageInstagram)), vm.CanManageInstagram).Should().Be((1, false));
        vm.IsFacebookBusy = false;
        (Raised(nameof(vm.CanManageInstagram)), vm.CanManageInstagram).Should().Be((1, true));
    }

    /// <summary>
    /// Kartın, çizim testinin bakmadığı görünürlük kuralları: birincil eki ve ipucu yalnız birincil satırda; hesabı
    /// çözülmüş satır uyarı renginde değil metin renginde; sonuç yokken sonuç satırı, tek Sayfada altyazı yer kaplamaz.
    /// </summary>
    [Fact]
    public void Birincil_eki_ve_ipucu_yalniz_birincil_satirda_notr_satir_metin_renginde_sonucsuz_serit_gizli() => OnSta(() =>
    {
        const string Suffix = " · birincil (Facebook yorumları ve moderasyon bu Sayfadan)";
        const string Caption = "İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar.";
        var (primary, extra) = (NewRow(primary: true), NewRow(primary: false));
        var vm = CreateVm(oauth: null);
        vm.InstagramPages.Add(primary);
        vm.InstagramPages.Add(extra);
        var page = RenderFacebookTab(vm);

        ShownTexts(page).Where(t => t.Text == Suffix).Should().ContainSingle().Which.DataContext.Should().BeSameAs(primary);
        foreach (var row in new[] { primary, extra })
            ShownTexts(page).Single(t => t.Text == row.InstagramText).Foreground.Should().BeSameAs(page.FindResource("OD.Brush.Text"));
        Descendants(page).OfType<FrameworkElement>().Where(e => ReferenceEquals(e.DataContext, extra))
            .Should().NotBeEmpty().And.OnlyContain(e => e.ToolTip == null, "ipucu birincil satıra özgüdür");
        Descendants(page).OfType<Button>().Where(b => ReferenceEquals(b.Command, vm.DismissInstagramNoticeCommand))
            .Should().ContainSingle().Which.Should().Match<Button>(b => !Shown(b), "sonuç yokken sonuç satırı yer kaplamaz");

        var single = CreateVm(oauth: null);
        single.InstagramPages.Add(primary);
        ShownTexts(RenderFacebookTab(single)).Select(t => t.Text).Should().NotContain(new[] { Caption, Suffix }, "tek Sayfada kart bugünkü gibi");
    });
}
