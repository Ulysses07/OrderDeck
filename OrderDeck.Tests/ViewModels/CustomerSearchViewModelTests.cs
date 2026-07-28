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
        var paymentService = new PaymentRequestService(settingsStore, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider);
        var dialogs = new FakeDialogService();
        var sut = new CustomerSearchViewModel(customers, customerService, sessions, labels, paymentService, dialogs);
        return (db, customers, sessions, labels, launcher, dialogs, settingsPath, sut);
    }

    [Fact]
    public void LastStreamShoppersOnly_True_UsesGetLastStreamShoppersSource()
    {
        var (db, customers, sessions, labels, _, _, path, sut) = Setup();
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

    [Fact]
    public async Task OpenWhatsApp_PhoneRequired_ShowsDialogThenRetries()
    {
        var (db, customers, sessions, labels, launcher, dialogs, path, sut) = Setup();
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
        var (db, customers, sessions, labels, launcher, dialogs, path, sut) = Setup();
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
        var (db, customers, sessions, labels, launcher, dialogs, path, sut) =
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
}
