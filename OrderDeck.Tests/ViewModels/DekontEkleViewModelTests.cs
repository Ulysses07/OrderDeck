using Dapper;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// "Gelen Ödemeler" çekmecesi (<see cref="DekontEkleViewModel"/>; 2026-10-19'a kadar "Yeni Dekont"). Dekont kaydı
/// kalktı: form müşteri + gelen tutardır, hiçbir <c>Payment</c> satırı yazılmaz. Kargo zinciri (eşleştirme, yönerge,
/// RecipientPays bayrağı, dosyaya bağlama, eşik, karar, "kazandın" mesajı) bugünkü gibi çalışır.
/// </summary>
public sealed class DekontEkleViewModelTests
{
    private sealed class Fixture
    {
        public DekontEkleViewModel Vm { get; }
        public CustomerRepository Customers { get; }
        public SessionRepository Sessions { get; }
        public LabelRepository Labels { get; }
        public ShipmentRepository Shipments { get; }
        public ShipmentService ShipmentSvc { get; }
        public AppSettings Settings { get; }

        /// <summary>Gerçek kargo ayarı sağlayıcısı: mod Off'ta (varsayılan) <see cref="Settings"/>.</summary>
        public ShippingSettingsProvider Shipping { get; }
        public InMemorySqlite Db { get; }

        public Fixture()
        {
            Db = new InMemorySqlite();
            new MigrationRunner(Db).Run();
            Customers = new CustomerRepository(Db);
            Sessions = new SessionRepository(Db);
            Labels = new LabelRepository(Db);
            Shipments = new ShipmentRepository(Db);
            Settings = new AppSettings();
            Shipping = new ShippingSettingsProvider(new SalesSyncStateRepository(Db), Settings);
            ShipmentSvc = new ShipmentService(Shipments, Labels, Shipping, () => 1_000L);

            Vm = NewVm();
        }

        public DekontEkleViewModel NewVm(OrderDeck.App.Services.PaymentRequestService? paymentRequest = null) =>
            new(Customers, Sessions, new PaymentMatcherService(Labels, Shipping), ShipmentSvc, Shipping,
                NullLogger<DekontEkleViewModel>.Instance, paymentRequest);

        /// <summary>Yerel <c>Payment</c> tablosunun satır sayısı: çekmece artık hiç yazmamalı.</summary>
        public int PaymentRows()
        {
            using var c = Db.Open();
            return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Payment");
        }

        public int ShipmentRows()
        {
            using var c = Db.Open();
            return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Shipment");
        }
    }

    /// <summary>Geçerli form: müşteri (instagram/@ayse_y) + gelen tutar. Müşteriyi veritabanına
    /// <see cref="SeedCustomer"/> ekler.</summary>
    private static void FillValid(DekontEkleViewModel vm)
    {
        vm.CustomerPlatform = "instagram";
        vm.CustomerUsername = "@ayse_y";
        vm.AmountText = "250,50";
    }

    private static Customer SeedCustomer(Fixture fx, string id = "c1",
        string platform = "instagram", string username = "@ayse_y")
    {
        var c = new Customer(id, platform, username, "Ayşe Y", null,
            FirstSeenAt: 500, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null);
        fx.Customers.Insert(c);
        return c;
    }

    private static string SeedActiveSession(Fixture fx)
    {
        var sid = System.Guid.NewGuid().ToString("N");
        fx.Sessions.Insert(new StreamSession(sid, null, 1000, null, new[] { "instagram" }, null));
        return sid;
    }

    private static string SeedEndedSession(Fixture fx, long startedAt = 900, long endedAt = 999)
    {
        var sid = System.Guid.NewGuid().ToString("N");
        fx.Sessions.Insert(new StreamSession(sid, null, startedAt, null, new[] { "instagram" }, null));
        fx.Sessions.End(sid, endedAt);
        return sid;
    }

    private static string SeedLabel(Fixture fx, string sessionId, string customerId, decimal price,
        long addedAt = 1100L, string text = "ürün")
    {
        var id = System.Guid.NewGuid().ToString("N");
        fx.Labels.Insert(new Label(
            Id: id, SessionId: sessionId, CustomerId: customerId,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: text, Code: null, Price: price,
            AddedAt: addedAt, PrintedAt: null));
        return id;
    }

    // ── Doğrulama ─────────────────────────────────────────────────────────

    [Fact]
    public void CanSave_is_false_when_form_is_empty()
    {
        var fx = new Fixture();
        fx.Vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public void CanSave_is_true_when_customer_and_amount_are_set()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.CanSave.Should().BeTrue();
    }

    /// <summary>Müşteri zorunlu: kargo zinciri müşterisiz çalışmaz. Boşsa satır içi hata, hiçbir şey yazılmaz.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("instagram", "")]
    [InlineData("", "@ayse_y")]
    [InlineData("instagram", "   ")]
    public async Task Musteri_bosken_kayit_yok_satir_ici_hata(string platform, string username)
    {
        var fx = new Fixture();
        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var label = SeedLabel(fx, sid, customer.Id, 250.50m);
        fx.Vm.AmountText = "250,50";
        fx.Vm.CustomerPlatform = platform;
        fx.Vm.CustomerUsername = username;

        fx.Vm.CanSave.Should().BeFalse("Kaydet pasif");
        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
        result.Error.Should().Contain("Müşteri gerekli");
        fx.Vm.ErrorMessage.Should().Be(result.Error, "satır içi hata");

        (await fx.Vm.SaveAsync()).Should().BeFalse("çekmece açık kalır, form düzeltilebilsin");
        fx.Vm.ErrorMessage.Should().NotBeNull();

        fx.PaymentRows().Should().Be(0);
        fx.ShipmentRows().Should().Be(0, "kargo zinciri çalışmadı");
        fx.Labels.GetById(label)!.ShipmentId.Should().BeNull();
    }

    [Fact]
    public void Bulunamayan_musteride_kayit_yok_satir_ici_hata()
    {
        // Eskiden dekont yine yazılıyor, eşleştirme sessizce atlanıyordu. Artık yazılacak bir şey yok: sessizce
        // kapanmak "oldu" sanılırdı.
        var fx = new Fixture();
        SeedActiveSession(fx);
        FillValid(fx.Vm);
        fx.Vm.CustomerUsername = "@bilinmeyen";

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
        fx.Vm.ErrorMessage.Should().Contain("Müşteri bulunamadı");
        fx.PaymentRows().Should().Be(0);
        fx.ShipmentRows().Should().Be(0);
    }

    [Theory]
    [InlineData("250,50")]
    [InlineData("250.50")]
    [InlineData("1000")]
    [InlineData("0.01")]
    public void Amount_accepts_comma_and_dot_decimal(string raw)
    {
        var fx = new Fixture();
        SeedCustomer(fx);
        FillValid(fx.Vm);
        fx.Vm.AmountText = raw;

        fx.Vm.CanSave.Should().BeTrue();
        fx.Vm.TrySave().Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-50")]
    public void TrySave_rejects_invalid_amount(string raw)
    {
        var fx = new Fixture();
        SeedCustomer(fx);
        FillValid(fx.Vm);
        fx.Vm.AmountText = raw;

        fx.Vm.TrySave().Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
        fx.ShipmentRows().Should().Be(0);
    }

    // ── Payment yazılmaz, kargo zinciri çalışır ──────────────────────────

    [Fact]
    public async Task Kaydet_hicbir_Payment_satiri_yazmaz_ama_kargo_zinciri_calisir()
    {
        var fx = new Fixture();
        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = SeedLabel(fx, sid, customer.Id, 1200m);
        FillValid(fx.Vm);
        fx.Vm.AmountText = "1200";

        (await fx.Vm.SaveAsync()).Should().BeTrue();

        fx.PaymentRows().Should().Be(0, "dekont kaydı kalktı: payments/sync'e yeni satır gitmez");
        var shipmentId = fx.Labels.GetById(labelId)!.ShipmentId;
        shipmentId.Should().NotBeNullOrEmpty("etiket açık kargo dosyasına bağlandı");
        fx.Shipments.GetById(shipmentId!)!.CumulativeAmount.Should().Be(1200m);
        fx.Vm.ErrorMessage.Should().BeNull();
    }

    // ── Müşteri + eşleştirme (Kargo PR D) ────────────────────────────────

    [Fact]
    public void TrySave_with_customer_no_session_at_all_skips_matcher()
    {
        var fx = new Fixture();
        SeedCustomer(fx);
        // Hiçbir session yok (ne aktif ne bitmiş) → matcher skip
        FillValid(fx.Vm);

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
    }

    [Fact]
    public void TrySave_falls_back_to_latest_ended_session_when_no_active()
    {
        // Ödemelerin çoğu yayın bittikten sonra işleniyor → matcher en son bitmiş yayın üzerinden çalışmalı. Bu
        // hotfix öncesi matcher sessizce skip ediyordu.
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;
        var customer = SeedCustomer(fx);
        var endedSid = SeedEndedSession(fx);

        // Müşteri bu (artık bitmiş) yayında 3000 TL alım yaptı
        SeedLabel(fx, endedSid, customer.Id, 3000m, addedAt: 950L);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3000"; // kargo eksik

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision,
            "yayın bittiyse bile en son yayına fallback yapıp matcher çalışmalı");
        result.Shortage!.ProductTotal.Should().Be(3000m);
    }

    [Fact]
    public void TrySave_prefers_active_session_over_ended_when_both_exist()
    {
        // Aktif yayın varsa, bitmiş yayınlara fallback gerek yok.
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;
        var customer = SeedCustomer(fx);
        var endedSid = SeedEndedSession(fx);
        var activeSid = SeedActiveSession(fx);

        // Bitmiş yayında 3000 TL, aktif yayında 500 TL
        SeedLabel(fx, endedSid, customer.Id, 3000m, addedAt: 950L, text: "eski ürün");
        SeedLabel(fx, activeSid, customer.Id, 500m, text: "yeni ürün");

        FillValid(fx.Vm);
        fx.Vm.AmountText = "500"; // sadece aktif yayın tutarı

        var result = fx.Vm.TrySave();

        // Aktif yayın seçildiğinde ProductTotal=500 → 500+150=650 expected, gelen 500 → shortage
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);
        result.Shortage!.ProductTotal.Should().Be(500m,
            "aktif yayın varken bitmiş yayına bakma");
    }

    [Fact]
    public void TrySave_with_customer_session_match_saves_as_normal()
    {
        var fx = new Fixture();
        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);

        // Müşterinin ürün label'ı 500 TL — kargo feature off, match if 500
        var labelId = SeedLabel(fx, sid, customer.Id, 500m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "500";

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        var shipment = fx.Shipments.GetById(fx.Labels.GetById(labelId)!.ShipmentId!)!;
        shipment.Status.Should().Be(ShipmentStatus.Pending, "Normal yönerge: dosya beklemeye alınmaz");
    }

    [Fact]
    public void TrySave_returns_NeedsShipmentDecision_when_shipping_fee_missing()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = SeedLabel(fx, sid, customer.Id, 3000m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3000"; // Sadece ürün, kargo eksik

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);
        result.Shortage.Should().NotBeNull();
        result.Shortage!.ProductTotal.Should().Be(3000m);
        result.Shortage.ExpectedAmount.Should().Be(3150m);
        result.Shortage.DekontAmount.Should().Be(3000m, "matcher gelen tutarı alır");

        // Yönerge bekleniyor: henüz hiçbir şey yazılmadı
        fx.PaymentRows().Should().Be(0);
        fx.Labels.GetById(labelId)!.ShipmentId.Should().BeNull();
    }

    [Fact]
    public void CommitWithDirective_RecipientPays_sets_customer_flag_and_shipment_status()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = SeedLabel(fx, sid, customer.Id, 2500m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "2500";
        fx.Vm.TrySave().Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);

        var commit = fx.Vm.CommitWithDirective(ShipmentDirective.RecipientPays);

        commit.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        commit.ThresholdContext.Should().BeNull("RecipientPays yapışkan karar: eşik sorulmaz");

        // Kargo PR F: RecipientPays seçimi customer flag'ini set etmeli.
        // Print template bu flag'i okuyup "ALICI ÖDEMELİ" render edecek.
        fx.Customers.GetById(customer.Id)!.RecipientPaysActive.Should().BeTrue();
        var shipment = fx.Shipments.GetById(fx.Labels.GetById(labelId)!.ShipmentId!)!;
        shipment.Status.Should().Be(ShipmentStatus.RecipientPays, "yönerge dosyaya uygulandı");
        fx.PaymentRows().Should().Be(0);
    }

    [Fact]
    public void CommitWithDirective_Hold_does_not_set_RecipientPaysActive_flag()
    {
        // Kargo PR F: Hold seçimi flag'i etkilemez (Hold ≠ RecipientPays).
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 2500m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "2500";
        fx.Vm.TrySave();
        fx.Vm.CommitWithDirective(ShipmentDirective.Hold);

        fx.Customers.GetById(customer.Id)!.RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void TrySave_when_amount_includes_shipping_fee_is_Saved_with_normal_directive()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = SeedLabel(fx, sid, customer.Id, 3000m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3150"; // Ürün + kargo dahil

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        fx.Shipments.GetById(fx.Labels.GetById(labelId)!.ShipmentId!)!.Status
            .Should().Be(ShipmentStatus.Pending);
    }

    // ── PR-C/2: Shipment hook integration tests ──────────────────────────

    [Fact]
    public void TrySave_attaches_label_to_open_shipment_when_customer_resolved()
    {
        var fx = new Fixture();
        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = SeedLabel(fx, sid, customer.Id, 1200m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "1200";

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);

        // Hook açtı + Label'ı attach etti
        var attachedLabel = fx.Labels.GetById(labelId)!;
        attachedLabel.ShipmentId.Should().NotBeNullOrEmpty();
        var shipment = fx.Shipments.GetById(attachedLabel.ShipmentId!)!;
        shipment.CumulativeAmount.Should().Be(1200m);
        shipment.Status.Should().Be(ShipmentStatus.Pending);
    }

    [Fact]
    public void TrySave_below_threshold_does_not_return_threshold_context()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 1200m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "1350"; // 1200 ürün + 150 kargo

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        result.ThresholdContext.Should().BeNull();
    }

    [Fact]
    public void TrySave_at_or_above_threshold_returns_threshold_context()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 5200m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "5200"; // ücretsiz kargo bölgesi

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        result.ThresholdContext.Should().NotBeNull();
        result.ThresholdContext!.ThresholdReached.Should().BeTrue();
        result.ThresholdContext.Shipment!.CumulativeAmount.Should().Be(5200m);
    }

    [Fact]
    public void CommitWithDirective_Hold_sets_shipment_status_to_held()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = SeedLabel(fx, sid, customer.Id, 1200m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "1200"; // ürün only, kargo eksik

        var initial = fx.Vm.TrySave();
        initial.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);

        var final = fx.Vm.CommitWithDirective(ShipmentDirective.Hold);
        final.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        final.ThresholdContext.Should().BeNull(); // Hold seçilince modal yok

        var attached = fx.Labels.GetById(labelId)!;
        attached.ShipmentId.Should().NotBeNull();
        var shipment = fx.Shipments.GetById(attached.ShipmentId!)!;
        shipment.Status.Should().Be(ShipmentStatus.Held);
        shipment.HeldAt.Should().NotBeNull();
        fx.PaymentRows().Should().Be(0, "yönerge eskiden Payment satırına da yazılıyordu; artık yalnız dosyada");
    }

    [Fact]
    public void ApplyShipmentDecision_ShipNow_marks_shipment_as_shipped()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 5200m);

        FillValid(fx.Vm);
        fx.Vm.AmountText = "5200";

        var result = fx.Vm.TrySave();
        var shipmentId = result.ThresholdContext!.Shipment!.Id;

        fx.Vm.ApplyShipmentDecision(shipmentId, ShipmentDecision.ShipNow);

        var shipped = fx.Shipments.GetById(shipmentId)!;
        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        shipped.ShippedAt.Should().NotBeNull();
    }

    // ── PR-E: ShipNow tetikleyici "kazandın" WhatsApp ────────────────────

    [Fact]
    public void ApplyShipmentDecision_ShipNow_triggers_kazandin_whatsapp()
    {
        // Settings + customer + open shipment with cumulative amount setup.
        // Then call ApplyShipmentDecision(ShipNow) with a real PaymentRequestService
        // wired to a FakeUrlLauncher; assert WhatsApp link launched with template.
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;
        fx.Settings.Payment.ShippingWonTemplate = "Tebrikler {ad}, {kumulatif_tutar} TL!";

        // Customer + phone (E.164 TR)
        var telefon = TestPhone.NewE164();
        var customer = new Customer(
            "c1", "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 500, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: telefon);
        fx.Customers.Insert(customer);

        // Eşik aşan label
        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 5300m);

        // SettingsStore + PaymentRequestService + FakeLauncher hazırla
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"od-pr-{System.Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(fx.Settings);
            var launcher = new OrderDeck.Tests.Fakes.FakeUrlLauncher();
            var paymentRequest = new OrderDeck.App.Services.PaymentRequestService(
                store, new WhatsAppMessageBuilder(), launcher,
                PaymentRequestServiceTestHelpers.StubApiClient(),
                new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                new OrderDeck.Tests.Fakes.InMemoryPaymentJobStore(), fx.Shipping);

            // Custom VM with the wired PaymentRequestService
            var vm = fx.NewVm(paymentRequest);

            FillValid(vm);
            vm.AmountText = "5300";

            var result = vm.TrySave();
            result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
            result.ThresholdContext.Should().NotBeNull();

            launcher.LaunchedUrls.Should().BeEmpty(); // henüz ShipNow yok

            vm.ApplyShipmentDecision(result.ThresholdContext!.Shipment!.Id,
                ShipmentDecision.ShipNow);

            launcher.LaunchedUrls.Should().HaveCount(1);
            launcher.LaunchedUrls[0].Should().StartWith($"https://wa.me/{telefon[1..]}?text=");
            launcher.LaunchedUrls[0].Should().Contain("Tebrikler%20Ay%C5%9Fe");
            launcher.LaunchedUrls[0].Should().Contain("5.300%2C00");
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ApplyShipmentDecision_ShipNow_kazandin_mesaji_tasimadan_kalan_dosyalarin_toplamini_soyler()
    {
        // U12: yerel taşıma kişide iki açık dosya bıraktı. Eşik ve "kazandın" mesajı havuzun
        // toplamından (3000 + 5300); Shipment nesneleri ise satırların aynası kalır.
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;
        fx.Settings.Payment.ShippingWonTemplate = "Tebrikler {ad}, {kumulatif_tutar} TL!";

        var telefon = TestPhone.NewE164();
        var customer = new Customer(
            "c1", "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 500, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: telefon);
        fx.Customers.Insert(customer);
        fx.Shipments.Insert(new Shipment("kargo-eski", customer.Id, ShipmentStatus.Held, 50, 60, null, 3000m));
        fx.Shipments.Insert(new Shipment("kargo-yeni", customer.Id, ShipmentStatus.Pending, 70, null, null, 0m));

        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 5300m);

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"od-pr-{System.Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(fx.Settings);
            var launcher = new OrderDeck.Tests.Fakes.FakeUrlLauncher();
            var paymentRequest = new OrderDeck.App.Services.PaymentRequestService(
                store, new WhatsAppMessageBuilder(), launcher,
                PaymentRequestServiceTestHelpers.StubApiClient(),
                new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                new OrderDeck.Tests.Fakes.InMemoryPaymentJobStore(), fx.Shipping);
            var vm = fx.NewVm(paymentRequest);

            FillValid(vm);
            vm.AmountText = "5300";

            var result = vm.TrySave();
            var ctx = result.ThresholdContext!;
            ctx.Shipment!.Id.Should().Be("kargo-yeni");
            ctx.Shipment.CumulativeAmount.Should().Be(5300m);
            ctx.PooledAmount.Should().Be(8300m);
            new ShipmentThresholdDialogViewModel(ctx, "instagram/@ayse_y", 5000m)
                .CumulativeAmount.Should().Be(8300m, "çekmece havuzun toplamını gösterir");

            vm.ApplyShipmentDecision(ctx.Shipment.Id, ShipmentDecision.ShipNow);

            launcher.LaunchedUrls.Should().ContainSingle()
                .Which.Should().Contain("8.300%2C00");
            fx.Shipments.GetById("kargo-eski")!.Status.Should().Be(ShipmentStatus.Shipped);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ApplyShipmentDecision_Hold_does_not_trigger_whatsapp()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;
        fx.Settings.Payment.ShippingWonTemplate = "Tebrikler!";

        var customer = new Customer(
            "c1", "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 500, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: TestPhone.NewE164());
        fx.Customers.Insert(customer);

        var sid = SeedActiveSession(fx);
        SeedLabel(fx, sid, customer.Id, 5300m);

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"od-pr-{System.Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(fx.Settings);
            var launcher = new OrderDeck.Tests.Fakes.FakeUrlLauncher();
            var paymentRequest = new OrderDeck.App.Services.PaymentRequestService(
                store, new WhatsAppMessageBuilder(), launcher,
                PaymentRequestServiceTestHelpers.StubApiClient(),
                new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                new OrderDeck.Tests.Fakes.InMemoryPaymentJobStore(), fx.Shipping);
            var vm = fx.NewVm(paymentRequest);

            FillValid(vm);
            vm.AmountText = "5300";

            var result = vm.TrySave();
            vm.ApplyShipmentDecision(result.ThresholdContext!.Shipment!.Id,
                ShipmentDecision.Hold);

            launcher.LaunchedUrls.Should().BeEmpty(); // Hold seçildi, mesaj yok
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
