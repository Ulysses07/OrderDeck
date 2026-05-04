using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Labeling;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Services;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Trial;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace OrderDeck.Tests.App;

/// <summary>
/// Shared scaffolding for <see cref="MainShellViewModel"/> tests. Builds an
/// in-memory SQLite + a started session + a synthetic Active license service
/// + every collaborator the VM constructor needs. Returns the VM along with
/// hooks the test author needs (printer, db, label service for backend
/// assertions).
///
/// Originally lived inside <c>MainShellPrintTests</c> as a private static
/// method; extracted here so backup-flow tests + future suites don't
/// duplicate the wiring.
/// </summary>
internal static class MainShellTestHarness
{
    public sealed class FakeLabelPrinter : ILabelPrinter
    {
        public List<List<Label>> Calls { get; } = new();
        public void Print(IReadOnlyList<Label> labels) => Calls.Add(labels.ToList());
    }

    public sealed record Harness(
        MainShellViewModel Vm,
        FakeLabelPrinter Printer,
        InMemorySqlite Db,
        LabelService Labels,
        CustomerRepository CustomerRepo,
        StreamSessionService Sessions,
        Mock<IClock> Clock);

    public static Harness Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        var sessionRepo  = new SessionRepository(db);
        var customerRepo = new CustomerRepository(db);
        var labelRepo    = new LabelRepository(db);
        var giveawayRepo = new GiveawayRepository(db);

        var customerSvc  = new CustomerService(customerRepo, sessionRepo, labelRepo, clock.Object);
        var sessionSvc   = new StreamSessionService(sessionRepo, clock.Object);
        var labelSvc     = new LabelService(labelRepo, customerSvc, clock.Object);
        var drawer       = new GiveawayDrawer();
        var giveawaySvc  = new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object);

        var bus = new ChatBus(ringBufferSize: 50);
        var printer = new FakeLabelPrinter();
        var banner = new GiveawayBannerViewModel(giveawayRepo, clock.Object);
        var licenseSvc = BuildActiveLicenseService();

        var stubHttp = new HttpClient(new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://localhost/") };
        var stubApi = new LicenseApiClient(stubHttp, new LicenseAuthHandler());
        var tempSettings = new AppSettings();
        var tempStore = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var intakeSync = new IntakeFormSyncService(
            stubApi, customerRepo, tempStore, tempSettings, clock.Object,
            NullLogger<IntakeFormSyncService>.Instance);

        sessionSvc.Start("Test", new[] { "instagram" });

        var vm = new MainShellViewModel(
            bus, labelSvc, sessionSvc, printer, customerSvc, customerRepo, giveawaySvc, banner,
            licenseSvc, intakeSync);

        return new Harness(vm, printer, db, labelSvc, customerRepo, sessionSvc, clock);
    }

    public static ChatMessageViewModel ChatVm(string username, string text,
        string platform = "instagram", bool blacklisted = false)
    {
        var msg = new ChatMessage(
            Guid.NewGuid().ToString("N"), platform, null,
            username, username, null, text, 1000, Array.Empty<string>());
        return new ChatMessageViewModel(msg, isSenderBlacklisted: blacklisted);
    }

    public static void EnqueueLabel(MainShellViewModel vm, string username, decimal price,
        string text = "alıyorum")
    {
        vm.ActivePriceText = price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        vm.AddChatToQueue(ChatVm(username, $"{text} {Guid.NewGuid():N}"));
    }

    /// <summary>Pre-seeded LicenseService → Active status. Same logic as the original
    /// <c>MainShellPrintTests.BuildActiveLicenseService</c>, lifted unchanged.</summary>
    private static LicenseService BuildActiveLicenseService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OrderDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var enc = new EncryptedStore();
        var authStore = new AuthStore(enc, Path.Combine(dir, "auth.dat"));
        var licenseStore = new LicenseStateStore(enc, Path.Combine(dir, "license.dat"));

        authStore.Save(new AuthRecord(
            CustomerId: Guid.NewGuid(),
            Email: "test@test.com",
            Name: "Test User",
            Token: "test-token",
            TokenExpiresAt: DateTimeOffset.UtcNow.AddDays(30)));

        licenseStore.Save(new LicenseRecord(
            LicenseKey: "LDK-TEST",
            SkuCode: "STD",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(365),
            RemainingDaysAtLastCheck: 365,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: DateTimeOffset.UtcNow,
            LastKnownStatus: "Active"));

        const string activeJson = """{"status":"Active","sku":"STD","expiresAt":null,"remainingDays":365}""";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(activeJson, Encoding.UTF8, "application/json")
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseAuthHandler());
        var opts = Options.Create(new LicensingOptions { OfflineGraceDays = 14, TrialDurationDays = 14 });
        var hwId = new StubHardwareIdProvider();
        var trialStorage = new NullTrialStorage();
        var trial = new TrialService(trialStorage, hwId, opts,
            () => DateTimeOffset.UtcNow, NullLogger<TrialService>.Instance);

        var svc = new LicenseService(api, authStore, licenseStore, hwId, opts, trial,
            NullLogger<LicenseService>.Instance);

        svc.InitializeAsync().GetAwaiter().GetResult();
        return svc;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(_responder(request));
    }

    private sealed class StubHardwareIdProvider : IHardwareIdProvider
    {
        public string GetHardwareId() => "test-hw-id";
        public string? GetLegacyHardwareId() => null;
    }

    private sealed class NullTrialStorage : ITrialStorage
    {
        public string Name => "null";
        public TrialRecord? TryRead() => null;
        public void Write(TrialRecord r) { }
        public void Clear() { }
    }
}
