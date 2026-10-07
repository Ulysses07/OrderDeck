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
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class StreamReportViewModel_OpenWhatsAppTests
{
    private static (
        InMemorySqlite db,
        CustomerRepository customers,
        SessionRepository sessions,
        LabelRepository labels,
        GiveawayRepository giveaways,
        FakeUrlLauncher launcher,
        FakeDialogService dialogs,
        string settingsPath,
        StreamReportViewModel sut
    // cloudApiInProgress: true ise Cloud API açık ve sunucu her gönderime
    // "in_progress" diyor — sonucu bilinmeyen gönderim senaryosu.
    ) Setup(bool cloudApiInProgress = false)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var giveaways = new GiveawayRepository(db);
        var launcher = new FakeUrlLauncher();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"orderdeck-srvm-{Guid.NewGuid():N}.json");
        var settingsStore = new SettingsStore(settingsPath);
        var settings = new AppSettings();
        settings.Payment.UseCloudApi = cloudApiInProgress;
        settingsStore.Save(settings);
        var (api, licenseProvider) = cloudApiInProgress
            ? PaymentRequestServiceTestHelpers.InProgressCloudApiClient()
            : (PaymentRequestServiceTestHelpers.StubApiClient(),
               (OrderDeck.App.Services.Sync.ICurrentLicenseProvider)
                   new PaymentRequestServiceTestHelpers.NullLicenseProvider());
        var paymentService = new PaymentRequestService(settingsStore, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider, new InMemoryPaymentJobStore());
        var dialogs = new FakeDialogService();
        var sut = new StreamReportViewModel(labels, sessions, giveaways, customers, paymentService, dialogs);
        return (db, customers, sessions, labels, giveaways, launcher, dialogs, settingsPath, sut);
    }

    [Fact]
    public async Task OpenWhatsApp_ValidPhone_LaunchesWithPerStreamAmount()
    {
        var (db, customers, sessions, labels, _, launcher, _, settingsPath, sut) = Setup();
        using var _db = db;
        try
        {
            var alice = new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, TestPhone.NewE164());
            customers.Insert(alice);
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 75m, 110, 120));
            sessions.End("s1", 200);

            sut.Load("s1");

            sut.TopCustomers.Should().HaveCount(1);
            var topCustomer = sut.TopCustomers[0];

            await sut.OpenWhatsAppCommand.ExecuteAsync(topCustomer);

            launcher.LaunchedUrls.Should().HaveCount(1);
            // Per-stream amount 75 TL → URL contains "75%2C00"
            launcher.LaunchedUrls[0].Should().Contain("75%2C00");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task OpenWhatsApp_PhoneRequired_OpensDialog()
    {
        var (db, customers, sessions, labels, _, _, dialogs, settingsPath, sut) = Setup();
        using var _db = db;
        try
        {
            customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, null));
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 75m, 110, 120));
            sessions.End("s1", 200);

            sut.Load("s1");

            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.TopCustomers[0]);

            dialogs.PhoneEntryShownFor.Should().ContainSingle().Which.Should().Be("c1");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task OpenWhatsApp_SendPending_WarnsOperatorInsteadOfSilentSuccess()
    {
        // İkinci gönderim yolu, aynı tuzak: in_progress "Sent"e eşlenirse VM
        // hiçbir dal çalıştırmaz — ne wa.me, ne mesaj. Operatör mesajın gittiğini
        // sanar, oysa sunucu sadece "bilmiyorum" demiştir.
        var (db, customers, sessions, labels, _, launcher, dialogs, settingsPath, sut) =
            Setup(cloudApiInProgress: true);
        using var _db = db;
        try
        {
            customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, TestPhone.NewE164()));
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 75m, 110, 120));
            sessions.End("s1", 200);

            sut.Load("s1");

            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.TopCustomers[0]);

            launcher.LaunchedUrls.Should().BeEmpty();
            dialogs.InfosShown.Should().ContainSingle()
                .Which.Should().Contain("doğrulayın");
            dialogs.ErrorsShown.Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    // R4-07: Telefon diyaloğundan SONRAKİ çağrının dönüşü okunmuyordu. Operatör
    // numarayı düzeltiyor, ikinci gönderim belirsiz dönüyor, ekranda hiçbir şey
    // olmuyordu. Bildirim iki çağrı yolunda da aynı yerden geçmeli.
    [Fact]
    public async Task OpenWhatsApp_TelefonKaydedildiktenSonrakiBelirsizSonucDaBildirilir()
    {
        var (db, customers, sessions, labels, _, launcher, dialogs, settingsPath, sut) =
            Setup(cloudApiInProgress: true);
        using var _db = db;
        try
        {
            // Telefonsuz → ilk çağrı PhoneRequired (kontrol en başta).
            customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
                100, 100, false, null, null, 0, 0m, null, null, null));
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice", "Apple", null, 75m, 110, 120));
            sessions.End("s1", 200);

            dialogs.PhoneEntryResult = id => { customers.UpdatePhone(id, TestPhone.NewE164()); return true; };

            sut.Load("s1");

            await sut.OpenWhatsAppCommand.ExecuteAsync(sut.TopCustomers[0]);

            dialogs.PhoneEntryShownFor.Should().ContainSingle().Which.Should().Be("c1");
            // İkinci çağrı SendPending döndü: sessiz kalınamaz.
            dialogs.InfosShown.Should().ContainSingle()
                .Which.Should().Contain("doğrulayın");
            launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    // ── C9 (U12): rapor açıkken senkron kopyayı asıl kayda taşıdı ───────────
    //
    // Rapor satırları yüklenirken Id başına toplandı; taşımadan sonra kopyanın satırı yalnız
    // kendi yazımının payını taşır ama ödeme işi asıl kaydın Id'sinde açılır. Ödeme akışı
    // tutarı kiraladığı GÜNCEL Id için yeniden okur.

    /// <summary>Senkronlu kurulum: ödeme servisi Id'yi çözer ve müşteriyi kiralar; dönen senkron
    /// deposu AYNI kümeyle taşır (DI'daki tekil örnek gibi).</summary>
    private static (StreamReportViewModel Sut, CustomerSyncRepository Sync, InMemoryPaymentJobStore Jobs) Synced(
        InMemorySqlite db, CustomerRepository customers, SessionRepository sessions, LabelRepository labels,
        GiveawayRepository giveaways, string settingsPath, FakeDialogService dialogs)
    {
        var busy = new CustomerBusySet();
        var jobs = new InMemoryPaymentJobStore();
        var (api, license) = PaymentRequestServiceTestHelpers.InProgressCloudApiClient();
        var payment = new PaymentRequestService(new SettingsStore(settingsPath), new WhatsAppMessageBuilder(),
            new FakeUrlLauncher(), api, license, jobs, log: null, customers: customers, busy: busy);
        return (new StreamReportViewModel(labels, sessions, giveaways, customers, payment, dialogs),
                new CustomerSyncRepository(db, busy), jobs);
    }

    [Fact]
    public async Task OpenWhatsApp_rapor_acikken_tasinan_kopyanin_satiri_kisinin_tam_yayin_tutarini_ister()
    {
        var (db, customers, sessions, labels, giveaways, _, dialogs, settingsPath, _) =
            Setup(cloudApiInProgress: true);
        using var _db = db;
        try
        {
            var (sut, sync, jobs) = Synced(db, customers, sessions, labels, giveaways, settingsPath, dialogs);
            // Aynı kişinin iki yazımı (harf farkı): taşımadan önce iki satır.
            var copy = Guid.NewGuid().ToString("N");
            var canonical = Guid.NewGuid().ToString("N");
            customers.Insert(new Customer(copy, "tiktok", "ornek.musteri", "Örnek Müşteri", null,
                100, 100, false, null, null, 0, 0m, null, null, TestPhone.NewE164()));
            customers.Insert(new Customer(canonical, "tiktok", "Ornek.Musteri", "Örnek Müşteri", null,
                100, 101, false, null, null, 0, 0m, null, null, TestPhone.NewE164()));
            sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
            labels.Insert(new Label("l1", "s1", copy, "tiktok", "ornek.musteri", "Elma", null, 100m, 110, 120));
            labels.Insert(new Label("l2", "s1", canonical, "tiktok", "Ornek.Musteri", "Armut", null, 150m, 111, 121));
            sessions.End("s1", 200);

            sut.Load("s1");
            sut.TopCustomers.Should().HaveCount(2);
            var copyRow = sut.TopCustomers.Single(t => t.Username == "ornek.musteri");

            // Rapor açıkken push yanıtı kopyayı asıl kayda taşıdı; ekran yenilenmedi.
            sync.RekeyToLocal(copy, canonical, pushedThroughSeq: long.MaxValue, nowUnix: 1_791_000_000)
                .Should().Be(RekeyResult.Rekeyed);

            await sut.OpenWhatsAppCommand.ExecuteAsync(copyRow);

            var job = jobs.Snapshot.Should().ContainSingle().Subject;
            job.CustomerId.Should().Be(canonical, "kopyanın adı kimlik anahtarıyla asıl kayda bulunur");
            job.ScopeKey.Should().Be("session:s1");
            job.ProductTotal.Should().Be(250m,
                "raporun kopya satırı yalnız kendi 100'ünü biliyordu; kişinin bu yayındaki toplamı 250");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }
}
