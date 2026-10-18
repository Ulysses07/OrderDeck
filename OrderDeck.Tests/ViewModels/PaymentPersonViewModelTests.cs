using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// "Ödeme iste" kişi düzeyinde (tasarım 1, 2026-10-10): müşteri kartı ve yayın raporu AYNI ödeme
/// servisini (uygulamadaki tekil örnek) ve aynı veritabanını kullanır. Kişinin hangi satırından,
/// hangi ekrandan girilirse girilsin aynı iş, aynı toplam, aynı kargo kararı.
/// </summary>
public sealed class PaymentPersonViewModelTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly PaymentJobRepository _jobs;
    private readonly BalanceApiStub _api = new() { PreviewBalance = 100m };
    private readonly FakeUrlLauncher _launcher = new();
    private readonly FakeDialogService _dialogs = new() { ConfirmResult = _ => true };
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"orderdeck-ppvm-{Guid.NewGuid():N}.json");
    private readonly StreamReportViewModel _report;
    private readonly CustomerSearchViewModel _search;

    public PaymentPersonViewModelTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        _jobs = new PaymentJobRepository(_db);
        var store = new SettingsStore(_settingsPath);
        store.Save(new AppSettings());
        var payment = new PaymentRequestService(store, new WhatsAppMessageBuilder(), _launcher,
            _api.Client(), _api.License(), _jobs, LocalShipping.Of(store), log: null, customers: _customers,
            busy: new CustomerBusySet());
        _report = new StreamReportViewModel(_labels, _sessions, new GiveawayRepository(_db), _customers, payment, _dialogs);
        var customerService = new CustomerService(_customers, _sessions, _labels, Mock.Of<IClock>(c => c.UnixNow() == 1L));
        _search = new CustomerSearchViewModel(_customers, customerService, _sessions, _labels, payment, _dialogs);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    private void Ayarla(Action<AppSettings> configure)
    {
        var settings = new AppSettings();
        configure(settings);
        new SettingsStore(_settingsPath).Save(settings);
    }

    /// <summary>Aynı kişinin iki satırı (Instagram telefonsuz, TikTok telefonlu — kartın birincili ve
    /// işin sahibi TikTok). Yayında: Instagram 100, TikTok 200.</summary>
    private (string Ig, string Tt) IkiSatirliKisi(bool igAliciOdemeli = false)
    {
        var ig = Guid.NewGuid().ToString("N");
        var tt = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(ig, "instagram", "ornek_ig", "Örnek", null,
            100, 101, false, null, null, 1, 100m, null, null, null, RecipientPaysActive: igAliciOdemeli));
        _customers.Insert(new Customer(tt, "tiktok", "ornek_tt", "Örnek", null,
            100, 100, false, null, null, 1, 200m, null, null, TestPhone.NewE164()));
        _customers.MergeIntoGroup(new[] { ig, tt });
        _sessions.Insert(new StreamSession("s1", "Yayın 1", 100, null, Array.Empty<string>(), null));
        _labels.Insert(new Label("l1", "s1", ig, "instagram", "ornek_ig", "Elma", null, 100m, 110, 120));
        _labels.Insert(new Label("l2", "s1", tt, "tiktok", "ornek_tt", "Armut", null, 200m, 111, 121));
        _sessions.End("s1", 200);
        return (ig, tt);
    }

    private PaymentJob[] KapsamIsleri(string scopeKey)
    {
        using var conn = _db.Open();
        return Dapper.SqlMapper.Query<string>(conn, "SELECT Id FROM PaymentJob WHERE ScopeKey = @scopeKey",
                new { scopeKey })
            .Select(id => _jobs.Get(id)!).ToArray();
    }

    /// <param name="yayin">true: "son yayında alanlar" listesi (yayın kapsamı); false: genel liste
    /// (kümülatif kapsam, son görülmeye göre sıralı).</param>
    private Task KartaTikla(bool yayin = true)
    {
        _search.LastStreamShoppersOnly = yayin;
        _search.RefreshSearch();
        return _search.OpenWhatsAppCommand.ExecuteAsync(_search.Results.Should().ContainSingle().Subject);
    }

    // Repro B: rapor satır başına, kart kişi başına toplam istiyordu — aynı kişi için iki giriş aynı işi
    // her tıklamada revize ediyordu (geri al + yeniden uygula, her seferinde farklı tutarlı mesaj).
    [Fact]
    public async Task Rapor_satirlari_ve_kart_ayni_kisiye_ayni_isi_ayni_toplamla_ister()
    {
        var (ig, tt) = IkiSatirliKisi();
        _report.Load("s1");
        _report.TopCustomers.Should().HaveCount(2);
        _report.TopCustomers.Should().OnlyContain(t => t.PersonTotalAmount == 300m,
            "satır kendi tutarının yanında kişinin yayın toplamını gösterir (sahip kararı 3)");

        await _report.OpenWhatsAppCommand.ExecuteAsync(_report.TopCustomers.Single(t => t.CustomerId == ig));
        await _report.OpenWhatsAppCommand.ExecuteAsync(_report.TopCustomers.Single(t => t.CustomerId == tt));
        await KartaTikla();

        var job = KapsamIsleri("session:s1").Should().ContainSingle("kişi × kapsam tek iş").Subject;
        job.CustomerId.Should().Be(tt, "sahip: telefonlu üye");
        job.ProductTotal.Should().Be(300m, "rapor da kart da kişinin yayın toplamını ister");
        _api.Applies.Should().ContainSingle("ikinci ve üçüncü tıklama tekrar paylaşım — yeni düşüm yok");
        _api.ReverseCalls.Should().BeEmpty("toplam girişler arasında değişmedi — revizyon yok");
        _launcher.LaunchedUrls.Should().HaveCount(3).And.OnlyContain(u => u.Contains("200%2C00"),
            "üç mesaj da aynı tutar: 300 − 100 bakiye");
        _dialogs.Confirmations.Should().HaveCount(2).And.OnlyContain(c => c.Message.Contains("tekrar gönderilsin mi?"),
            "teslim edilmiş istek her tekrarda önce sorulur (sahip kararı 2)");
    }

    // Karşıt yargıç: alıcı ödemeli bayrağı kiralanan satırdan okunuyordu — kart (TikTok, bayrak
    // kapalı) ile rapor (Instagram, bayrak açık) farklı toplam istiyor, aynı iş her girişte revize
    // ediliyordu. Sahip kararı 1: üyelerden HERHANGİ birinde açıksa kişi alıcı ödemelidir.
    [Fact]
    public async Task Alici_odemeli_herhangi_bir_uyede_aciksa_kart_ve_rapor_ayni_tutari_ister()
    {
        Ayarla(s =>
        {
            s.Shipping.FreeShippingThreshold = 10_000m;
            s.Shipping.ShippingFee = 50m;
        });
        var (ig, _) = IkiSatirliKisi(igAliciOdemeli: true);
        _report.Load("s1");

        await KartaTikla();
        await _report.OpenWhatsAppCommand.ExecuteAsync(_report.TopCustomers.Single(t => t.CustomerId == ig));
        await KartaTikla();

        _api.ReverseCalls.Should().BeEmpty("girişler arasında tutar değişmedi");
        KapsamIsleri("session:s1").Should().ContainSingle().Which.ProductTotal.Should().Be(300m,
            "alıcı ödemeli: kargo ücreti eklenmez");
        _launcher.LaunchedUrls.Should().HaveCount(3).And.OnlyContain(u => u.Contains("200%2C00"));
    }

    // Repro A (kart): telefon sonradan eklenince kartın birincili değişir; aynı kişi için aynı iş bulunur.
    [Fact]
    public async Task Telefon_eklenip_birincil_degisince_ikinci_tiklama_ayni_isi_bulur()
    {
        var (ig, tt) = IkiSatirliKisi();
        await KartaTikla(yayin: false);
        _search.Results.Single().Primary.Id.Should().Be(tt, "ön koşul: birincil telefonlu TikTok satırı");

        // Genel liste son görülmeye göre sıralı (Instagram daha yeni): telefon eklenince birincil o olur.
        _customers.UpdatePhone(ig, TestPhone.NewE164());
        _search.RefreshSearch();
        _search.Results.Single().Primary.Id.Should().Be(ig, "ön koşul: telefon eklenince birincil Instagram");
        await _search.OpenWhatsAppCommand.ExecuteAsync(_search.Results.Single());

        var job = KapsamIsleri("cumulative").Should().ContainSingle("birincil kayması ikinci iş açmaz").Subject;
        job.CustomerId.Should().Be(tt, "işin sahibi bir kez seçilir; yeni telefon yalnız YENİ işin sahibini etkiler");
        _api.Applies.Should().ContainSingle();
        _dialogs.Confirmations.Should().ContainSingle("ikinci tıklama tekrar paylaşım — önce sorulur");
        _launcher.LaunchedUrls.Should().HaveCount(2).And.OnlyContain(u => u.Contains("200%2C00"),
            "iki mesaj aynı tutar: 300 − 100");
    }

    // Ayırma (UnmergeGroup): iş sahip satırında kalır. Sahibin kişisi kendi toplamına revize edilir;
    // öbür satır artık başka kişidir ve kendi işini açar.
    [Fact]
    public async Task Ayirmadan_sonra_sahip_kendi_toplamina_revize_edilir_obur_satir_kendi_isini_acar()
    {
        var (ig, tt) = IkiSatirliKisi();
        await KartaTikla(yayin: false);
        var ilk = KapsamIsleri("cumulative").Should().ContainSingle().Subject;
        ilk.ProductTotal.Should().Be(300m, "kümülatif: 100 + 200");
        ilk.CustomerId.Should().Be(tt);

        _customers.UpdatePhone(ig, TestPhone.NewE164());   // ayrılınca kendi mesajı gidebilsin
        _search.UnmergeGroupCommand.Execute(_search.Results.Single());
        _search.Results.Should().HaveCount(2);
        await _search.OpenWhatsAppCommand.ExecuteAsync(_search.Results.Single(c => c.Primary.Id == ig));
        await _search.OpenWhatsAppCommand.ExecuteAsync(_search.Results.Single(c => c.Primary.Id == tt));

        var isler = KapsamIsleri("cumulative");
        isler.Should().HaveCount(2);
        isler.Single(j => j.CustomerId == ig).ProductTotal.Should().Be(100m, "ayrılan satır yeni kişi: kendi işi");
        var sahip = isler.Single(j => j.CustomerId == tt);
        sahip.Id.Should().Be(ilk.Id, "sahibin işi aynı iş");
        sahip.ProductTotal.Should().Be(200m, "kendi toplamına revize edildi");
        _api.ReverseCalls.Should().ContainSingle("revizyon eski düşümü geri alır").Which.Should().Be(ilk.ApplyKey!.Value);
    }
}
