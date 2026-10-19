using System.Text.Json;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.App;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Dekontun kargo adımı ve eşik kararı yönlendiriciden (W2-08; spec 2026-10-13 §5.3 "Dekont kargo adımı" ve "Eşik
/// çekmecesi kararı", §5.8 "Dekont kaydı", §7.1–§7.3). 2026-10-19'dan beri adım "Gelen Ödemeler" çekmecesinden yürür ve
/// hiçbir <c>Payment</c> satırı yazılmaz. Mod On'da dosya sunucunun: <c>attach-shipment</c> (beklenen
/// küme yerel bağlanmamış etiketler; <c>stale</c>'de sunucunun kümesiyle bir kez daha) ve <c>shipment-decision</c>
/// (beklenen sürüm aynanın <c>ServerVersion</c>'ı); yanıt kilit altında aynaya iner, yerel <c>Shipment</c>'a hiç
/// yazılmaz. Mod Off'ta bugünkü yerel sıra, ağsız ve eşzamanlı. Sunucu <see cref="FakeSalesServer"/>, çekmeceler
/// gerçek WPF içerikleriyle kurulur (STA) ve operatörün seçimi ViewModel'lerine yazılır.
/// </summary>
public sealed class DekontShipmentCommandTests : IDisposable
{
    /// <summary>Çekmeceler: içerik STA'da kurulur, ViewModel'e operatörün seçimi yazılır.</summary>
    private sealed class Drawers : IDrawerService
    {
        public List<string> Titles { get; } = new();
        public ShipmentDirective? Directive { get; set; }
        public ShipmentDecision? Decision { get; set; }

        /// <summary>Verilirse eşik çekmecesinin sırayla açılışlarının kararları (sonrası null); yoksa hep
        /// <see cref="Decision"/>.</summary>
        public Queue<ShipmentDecision?>? Decisions { get; set; }
        public Action? BeforeDecision { get; set; }
        public ShipmentThresholdDialogViewModel? Threshold { get; private set; }
        public List<ShipmentThresholdDialogViewModel> Thresholds { get; } = new();

        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            Titles.Add(title);
            object? context = null;
            var error = ThemeTestHost.RunOnSta(() => context = ((FrameworkElement)buildContent(new Drawer(title))).DataContext);
            if (error is not null) throw new InvalidOperationException(error);
            switch (context)
            {
                case ShipmentDirectiveDialogViewModel directive:
                    directive.ChosenDirective = Directive;
                    break;
                case ShipmentThresholdDialogViewModel threshold:
                    Threshold = threshold;
                    Thresholds.Add(threshold);
                    BeforeDecision?.Invoke();
                    threshold.ChosenDecision = Decisions is null ? Decision
                        : Decisions.Count > 0 ? Decisions.Dequeue() : null;
                    break;
            }
            return Task.FromResult(true);
        }

        public bool CloseTop() => false;
    }

    private readonly SalesEditTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private FakeSalesServer Server => _bed.Server;

    private static string N(Guid id) => id.ToString("N");

    private DekontEkleViewModel Vm(string amount, IDrawerService? drawers = null,
        Microsoft.Extensions.Logging.ILogger<DekontEkleViewModel>? log = null,
        OrderDeck.App.Services.PaymentRequestService? paymentRequest = null, bool routed = true) =>
        new(_bed.Customers, _bed.Sessions,
            new PaymentMatcherService(_bed.LabelRepo, _bed.Shipping), _bed.Shipments, _bed.Shipping,
            log ?? NullLogger<DekontEkleViewModel>.Instance, paymentRequest, drawers, routed ? _bed.Router : null)
        {
            AmountText = amount, CustomerPlatform = "tiktok", CustomerUsername = "alici_ornek",
        };

    /// <summary>Gelen Ödemeler çekmecesi (2026-10-19) hiçbir <c>Payment</c> satırı yazmaz; kargo adımı yine çalışır.</summary>
    private int PaymentRows() => _bed.Scalar<int>("SELECT COUNT(*) FROM Payment");

    /// <summary>Yerel ayar ve lisans geneli ayar aynı değerde: mod Off yereli, mod On lisans genelini okur (spec
    /// 2026-10-13 §6.2). Kaynağın ayrımını <see cref="Mod_On_esik_ve_cekmecenin_esik_metni_lisans_geneli_ayardan"/>
    /// sınar.</summary>
    private void Threshold(decimal threshold, decimal fee)
    {
        _bed.Settings.Shipping.FreeShippingThreshold = threshold;
        _bed.Settings.Shipping.ShippingFee = fee;
        _bed.LicenseShipping(threshold, fee);
    }

    private static string PayloadString(FakeSalesServer.PushCall call, string name) =>
        call.Command.Payload!.Value.GetProperty(name).ToString();

    private int ShipmentRows() => _bed.Scalar<int>("SELECT COUNT(*) FROM Shipment");

    // ── mod On: attach-shipment ────────────────────────────────────────────

    [Fact]
    public async Task Mod_On_dekont_attach_shipment_gonderir_beklenen_kume_yerel_baglanmamis_etiketler_yanit_aynaya_iner_Odendi()
    {
        var (session, customer, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var before = _bed.Local(order);
        var vm = Vm("150");

        (await vm.SaveAsync()).Should().BeTrue();

        Server.Faults.Should().BeEmpty();
        var call = Server.CommandCalls.Should().ContainSingle().Subject;
        call.Command.Kind.Should().Be("attach-shipment");
        PayloadString(call, "customerId").Should().Be(N(customer), "yerel asıl Id");
        call.Command.Expected!.Select(e => (e!.OrderId, e.Version)).Should().Equal([(order, 1)],
            "beklenen küme: yerel bağlanmamış etiketler ve sunucu sürümleri");
        var proposed = Guid.Parse(PayloadString(call, "proposedShipmentId"));

        var label = _bed.LabelRepo.GetById(N(order))!;
        label.ShipmentId.Should().Be(N(proposed), "yanıtın satırı kilit altında yazıldı (ABORT yok)");
        var after = _bed.Local(order);
        after.SyncedAt.Should().Be(before.SyncedAt, "akış öğesi gibi: gönderim kuyruğuna dokunulmaz (DM8)");
        after.Revision.Should().Be(before.Revision);
        var mirror = _bed.ShipmentRepo.GetById(N(proposed))!;
        mirror.Status.Should().Be(ShipmentStatus.Pending);
        mirror.CumulativeAmount.Should().Be(150m);
        mirror.ServerVersion.Should().Be(Server.Shipment(proposed)!.Version);
        mirror.SyncedAt.Should().NotBeNull("ayna onaylı: shipments/sync'e girmez");
        vm.ShipmentWarning.Should().BeNull();
        PaymentRows().Should().Be(0, "Gelen Ödemeler Payment yazmaz");

        var printed = new PrintedLabelsViewModel(_bed.LabelRepo);
        printed.Load(N(session), withPaymentStatus: false);
        printed.Rows.Should().ContainSingle(r => r.Id == N(order))
            .Which.Status.Should().Be(PrintedLabelStatus.Paid, "Basılanlar: dosyaya bağlı etiket ödendi");
    }

    [Fact]
    public async Task Bayat_aday_kumesinde_sunucunun_kumesiyle_tam_bir_kez_yeniden_denenir()
    {
        var (_, customer, order) = await _bed.RemoteLabelAsync(150m);
        // Bu bilgisayarın canlı yayınında aynı kişinin henüz sunucuya gitmemiş etiketi: yerelde bağlanmamış,
        // sunucunun aday kümesinde yok.
        var live = _bed.LocalSession(ended: false);
        var unsent = _bed.LocalLabel(live, N(customer), 90m);
        _bed.ModeOn();
        var vm = Vm("150");

        (await vm.SaveAsync()).Should().BeTrue();

        var calls = Server.CommandCalls;
        calls.Should().HaveCount(2, "stale'den sonra tam bir kez");
        calls[0].Command.Expected!.Select(e => e!.OrderId).Should().BeEquivalentTo([order, Guid.Parse(unsent)]);
        calls[1].Command.Expected!.Select(e => (e!.OrderId, e.Version)).Should().Equal([(order, 1)],
            "yeniden deneme sunucunun aday kümesiyle");
        calls[1].Command.CommandId.Should().Be(calls[0].Command.CommandId, "aynı niyet");
        PayloadString(calls[1], "proposedShipmentId").Should().Be(PayloadString(calls[0], "proposedShipmentId"),
            "önerilen dosya Id'si yeniden denemede aynı");
        _bed.LabelRepo.GetById(N(order))!.ShipmentId.Should().Be(
            Guid.Parse(PayloadString(calls[0], "proposedShipmentId")).ToString("N"));
        _bed.LabelRepo.GetById(unsent)!.ShipmentId.Should().BeNull("bir sonraki dekont onu alır");
        vm.ShipmentWarning.Should().BeNull();
    }

    [Fact]
    public async Task Ikinci_stale_adimi_uyariyla_atlar_ucuncu_istek_yok()
    {
        var (_, customer, order) = await _bed.RemoteLabelAsync(150m);
        var live = _bed.LocalSession(ended: false);
        _bed.LocalLabel(live, N(customer), 90m);
        _bed.ModeOn();
        var commands = 0;
        Server.DuringPush = endpoint =>
        {
            // Yeniden denemeden hemen önce başka bilgisayar aynı etiketi değiştirir: küme yine bayat.
            if (endpoint == FakeSalesServer.SalesCommands && ++commands == 2)
                Server.MutateOrder(order, o => o with { PrintBatchId = "b-baska" });
        };
        var vm = Vm("150");

        (await vm.SaveAsync()).Should().BeTrue("uyarı şeritte, çekmece kapanır");

        Server.CommandCalls.Should().HaveCount(2);
        vm.ShipmentWarning.Should().Be(SalesCommandMessages.DekontShipmentFailed(SalesCommandMessages.Stale));
        _bed.LabelRepo.GetById(N(order))!.ShipmentId.Should().BeNull();
        ShipmentRows().Should().Be(0);
        PaymentRows().Should().Be(0);
    }

    // ── mod On: yönerge ve eşik kararı ─────────────────────────────────────

    [Fact]
    public async Task Esige_ulasan_havuz_cekmeceyi_acar_karar_shipment_decision_beklenen_surumle_gider()
    {
        Threshold(100m, 50m);
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        long? mirrorVersionAtDecision = null;
        int? serverVersionAtDecision = null;
        var drawers = new Drawers { Decision = ShipmentDecision.ShipNow };
        Server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.SalesCommands || Server.CommandCalls.Count != 2) return;
            var shipment = _bed.LabelRepo.GetById(N(order))!.ShipmentId!;
            mirrorVersionAtDecision = _bed.ShipmentRepo.GetById(shipment)!.ServerVersion;
            serverVersionAtDecision = Server.Shipment(Guid.Parse(shipment))!.Version;
        };
        var vm = Vm("150", drawers);

        (await vm.SaveAsync()).Should().BeTrue();

        drawers.Titles.Should().Equal("Ücretsiz Kargo Hakkı");
        drawers.Threshold!.CumulativeAmount.Should().Be(150m, "eşik attach-shipment yanıtının havuzuyla");
        var calls = Server.CommandCalls;
        calls.Select(c => c.Command.Kind).Should().Equal("attach-shipment", "shipment-decision");
        var shipmentId = _bed.LabelRepo.GetById(N(order))!.ShipmentId!;
        PayloadString(calls[1], "shipmentId").Should().Be(Guid.Parse(shipmentId).ToString());
        PayloadString(calls[1], "decision").Should().Be("ship-now");
        var expectedVersion = calls[1].Command.Payload!.Value.GetProperty("expectedVersion").GetInt32();
        expectedVersion.Should().Be((int)mirrorVersionAtDecision!.Value, "beklenen sürüm aynanın ServerVersion'ı");
        expectedVersion.Should().Be(serverVersionAtDecision);
        _bed.ShipmentRepo.GetById(shipmentId)!.Status.Should().Be(ShipmentStatus.Shipped);
        Server.Shipment(Guid.Parse(shipmentId))!.Row.Status.Should().Be("shipped");
        vm.ShipmentWarning.Should().BeNull();
    }

    [Fact]
    public async Task Mod_On_esik_ve_cekmecenin_esik_metni_lisans_geneli_ayardan()
    {
        // Spec 2026-10-13 §6.2 (Y4): yerel eşik 200 (150'lik havuz altında kalırdı), lisans geneli 140. Eşik
        // değerlendirmesi de çekmecenin eşik metni de lisans geneli ayardan.
        _bed.Settings.Shipping.FreeShippingThreshold = 200m;
        _bed.Settings.Shipping.ShippingFee = 50m;
        _bed.LicenseShipping(140m, 50m, version: 4);
        await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Decision = ShipmentDecision.ShipNow };
        var vm = Vm("150", drawers);

        (await vm.SaveAsync()).Should().BeTrue();

        drawers.Titles.Should().Equal("Ücretsiz Kargo Hakkı");
        drawers.Threshold!.FreeShippingThreshold.Should().Be(140m);
        drawers.Threshold.ThresholdText.Should().Be("140,00 TL");
        drawers.Threshold.CumulativeAmount.Should().Be(150m);
    }

    [Fact]
    public async Task Kaydetme_surerken_ikinci_Kaydet_zinciri_yeniden_kosturmaz()
    {
        // 2026-10-19'a kadar referans no tekrar kontrolü çift tıklamayı da durduruyordu: Payment satırı ilk tıklamada,
        // ilk await'ten önce yazılırdı, ikinci tıklama "zaten kayıtlı" alırdı. Mod On'da attach-shipment ağda
        // beklerken çekmece en üstte ve Kaydet etkin; WPF çift tıklamada Click'i iki kez yükseltir. Koruma yoksa
        // ikinci tıklama zinciri baştan koşturur: ikinci attach-shipment, ikinci eşik çekmecesi, ikinci karar.
        Threshold(100m, 50m);
        await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Decision = ShipmentDecision.ShipNow };
        using var release = new ManualResetEventSlim();
        var commands = 0;
        Server.DuringPush = endpoint =>
        {
            // İlk komut (attach-shipment) ikinci tıklamadan sonrasına kadar yanıtlanmaz.
            if (endpoint == FakeSalesServer.SalesCommands && Interlocked.Increment(ref commands) == 1)
                release.Wait(TimeSpan.FromSeconds(10));
        };
        var vm = Vm("150", drawers);

        Task<bool> first, second;
        bool firstPending, canSaveWhileSaving;
        try
        {
            first = vm.SaveAsync();
            firstPending = !first.IsCompleted;
            canSaveWhileSaving = vm.CanSave;
            second = vm.SaveAsync();
        }
        finally
        {
            release.Set();
        }
        var secondResult = await second;
        var firstResult = await first;

        firstPending.Should().BeTrue("mod On'da kargo adımı ağda bekliyor");
        secondResult.Should().BeFalse("zincir zaten sürüyor: ikinci tıklama çekmeceyi kapatmaz");
        firstResult.Should().BeTrue();
        Server.CommandCalls.Select(c => c.Command.Kind).Should().Equal(["attach-shipment", "shipment-decision"],
            "zincir bir kez koştu");
        drawers.Titles.Should().Equal(["Ücretsiz Kargo Hakkı"], "eşik çekmecesi bir kez açıldı");
        canSaveWhileSaving.Should().BeFalse("kaydetme sürerken Kaydet pasif");
        vm.CanSave.Should().BeTrue("zincir bitti: form yeniden kullanılabilir");
        vm.ShipmentWarning.Should().BeNull();
    }

    [Fact]
    public async Task Hold_yonergesi_cekmecesiz_shipment_decision_gonderir()
    {
        Threshold(1000m, 60m);
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Directive = ShipmentDirective.Hold };
        var vm = Vm("150", drawers);                                       // kargo ücreti eksik

        (await vm.SaveAsync()).Should().BeTrue();

        drawers.Titles.Should().Equal(["Kargo Ücreti Eksik"], "Hold kararı eşik çekmecesi açmaz");
        var calls = Server.CommandCalls;
        calls.Select(c => c.Command.Kind).Should().Equal("attach-shipment", "shipment-decision");
        PayloadString(calls[1], "decision").Should().Be("hold");
        var shipmentId = _bed.LabelRepo.GetById(N(order))!.ShipmentId!;
        _bed.ShipmentRepo.GetById(shipmentId)!.Status.Should().Be(ShipmentStatus.Held);
        Server.Shipment(Guid.Parse(shipmentId))!.Row.Status.Should().Be("held");
        PaymentRows().Should().Be(0, "yönerge yalnız dosyada; eskiden Payment satırına da yazılıyordu");
    }

    [Fact]
    public async Task Kargolanmis_dosyaya_karar_satir_ici_uyari_ayna_guncel_dosyayla_tazelenir()
    {
        Threshold(100m, 50m);
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Decision = ShipmentDecision.Hold };
        drawers.BeforeDecision = () =>
        {
            // Operatör çekmecedeyken başka bilgisayar dosyayı kargoladı.
            var shipment = Guid.Parse(_bed.LabelRepo.GetById(N(order))!.ShipmentId!);
            Server.MutateShipment(shipment, s => s with { Status = "shipped", ShippedAt = SalesEditTestBed.T0.AddHours(3) });
        };
        var vm = Vm("150", drawers);

        (await vm.SaveAsync()).Should().BeTrue();

        Server.CommandCalls.Select(c => c.Command.Kind).Should().Equal("attach-shipment", "shipment-decision");
        vm.ShipmentWarning.Should().Be(
            SalesCommandMessages.ShipmentDecisionFailed(SalesCommandMessages.ShipmentShippedDecision));
        var shipmentId = _bed.LabelRepo.GetById(N(order))!.ShipmentId!;
        _bed.ShipmentRepo.GetById(shipmentId)!.Status.Should().Be(ShipmentStatus.Shipped,
            "ret currentShipments taşır: ayna tazelenir");
    }

    /// <summary>Çekmece açıkken başka bilgisayar dosyayı değiştirir (bir kez): kararın beklenen sürümü bayatlar.</summary>
    private Action ChangeOnceWhileDrawerOpen(Guid order, Func<FakeSalesServer.ShipmentBody, FakeSalesServer.ShipmentBody> change)
    {
        var done = false;
        return () =>
        {
            if (done) return;
            done = true;
            Server.MutateShipment(Guid.Parse(_bed.LabelRepo.GetById(N(order))!.ShipmentId!), change);
        };
    }

    [Fact]
    public async Task Bayat_dosya_surumunde_karar_sessizce_yeniden_denenmez_ayna_tazelenir_plan_yeniden_gosterilir()
    {
        Threshold(100m, 50m);
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Decisions = new([ShipmentDecision.ShipNow, null]) };
        drawers.BeforeDecision = ChangeOnceWhileDrawerOpen(order,
            s => s with { Status = "held", HeldAt = SalesEditTestBed.T0.AddHours(2) });
        var vm = Vm("150", drawers);

        (await vm.SaveAsync()).Should().BeTrue();

        Server.CommandCalls.Select(c => c.Command.Kind).Should().Equal(["attach-shipment", "shipment-decision"],
            "kargoda sessiz yeniden deneme yok (§5.3); ikinci çekmecede operatör karar vermedi");
        drawers.Thresholds.Should().HaveCount(2, "plan güncel havuzla bir kez yeniden gösterilir (§5.3)");
        drawers.Thresholds[0].Notice.Should().BeNull();
        drawers.Thresholds[1].Notice.Should().Be(SalesCommandMessages.Stale);
        drawers.Thresholds[1].Context.Shipment!.Status.Should().Be(ShipmentStatus.Held, "aynanın güncel hâli");
        vm.ShipmentWarning.Should().Be(SalesCommandMessages.ShipmentDecisionFailed(SalesCommandMessages.Stale),
            "karar verilmedi: şerit sebebi söyler");
        var shipmentId = _bed.LabelRepo.GetById(N(order))!.ShipmentId!;
        var mirror = _bed.ShipmentRepo.GetById(shipmentId)!;
        mirror.Status.Should().Be(ShipmentStatus.Held);
        mirror.ServerVersion.Should().Be(Server.Shipment(Guid.Parse(shipmentId))!.Version);
    }

    [Fact]
    public async Task Bayat_karardan_sonra_yeniden_gosterilen_planda_verilen_karar_guncel_surumle_uygulanir()
    {
        Threshold(100m, 50m);
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Decisions = new([ShipmentDecision.ShipNow, ShipmentDecision.ShipNow]) };
        drawers.BeforeDecision = ChangeOnceWhileDrawerOpen(order,
            s => s with { Status = "held", HeldAt = SalesEditTestBed.T0.AddHours(2) });
        var vm = Vm("150", drawers);

        (await vm.SaveAsync()).Should().BeTrue();

        Server.CommandCalls.Select(c => c.Command.Kind).Should().Equal(
            ["attach-shipment", "shipment-decision", "shipment-decision"]);
        var shipmentId = _bed.LabelRepo.GetById(N(order))!.ShipmentId!;
        Server.Shipment(Guid.Parse(shipmentId))!.Row.Status.Should().Be("shipped", "operatörün ikinci kararı uygulandı");
        _bed.ShipmentRepo.GetById(shipmentId)!.Status.Should().Be(ShipmentStatus.Shipped);
        vm.ShipmentWarning.Should().BeNull("karar uygulandı");
    }

    [Fact]
    public async Task Bayat_karardan_sonra_havuz_esigin_altindaysa_plan_yeniden_gosterilmez()
    {
        Threshold(100m, 50m);
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        var drawers = new Drawers { Decisions = new([ShipmentDecision.ShipNow, ShipmentDecision.ShipNow]) };
        drawers.BeforeDecision = ChangeOnceWhileDrawerOpen(order, s => s with { CumulativeAmount = 60m });
        var vm = Vm("150", drawers);

        (await vm.SaveAsync()).Should().BeTrue();

        drawers.Thresholds.Should().ContainSingle("eşiğin altında karar sorulmaz (§7.2)");
        Server.CommandCalls.Select(c => c.Command.Kind).Should().Equal(["attach-shipment", "shipment-decision"]);
        vm.ShipmentWarning.Should().Be(SalesCommandMessages.ShipmentDecisionFailed(SalesCommandMessages.Stale));
    }

    [Fact]
    public async Task Kazandin_mesaji_kararin_yanitindaki_havuzu_soyler()
    {
        // Kişinin kabul edilmiş (türetilmemiş) eski açık dosyası 3000 ₺; yeni etiket 5300 ₺. Havuz ve mesaj 8300 ₺.
        Threshold(5000m, 150m);
        _bed.Settings.Payment.ShippingWonTemplate = "Tebrikler {ad}, {kumulatif_tutar} TL!";
        var (_, customer, _) = await _bed.RemoteLabelAsync(5300m);
        Server.SeedShipment(new FakeSalesServer.ShipmentBody(Guid.NewGuid(), N(customer), "held", 3000m,
            SalesEditTestBed.T0, SalesEditTestBed.T0, null), origin: Guid.NewGuid());
        _bed.Exec("UPDATE Customer SET Phone = @phone WHERE Id = @id", new { phone = TestPhone.NewE164(), id = N(customer) });
        _bed.ModeOn();
        var path = Path.Combine(Path.GetTempPath(), $"od-dekont-w2-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(_bed.Settings);
            var launcher = new OrderDeck.Tests.Fakes.FakeUrlLauncher();
            var paymentRequest = new OrderDeck.App.Services.PaymentRequestService(store,
                new OrderDeck.Core.Customers.WhatsAppMessageBuilder(),
                launcher, PaymentRequestServiceTestHelpers.StubApiClient(),
                new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                new OrderDeck.Tests.Fakes.InMemoryPaymentJobStore(), _bed.Shipping);
            var drawers = new Drawers { Decision = ShipmentDecision.ShipNow };
            var vm = Vm("5300", drawers, paymentRequest: paymentRequest);

            (await vm.SaveAsync()).Should().BeTrue();

            drawers.Threshold!.CumulativeAmount.Should().Be(8300m);
            launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("8.300%2C00",
                "mesaj kararın kapsadığı dosyaların (U12) yanıttaki toplamını söyler");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── mod On: adım atlanır, uyarı şeritte (§5.8) ─────────────────────────

    [Fact]
    public async Task Cevrimdisi_kargo_adimi_atlanir_uyari_hicbir_sey_yazilmaz_yerel_kargo_yazimi_denenmez()
    {
        var (_, _, order) = await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        _bed.Online = false;
        var http = _bed.HttpCount;
        var log = new CapturingLogger<DekontEkleViewModel>();
        var vm = Vm("150", log: log);

        (await vm.SaveAsync()).Should().BeTrue("çekmece kapanır, sebep şeritte");

        PaymentRows().Should().Be(0, "dekont kaydı kalktı: çevrimdışıyken de hiçbir şey yazılmaz");
        vm.ShipmentWarning.Should().Be(SalesCommandMessages.DekontShipmentDeferred);
        vm.ShipmentWarning.Should().Be(
            "Kargo adımı yapılamadı: sunucuya ulaşılamadı. Bağlantı gelince Gelen Ödemeler'den yeniden kaydet.");
        _bed.HttpCount.Should().Be(http, "çevrimdışıyken istek gitmez");
        ShipmentRows().Should().Be(0);
        _bed.LabelRepo.GetById(N(order))!.ShipmentId.Should().BeNull();
        log.Warnings.Should().BeEmpty("yerel kargo yazımı denenmedi (mod On'da ABORT ederdi)");
    }

    [Fact]
    public async Task Baglanti_istekte_kopunca_uyari_ve_kayit_yerel_kargo_yazimi_yok()
    {
        await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();
        Server.Offline = true;
        var log = new CapturingLogger<DekontEkleViewModel>();
        var vm = Vm("150", log: log);

        (await vm.SaveAsync()).Should().BeTrue();

        vm.ShipmentWarning.Should().Be(SalesCommandMessages.DekontShipmentDeferred);
        PaymentRows().Should().Be(0);
        ShipmentRows().Should().Be(0);
        log.Warnings.Should().BeEmpty();
        _bed.Connectivity.IsOnline.Should().BeFalse("komutun ağ hatası bağlantı ölçüsüne işlendi");
    }

    [Fact]
    public async Task Komut_ucu_yoksa_guncelleme_gerekiyorsa_ya_da_kume_cok_buyukse_adim_atlanir_uyari_gosterilir()
    {
        await _bed.RemoteLabelAsync(150m);
        _bed.ModeOn();

        Server.EnqueueStatus(FakeSalesServer.Commands, 404);
        var missing = Vm("150");
        (await missing.SaveAsync()).Should().BeTrue();
        missing.ShipmentWarning.Should().Be(SalesCommandMessages.DekontShipmentDeferred);

        _bed.Connectivity.Report(SalesCommandStatus.Applied);              // uç yeniden denenebilir
        Server.MinClientVersion = "99.0.0";
        var update = Vm("150");
        (await update.SaveAsync()).Should().BeTrue();
        update.ShipmentWarning.Should().Be(SalesCommandMessages.DekontShipmentFailed(SalesCommandMessages.ClientUpdateRequired));

        Server.MinClientVersion = "0.9.11";
        Server.MaxAttachCandidates = 0;
        var tooMany = Vm("150");
        (await tooMany.SaveAsync()).Should().BeTrue();
        tooMany.ShipmentWarning.Should().Be(SalesCommandMessages.DekontShipmentFailed(SalesCommandMessages.TooManyCandidates(1)));
        _bed.Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @kind",
            new { kind = SalesConflictLog.CommandTooManyCandidates }).Should().Be(1);

        ShipmentRows().Should().Be(0);
        PaymentRows().Should().Be(0);
    }

    // ── mod Off: bugünkü yerel yol ─────────────────────────────────────────

    [Fact]
    public void Mod_Off_yonlendirici_bugunku_yerel_yolu_esZamanli_ve_agsiz_yurutur()
    {
        Threshold(100m, 50m);
        _bed.FixedUnixNow = 1_760_100_000L;
        var session = _bed.LocalSession(ended: false);
        var customer = Guid.NewGuid().ToString("N");
        var label = _bed.LocalLabel(session, customer, 150m);
        _bed.Exec("UPDATE Customer SET Platform = 'tiktok', Username = 'alici_ornek' WHERE Id = @customer", new { customer });
        var vm = Vm("150");

        var result = vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        result.ThresholdContext.Should().NotBeNull("mod Off'ta adım eşzamanlı biter, bağlam hemen döner");
        result.PendingShipmentStep.Should().BeNull();
        var shipment = _bed.LabelRepo.GetById(label)!.ShipmentId;
        shipment.Should().Be(result.ThresholdContext!.Shipment!.Id);
        _bed.ShipmentRepo.GetById(shipment!)!.SyncedAt.Should().BeNull("yerel dosya: shipments/sync gönderir");

        vm.ApplyShipmentDecision(shipment!, ShipmentDecision.ShipNow);

        _bed.ShipmentRepo.GetById(shipment!)!.Status.Should().Be(ShipmentStatus.Shipped, "karar eşzamanlı uygulandı");
        _bed.HttpCount.Should().Be(0, "mod Off'ta hiçbir istek gitmez");
        Server.CommandCalls.Should().BeEmpty();
    }

    [Fact]
    public void Mod_Off_yonlendiricinin_yazimi_yerel_duzenleyicininkiyle_birebir_ayni()
    {
        string Run(bool routed)
        {
            using var bed = new SalesEditTestBed { FixedUnixNow = 1_760_100_000L };
            bed.Settings.Shipping.FreeShippingThreshold = 100m;
            bed.Settings.Shipping.ShippingFee = 50m;
            var session = bed.LocalSession(ended: false, id: "yerel-yayin");
            bed.LocalLabel(session, "kisi-0001", 150m, id: "etiket-1");
            bed.LocalLabel(session, "kisi-0001", 40m, id: "etiket-2", tentative: true);
            bed.Exec("UPDATE Customer SET Platform = 'tiktok', Username = 'alici_ornek' WHERE Id = 'kisi-0001'");
            var vm = new DekontEkleViewModel(bed.Customers, bed.Sessions,
                new PaymentMatcherService(bed.LabelRepo, bed.Shipping), bed.Shipments, bed.Shipping,
                NullLogger<DekontEkleViewModel>.Instance, editor: routed ? bed.Router : null)
            {
                AmountText = "150", CustomerPlatform = "tiktok", CustomerUsername = "alici_ornek",
            };
            var ctx = vm.TrySave().ThresholdContext!;
            vm.ApplyShipmentDecision(ctx.Shipment!.Id, ShipmentDecision.Hold);
            bed.HttpCount.Should().Be(0);
            // Dosya Id'si ve veritabanının kopya kimliği her koşuda yeni: karşılaştırma için sabitlenir.
            return bed.Snapshot().Replace(ctx.Shipment.Id, "<dosya>")
                .Replace(bed.Scalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1"), "<kopya>");
        }

        Run(routed: true).Should().Be(Run(routed: false));
    }
}
