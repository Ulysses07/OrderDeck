using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Pages;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.App.Views.Pages;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Shortcuts;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.App;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class SettingsViewModel_PaymentTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModel_PaymentTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"orderdeck-svm-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>HttpMessageHandler that always returns 404 — keeps IntakeForm.LoadAsync fire-and-forget tame.</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}")
            });
    }

    private SettingsViewModel CreateVm(AppSettings settings, SettingsStore store)
    {
        var registry = new ShortcutRegistry(store);
        // ShortcutBinder is only invoked from ShortcutsTabViewModel.SaveCommand —
        // not used in payment tests, so null! is acceptable here.
        var shortcutsTab = new ShortcutsTabViewModel(registry, null!);

        var http = new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var intakeForm = new IntakeFormSettingsViewModel(api);
        var shopperApp = new ShopperAppSettingsViewModel(api, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShopperAppSettingsViewModel>.Instance);

        return new SettingsViewModel(settings, store, shortcutsTab, intakeForm, shopperApp);
    }

    [Fact]
    public void Load_PopulatesPaymentFieldsFromSettings()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings();
        s.Payment.WhatsAppMessageTemplate = "Hi {ad}";
        s.Payment.Iban = "TR12";
        s.Payment.AccountHolder = "Burak";
        s.Payment.Papara = "1234567";

        var sut = CreateVm(s, store);

        sut.PaymentTemplate.Should().Be("Hi {ad}");
        sut.Iban.Should().Be("TR12");
        sut.AccountHolder.Should().Be("Burak");
        sut.Papara.Should().Be("1234567");
    }

    [Fact]
    public void Save_PersistsPaymentFields()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings(); // OverlayPort defaults to 4747 (valid)

        var sut = CreateVm(s, store);
        sut.PaymentTemplate = "New {tutar}";
        sut.Iban = "TR99";
        sut.AccountHolder = "X";
        sut.Papara = "9";

        sut.SaveCommand.Execute(null);

        sut.Saved.Should().BeTrue();

        var loaded = store.Load();
        loaded.Payment.WhatsAppMessageTemplate.Should().Be("New {tutar}");
        loaded.Payment.Iban.Should().Be("TR99");
        loaded.Payment.AccountHolder.Should().Be("X");
        loaded.Payment.Papara.Should().Be("9");
    }

    // ── Kargo PR A — Shipping settings ──────────────────────────────────

    [Fact]
    public void Load_displays_shipping_as_empty_when_settings_null()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings(); // Shipping defaults to null/null

        var sut = CreateVm(s, store);

        sut.FreeShippingThresholdText.Should().BeEmpty();
        sut.ShippingFeeText.Should().BeEmpty();
    }

    [Fact]
    public void Load_displays_shipping_values_when_set()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings();
        s.Shipping.FreeShippingThreshold = 5000m;
        s.Shipping.ShippingFee = 150m;

        var sut = CreateVm(s, store);

        sut.FreeShippingThresholdText.Should().Be("5000");
        sut.ShippingFeeText.Should().Be("150");
    }

    [Theory]
    [InlineData("5000", "150", 5000.00, 150.00)]
    [InlineData("4999,99", "149,50", 4999.99, 149.50)]   // Türkçe virgül
    [InlineData("4999.99", "149.50", 4999.99, 149.50)]   // Invariant nokta
    public void Save_persists_shipping_values_parsing_both_decimal_styles(
        string thresholdText, string feeText, decimal expectedThreshold, decimal expectedFee)
    {
        var store = new SettingsStore(_path);
        var sut = CreateVm(new AppSettings(), store);
        sut.FreeShippingThresholdText = thresholdText;
        sut.ShippingFeeText = feeText;

        sut.SaveCommand.Execute(null);
        sut.Saved.Should().BeTrue();

        var loaded = store.Load();
        loaded.Shipping.FreeShippingThreshold.Should().Be(expectedThreshold);
        loaded.Shipping.ShippingFee.Should().Be(expectedFee);
        loaded.Shipping.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("0", "0")]
    [InlineData("-100", "-50")]
    [InlineData("abc", "xyz")]
    public void Save_disables_shipping_when_invalid_or_zero_or_negative(
        string thresholdText, string feeText)
    {
        var store = new SettingsStore(_path);
        var sut = CreateVm(new AppSettings(), store);
        sut.FreeShippingThresholdText = thresholdText;
        sut.ShippingFeeText = feeText;

        sut.SaveCommand.Execute(null);
        sut.Saved.Should().BeTrue();

        var loaded = store.Load();
        loaded.Shipping.FreeShippingThreshold.Should().BeNull();
        loaded.Shipping.ShippingFee.Should().BeNull();
        loaded.Shipping.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Save_one_field_only_persists_partial_state()
    {
        // Operatör sadece threshold girip fee'yi unutursa: ikisi de
        // bağımsız parse; IsEnabled false kalır (ShippingSettings.IsEnabled
        // ikisinin de positive olmasını ister).
        var store = new SettingsStore(_path);
        var sut = CreateVm(new AppSettings(), store);
        sut.FreeShippingThresholdText = "5000";
        sut.ShippingFeeText = ""; // forgot

        sut.SaveCommand.Execute(null);

        var loaded = store.Load();
        loaded.Shipping.FreeShippingThreshold.Should().Be(5000m);
        loaded.Shipping.ShippingFee.Should().BeNull();
        loaded.Shipping.IsEnabled.Should().BeFalse();
    }
}

/// <summary>
/// Ayar ekranının Kargo kartı mod On'da (spec 2026-10-13 §4.8, §6.2, sahip kararı Y4; W3-03): alanlar lisans geneli
/// ayarı gösterir, kaydet kargo kısmını <c>PUT shipping-settings</c> ile yazar ve yerel <c>AppSettings.Shipping</c>'e
/// dokunmaz; kargo dışı alanlar bugünkü gibi yerel. Sonuçlar satır içi şeritte (<see cref="SettingsViewModel.ShippingNotice"/>);
/// açılır pencere yok. Mod Off (bugün her lisans) bugünkü yerel yazımdır.
/// </summary>
public sealed class SettingsViewModel_LicenseShippingTests : IDisposable
{
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";
    private static readonly Guid LicenseId = Guid.NewGuid();

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"orderdeck-svm-{Guid.NewGuid():N}.json");
    private readonly InMemorySqlite _db = new();
    private readonly SalesSyncStateRepository _state;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings = new();
    private readonly List<(HttpMethod Method, string Path, string? Body)> _requests = new();
    private int _modeOffRequests;

    /// <summary>PUT shipping-settings'in yanıtı (testin senaryosu).</summary>
    private Func<HttpResponseMessage> _put = () => FakeHttpMessageHandler.Empty(500);

    private sealed class License : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => Lisans;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    public SettingsViewModel_LicenseShippingTests()
    {
        new MigrationRunner(_db).Run();
        _state = new SalesSyncStateRepository(_db);
        _store = new SettingsStore(_path);

        // Bu bilgisayarın yerel kargo ayarı: mod On'da hiçbir yolda değişmemeli (diskte ve canlı örnekte).
        _settings.Shipping.FreeShippingThreshold = 600m;
        _settings.Shipping.ShippingFee = 49m;
        _store.Save(_settings);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Yerel kip On ve sales/state'in sakladığı lisans geneli ayar.</summary>
    private void ModeOn(int version = 4, decimal? threshold = 500m, decimal? fee = 30m)
    {
        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        _state.SaveServerState(version, threshold, fee, scrubOrderText: false);
    }

    private HttpResponseMessage Respond(HttpRequestMessage req)
    {
        var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        lock (_requests) _requests.Add((req.Method, req.RequestUri!.AbsolutePath, body));
        if (req.RequestUri!.AbsolutePath == "/api/v1/me/licenses")
            return FakeHttpMessageHandler.Json(200,
                $$"""[{"licenseKey":"{{Lisans}}","skuCode":"standart","expiresAt":"2027-01-01T00:00:00+00:00","revokedAt":null,"id":"{{LicenseId}}"}]""");
        if (req.Method == HttpMethod.Put && req.RequestUri.AbsolutePath == $"/api/v1/licenses/{LicenseId}/shipping-settings")
            return _put();
        return FakeHttpMessageHandler.Empty(404);
    }

    private List<(HttpMethod Method, string Path, string? Body)> Puts
    {
        get { lock (_requests) return _requests.Where(r => r.Method == HttpMethod.Put).ToList(); }
    }

    private SettingsViewModel CreateVm()
    {
        var registry = new ShortcutRegistry(_store);
        var shortcutsTab = new ShortcutsTabViewModel(registry, null!);
        var quietHttp = new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") };
        var quietApi = new LicenseApiClient(quietHttp, new LicenseTokenStore());

        var http = new HttpClient(new FakeHttpMessageHandler(Respond)) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        var license = new License();
        var writer = new LicenseShippingSettingsWriter(api, TestSalesEnvelope.For(_db, license), _state, license,
            () => Interlocked.Increment(ref _modeOffRequests), NullLogger<LicenseShippingSettingsWriter>.Instance);

        return new SettingsViewModel(_settings, _store, shortcutsTab,
            new IntakeFormSettingsViewModel(quietApi),
            new ShopperAppSettingsViewModel(quietApi, NullLogger<ShopperAppSettingsViewModel>.Instance),
            shipping: new ShippingSettingsProvider(_state, _settings),
            shippingWriter: writer);
    }

    private static HttpResponseMessage Snapshot(int version, string threshold, string fee) =>
        FakeHttpMessageHandler.Json(200, $$"""
            {"version":{{version}},"freeShippingThreshold":{{threshold}},"fee":{{fee}},
             "updatedAt":"2026-10-18T09:00:00+00:00","updatedBy":"{{Guid.NewGuid()}}"}
            """);

    private void LocalShippingUnchanged()
    {
        _settings.Shipping.FreeShippingThreshold.Should().Be(600m, "mod On'da yerel ayar yazılmaz (canlı örnek)");
        _settings.Shipping.ShippingFee.Should().Be(49m);
        var disk = _store.Load();
        disk.Shipping.FreeShippingThreshold.Should().Be(600m, "mod On'da yerel ayar yazılmaz (disk)");
        disk.Shipping.ShippingFee.Should().Be(49m);
    }

    // ── mod Off: bugünkü yerel yazım ─────────────────────────────────────

    [Fact]
    public async Task Mod_Offta_kargo_yerel_ayara_yazilir_istek_gitmez_cip_yok()
    {
        var sut = CreateVm();
        sut.IsShippingLicenseWide.Should().BeFalse();
        sut.FreeShippingThresholdText.Should().Be("600");
        sut.ShippingFeeText.Should().Be("49");

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39,90";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeTrue();
        _store.Load().Shipping.FreeShippingThreshold.Should().Be(750m);
        _store.Load().Shipping.ShippingFee.Should().Be(39.90m);
        _settings.Shipping.FreeShippingThreshold.Should().Be(750m);
        _requests.Should().BeEmpty("mod Off'ta lisans geneli ayara hiçbir istek gitmez");
        _state.GetShippingCache().Version.Should().BeNull();
    }

    // ── mod On ───────────────────────────────────────────────────────────

    [Fact]
    public void Mod_Onda_alanlar_lisans_geneli_degeri_gosterir_cip_gorunur()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);

        var sut = CreateVm();

        sut.IsShippingLicenseWide.Should().BeTrue();
        sut.FreeShippingThresholdText.Should().Be("500", "yerel 600 değil, lisans geneli değer");
        sut.ShippingFeeText.Should().Be("30");
    }

    [Fact]
    public async Task Mod_Onda_kaydet_PUT_eder_onbellegi_gunceller_yerel_kargoya_dokunmaz_kargo_disi_alanlar_kaydedilir()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => Snapshot(5, "750", "39.90");
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39,90";
        sut.PaymentTemplate = "Yeni şablon {tutar}";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeTrue();
        sut.ShippingNotice.Should().BeNull();
        var put = Puts.Should().ContainSingle().Subject;
        var json = JsonDocument.Parse(put.Body!).RootElement;
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "expectedVersion", "freeShippingThreshold", "fee", "replicaId", "protocol", "seenMode");
        json.GetProperty("expectedVersion").GetInt32().Should().Be(4, "formun açıldığı önbellek sürümü");
        json.GetProperty("freeShippingThreshold").GetDecimal().Should().Be(750m);
        json.GetProperty("fee").GetDecimal().Should().Be(39.90m);
        json.GetProperty("replicaId").GetGuid().Should().Be(new ReplicaIdentityRepository(_db).GetReplicaId()!.Value);
        json.GetProperty("protocol").GetInt32().Should().Be(2);
        json.GetProperty("seenMode").GetInt32().Should().Be(1);

        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 5, 750m, 39.90m), "önbellek yanıtın satırı");
        LocalShippingUnchanged();
        _store.Load().Payment.WhatsAppMessageTemplate.Should().Be("Yeni şablon {tutar}", "kargo dışı alanlar yine yerel");
        _settings.Payment.WhatsAppMessageTemplate.Should().Be("Yeni şablon {tutar}");
    }

    [Fact]
    public async Task Mod_Onda_stale_formu_guncel_degerle_doldurur_seritte_soyler_sonraki_kayit_yeni_surumle_gider()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => FakeHttpMessageHandler.Json(409, """
            {"title":"stale","status":409,"detail":"x",
             "current":{"version":6,"freeShippingThreshold":900,"fee":45,"updatedAt":"2026-10-18T09:30:00+00:00",
                        "updatedBy":"cli"}}
            """);
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeFalse("kargo yazılmadı: sayfa açık kalır, şerit görünür");
        sut.ShippingNotice.Should().Be("Kargo ayarı bu arada başka bilgisayarda değişti; güncel değer gösterildi.");
        sut.FreeShippingThresholdText.Should().Be("900");
        sut.ShippingFeeText.Should().Be("45");
        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 6, 900m, 45m), "önbellek = current");
        LocalShippingUnchanged();

        // Operatör güncel değeri görüp yeniden düzenler: yazım güncel sürümle gider.
        _put = () => Snapshot(7, "950", "45");
        sut.FreeShippingThresholdText = "950";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeTrue();
        sut.ShippingNotice.Should().BeNull();
        JsonDocument.Parse(Puts[^1].Body!).RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(6);
        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 7, 950m, 45m));
    }

    [Fact]
    public async Task Mod_Onda_yazim_formun_acildigi_surumle_gider_arada_gelen_deger_sessizce_ezilmez()
    {
        // Form v4 ile açıldı; operatör düzenlerken sales/state turu başka bilgisayarın yazdığı v5'i getirdi. Kaydet v5 ile
        // gitseydi operatörün hiç görmediği değer sessizce ezilirdi; v4 ile gider, sunucu stale ve güncel değeri döner.
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        var sut = CreateVm();
        _state.SaveServerState(5, 800m, 35m, scrubOrderText: false);
        _put = () => FakeHttpMessageHandler.Json(409, """
            {"title":"stale","status":409,"detail":"x",
             "current":{"version":5,"freeShippingThreshold":800,"fee":35,"updatedAt":"2026-10-18T09:30:00+00:00",
                        "updatedBy":"cli"}}
            """);

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39";
        await sut.SaveCommand.ExecuteAsync(null);

        JsonDocument.Parse(Puts.Should().ContainSingle().Subject.Body!).RootElement
            .GetProperty("expectedVersion").GetInt32().Should().Be(4);
        sut.FreeShippingThresholdText.Should().Be("800");
        sut.ShippingNotice.Should().Be(SalesCommandMessages.ShippingSettingsStale);
    }

    [Fact]
    public async Task Mod_Offta_acilan_form_mod_Onda_kaydedilirse_hic_gosterilmemis_lisans_geneli_deger_ezilmez()
    {
        // Form mod Off'ta yerel 600/49 ile açıldı; sayfa açıkken açılış turu kipi 1 yaptı (lisans geneli 1000/80, v3).
        // Operatör yalnız eşiği değiştirir. Kaydet önbelleğin sürümüyle gitseydi formun hiç göstermediği ücret (80) yerel
        // 49'la sessizce ezilirdi; sürümsüz (0) gider, sunucu stale ve güncel değeri döner.
        var sut = CreateVm();
        sut.IsShippingLicenseWide.Should().BeFalse("ön koşul: form yerel değerle açıldı");
        ModeOn(version: 3, threshold: 1000m, fee: 80m);
        _put = () => FakeHttpMessageHandler.Json(409, """
            {"title":"stale","status":409,"detail":"x",
             "current":{"version":3,"freeShippingThreshold":1000,"fee":80,"updatedAt":"2026-10-18T09:30:00+00:00",
                        "updatedBy":"cli"}}
            """);

        sut.FreeShippingThresholdText = "650";
        await sut.SaveCommand.ExecuteAsync(null);

        JsonDocument.Parse(Puts.Should().ContainSingle().Subject.Body!).RootElement
            .GetProperty("expectedVersion").GetInt32().Should().Be(0, "form lisans geneli hiçbir sürümü göstermedi");
        sut.Saved.Should().BeFalse();
        sut.IsShippingLicenseWide.Should().BeTrue();
        sut.FreeShippingThresholdText.Should().Be("1000");
        sut.ShippingFeeText.Should().Be("80");
        sut.ShippingNotice.Should().Be(SalesCommandMessages.ShippingSettingsStale);
        LocalShippingUnchanged();
    }

    [Fact]
    public async Task Mod_Onda_acilan_form_disabledan_sonra_kaydedilirse_Off_turunun_kopyasi_ezilmez()
    {
        // Form mod On'da lisans geneli 500/60 (v3) ile açıldı. PC-B ayarı 700/70 (v4) yaptı, sonra disable geldi: Off turu v4'ü
        // yerel ayara kopyaladı. Operatör yalnız etiket genişliğini değiştirip kaydetti — kip artık Off olduğu için form,
        // değişmemiş kargo alanlarını yerel ayara yazıyor, kopyayı bayat 500/60 ile eziyordu (§6.2/§8.5: PC'ler Off'a
        // döndüklerinde farklı yerel değerlerle farklı kargo istemez).
        ModeOn(version: 3, threshold: 500m, fee: 60m);
        var sut = CreateVm();
        sut.IsShippingLicenseWide.Should().BeTrue("ön koşul: form lisans geneli değerle açıldı");
        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1");
        _state.SaveServerState(4, 700m, 70m, scrubOrderText: false);
        _settings.Shipping = new ShippingSettings { FreeShippingThreshold = 700m, ShippingFee = 70m };
        _store.Update(s => s.Shipping = new ShippingSettings { FreeShippingThreshold = 700m, ShippingFee = 70m });

        sut.LabelWidthMm += 5;
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeTrue();
        Puts.Should().BeEmpty("mod Off: istek gitmez");
        _settings.Shipping.FreeShippingThreshold.Should().Be(700m, "değişmemiş kargo alanı Off turunun kopyasını ezmez");
        _settings.Shipping.ShippingFee.Should().Be(70m);
        var disk = _store.Load();
        disk.Shipping.FreeShippingThreshold.Should().Be(700m);
        disk.Shipping.ShippingFee.Should().Be(70m);
        disk.LabelWidthMm.Should().Be(sut.LabelWidthMm, "kargo dışı alan yine kaydedilir");
    }

    [Fact]
    public async Task Mod_Onda_cevrimdisi_serit_baglanti_ister_alanlar_onbellege_doner_yerel_yazim_yok()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => throw new HttpRequestException("ağ yok");
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39";
        sut.PaymentTemplate = "Çevrimdışı şablon {tutar}";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeFalse();
        sut.ShippingNotice.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        sut.FreeShippingThresholdText.Should().Be("500", "alanlar önbellek değerine döner");
        sut.ShippingFeeText.Should().Be("30");
        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 4, 500m, 30m));
        LocalShippingUnchanged();
        _store.Load().Payment.WhatsAppMessageTemplate.Should().Be("Çevrimdışı şablon {tutar}",
            "kargo dışı alanlar bağlantı istemez");
    }

    [Theory]
    [InlineData(500, "Bu değişiklik için sunucu bağlantısı gerekli")]
    [InlineData(404, "Sunucu güncelleniyor; biraz sonra tekrar dene.")]
    public async Task Mod_Onda_sunucu_hatasi_ve_uc_yok_seritte_soylenir_kargo_yazilmaz(int status, string notice)
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => FakeHttpMessageHandler.Empty(status);
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeFalse();
        sut.ShippingNotice.Should().Be(notice);
        sut.FreeShippingThresholdText.Should().Be("500");
        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 4, 500m, 30m));
        LocalShippingUnchanged();
    }

    [Fact]
    public async Task Mod_Onda_mode_off_Off_turunu_ister_kargo_kaydedilmez()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => FakeHttpMessageHandler.Problem(409, "mode-off", "x");
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39";
        await sut.SaveCommand.ExecuteAsync(null);

        Volatile.Read(ref _modeOffRequests).Should().Be(1, "istemci modu yanlış biliyordu: Off turu (§8.5)");
        sut.Saved.Should().BeFalse();
        sut.ShippingNotice.Should().Be("Senkron kapatıldı; ekran yenileniyor.");
        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 4, 500m, 30m));
        LocalShippingUnchanged();
    }

    [Theory]
    [InlineData("750", "")]
    [InlineData("", "39")]
    [InlineData("750,123", "39")]
    [InlineData("750", "-5")]
    [InlineData("abc", "39")]
    public async Task Mod_Onda_gecersiz_kargo_dogrulama_hatasi_verir_hicbir_sey_yazilmaz(string threshold, string fee)
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        var sut = CreateVm();

        sut.FreeShippingThresholdText = threshold;
        sut.ShippingFeeText = fee;
        sut.PaymentTemplate = "Yazılmamalı {tutar}";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeFalse();
        sut.ValidationError.Should().Contain("Kargo");
        Puts.Should().BeEmpty("doğrulanmamış değer sunucuya gitmez");
        _store.Load().Payment.WhatsAppMessageTemplate.Should().NotBe("Yazılmamalı {tutar}",
            "doğrulama düşünce bugünkü gibi hiçbir alan yazılmaz");
        LocalShippingUnchanged();
    }

    [Fact]
    public async Task Mod_Onda_iki_alan_sifir_ya_da_bos_kargoyu_kapatir()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => Snapshot(5, "null", "null");
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "0";   // kartın ipucu: "0 yaz = kargo kapalı"
        sut.ShippingFeeText = "";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeTrue();
        var json = JsonDocument.Parse(Puts.Should().ContainSingle().Subject.Body!).RootElement;
        json.GetProperty("freeShippingThreshold").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("fee").ValueKind.Should().Be(JsonValueKind.Null);
        _state.GetShippingCache().Should().Be(new SalesShippingCache(1, 5, null, null));
        LocalShippingUnchanged();
    }

    [Fact]
    public async Task Mod_Onda_kargo_degismediyse_istek_gitmez_kaydet_baglanti_istemez()
    {
        ModeOn(version: 4, threshold: 500m, fee: 30m);
        _put = () => throw new HttpRequestException("ağ yok");
        var sut = CreateVm();

        sut.FreeShippingThresholdText = "500,00";   // aynı değer, farklı yazım
        sut.PaymentTemplate = "Yalnız şablon {tutar}";
        await sut.SaveCommand.ExecuteAsync(null);

        sut.Saved.Should().BeTrue();
        sut.ShippingNotice.Should().BeNull();
        _requests.Should().BeEmpty();
        _store.Load().Payment.WhatsAppMessageTemplate.Should().Be("Yalnız şablon {tutar}");
        LocalShippingUnchanged();
    }

    [Fact]
    public async Task Serit_Tamam_ile_kapanir()
    {
        ModeOn();
        _put = () => FakeHttpMessageHandler.Empty(404);
        var sut = CreateVm();
        sut.FreeShippingThresholdText = "750";
        sut.ShippingFeeText = "39";
        await sut.SaveCommand.ExecuteAsync(null);
        sut.ShippingNotice.Should().NotBeNull();

        sut.DismissShippingNoticeCommand.Execute(null);

        sut.ShippingNotice.Should().BeNull();
    }

    [Fact]
    public void Ayar_ekrani_acilir_pencere_servisine_bagli_degil()
    {
        // #508 sahip tercihi: bu akışların sonucu satır içi şerittir (ShippingNotice). Ekran IDialogService almadığı
        // için hiçbir yol açılır pencere açamaz.
        typeof(SettingsViewModel).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(IDialogService));
    }

    [Fact]
    public void Sayfa_cipi_yalniz_mod_Onda_seridi_yalniz_sonuc_varken_gosterir()
    {
        const string Notice = "Bu değişiklik için sunucu bağlantısı gerekli";
        System.Windows.Visibility? offChip = null, onChip = null, onNotice = null;
        string? offNoticeText = null;

        var error = ThemeTestHost.RunOnSta(() =>
        {
            // Mod Off (bugün her lisans): çip yok, şerit yok — ekran bugünkü gibi.
            var off = Render(CreateVm());
            offChip = HostVisibility(off, SalesCommandMessages.ShippingLicenseWideChip);
            offNoticeText = NoticeStripVisible(off);

            ModeOn();
            var vm = CreateVm();
            vm.ShippingNotice = Notice;
            var on = Render(vm);
            onChip = HostVisibility(on, SalesCommandMessages.ShippingLicenseWideChip);
            onNotice = HostVisibility(on, Notice);
        });

        error.Should().BeNull();
        offChip.Should().Be(System.Windows.Visibility.Collapsed, "mod Off'ta kargo yerel ayardır");
        offNoticeText.Should().BeNull("sonuç yokken şerit görünmez");
        onChip.Should().Be(System.Windows.Visibility.Visible, "Kargo kartında lisans geneli çipi");
        onNotice.Should().Be(System.Windows.Visibility.Visible, "şerit sayfanın alt çubuğunun üstünde");
    }

    private static SettingsPage Render(SettingsViewModel vm)
    {
        var pages = new PageStack();
        pages.ShowAsync("settings", "Ayarlar", p => SettingsPage.Create(p, vm));
        var page = Assert.IsType<SettingsPage>(pages.Top!.Content);
        page.Measure(new System.Windows.Size(1200, 900));
        page.Arrange(new System.Windows.Rect(0, 0, 1200, 900));
        page.UpdateLayout();
        return page;
    }

    /// <summary>Şerit görünürse (Amber zeminli, "Tamam"lı Border) metni; değilse null.</summary>
    private static string? NoticeStripVisible(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is System.Windows.Controls.Border { Visibility: System.Windows.Visibility.Visible } border
                && border.Child is System.Windows.Controls.DockPanel dock
                && dock.Children.OfType<System.Windows.Controls.Button>().Any(b => Equals(b.Content, "Tamam"))
                && dock.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault() is { } tb)
                return tb.Text;
            if (NoticeStripVisible(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Metni verilen TextBlock'u mantıksal ağaçta bulur (seçili olmayan sekmenin içeriği görsel ağaçta yoktur) ve
    /// onu taşıyan en yakın Border'ın görünürlüğünü döner; bulunamazsa null.</summary>
    private static System.Windows.Visibility? HostVisibility(System.Windows.DependencyObject root, string text)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is System.Windows.Controls.TextBlock tb && tb.Text == text)
            {
                System.Windows.DependencyObject? node = tb;
                while (node is not null and not System.Windows.Controls.Border)
                    node = System.Windows.LogicalTreeHelper.GetParent(node);
                return (node as System.Windows.Controls.Border)?.Visibility;
            }
            if (HostVisibility(child, text) is { } found) return found;
        }
        return null;
    }
}
