using System.Collections.Specialized;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Drawers;
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
    /// <param name="drawers">Verilmezse sınıfın çekmece sahtesi (<see cref="_drawers"/>).</param>
    /// <param name="withoutDrawers">True: çekmece servisi hiç verilmez (<paramref name="drawers"/> yok sayılır).</param>
    private SettingsViewModel CreateVm(
        FacebookOAuthService? oauth, IHttpClientFactory? httpFactory = null, ILogger<SettingsViewModel>? log = null,
        IDrawerService? drawers = null, bool withoutDrawers = false)
    {
        var store = new SettingsStore(_path);
        var shortcutsTab = new ShortcutsTabViewModel(new ShortcutRegistry(store), null!);   // bağlayıcı yalnız kısayol kaydında çağrılır
        var api = new LicenseApiClient(
            new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") }, new LicenseTokenStore());

        return new SettingsViewModel(_kit.Settings, store, shortcutsTab,
            new IntakeFormSettingsViewModel(api),
            new ShopperAppSettingsViewModel(api, NullLogger<ShopperAppSettingsViewModel>.Instance),
            facebookOAuth: oauth, httpFactory: httpFactory ?? _kit.HttpFactory, drawers: withoutDrawers ? null : drawers ?? _drawers, log: log);
    }

    private async Task SeedAsync(int extraPages = 0) => _pages = await _kit.SeedAsync(extraPages);

    /// <summary>Ayarlar'ı o ana dek tohumlanan bağlantıyla (tohumlanmadıysa bağlantısız) açar ve iki durum okumasını
    /// bekler. Tazelemeler sıraya girdiği için kurucunun başlattığı tazeleme de bitmiş olur. Çekmece sahtesi kararını
    /// bu ViewModel üstünden verir.</summary>
    /// <param name="log">Verilmezse ViewModel'in varsayılanı (günlük yazılmaz).</param>
    /// <param name="callbackTimeout">Tarayıcı onayının süresi; verilmezse servisin kendi süresi.</param>
    private async Task<SettingsViewModel> OpenAsync(ILogger<SettingsViewModel>? log = null, TimeSpan? callbackTimeout = null)
    {
        _service = _kit.CreateService(callbackTimeout: callbackTimeout);
        var vm = CreateVm(_service, log: log);
        _drawers.Vm = vm;
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

    // ── "Instagram hesabı ekle" (çekmece) ──

    /// <summary>Çekmece sahtesi: içerik fabrikasını ÇAĞIRMAZ; kararı ViewModel'in tuttuğu seçici üstünden verir.</summary>
    private sealed class FakeDrawers : IDrawerService
    {
        public Func<SettingsViewModel, bool> Decide { get; set; } = _ => false;         // varsayılan: İptal / Vazgeç
        public SettingsViewModel? Vm { get; set; }
        public List<string> Titles { get; } = new();
        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent) { Titles.Add(title); return Task.FromResult(Decide(Vm!)); }
        public bool CloseTop() => false;
    }

    private readonly FakeDrawers _drawers = new();
    private const string ReconnectText = "Facebook oturumunun yenilenmesi gerekiyor. \"Facebook izinlerini güncelle\"ye bas.";

    /// <summary>Graph'a bağlı olmayan yeni bir Sayfa tanıtır (değerler üretilir).</summary>
    private (string PageId, string PageName, string? IgUsername) AddCandidate(bool withInstagram = true)
    {
        var (pageId, pageName) = (FacebookOAuthTestKit.NewId(), $"sayfa-{Guid.NewGuid():N}");
        var igUsername = withInstagram ? $"hesap-{Guid.NewGuid():N}" : null;
        _kit.Graph.AddPage(pageId, pageName, withInstagram ? FacebookOAuthTestKit.NewId() : null, igUsername);
        return (pageId, pageName, igUsername);
    }

    /// <summary>Üretilmiş bir listeleme satırı.</summary>
    private static FacebookAddablePage NewAddable(bool withInstagram, bool connected = false) => new(
        FacebookOAuthTestKit.NewId(), $"sayfa-{Guid.NewGuid():N}",
        withInstagram ? FacebookOAuthTestKit.NewId() : null,
        withInstagram ? $"hesap-{Guid.NewGuid():N}" : null,
        connected);

    /// <summary>Çekmecede o Sayfayı seçip "Ekle".</summary>
    private static bool Pick(SettingsViewModel vm, string pageId)
    {
        vm.ActiveInstagramPicker!.Selected = vm.ActiveInstagramPicker.Options.Single(o => o.PageId == pageId);
        return true;
    }

    private static bool IsAccounts(Uri u) => u.AbsolutePath.EndsWith("/me/accounts", StringComparison.Ordinal);
    private static bool IsPageToken(Uri u, string pageId) => u.AbsolutePath.EndsWith("/" + pageId, StringComparison.Ordinal)
        && u.Query.Contains("fields=access_token", StringComparison.Ordinal);

    [Fact]
    public async Task Ekle_secilen_hesabi_tarayicisiz_ekler_ve_karti_tazeler()
    {
        await SeedAsync();
        var candidate = AddCandidate();
        var vm = await OpenAsync();
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.InstagramAccountStatus)) statuses.Add(vm.InstagramAccountStatus); };
        string? statusAtDrawer = null;
        _drawers.Decide = v => { statusAtDrawer = v.InstagramAccountStatus; return Pick(v, candidate.PageId); };
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);
        _drawers.Titles.Should().Equal("Instagram hesabı ekle");
        statuses.Should().Contain("Sayfalar okunuyor...");
        statusAtDrawer.Should().Be($"Bağlı Instagram hesabı: @{_pages[0].IgUsername}", "çekmeceden önce önceki metin geri konur");
        vm.InstagramNotice.Should().Be($"@{candidate.IgUsername} eklendi. Yayın açıksa birkaç saniye içinde okunmaya başlar.");
        vm.InstagramPages.Select(r => r.PageId).Should().Equal(_pages[0].Page.PageId, candidate.PageId);
        vm.InstagramAccountStatus.Should().Be("2 Instagram hesabı bağlı");
        vm.ActiveInstagramPicker.Should().BeNull();
        (_kit.Broker.ConfigCalls, _kit.Broker.ExchangeCalls).Should().Be((0, 0), "TK5: tarayıcı ve sunucu takası yok");
    }

    [Fact]
    public async Task Eklenebilecek_hesap_yoksa_cekmece_acilmaz_yonlendirme_metni_yazilir()
    {
        await SeedAsync();
        AddCandidate(withInstagram: false);
        var vm = await OpenAsync();
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);
        _drawers.Titles.Should().BeEmpty();
        vm.InstagramNotice.Should().Be("Eklenebilecek başka hesap bulunamadı. Yeni hesabı önce bir Facebook Sayfasına bağla; Sayfa hazırsa \"Facebook izinlerini güncelle\"ye bas ve açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretle.");
    }

    [Theory, InlineData(true), InlineData(false)]      // true: izin sondası "izin yok" dedi (0. adım); false: listeleme 190 döndü (NeedsReauthorize)
    public async Task Oturum_yenilenmeliyse_cekmece_acilmaz_iki_yol_ayni_metne_varir(bool probeSaysNo)
    {
        await SeedAsync();
        AddCandidate();
        if (probeSaysNo) _kit.Graph.Map(IsPermissions, _ => FakeGraph.Json("""{"data":[]}"""));
        else _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(190));
        var vm = await OpenAsync();
        var listings = _kit.Graph.Requests.Count(IsAccounts);
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);
        vm.InstagramNeedsReconnect.Should().Be(probeSaysNo);
        _kit.Graph.Requests.Count(IsAccounts).Should().Be(probeSaysNo ? listings : listings + 1, "0. adımda listeleme yapılmaz");
        (_drawers.Titles.Count, vm.InstagramNotice).Should().Be((0, ReconnectText));
    }

    [Theory]                                                            // karar eki: sınır, yetki ve geçici hata metinleri
    [InlineData("sinir"), InlineData("yetki"), InlineData("listeleme-5xx"), InlineData("listeleme-ag"), InlineData("ekleme-5xx"), InlineData("ekleme-kota")]
    public async Task Ekleme_hatalari_kendi_metnine_duser_hicbir_sey_eklenmez_durum_metni_geri_konur(string fault)
    {
        await SeedAsync(extraPages: fault == "sinir" ? 3 : 0);              // "sinir": 4 Sayfa bağlı, AddExtraPageAsync reddeder
        var candidate = AddCandidate();
        var vm = await OpenAsync();
        _drawers.Decide = v => Pick(v, candidate.PageId);
        var (status, rows) = (vm.InstagramAccountStatus, vm.InstagramPages.Count);
        if (fault == "yetki") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Json("{}"));     // Sayfa yetkisi türetilemez
        if (fault == "listeleme-5xx") _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(2, HttpStatusCode.ServiceUnavailable));
        if (fault == "listeleme-ag") _kit.Graph.Map(IsAccounts, _ => throw new HttpRequestException("ağ yok"));
        if (fault == "ekleme-5xx") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Error(2, HttpStatusCode.ServiceUnavailable));
        if (fault == "ekleme-kota") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Error(613));
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);
        vm.InstagramNotice.Should().Be(fault switch
        {
            "sinir" => "En çok 4 Instagram hesabı bağlanabilir.",
            "yetki" => $"\"{candidate.PageName}\" için yetki alınamadı: bu Sayfada yönetici olduğundan emin ol.",
            _ => "Sayfalar okunamadı, internetini kontrol edip yeniden dene.",
        });
        (vm.InstagramAccountStatus, vm.InstagramPages.Count).Should().Be((status, rows));
    }

    [Fact]
    public async Task Cekmecede_vazgecilirse_hicbir_sey_degismez()
    {
        await SeedAsync();
        AddCandidate();
        var vm = await OpenAsync();                                         // Decide varsayılanı: İptal
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);
        _drawers.Titles.Should().ContainSingle();
        vm.InstagramNotice.Should().BeNull();
        vm.InstagramPages.Should().ContainSingle();
    }

    [Fact]
    public void Secici_bagli_Sayfalari_eler_hesapsiz_Sayfayi_pasif_gosterir_ilk_secilebiliri_secer()
    {
        var (connected, bare, addable) = (NewAddable(true, connected: true), NewAddable(false), NewAddable(true));
        var picker = new InstagramAccountPickerViewModel(new[] { connected, bare, addable });
        picker.Options.Should().Equal(
            new InstagramAccountOption(bare.PageId, bare.PageName, $"{bare.PageName} — Instagram hesabı bağlı değil", false),
            new InstagramAccountOption(addable.PageId, addable.PageName, $"@{addable.IgUsername} — Sayfa: {addable.PageName}", true));
        picker.Selected.Should().Be(picker.Options[1]);
    }

    [Fact]                                                                  // Review Focus 3
    public void Cekmece_secenekleri_cizer_hesapsiz_Sayfa_pasif_secim_yokken_Ekle_kapatmaz() => OnSta(() =>
    {
        var picker = new InstagramAccountPickerViewModel(new[] { NewAddable(false), NewAddable(true) });
        var stack = new DrawerStack();
        var closed = stack.ShowAsync("Instagram hesabı ekle", d => InstagramAccountAddDrawer.Create(d, picker));
        var view = (InstagramAccountAddDrawer)stack.Top!.Content!;
        view.Measure(new Size(400, 700));
        view.Arrange(new Rect(0, 0, 400, 700));
        ThemeTestHost.Pump();
        view.UpdateLayout();
        Descendants(view).OfType<ListBoxItem>().Select(i => i.IsEnabled).Should().Equal(picker.Options.Select(o => o.CanSelect));
        ShownTexts(view).Select(t => t.Text).Should().Contain(picker.Options.Select(o => o.Display));
        var add = Descendants(view).OfType<Button>().Single(b => Equals(b.Content, "Ekle"));
        picker.Selected = null;
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        closed.IsCompleted.Should().BeFalse("seçim yokken Ekle kapatmaz");
        picker.Selected = picker.Options.Single(o => o.CanSelect);
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        (closed.IsCompletedSuccessfully && closed.Result).Should().BeTrue();
    });

    [Fact]                                                                  // Review Focus 3
    public void Kartta_Instagram_hesabi_ekle_dugmesi_bagli_degilken_pasif() => OnSta(() =>
    {
        Button Add(SettingsViewModel vm) => Descendants(RenderFacebookTab(vm)).OfType<Button>().Single(b => Equals(b.Content, "Instagram hesabı ekle"));
        var offline = CreateVm(oauth: null);
        var button = Add(offline);
        button.IsEnabled.Should().BeFalse();
        button.Command.Should().BeSameAs(offline.AddInstagramAccountCommand);
        var connected = CreateVm(oauth: null);
        connected.IsFacebookConnected = true;
        Add(connected).IsEnabled.Should().BeTrue();
    });

    // ── Ek sabitlemeler: ekleme akışının günlük kuralı ──

    /// <summary>
    /// Sebebi tam göstermeyen metne ("Sayfalar okunamadı", "yetki alınamadı") düşen hata günlüğe Warning olarak düşer:
    /// operatör sabit metni görür, destek sebebi (Graph kodu, HTTP durumu) günlükten okur. Satır yalnız Sayfa kimliğini
    /// taşır; istisna eklidir; token ve Sayfa adı yoktur.
    /// </summary>
    [Theory, InlineData("listeleme-5xx"), InlineData("listeleme-ag"), InlineData("ekleme-5xx"), InlineData("yetki")]
    public async Task Sebebi_soylemeyen_hata_metninde_sebep_gunluge_uyari_olarak_duser(string fault)
    {
        await SeedAsync();
        var candidate = AddCandidate();
        var log = new CapturingLogger<SettingsViewModel>();
        var vm = await OpenAsync(log);
        _drawers.Decide = v => Pick(v, candidate.PageId);
        if (fault == "listeleme-5xx") _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(2, HttpStatusCode.ServiceUnavailable));
        if (fault == "listeleme-ag") _kit.Graph.Map(IsAccounts, _ => throw new HttpRequestException($"ağ yok {Guid.NewGuid():N}"));
        if (fault == "ekleme-5xx") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Error(2, HttpStatusCode.ServiceUnavailable));
        if (fault == "yetki") _kit.Graph.Map(u => IsPageToken(u, candidate.PageId), _ => FakeGraph.Json("{}"));
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);

        var warning = log.Warnings.Should().ContainSingle().Which;
        (warning.Level, warning.Message).Should().Be((LogLevel.Warning, fault.StartsWith("listeleme", StringComparison.Ordinal)
            ? "Instagram account listing failed"
            : $"Instagram account add failed for Page {candidate.PageId}"));
        warning.Exception.Should().NotBeNull("sebep istisnadadır");
        var bundle = (await _kit.Store.LoadAsync())!;
        foreach (var secret in new[] { bundle.UserAccessToken, _pages[0].Page.PageAccessToken, candidate.PageName })
            warning.Full.Should().NotContain(secret);
    }

    /// <summary>Metnin sebebi tam söylediği (sınır), sebebin başka yerde günlüğe düştüğü (oturum yenilenmeli: servis
    /// yazar) ve kullanıcının vazgeçtiği yollar günlüğe bir şey yazmaz.</summary>
    [Theory, InlineData("vazgec"), InlineData("sinir"), InlineData("oturum")]
    public async Task Vazgecme_sinir_ve_oturum_yenileme_gunluge_yazilmaz(string kind)
    {
        await SeedAsync(extraPages: kind == "sinir" ? 3 : 0);
        var candidate = AddCandidate();
        if (kind == "oturum") _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(190));
        var log = new CapturingLogger<SettingsViewModel>();
        var vm = await OpenAsync(log);
        if (kind == "sinir") _drawers.Decide = v => Pick(v, candidate.PageId);
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);

        vm.InstagramNotice.Should().Be(kind switch
        {
            "vazgec" => null,
            "sinir" => "En çok 4 Instagram hesabı bağlanabilir.",
            _ => ReconnectText,
        });
        log.Entries.Should().BeEmpty();
    }

    // ── "Facebook izinlerini güncelle" ve "İptal" ──

    private const string WaitingText = "Tarayıcıdan onay bekleniyor... Açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretli bırak; yalnız yenisini işaretlersen eski hesabın bağlantısı düşer.";
    private const string CanceledText = "Güncelleme iptal edildi; bağlantı değişmedi.";

    /// <summary>Komutu başlatır ve servis tarayıcıyı açana dek bekler; dönen görev komutun kendisidir. Komut
    /// tarayıcı açılmadan biterse (ör. erken bir hata) 30 sn beklemeden düşer ve sonuç satırını söyler.</summary>
    private async Task<Task> BeginReauthorizeAsync(SettingsViewModel vm)
    {
        var run = vm.ReauthorizeFacebookCommand.ExecuteAsync(null);
        var opened = _kit.Browser.WaitUntilOpenedAsync();
        await Task.WhenAny(run, opened);
        run.IsCompleted.Should().BeFalse("komut tarayıcı açılmadan bitti: {0}", vm.InstagramNotice);
        await opened;
        return run;
    }

    /// <summary><see cref="BeginReauthorizeAsync"/> + <paramref name="finish"/> + komutun bitmesini bekler
    /// (üst sınır <see cref="TestPatience.Upper"/>).</summary>
    private async Task ReauthorizeAsync(SettingsViewModel vm, Action<FakeFacebookOAuthBrowser> finish)
    {
        var run = await BeginReauthorizeAsync(vm);
        finish(_kit.Browser);
        await run.WaitAsync(TestPatience.Upper);
    }

    /// <summary><c>/me/accounts</c> yanıtı: yalnız verilen Sayfalar, Instagram hesaplarıyla.</summary>
    private static HttpResponseMessage AccountsOf(params SeededPage[] pages)
    {
        var data = new System.Text.Json.Nodes.JsonArray(pages.Select(p => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = p.Page.PageId,
            ["name"] = p.Page.PageName,
            ["instagram_business_account"] = new System.Text.Json.Nodes.JsonObject { ["id"] = p.IgUserId, ["username"] = p.IgUsername },
        }).ToArray());
        return FakeGraph.Json(new System.Text.Json.Nodes.JsonObject { ["data"] = data }.ToJsonString());
    }

    /// <summary>Post'u çağıranın iş parçacığında, işaret koyarak koşturan bağlam.</summary>
    private sealed class MarkingContext : SynchronizationContext
    {
        [ThreadStatic] public static bool InPost;
        public override void Post(SendOrPostCallback d, object? state) { var was = InPost; InPost = true; try { d(state); } finally { InPost = was; } }
    }

    [Fact]
    public async Task Izin_guncelle_beklerken_dugmeleri_kapatir_bitince_sonucu_yazar_karti_tazeler()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var run = await BeginReauthorizeAsync(vm);
        vm.InstagramAccountStatus.Should().Be(WaitingText);
        (vm.IsReauthorizePending, vm.IsFacebookBusy, vm.CanManageInstagram).Should().Be((true, true, false));
        var reopened = CreateVm(_service);                                  // R18: Ayarlar kapatılıp açıldı; yeni ViewModel, aynı servis
        await reopened.ReauthorizeFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
        reopened.InstagramNotice.Should().Be("Güncelleme başarısız: Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene. Bağlantı değişmedi.");
        _kit.Browser.Complete(); await run.WaitAsync(TestPatience.Upper);
        vm.InstagramNotice.Should().Be("Facebook izinleri güncellendi: 2 Sayfa yenilendi.");
        vm.InstagramAccountStatus.Should().Be("2 Instagram hesabı bağlı");
        (vm.IsReauthorizePending, vm.IsFacebookBusy, vm.CanManageInstagram).Should().Be((false, false, true));
    }

    [Theory, InlineData("0"), InlineData("1"), InlineData("02")]           // eksik Sayfaların sıra numaraları: birincil (0) eksikse parantez yazılır; iki eksik çoğuldur (karar)
    public async Task Eksik_Sayfa_kabul_edilince_soru_ve_sonuc_Sayfayi_adiyla_soyler(string missing)
    {
        await SeedAsync(extraPages: 2);
        var gone = missing.Select(c => _pages[c - '0']).ToArray();
        var names = string.Join(", ", gone.Select(p => $"\"{p.Page.PageName}\""));          // "A" ya da "A", "B"
        _kit.Graph.Map(IsAccounts, _ => AccountsOf(_pages.Except(gone).ToArray()));
        var vm = await OpenAsync();
        string? question = null;
        _drawers.Decide = v => { question = v.ActiveReauthorizeQuestion; return true; };
        await ReauthorizeAsync(vm, b => b.Complete());
        question.Should().Be($"{names} bu girişte paylaşılmadı. Kaydedersen bu {(gone.Length == 1 ? "Sayfanın" : "Sayfaların")} yorumları kesilebilir"
            + (missing.Contains('0') ? " (Facebook yorumları dahil)" : "")
            + ". Vazgeçersen OrderDeck'te hiçbir şey değişmez; \"Facebook izinlerini güncelle\"ye yeniden basıp tüm Sayfalarını işaretle. Yine de kaydedilsin mi?");
        vm.InstagramNotice.Should().Be($"Facebook izinleri güncellendi: {3 - gone.Length} Sayfa yenilendi. {names} bu girişte yoktu; yorumları kesilebilir.");
        _drawers.Titles.Should().Equal("Facebook izinlerini güncelle");
        vm.ActiveReauthorizeQuestion.Should().BeNull();
    }

    [Fact]
    public async Task Eksik_Sayfa_onayi_yakalanan_baglamda_sorulur_reddedilince_hicbir_sey_degismez()
    {
        await SeedAsync(extraPages: 1);
        _kit.Graph.Map(IsAccounts, _ => AccountsOf(_pages[0]));
        var viaContext = false;
        _drawers.Decide = _ => { viaContext = MarkingContext.InPost; return false; };          // Vazgeç
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new MarkingContext());
        SettingsViewModel vm;
        try { vm = CreateVm(_service = _kit.CreateService()); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await vm.RefreshInstagramStatusAsync();
        var before = await _service.GetUserAccessTokenAsync();
        await ReauthorizeAsync(vm, b => b.Complete());
        viaContext.Should().BeTrue("confirmMissing OAuth servisinin iş parçacığından gelir; çekmece arayüz bağlamında açılmalı");
        vm.InstagramNotice.Should().Be(CanceledText);
        (await _kit.CreateService().GetUserAccessTokenAsync()).Should().Be(before, "diske hiçbir şey yazılmadı");
        vm.InstagramPages.Should().HaveCount(2);
    }

    [Fact]
    public async Task Iptal_zaman_asimi_ve_diger_hatalar_ayri_metinlere_duser()
    {
        await SeedAsync();
        var vm = await OpenAsync(); var status = vm.InstagramAccountStatus;
        await ReauthorizeAsync(vm, _ => vm.CancelReauthorizeCommand.Execute(null));            // [İptal]
        vm.InstagramNotice.Should().Be(CanceledText);
        (vm.IsReauthorizePending, vm.IsFacebookBusy, vm.InstagramAccountStatus).Should().Be((false, false, status));
        await ReauthorizeAsync(vm, b => b.CompleteWithError("access_denied"));                 // Facebook ekranındaki "İptal": başarısız
        vm.InstagramNotice.Should().StartWith("Güncelleme başarısız: ").And.Contain("access_denied").And.EndWith(". Bağlantı değişmedi.");
        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(200));                                 // Graph hatası: ham gövde yazılmaz
        await ReauthorizeAsync(vm, b => b.Complete());
        vm.InstagramNotice.Should().Be("Güncelleme başarısız: Facebook hatası (kod 200). Bağlantı değişmedi.");
        _kit.Graph.Map(IsAccounts, _ => throw new TaskCanceledException());                    // ağ zaman aşımı: kullanıcı iptal etmedi, "iptal edildi" DEĞİL
        await ReauthorizeAsync(vm, b => b.Complete());
        vm.InstagramNotice.Should().StartWith("Güncelleme başarısız: ").And.EndWith(". Bağlantı değişmedi.");
        var slow = await OpenAsync(callbackTimeout: TimeSpan.FromMilliseconds(50));            // zaman aşımı
        await slow.ReauthorizeFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
        slow.InstagramNotice.Should().Be("Facebook onayı 5 dakika içinde tamamlanmadı; bağlantı değişmedi. Yeniden dene.");
    }

    [Fact]                                                                  // Review Focus 1
    public async Task Beklerken_baglanti_kaldirilirsa_guncelleme_basarisiz_olur_baglanti_dirilmez()
    {
        await SeedAsync(extraPages: 1);
        var vm = await OpenAsync();
        var run = await BeginReauthorizeAsync(vm);
        await vm.DisconnectFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
        (vm.IsFacebookBusy, vm.IsFacebookIdle).Should().Be((false, false), "akış beklerken \"Facebook'a Bağlan\" pasif kalır (TK17)");
        _kit.Browser.Complete();
        await run.WaitAsync(TestPatience.Upper);
        vm.InstagramNotice.Should().StartWith("Güncelleme başarısız: ").And.EndWith(". Bağlantı değişmedi.");
        (await _kit.CreateService().IsConnectedAsync()).Should().BeFalse("kaldırılmış bağlantı dirilmez");
        (vm.InstagramPages.Count, vm.InstagramAccountStatus, vm.IsFacebookIdle).Should().Be((0, "Önce Facebook'a bağlan.", true), "bekleme metni artık durmuyor: eski metin geri konmaz; akış bitti, \"Facebook'a Bağlan\" açılır");
    }

    [Fact]
    public async Task Basarili_guncellemeden_sonra_izin_uyarisi_kalkar_ekleme_listelemeye_gecer()
    {
        await SeedAsync();
        var granted = false;
        _kit.Graph.Map(IsPermissions, _ => FakeGraph.Json(granted
            ? """{"data":[{"permission":"instagram_basic","status":"granted"},{"permission":"instagram_manage_comments","status":"granted"}]}"""
            : """{"data":[]}"""));
        var vm = await OpenAsync();
        vm.InstagramNeedsReconnect.Should().BeTrue();
        await ReauthorizeAsync(vm, b => { granted = true; b.Complete(); });
        vm.InstagramNeedsReconnect.Should().BeFalse();
        var listings = _kit.Graph.Requests.Count(IsAccounts);
        await vm.AddInstagramAccountCommand.ExecuteAsync(null);
        _kit.Graph.Requests.Count(IsAccounts).Should().Be(listings + 1, "0. adım kalktı: listeleme yapılır");
    }

    [Fact]                                                                  // bağlanınca kart da tazelenir; tarayıcı dikişiyle
    public async Task Baglan_tamamlaninca_Instagram_karti_da_tazelenir()
    {
        var candidate = AddCandidate();                                     // depo boş: Graph tek Sayfa listeler, Sayfa seçici açılmaz
        var vm = await OpenAsync();
        var run = vm.ConnectFacebookCommand.ExecuteAsync(null);
        await _kit.Browser.WaitUntilOpenedAsync();
        _kit.Browser.Complete();
        await run.WaitAsync(TestPatience.Upper);
        (vm.InstagramAccountStatus, vm.CanManageInstagram).Should().Be(($"Bağlı Instagram hesabı: @{candidate.IgUsername}", true));
    }

    [Fact]                                                                  // Review Focus 3
    public void Kart_beklerken_Iptali_gosterir_uc_dugmeyi_kapatir_bitince_Iptal_gizlenir() => OnSta(() =>
    {
        Button Named(DependencyObject root, string content) => Descendants(root).OfType<Button>().Single(b => Equals(b.Content, content));
        var waiting = CreateVm(oauth: null);
        waiting.IsFacebookConnected = waiting.IsFacebookBusy = waiting.IsReauthorizePending = waiting.InstagramNeedsReconnect = true;
        var page = RenderFacebookTab(waiting);
        var cancel = Descendants(page).OfType<Button>().Single(b => ReferenceEquals(b.Command, waiting.CancelReauthorizeCommand));
        cancel.Content.Should().Be("İptal");
        Shown(cancel).Should().BeTrue();
        new[] { "Facebook'a Bağlan", "Facebook izinlerini güncelle", "Instagram hesabı ekle" }
            .Select(name => Named(page, name).IsEnabled).Should().OnlyContain(enabled => !enabled);
        var reauthorize = Named(page, "Facebook izinlerini güncelle");
        reauthorize.Command.Should().BeSameAs(waiting.ReauthorizeFacebookCommand);
        reauthorize.ToolTip.Should().Be("Yayın sırasında yalnız kopan bir hesabı düzeltmek için bas: çalışan Instagram ve Facebook yorumlarında kısa bir kesinti olabilir.");
        ShownTexts(page).Select(t => t.Text).Should().Contain("Instagram yorumları için Facebook izinlerini güncellemen gerekiyor: aşağıdaki \"Facebook izinlerini güncelle\"ye bas.");

        var idle = CreateVm(oauth: null);
        idle.IsFacebookConnected = true;
        var after = RenderFacebookTab(idle);
        Shown(Descendants(after).OfType<Button>().Single(b => ReferenceEquals(b.Command, idle.CancelReauthorizeCommand))).Should().BeFalse();
        Named(after, "Facebook izinlerini güncelle").IsEnabled.Should().BeTrue();
    });

    // ── Ek sabitlemeler: izin güncellemesinin günlük kuralı ve hata metinleri ──

    /// <summary>
    /// Sebebi tam göstermeyen "Güncelleme başarısız" metnine düşen hata (Graph hatası, genel hata) günlüğe Warning
    /// olarak düşer: destek sebebi günlükten okur. İstisna eklidir; token yoktur. Kullanıcının iptali ("İptal",
    /// onayda "Vazgeç") ve onay zaman aşımı günlüğe yazılmaz: metin sebebi tam söyler.
    /// </summary>
    [Fact]
    public async Task Basarisiz_guncelleme_sebebi_gunluge_uyari_olarak_duser_iptal_ve_zaman_asimi_yazilmaz()
    {
        await SeedAsync(extraPages: 1);
        var log = new CapturingLogger<SettingsViewModel>();
        var vm = await OpenAsync(log);
        var oldUserToken = (await _kit.Store.LoadAsync())!.UserAccessToken;

        await ReauthorizeAsync(vm, _ => vm.CancelReauthorizeCommand.Execute(null));            // [İptal]
        _kit.Graph.Map(IsAccounts, _ => AccountsOf(_pages[0]));                                 // ek Sayfa eksik; Decide varsayılanı: Vazgeç
        await ReauthorizeAsync(vm, b => b.Complete());
        (vm.InstagramNotice, log.Entries.Count).Should().Be((CanceledText, 0), "kullanıcının iptali günlüğe yazılmaz");

        _kit.Graph.Map(IsAccounts, _ => FakeGraph.Error(200));                                 // Graph hatası
        await ReauthorizeAsync(vm, b => b.Complete());
        await ReauthorizeAsync(vm, b => b.CompleteWithError("access_denied"));                 // genel hata
        log.Warnings.Should().HaveCount(2);
        var secrets = new[] { oldUserToken, _kit.Broker.LastIssuedToken! }
            .Concat((await _service.GetConnectedPagesAsync()).Select(p => p.PageAccessToken));
        foreach (var warning in log.Warnings)
        {
            (warning.Level, warning.Message).Should().Be((LogLevel.Warning, "Facebook permission update failed"));
            warning.Exception.Should().NotBeNull("sebep istisnadadır");
            foreach (var secret in secrets) warning.Full.Should().NotContain(secret);
            warning.Full.Should().NotContain("access_token=");
        }
        log.Warnings[0].Exception.Should().Contain(nameof(FacebookGraphException));
        log.Warnings[1].Exception.Should().Contain("access_denied");

        var slowLog = new CapturingLogger<SettingsViewModel>();
        var slow = await OpenAsync(slowLog, TimeSpan.FromMilliseconds(50));                    // zaman aşımı
        await slow.ReauthorizeFacebookCommand.ExecuteAsync(null).WaitAsync(TestPatience.Upper);
        (slow.InstagramNotice, slowLog.Entries.Count)
            .Should().Be(("Facebook onayı 5 dakika içinde tamamlanmadı; bağlantı değişmedi. Yeniden dene.", 0));
    }

    /// <summary>Graph kodu okunamayan hatada (JSON olmayan gövde) metin HTTP durumunu yazar; ham gövde yazılmaz.</summary>
    [Fact]
    public async Task Graph_kodu_okunamazsa_metin_HTTP_durumunu_yazar()
    {
        await SeedAsync();
        var body = $"<html>{Guid.NewGuid():N}</html>";
        _kit.Graph.Map(IsAccounts, _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) });
        var vm = await OpenAsync();
        await ReauthorizeAsync(vm, b => b.Complete());
        vm.InstagramNotice.Should().Be("Güncelleme başarısız: Facebook hatası (HTTP 502). Bağlantı değişmedi.");
    }

    /// <summary>Akış yokken "İptal" etkisizdir; bayrak "Facebook'a Bağlan"ın etkinliğini bildirir. Onay çekmecesi
    /// verilmemişse eksik Sayfa onaylanmamış sayılır: hiçbir şey kaydedilmez.</summary>
    [Fact]
    public async Task Akis_yokken_Iptal_etkisiz_cekmece_yoksa_eksik_Sayfa_onaylanmaz()
    {
        await SeedAsync(extraPages: 1);
        _service = _kit.CreateService();
        var vm = CreateVm(_service);
        await vm.RefreshInstagramStatusAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.CancelReauthorizeCommand.Execute(null);
        raised.Should().BeEmpty("akış yokken İptal etkisizdir");
        vm.IsReauthorizePending = true;
        raised.Should().Contain(nameof(SettingsViewModel.IsFacebookIdle));
        vm.IsReauthorizePending = false;

        var noDrawers = CreateVm(_service, withoutDrawers: true);
        await noDrawers.RefreshInstagramStatusAsync();
        _kit.Graph.Map(IsAccounts, _ => AccountsOf(_pages[0]));
        var before = await _service.GetUserAccessTokenAsync();
        await ReauthorizeAsync(noDrawers, b => b.Complete());
        noDrawers.InstagramNotice.Should().Be(CanceledText);
        (await _kit.CreateService().GetUserAccessTokenAsync()).Should().Be(before, "diske hiçbir şey yazılmadı");
    }
}
