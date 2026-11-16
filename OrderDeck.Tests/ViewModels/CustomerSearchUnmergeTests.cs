using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Ayır onayı (ortak bakiye spec 2026-10-11 §6.1-e, K1; ek sahip kararı 12.10.2026): ayırmadan önce kişinin bakiye
/// dağılımı panel ucundan (<c>members[]</c>) okunur. Bir hesapta bakiye ≠ 0 ise "Grubu ayır" sorulur, [Ayır] [Vazgeç].
/// Tutarlar 0'sa, kişinin gönderilebilir sahibi yoksa ya da istemci verilmemişse bugünkü gibi sorusuz ayrılır; okuma
/// başarısızsa genel uyarı sorulur. Ayırmanın kendisi (<c>CustomerRepository.UnmergeGroup</c>) değişmez.
/// </summary>
public sealed class CustomerSearchUnmergeTests : IDisposable
{
    private const string Baslik = "Grubu ayır";
    private const string GenelUyari =
        "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır.";
    private const string Kapanis =
        ". Başka hesaptan bu kişinin alışverişine kullanılmış tutarlar, o satış geri alınmadıkça geri dönmez.";

    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly BalanceApiStub _stub = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"orderdeck-unmerge-{Guid.NewGuid():N}.json");
    private readonly CustomerSearchViewModel _vm;

    public CustomerSearchUnmergeTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        new SettingsStore(_settingsPath).Save(new AppSettings());
        _vm = Kur(_stub.Client());
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    /// <summary>Arama VM'i aynı veritabanı ve aynı diyalog sahtesiyle; <paramref name="api"/> Ayır onayının istemcisi
    /// (null = DI dışı kurulum, bugünkü sorusuz yol). Ödeme servisi bu testlerde çağrılmaz.</summary>
    private CustomerSearchViewModel Kur(LicenseApiClient? api)
    {
        var store = new SettingsStore(_settingsPath);
        var sessions = new SessionRepository(_db);
        var labels = new LabelRepository(_db);
        var payment = new PaymentRequestService(store, new WhatsAppMessageBuilder(), new FakeUrlLauncher(),
            PaymentRequestServiceTestHelpers.StubApiClient(), new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
            new InMemoryPaymentJobStore(), LocalShipping.Of(store));
        var customerService = new CustomerService(_customers, sessions, labels, Mock.Of<IClock>(c => c.UnixNow() == 1L));
        return new CustomerSearchViewModel(_customers, customerService, sessions, labels, payment, _dialogs, api);
    }

    /// <summary>Kişinin bir satırı; kullanıcı adı ve Id verilmezse üretilir (repo public: literal yok).</summary>
    private Customer Satir(string platform, bool phone = false, string? displayName = null, string? id = null,
        string? username = null)
    {
        var c = new Customer(id ?? Guid.NewGuid().ToString("N"), platform, username ?? $"u{Guid.NewGuid():N}"[..12], displayName,
            null, 100, 100, false, null, null, 0, 0m, null, null, phone ? TestPhone.NewE164() : null);
        _customers.Insert(c);
        return c;
    }

    /// <summary>Satırları tek kişide birleştirir ve listedeki tek kartı döner.</summary>
    private CustomerSearchViewModel.CustomerCard Kart(CustomerSearchViewModel vm, params Customer[] uyeler)
    {
        _customers.MergeIntoGroup(uyeler.Select(u => u.Id).ToList());
        vm.RefreshSearch();
        return vm.Results.Should().ContainSingle().Subject;
    }

    private static Guid G(Customer c) => Guid.ParseExact(c.Id, "N");

    private bool Ayrildi(Customer c) => _customers.GetById(c.Id)!.GroupId is null;

    [Fact] // §6.1-e: bütün tutarlar 0 → bugünkü gibi sorusuz
    public async Task Butun_tutarlar_sifirsa_sorusuz_ayrilir()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 0m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().ContainSingle("dağılım bir kez okunur");
        _dialogs.Confirmations.Should().BeEmpty();
        Ayrildi(ig).Should().BeTrue();
        Ayrildi(tt).Should().BeTrue();
        _vm.Results.Should().HaveCount(2);
    }

    [Fact] // §6.1-e metni birebir: canlılar sunucu sırasıyla, sonra silinmiş hesap; düğme [Ayır]
    public async Task Bakiye_varsa_dagilim_sorulur_metin_birebir()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 10m, true));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik,
            $"Ayırınca her hesap kendi bakiyesiyle kalır: Instagram @{ig.Username}: 70,00 TL · TikTok @{tt.Username}: "
            + "0,00 TL · (silinmiş hesap): 10,00 TL — ayırınca kullanılamaz" + Kapanis));
        _dialogs.ConfirmTexts.Should().Equal(new[] { "Ayır" }, "spec: [Ayır] [Vazgeç]");
    }

    [Fact] // [Vazgeç] → hiçbir şey: grup durur, liste aynı
    public async Task Vazgec_ile_grup_ayrilmaz()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _dialogs.ConfirmResult = _ => false;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle();
        Ayrildi(ig).Should().BeFalse();
        Ayrildi(tt).Should().BeFalse();
        _vm.Results.Should().ContainSingle().Which.Should().BeSameAs(kart, "liste yenilenmedi");
    }

    [Fact] // [Ayır] → bugünkü ayırma + liste yenileme
    public async Task Ayir_ile_grup_ayrilir()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _dialogs.ConfirmResult = _ => true;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle();
        Ayrildi(ig).Should().BeTrue();
        Ayrildi(tt).Should().BeTrue();
        _vm.Results.Should().HaveCount(2);
    }

    [Fact] // §6.1-e "bakiye ≠ 0": eksi bakiye de sorulur
    public async Task Eksi_bakiye_de_sorulur()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), -20m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message
            .Should().Contain($"Instagram @{ig.Username}: -20,00 TL");
        Ayrildi(ig).Should().BeFalse("varsayılan cevap Vazgeç");
    }

    [Fact] // Ek sahip kararı 12.10.2026: silinmiş hesapta para varken bütün canlılar ayrılsa da Ayır SERBEST + uyarı
    public async Task Silinmis_hesapta_para_varken_ayir_serbest()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 0m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 10m, true));
        _dialogs.ConfirmResult = _ => true;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message
            .Should().Contain("(silinmiş hesap): 10,00 TL — ayırınca kullanılamaz");
        Ayrildi(ig).Should().BeTrue("engel yok: onaylanınca ayrılır");
        Ayrildi(tt).Should().BeTrue();
    }

    [Theory] // §6.1-e: çağrı başarısızsa genel uyarı, [Ayır] [Vazgeç]; cevaba göre ayrılır ya da ayrılmaz
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cagri_hatasinda_genel_uyari(bool ayir)
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelFails = true;
        _dialogs.ConfirmResult = _ => ayir;

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik,
            "Bakiye dağılımı alınamadı. Ayırınca her hesap kendi satırındaki bakiyeyle kalır."));
        _dialogs.ConfirmTexts.Should().Equal(new[] { "Ayır" });
        Ayrildi(ig).Should().Be(ayir);
        Ayrildi(tt).Should().Be(ayir);
    }

    [Fact] // members alanı olmayan yanıt (S1 öncesi sunucu) dağılım vermez → çağrı başarısız sayılır
    public async Task Members_alani_yoksa_genel_uyari()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"balance":{"wpfCustomerId":"{{Guid.NewGuid()}}","licenseId":"{{Guid.NewGuid()}}","balance":70,
              "updatedAt":"2030-01-01T00:00:00+00:00"},"transactions":[]}
            """));
        var vm = Kur(new LicenseApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://stub") },
            new LicenseTokenStore()));
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(vm, ig, tt);

        await vm.UnmergeGroupCommand.ExecuteAsync(kart);

        handler.Requests.Should().ContainSingle();
        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik, GenelUyari));
    }

    [Fact] // §6.1-e "GUID'li üye yoksa da öyle (sunucuda bakiye olamaz)": sunucuya gidilmez, sorusuz
    public async Task Gonderilebilir_uyesi_olmayan_kart_sunucuya_sorulmadan_ayrilir()
    {
        var ig = Satir("instagram", id: $"yerel-{Guid.NewGuid():N}");
        var tt = Satir("tiktok", phone: true, id: $"yerel-{Guid.NewGuid():N}");
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((Guid.NewGuid(), 50m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().BeEmpty();
        _dialogs.Confirmations.Should().BeEmpty();
        Ayrildi(ig).Should().BeTrue();
        Ayrildi(tt).Should().BeTrue();
    }

    [Fact] // İskelet fark 17: dağılım kartın birincili ile değil, kişinin sahip satırıyla (Görev 1'in kuralı) okunur
    public async Task Dagilim_kisinin_sahip_satiriyla_sorulur()
    {
        var yerel = Satir("tiktok", phone: true, id: $"yerel-{Guid.NewGuid():N}");
        var ig = Satir("instagram");
        var kart = Kart(_vm, yerel, ig);
        kart.Primary.Id.Should().Be(yerel.Id, "ön koşul: kartın birincili telefonlu satır (BuildCards), GUID'siz");

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().Equal(new[] { G(ig) }, "sahip: kişinin tek GUID'li üyesi; birincil GUID'siz");
    }

    [Fact] // Görev 1 (§6.1-a, M14): GUID'li ama sunucunun kabul etmediği üye sahip olamaz — telefonu olsa da
    public async Task Gonderilemeyen_GUIDli_uye_sahip_olmaz_dagilim_gonderilebilir_uyeyle_sorulur()
    {
        var uzun = Satir("tiktok", phone: true, username: new string('u', ServerCustomerRule.MaxUsernameLength + 1));
        var ig = Satir("instagram");
        var kart = Kart(_vm, uzun, ig);
        kart.Primary.Id.Should().Be(uzun.Id, "ön koşul: kartın birincili telefonlu satır (BuildCards)");

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().Equal(new[] { G(ig) },
            "Username 129 karakter: sunucu satırı kabul etmez, sahip gönderilebilir üyedir");
    }

    [Fact] // Görev 1 + §6.1-e "GUID'li üye yoksa da öyle": yalnız gönderilemeyen GUID'li üyeler → sahip yok, sorusuz
    public async Task Yalniz_gonderilemeyen_GUIDli_uyeler_sunucuya_sorulmadan_ayrilir()
    {
        var uzun = new string('u', ServerCustomerRule.MaxUsernameLength + 1);
        var tt = Satir("tiktok", phone: true, username: uzun);
        var ig = Satir("instagram", username: uzun);
        var kart = Kart(_vm, tt, ig);
        _stub.PanelMembers.Add((G(tt), 50m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _stub.PanelBalanceRequests.Should().BeEmpty();
        _dialogs.Confirmations.Should().BeEmpty();
        Ayrildi(tt).Should().BeTrue();
        Ayrildi(ig).Should().BeTrue();
    }

    [Fact] // Sahip sorusu 1 = (A), 16.11.2026: kartta olmayan canlı sunucu üyesinde bakiye ≠ 0 → liste yerine genel uyarı
    public async Task Kartta_olmayan_canli_uyede_bakiye_varsa_genel_uyari()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((G(tt), 0m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 15m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Should().Be((Baslik, GenelUyari));
    }

    [Fact] // Sahip sorusu 1 = (A), 16.11.2026: kartta olmayan canlı üyenin 0 bakiyesi listelenmez; sunucunun görmediği yerel satır da
    public async Task Kartta_olmayan_canli_uyenin_sifir_bakiyesi_listelenmez()
    {
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(_vm, ig, tt);
        _stub.PanelMembers.Add((G(ig), 70m, false));
        _stub.PanelMembers.Add((Guid.NewGuid(), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Be(
            $"Ayırınca her hesap kendi bakiyesiyle kalır: Instagram @{ig.Username}: 70,00 TL" + Kapanis);
    }

    [Fact] // YouTube'da tanıtıcı görünen ad (Username = channelId) ve zaten "@" ile başlar → "@@" yok
    public async Task YouTube_uyesinde_gorunen_tanitici_cift_at_yok()
    {
        var ad = $"y{Guid.NewGuid():N}"[..12];
        var yt = Satir("youtube", displayName: "@" + ad);
        var ig = Satir("instagram", phone: true);
        var kart = Kart(_vm, yt, ig);
        _stub.PanelMembers.Add((G(yt), 25m, false));
        _stub.PanelMembers.Add((G(ig), 0m, false));

        await _vm.UnmergeGroupCommand.ExecuteAsync(kart);

        var mesaj = _dialogs.Confirmations.Should().ContainSingle().Subject.Message;
        mesaj.Should().Contain($"YouTube @{ad}: 25,00 TL").And.NotContain("@@");
        mesaj.Should().NotContain(yt.Username, "YouTube'da Username channelId'dir, gösterilmez");
    }

    [Fact] // İstemci verilmemiş kurulum (DI dışı) bugünkü gibi: sorusuz, sunucuya gidilmez, komut senkron biter
    public void Istemci_yoksa_bugunku_gibi_sorusuz()
    {
        var vm = Kur(api: null);
        var ig = Satir("instagram");
        var tt = Satir("tiktok", phone: true);
        var kart = Kart(vm, ig, tt);
        // İstemci yok: sunucuya gidilemez — sorusuz yol senkron bitişle ve sorunun yokluğuyla sınanır.

        vm.UnmergeGroupCommand.Execute(kart);

        Ayrildi(ig).Should().BeTrue("Execute döndüğünde ayrılmış: await edilecek iş yok");
        Ayrildi(tt).Should().BeTrue();
        _dialogs.Confirmations.Should().BeEmpty();
    }
}
