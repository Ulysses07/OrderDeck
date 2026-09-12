using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
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
using Moq;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class CustomerSearchViewModelTests
{
    private static (
        InMemorySqlite db,
        CustomerRepository customers,
        SessionRepository sessions,
        LabelRepository labels,
        FakeUrlLauncher launcher,
        FakeDialogService dialogs,
        InMemoryPaymentJobStore jobs,
        string settingsPath,
        CustomerSearchViewModel sut
    // cloudApiInProgress: true ise Cloud API açık ve sunucu her gönderime
    // "in_progress" diyor — sonucu bilinmeyen gönderim senaryosu.
    ) Setup(bool cloudApiInProgress = false)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var customerService = new CustomerService(customers, sessions, labels, clock);
        var launcher = new FakeUrlLauncher();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"orderdeck-csvm-{Guid.NewGuid():N}.json");
        var settingsStore = new SettingsStore(settingsPath);
        var settings = new AppSettings();
        settings.Payment.UseCloudApi = cloudApiInProgress;
        settingsStore.Save(settings);
        var (api, licenseProvider) = cloudApiInProgress
            ? PaymentRequestServiceTestHelpers.InProgressCloudApiClient()
            : (PaymentRequestServiceTestHelpers.StubApiClient(),
               (OrderDeck.App.Services.Sync.ICurrentLicenseProvider)
                   new PaymentRequestServiceTestHelpers.NullLicenseProvider());
        var jobs = new InMemoryPaymentJobStore();
        var paymentService = new PaymentRequestService(settingsStore, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider, jobs);
        var dialogs = new FakeDialogService();
        var sut = new CustomerSearchViewModel(customers, customerService, sessions, labels, paymentService, dialogs);
        return (db, customers, sessions, labels, launcher, dialogs, jobs, settingsPath, sut);
    }

    [Fact]
    public void LastStreamShoppersOnly_True_UsesGetLastStreamShoppersSource()
    {
        var (db, customers, sessions, labels, _, _, _, path, sut) = Setup();
        try
        {
            using var _db = db;
            var alice = new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
            customers.Insert(alice);
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
            sessions.End("s1", 200);

            sut.LastStreamShoppersOnly = true;

            sut.Results.Should().HaveCount(1);
            sut.Results[0].Primary.Username.Should().Be("alice");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // R3-03: Arama önce Search(limit:50) çekip SONRA platform süzgecini
    // uyguluyordu — süzgece uyan kayıt ilk 50 genel eşleşmenin dışındaysa
    // sonuç YANLIŞ olarak boş dönüyordu. Süzgeç limit'ten ÖNCE çalışmalı.
    [Fact]
    public void Search_PlatformFilter_LimitinDisindakiEslesmeyiBulur()
    {
        var (db, customers, _, _, _, _, _, path, sut) = Setup();
        try
        {
            using var _db = db;
            // 50 tiktok kaydı sorguya uyuyor ve hepsi daha yeni (LastSeenAt
            // yüksek) → genel eşleşmenin ilk 50'sini dolduruyorlar.
            for (var i = 0; i < 50; i++)
            {
                customers.Insert(new Customer($"t{i:D2}", "tiktok", $"elma{i:D2}", null, null,
                    1000 + i, 1000 + i, false, null, null, 0, 0m, null, null, null));
            }
            // Aranan kayıt: youtube'da, daha eski → ilk 50'nin dışında.
            customers.Insert(new Customer("y1", "youtube", "elma_gercek", "Elma Gerçek", null,
                10, 10, false, null, null, 0, 0m, null, null, "+905551111111"));

            sut.PlatformFilter = "youtube";
            sut.Query = "elma";

            sut.Results.Should().ContainSingle()
                .Which.Primary.Username.Should().Be("elma_gercek");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Search_RegisteredOnly_LimitinDisindakiKayitliMusteriyiBulur()
    {
        var (db, customers, _, _, _, _, _, path, sut) = Setup();
        try
        {
            using var _db = db;
            // 50 telefonsuz kayıt sorguya uyuyor ve daha yeni.
            for (var i = 0; i < 50; i++)
            {
                customers.Insert(new Customer($"t{i:D2}", "tiktok", $"elma{i:D2}", null, null,
                    1000 + i, 1000 + i, false, null, null, 0, 0m, null, null, null));
            }
            // Tek kayıtlı (telefonlu) müşteri en eski → ilk 50'nin dışında.
            customers.Insert(new Customer("y1", "youtube", "elma_gercek", "Elma Gerçek", null,
                10, 10, false, null, null, 0, 0m, null, null, "+905551111111"));

            sut.RegisteredOnly = true;
            sut.Query = "elma";

            sut.Results.Should().ContainSingle()
                .Which.Primary.Username.Should().Be("elma_gercek");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // R5-02: Limit SATIRI kesiyor, kart ise GroupId'ye göre topluyor. Kesilen
    // üye geri getirilmezse sorun "eski kartın görünmemesi" değil — GÖRÜNEN
    // kartın kendi toplamı eksiliyor. Ödeme komutu bu toplamı tükettiği için
    // eksik toplam doğrudan yanlış tutarlı ödeme isteğine dönüşür.
    [Fact]
    public void Search_GrubunLimitDisindaKalanUyesiKartToplamindaSayilir()
    {
        var (db, customers, _, _, _, _, _, path, sut) = Setup();
        try
        {
            using var _db = db;
            // Aynı kişinin iki satırı: yeni instagram (100) + çok eski tiktok (200).
            customers.Insert(new Customer("g-new", "instagram", "elma_yeni", "Ali Veli", null,
                1000, 900_000, false, null, null, 1, 100m, null, null, null, GroupId: "grp-1"));
            customers.Insert(new Customer("g-old", "tiktok", "elma_eski", "Ali Veli", null,
                1000, 1, false, null, null, 2, 200m, null, null, "+905551112233",
                GroupId: "grp-1"));

            // 60 dolgu: arada kalıp ilk 50'yi doldururlar, eski üye dışarıda kalır.
            for (var i = 0; i < 60; i++)
            {
                customers.Insert(new Customer($"f{i:D2}", "instagram", $"elma_dolgu{i:D2}", null, null,
                    1000, 100_000 + i, false, null, null, 0, 0m, null, null, null));
            }

            sut.Query = "elma";

            var card = sut.Results.Should().ContainSingle(c => c.IsGroup).Subject;
            card.Members.Should().HaveCount(2);
            card.TotalAmount.Should().Be(300m);
            // Birincil üye telefonlu olan: kart iletişimsiz görünmemeli.
            card.Primary.Id.Should().Be("g-old");
            card.Phone.Should().Be("+905551112233");
            card.Platforms.Should().BeEquivalentTo(new[] { "instagram", "tiktok" });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OpenWhatsApp_PhoneRequired_ShowsDialogThenRetries()
    {
        var (db, customers, sessions, labels, launcher, dialogs, _, path, sut) = Setup();
        try
        {
            using var _db = db;
            var alice = new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, null);
            customers.Insert(alice);
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
            sessions.End("s1", 200);

            dialogs.PhoneEntryResult = id => { customers.UpdatePhone(id, "+905551111111"); return true; };

            sut.RefreshSearch();
            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.Results[0]);

            dialogs.PhoneEntryShownFor.Should().ContainSingle().Which.Should().Be("c1");
            launcher.LaunchedUrls.Should().HaveCount(1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OpenWhatsApp_PhoneAlreadyValid_LaunchesDirectly()
    {
        var (db, customers, sessions, labels, launcher, dialogs, _, path, sut) = Setup();
        try
        {
            using var _db = db;
            var alice = new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
            customers.Insert(alice);
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
            sessions.End("s1", 200);

            sut.RefreshSearch();
            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.Results[0]);

            dialogs.PhoneEntryShownFor.Should().BeEmpty();
            launcher.LaunchedUrls.Should().HaveCount(1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OpenWhatsApp_SendPending_WarnsOperatorInsteadOfSilentSuccess()
    {
        // Bu testin yokluğu hatayı hayatta tuttu: servis in_progress'i "Sent"
        // sayıyordu ve VM Sent için HİÇBİR dal çalıştırmıyor — ne wa.me, ne
        // mesaj, ne uyarı. Operatör için sonuç sessiz bir no-op'tu ve mesajın
        // gittiğini varsayıyordu. Sonuç bilinmiyorsa bunu SÖYLEMEK zorundayız.
        var (db, customers, sessions, labels, launcher, dialogs, _, path, sut) =
            Setup(cloudApiInProgress: true);
        try
        {
            using var _db = db;
            customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, "+905551111111"));
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
            sessions.End("s1", 200);

            sut.RefreshSearch();
            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.Results[0]);

            // Gönderim gerçekten uçuşta olabilir → wa.me açılmaz (çift mesaj riski).
            launcher.LaunchedUrls.Should().BeEmpty();
            // Ama operatör bilgilendirilir — hata kutusu değil, doğrulama uyarısı.
            dialogs.InfosShown.Should().ContainSingle()
                .Which.Should().Contain("doğrulayın");
            dialogs.ErrorsShown.Should().BeEmpty();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // R4-07: Telefon diyaloğundan SONRAKİ çağrının sonucu okunmuyordu. Operatör
    // numarayı düzeltiyor, ikinci gönderim belirsiz dönüyor ve ekranda hiçbir
    // şey olmuyordu — "mesaj gitti" sanıp bekliyordu. Bildirim iki yolda da
    // aynı yerden geçmeli ve TAM BİR KEZ görünmeli.
    [Fact]
    public async Task OpenWhatsApp_TelefonKaydedildiktenSonrakiBelirsizSonucDaBildirilir()
    {
        var (db, customers, sessions, labels, launcher, dialogs, _, path, sut) =
            Setup(cloudApiInProgress: true);
        try
        {
            using var _db = db;
            // Telefonsuz müşteri → ilk çağrı PhoneRequired (kontrol en başta).
            customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, null));
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 50m, 110, 120));
            sessions.End("s1", 200);

            dialogs.PhoneEntryResult = id => { customers.UpdatePhone(id, "+905551111111"); return true; };

            sut.RefreshSearch();
            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.Results[0]);

            dialogs.PhoneEntryShownFor.Should().ContainSingle().Which.Should().Be("c1");
            // İkinci çağrı SendPending döndü: sessiz kalınamaz.
            dialogs.InfosShown.Should().ContainSingle()
                .Which.Should().Contain("doğrulayın");
            launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // R4-08: Tutar listeyle birlikte S1'den okunuyor, kapsam ise tıklama anında
    // yeniden okunuyordu. Araya S2 biterse S1'in TUTARI S2 kapsamına yazılıyordu.
    // Kapsam artık sadece etiket değil, finansal işlemin KİMLİĞİ (PaymentJob
    // anahtarı) — tutar ile kimlik aynı anlık görüntüden gelmek zorunda.
    [Fact]
    public async Task OpenWhatsApp_ListeKurulduktanSonraYeniYayinBitse_IslemListedekiYayinaGider()
    {
        var (db, customers, sessions, labels, _, _, jobs, path, sut) =
            Setup(cloudApiInProgress: true);
        try
        {
            using var _db = db;
            // Bakiye/iş akışı yalnız GUID biçimli müşteri kimliğinde çalışır.
            var cid = Guid.NewGuid().ToString("N");
            customers.Insert(new Customer(cid, "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, "+905551111111"));
            sessions.Insert(new StreamSession("s1", "Yayın 1", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", cid, "twitch", "alice", "Apple", null, 50m, 110, 120));
            sessions.End("s1", 200);

            // Liste, S1 son biten yayınken kurulur (yayın-içi tutarlar S1'den).
            sut.LastStreamShoppersOnly = true;
            sut.Results.Should().ContainSingle();

            // Operatör tıklamadan önce S2 biter. Ekran YENİLENMEZ — gördüğü
            // rakam hâlâ S1'in.
            sessions.Insert(new StreamSession("s2", "Yayın 2", 300, null, Array.Empty<string>(), null));
            sessions.End("s2", 400);

            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.Results[0]);

            // İşin kimliği ekranda görünen yayına bağlı olmalı.
            var job = jobs.Snapshot.Should().ContainSingle().Subject;
            job.ScopeKey.Should().Be("session:s1");
            job.ProductTotal.Should().Be(50m);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
