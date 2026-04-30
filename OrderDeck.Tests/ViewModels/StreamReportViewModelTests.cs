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
    ) Setup()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var customers = new CustomerRepository(db);
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var giveaways = new GiveawayRepository(db);
        var launcher = new FakeUrlLauncher();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"livedeck-srvm-{Guid.NewGuid():N}.json");
        var settingsStore = new SettingsStore(settingsPath);
        settingsStore.Save(new AppSettings());
        var paymentService = new PaymentRequestService(settingsStore, new WhatsAppMessageBuilder(), launcher);
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
                100, 100, false, null, null, 0, 0m, null, null, "+905551111111");
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
}
