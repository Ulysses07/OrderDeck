using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
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
using OrderDeck.Shared.Text;   // SearchNormalizer
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace OrderDeck.Tests.App;

public class MainShellPrintTests
{
    private sealed class FakeLabelPrinter : ILabelPrinter
    {
        public List<List<Label>> Calls { get; } = new();
        public List<IReadOnlySet<string>?> RecipientPaysCalls { get; } = new();
        public List<List<Label>> GiftCalls { get; } = new();
        public void Print(IReadOnlyList<Label> labels, IReadOnlySet<string>? recipientPaysLabelIds = null)
        {
            Calls.Add(labels.ToList());
            RecipientPaysCalls.Add(recipientPaysLabelIds);
        }
        public void PrintGiftLabels(IReadOnlyList<Label> labels) => GiftCalls.Add(labels.ToList());
    }

    /// <summary>
    /// Builds a LicenseService pre-seeded to Active status (no network needed).
    /// The service is NOT initialized via InitializeAsync — instead we seed the store
    /// and use a fake HTTP handler that returns an Active validate response, then
    /// call RefreshAsync so CurrentStatus becomes Active before the VM is created.
    /// </summary>
    private static LicenseService BuildActiveLicenseService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OrderDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var enc = new EncryptedStore();
        var authStore = new AuthStore(enc, Path.Combine(dir, "auth.dat"));
        var licenseStore = new LicenseStateStore(enc, Path.Combine(dir, "license.dat"));

        // Seed valid auth
        authStore.Save(new AuthRecord(
            CustomerId: Guid.NewGuid(),
            Email: "test@test.com",
            Name: "Test User",
            Token: "test-token",
            TokenExpiresAt: DateTimeOffset.UtcNow.AddDays(30)));

        // Seed valid license state so RefreshAsync has a key to validate
        licenseStore.Save(new LicenseRecord(
            LicenseKey: "LDK-TEST",
            SkuCode: "STD",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(365),
            RemainingDaysAtLastCheck: 365,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: DateTimeOffset.UtcNow,
            LastKnownStatus: "Active"));

        // Fake HTTP handler that returns an Active validate response
        const string activeJson = """{"status":"Active","sku":"STD","expiresAt":null,"remainingDays":365}""";
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(activeJson, Encoding.UTF8, "application/json")
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var opts = Options.Create(new LicensingOptions { OfflineGraceDays = 14, TrialDurationDays = 14 });
        var hwId = new StubHardwareIdProvider();
        var trialStorage = new NullTrialStorage();
        var trial = new TrialService(trialStorage, hwId, opts, () => DateTimeOffset.UtcNow, NullLogger<TrialService>.Instance);

        var svc = new LicenseService(api, authStore, licenseStore, hwId, opts, trial,
            NullLogger<LicenseService>.Instance);

        // Initialize synchronously — loads stored auth+license, then calls RefreshAsync
        svc.InitializeAsync().GetAwaiter().GetResult();

        return svc;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken ct)
            => Task.FromResult(_responder(request));
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

    private static (MainShellViewModel Vm, FakeLabelPrinter Printer, InMemorySqlite Db) Fx()
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

        // Stub IntakeFormSyncService — tests don't exercise sync; construct with no-op HTTP
        var stubHttp = new HttpClient(new FakeHttpMessageHandler(
            _ => new System.Net.Http.HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://localhost/") };
        var stubApi = new LicenseApiClient(stubHttp, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var tempSettings = new AppSettings();
        var tempStore = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var intakeSync = new IntakeFormSyncService(
            stubApi, customerRepo, tempStore, tempSettings, clock.Object,
            NullLogger<IntakeFormSyncService>.Instance);

        // Start a session so AddChatToQueue works
        sessionSvc.Start("Test", new[] { "instagram" });

        var catalogRepo = new CatalogReplicaRepository(db);
        var stockProvider = new StockBalanceProvider(
            new StockBalanceRepository(db), labelRepo);
        var productCard = new ProductCardViewModel(
            new BroadcastCodeResolver(catalogRepo),
            new CatalogPhotoCache(Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))),
            stockProvider);

        var vm = new MainShellViewModel(
            bus, labelSvc, sessionSvc, printer, customerSvc, customerRepo,
            labelRepo, clock.Object, productCard,
            giveawaySvc, banner, licenseSvc, intakeSync, tempStore,
            new FakeDialogService());

        return (vm, printer, db);
    }

    private static ChatMessageViewModel ChatVm(string username, string text)
    {
        var msg = new ChatMessage(
            Guid.NewGuid().ToString("N"), "instagram", null,
            username, username, null, text, 1000, Array.Empty<string>());
        return new ChatMessageViewModel(msg, isSenderBlacklisted: false);
    }

    private static void Enqueue(MainShellViewModel vm, string username, decimal price)
    {
        vm.ActivePriceText = price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        // AddChatToQueueAsync'i senkron köprülemek burada GÜVENLİ: bu yardımcı
        // çekmece servisi VERİLMEMİŞ Fx() kurulumunda kullanılıyor, o yolda hiç
        // await edilen bir şey yok — metot baştan sona senkron koşup biter.
        vm.AddChatToQueueAsync(ChatVm(username, $"alıyorum {Guid.NewGuid():N}"))
          .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Print_with_no_selection_prints_all_and_empties_queue()
    {
        var (vm, printer, db) = Fx();
        using var _ = db;

        Enqueue(vm, "@a", 100);
        Enqueue(vm, "@b", 200);
        Enqueue(vm, "@c", 300);

        // PrintCommand artık async (UI freeze fix 2026-05-13) — ExecuteAsync await edilir.
        await vm.PrintCommand.ExecuteAsync(null);

        printer.Calls.Should().HaveCount(1);
        printer.Calls[0].Should().HaveCount(3);
        vm.PrintQueue.Should().BeEmpty();
        vm.SelectedQueueItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Print_with_partial_selection_prints_selected_only_and_keeps_remainder()
    {
        var (vm, printer, db) = Fx();
        using var _ = db;

        Enqueue(vm, "@a", 100);
        Enqueue(vm, "@b", 200);
        Enqueue(vm, "@c", 300);

        // Select 2 of 3
        vm.SelectedQueueItems.Add(vm.PrintQueue[0]);
        vm.SelectedQueueItems.Add(vm.PrintQueue[2]);

        await vm.PrintCommand.ExecuteAsync(null);

        printer.Calls.Should().HaveCount(1);
        printer.Calls[0].Should().HaveCount(2);
        vm.PrintQueue.Should().HaveCount(1);
        vm.PrintQueue[0].Username.Should().Be("@b");   // unselected, retained
        vm.SelectedQueueItems.Should().BeEmpty();
    }

    [Fact]
    public void RemoveSelectedFromQueue_with_multi_selection_deletes_all_selected()
    {
        var (vm, _, db) = Fx();
        using var _2 = db;

        Enqueue(vm, "@a", 100);
        Enqueue(vm, "@b", 200);
        Enqueue(vm, "@c", 300);

        vm.SelectedQueueItems.Add(vm.PrintQueue[0]);
        vm.SelectedQueueItems.Add(vm.PrintQueue[1]);

        vm.RemoveSelectedFromQueueCommand.Execute(null);

        vm.PrintQueue.Should().HaveCount(1);
        vm.PrintQueue[0].Username.Should().Be("@c");
        vm.SelectedQueueItems.Should().BeEmpty();
    }

    /// <summary>
    /// Ürünün YALNIZ satıcı ekseni var (Renk, rol 1) — izleyici ekseni yok.
    /// Bu bilerek: izleyici ekseni olsaydı <c>AddChatToQueueAsync</c> varyant
    /// seçici çekmecesini açmaya çalışır, harness ise <c>drawers: null</c> ile
    /// kuruluyor ve akış hiç sipariş yazmadan sessizce dönerdi.
    /// Satıcı ekseni tekil kaldığı için <c>ResolveVariantId(null)</c> "v1"
    /// veriyor, yani düşüş VARYANT kovasında görünüyor.
    /// </summary>
    private static void SeedProductWithBalance(
        MainShellTestHarness.Harness h, string code, int quantity)
    {
        new CatalogReplicaRepository(h.Db).Replace(
            [new CatalogProduct("p1", null, "SK00001",
                                SearchNormalizer.Normalize("SK00001"), "Kolye",
                                89.90m, null, "Renk", 1, null, null, null,
                                1_700_000_000)],
            [new CatalogVariant("v1", "p1", "Kırmızı", null, null, true, 0)],
            [],
            [new CatalogBroadcastCode("p1", "Kırmızı", code,
                                      SearchNormalizer.Normalize(code),
                                      1_700_000_000, 0)]);

        new StockBalanceRepository(h.Db).ApplyPage(
            [new CatalogStockBalance("p1", "v1", quantity)],
            new StockCursor(DateTimeOffset.UnixEpoch, Guid.Empty));
    }

    [Fact]
    public void Writing_an_order_immediately_drops_the_shown_balance()
    {
        var h = MainShellTestHarness.Build();
        SeedProductWithBalance(h, code: "Buz", quantity: 5);

        // ActiveCode ataması ProductCard.Load'u tetikliyor (OnActiveCodeChanged).
        h.Vm.ActiveCode = "Buz";
        h.Vm.ProductCard.Variants.Single().Quantity.Should().Be(5);

        MainShellTestHarness.EnqueueLabel(h.Vm, "@ali", 100m);

        // Senkron turu BEKLENMEDEN düşmeli: operatör aynı kodu arka arkaya
        // satarken ekrandaki sayının gerçeği göstermesi gerekiyor.
        h.Vm.ProductCard.Variants.Single().Quantity.Should().Be(4);
    }
}
