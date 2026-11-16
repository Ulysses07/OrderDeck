using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Müşteri kartının bakiye kimliği — spec 2026-10-11 §6.1-f, §8.4-6, §11 risk 4. Açılan satır GUID ("N") ise o satır
/// (bugünkü gibi); değilse kişinin sahip satırı (<c>GetPaymentPerson(id).OwnerId</c>, sahip kuralı: GUID'li VE
/// sunucunun kabul edeceği satır, spec §6.1-a). Sahip yoksa bugünkü "server eşlemesi yok" metni (W-havuz planı,
/// Spec ↔ kod farkı 19). Bakiye okuması ve "Bakiye Ekle" aynı Id'yi kullanır. <c>Load</c> bakiyeyi ateşle-unut çağırır;
/// testler ayrıca <c>await ReloadBalanceAsync()</c> ile belirlenimli bekler, bu yüzden panel isteği iki kez kaydedilebilir.
/// </summary>
public sealed class CustomerDetailOwnerBalanceTests
{
    private const string EslemeYokMetni = "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor.";

    private sealed record Kurulum(InMemorySqlite Db, CustomerRepository Customers, BalanceApiStub Stub,
        CustomerDetailViewModel Vm);

    /// <summary>Yerel veritabanı hatası: <see cref="Bozuk"/> açıkken her açılış fırlatır
    /// (<c>MainShellSyncStatusTests.FailingFactory</c> emsali). Kurulumda kapalı.</summary>
    private sealed class BozukFabrika(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public bool Bozuk { get; set; }
        public IDbConnection Open() => Bozuk ? throw new SqliteException("database is locked", 5) : inner.Open();
    }

    /// <summary><see cref="CustomerDetailViewModelTests"/>'in <c>Build</c> emsali; istemci <see cref="BalanceApiStub"/>
    /// (panel GET kolu istenen Id'leri kaydeder). Çekmece servisi verilmez (Bakiye Ekle çekmecesi açılmaz).</summary>
    private static Kurulum Kur() => Kur(out _);

    /// <summary><see cref="Kur()"/>; müşteri deposu <paramref name="fabrika"/> üzerinden açar (kapalı başlar), böylece
    /// test kurulumdan sonra yalnız müşteri okumalarını bozabilir.</summary>
    private static Kurulum Kur(out BozukFabrika fabrika)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 2_000L);
        fabrika = new BozukFabrika(db);

        var sessions = new SessionRepository(db);
        var customers = new CustomerRepository(fabrika);
        var labels = new LabelRepository(db);
        var customerSvc = new CustomerService(customers, sessions, labels, clock);
        var labelSvc = new LabelService(labels, customerSvc, db, clock);

        var stub = new BalanceApiStub();
        var vm = new CustomerDetailViewModel(customers, labels, labelSvc, new GiveawayRepository(db),
            new StreamSessionService(sessions, clock), stub.Client());
        return new Kurulum(db, customers, stub, vm);
    }

    private static Customer Satir(Kurulum k, string id, string platform, string username, string? phone = null)
    {
        var c = new Customer(id, platform, username, null, null,
            100, 100, false, null, null, 0, 0m, null, null, phone);
        k.Customers.Insert(c);
        return c;
    }

    private static string GuidliId() => Guid.NewGuid().ToString("N");
    private static string YerelId() => $"yerel-{Guid.NewGuid():N}";
    private static string KullaniciAdi() => $"u{Guid.NewGuid():N}"[..12];

    [Fact] // §6.1-f, §8.4-6: GUID'siz satırdan açılan kart kişinin sahip satırını okur; Bakiye Ekle aynı Id
    public async Task Guidsiz_satirdan_acilan_kart_kisinin_sahip_satirinin_bakiyesini_okur()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "tiktok", KullaniciAdi(), TestPhone.NewE164());
        var a = Satir(k, GuidliId(), "instagram", KullaniciAdi(), TestPhone.NewE164());
        k.Customers.MergeIntoGroup(new[] { yerel.Id, a.Id });
        k.Stub.PanelBalance = 125m;

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        var beklenen = Guid.ParseExact(a.Id, "N");
        var istekler = k.Stub.PanelBalanceRequests.ToArray();
        istekler.Should().NotBeEmpty("GUID'siz satırın kişisinde gönderilebilir bir sahip var");
        istekler.Should().OnlyContain(id => id == beklenen, "kart kişinin sahip satırının bakiyesini sorar");
        k.Vm.BalanceLoaded.Should().BeTrue();
        k.Vm.BalanceLabel.Should().Be("125,00 TL");
        k.Vm.BalanceError.Should().BeNull();

        k.Vm.TryGetProjectionId(out var hedef).Should().BeTrue();
        hedef.Should().Be(beklenen, "Bakiye Ekle hedefini aynı yöntemden alır (AddBalanceDrawer.Create)");
    }

    [Fact] // §6.1-f: açılan satır GUID'liyse bugünkü gibi o satır — sahibe düşmez
    public async Task Guidli_satirdan_acilan_kart_bugunku_gibi_o_satiri_okur()
    {
        var k = Kur();
        var a = Satir(k, GuidliId(), "instagram", KullaniciAdi(), TestPhone.NewE164());
        var b = Satir(k, GuidliId(), "tiktok", KullaniciAdi());
        k.Customers.MergeIntoGroup(new[] { a.Id, b.Id });

        k.Vm.Load(b.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        var beklenen = Guid.ParseExact(b.Id, "N");
        var istekler = k.Stub.PanelBalanceRequests.ToArray();
        istekler.Should().NotBeEmpty();
        istekler.Should().OnlyContain(id => id == beklenen,
            "açılan satır GUID'li: telefonlu sahip A olsa da kart B'nin kendi Id'siyle sorar");
        k.Vm.TryGetProjectionId(out var hedef).Should().BeTrue();
        hedef.Should().Be(beklenen);
    }

    [Fact] // §6.1-a/f: sahip kuralı gönderilemeyen GUID'li satırı atlar (kullanıcı adı boş)
    public async Task Sahip_kurali_gonderilemeyen_satiri_atlar()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "tiktok", KullaniciAdi(), TestPhone.NewE164());
        // A telefonlu: eski kuralla sıranın başında ve sahip olurdu; kullanıcı adı boş → sunucuya gönderilemez.
        var a = Satir(k, GuidliId(), "instagram", "", TestPhone.NewE164());
        var b = Satir(k, GuidliId(), "youtube", KullaniciAdi());
        k.Customers.MergeIntoGroup(new[] { yerel.Id, a.Id, b.Id });

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        var beklenen = Guid.ParseExact(b.Id, "N");
        var istekler = k.Stub.PanelBalanceRequests.ToArray();
        istekler.Should().NotBeEmpty();
        istekler.Should().OnlyContain(id => id == beklenen, "sahip GUID'li VE gönderilebilir ilk üyedir (spec §6.1-a, ServerCustomerRule.IsServerAcceptable)");
        k.Vm.TryGetProjectionId(out var hedef).Should().BeTrue();
        hedef.Should().Be(beklenen);
    }

    [Fact] // §6.1-f: kişinin hiç GUID'li üyesi yoksa bugünkü metin, sunucuya istek yok
    public async Task Hic_guidli_uyesi_olmayan_kiside_server_eslemesi_yok_metni()
    {
        var k = Kur();
        var yerel1 = Satir(k, YerelId(), "instagram", KullaniciAdi(), TestPhone.NewE164());
        var yerel2 = Satir(k, YerelId(), "tiktok", KullaniciAdi());
        k.Customers.MergeIntoGroup(new[] { yerel1.Id, yerel2.Id });

        k.Vm.Load(yerel1.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        k.Vm.BalanceError.Should().Be(EslemeYokMetni);
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
        k.Vm.TryGetProjectionId(out _).Should().BeFalse("Bakiye Ekle de aynı kapıdan uyarıya düşer");
    }

    [Fact] // §6.1-f + plan farkı 19: GUID'li üyelerin hiçbiri gönderilemiyorsa da sahip yok → aynı metin, istek yok
    public async Task Guidli_uyeleri_gonderilemeyen_kiside_server_eslemesi_yok_metni()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "tiktok", KullaniciAdi(), TestPhone.NewE164());
        var a = Satir(k, GuidliId(), "instagram", "", TestPhone.NewE164());
        k.Customers.MergeIntoGroup(new[] { yerel.Id, a.Id });

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        k.Vm.BalanceError.Should().Be(EslemeYokMetni, "gönderilemeyen satır sunucuya hiç ulaşmaz; eşlemesi yok");
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
        k.Vm.TryGetProjectionId(out _).Should().BeFalse();
    }

    [Fact] // bugünkü davranış: grupsuz GUID'siz satır → metin
    public async Task Grupsuz_guidsiz_satir_bugunku_gibi_metin()
    {
        var k = Kur();
        var yerel = Satir(k, YerelId(), "instagram", KullaniciAdi(), TestPhone.NewE164());

        k.Vm.Load(yerel.Id).Should().BeTrue();
        await k.Vm.ReloadBalanceAsync();

        k.Vm.BalanceError.Should().Be(EslemeYokMetni);
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
        k.Vm.TryGetProjectionId(out _).Should().BeFalse();
    }

    [Fact] // sahip okuması yerel DB'ye gider: atarsa kart sessizce "0,00 TL"de kalmaz, bugünkü hata metnini gösterir
    public async Task Sahip_okumasi_atarsa_bakiye_yuklenemedi_metni()
    {
        var k = Kur(out var fabrika);
        var yerel = Satir(k, YerelId(), "instagram", KullaniciAdi(), TestPhone.NewE164());

        k.Vm.Load(yerel.Id).Should().BeTrue();   // ateşle-unut bakiye: sahip yok → metin, ağ isteği yok
        fabrika.Bozuk = true;                    // kilitli/meşgul yerel DB

        await k.Vm.ReloadBalanceAsync();         // atmamalı

        k.Vm.BalanceError.Should().StartWith("Bakiye yüklenemedi:",
            "Load bunu ateşle-unut çağırır; istisna Task'ta yutulursa operatör hiçbir hata görmez");
        k.Vm.BalanceLoaded.Should().BeFalse();
        k.Vm.BalanceTransactions.Should().BeEmpty();
        k.Stub.PanelBalanceRequests.Should().BeEmpty();
    }
}
