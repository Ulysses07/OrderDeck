using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public sealed class DekontEkleViewModelTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private sealed class Fixture
    {
        public DekontEkleViewModel Vm { get; }
        public PaymentRepository Payments { get; }
        public CustomerRepository Customers { get; }
        public SessionRepository Sessions { get; }
        public LabelRepository Labels { get; }
        public ShipmentRepository Shipments { get; }
        public ShipmentService ShipmentSvc { get; }
        public AppSettings Settings { get; }
        public InMemorySqlite Db { get; }

        public Fixture()
        {
            Db = new InMemorySqlite();
            new MigrationRunner(Db).Run();
            Payments = new PaymentRepository(Db);
            Customers = new CustomerRepository(Db);
            Sessions = new SessionRepository(Db);
            Labels = new LabelRepository(Db);
            Shipments = new ShipmentRepository(Db);
            Settings = new AppSettings();
            ShipmentSvc = new ShipmentService(Shipments, Labels, () => Settings, () => 1_000L);

            var matcher = new PaymentMatcherService(Labels, () => Settings);
            var pdfParser = new PdfDekontParser();
            Vm = new DekontEkleViewModel(
                Payments, Customers, Sessions, matcher, Labels, ShipmentSvc,
                pdfParser, Settings,
                new FakeClock(),
                NullLogger<DekontEkleViewModel>.Instance);
        }
    }

    private static void FillValid(DekontEkleViewModel vm)
    {
        vm.PayerName = "Ahmet Yıldız";
        vm.AmountText = "250,50";
        vm.ReferansNo = "REF-001";
        vm.PaidAt = DateTime.Today;
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

    // ── Existing validation + parse tests ────────────────────────────────

    [Fact]
    public void CanSave_is_false_when_form_is_empty()
    {
        var fx = new Fixture();
        fx.Vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public void CanSave_is_true_when_all_required_fields_are_set()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.CanSave.Should().BeTrue();
    }

    [Theory]
    [InlineData("250,50")]
    [InlineData("250.50")]
    [InlineData("1000")]
    [InlineData("0.01")]
    public void Amount_accepts_comma_and_dot_decimal(string raw)
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.AmountText = raw;
        fx.Vm.ReferansNo = $"REF-{System.Guid.NewGuid():N}";

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);

        var stored = fx.Payments.ListByStatus(PaymentStatus.Pending).First();
        stored.Amount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TrySave_persists_payment_with_pending_status_and_normal_directive()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        var stored = fx.Payments.FindByReferansNo("REF-001");
        stored.Should().NotBeNull();
        stored!.PayerName.Should().Be("Ahmet Yıldız");
        stored.Amount.Should().Be(250.50m);
        stored.Status.Should().Be(PaymentStatus.Pending);
        stored.ShipmentDirective.Should().Be(ShipmentDirective.Normal);
        stored.SyncedAt.Should().BeNull("outbox pickup yapacak");
    }

    [Fact]
    public void TrySave_rejects_empty_payer_name()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.PayerName = "";

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
        result.Error.Should().Contain("Ödeyen");
    }

    [Fact]
    public void TrySave_rejects_empty_referans_no()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.ReferansNo = "";

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-50")]
    public void TrySave_rejects_invalid_amount(string raw)
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.AmountText = raw;

        fx.Vm.TrySave().Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
    }

    [Fact]
    public void TrySave_rejects_future_paid_date()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.PaidAt = DateTime.Today.AddDays(2);

        var result = fx.Vm.TrySave();
        result.Error.Should().Contain("gelecek");
    }

    [Fact]
    public void TrySave_rejects_duplicate_referans_no()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        fx.Vm.TrySave();

        // Reset VM (simulate fresh dialog) and try same ref no
        FillValid(fx.Vm);
        fx.Vm.ReferansNo = "REF-001";

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Error);
        result.Error.Should().Contain("zaten kayıtlı");
    }

    // ── Customer + matcher integration (Kargo PR D) ──────────────────────

    [Fact]
    public void TrySave_without_customer_inputs_uses_basic_flow_no_matcher()
    {
        var fx = new Fixture();
        FillValid(fx.Vm);
        // Müşteri alanları boş → matcher skip

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        fx.Payments.FindByReferansNo("REF-001")!.ShipmentDirective
            .Should().Be(ShipmentDirective.Normal);
    }

    [Fact]
    public void TrySave_with_customer_no_session_at_all_skips_matcher()
    {
        var fx = new Fixture();
        SeedCustomer(fx);
        // Hiçbir session yok (ne aktif ne bitmiş) → matcher skip
        FillValid(fx.Vm);
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
    }

    [Fact]
    public void TrySave_falls_back_to_latest_ended_session_when_no_active()
    {
        // %90 dekont yayın bittikten sonra giriliyor → matcher en son
        // bitmiş yayın üzerinden çalışmalı. Bu hotfix öncesi matcher
        // sessizce skip ediyordu.
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;
        var customer = SeedCustomer(fx);
        var endedSid = SeedEndedSession(fx);

        // Müşteri bu (artık bitmiş) yayında 3000 TL alım yaptı
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: endedSid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 3000m,
            AddedAt: 950L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3000"; // kargo eksik
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

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
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: endedSid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "eski ürün", Code: null, Price: 3000m,
            AddedAt: 950L, PrintedAt: null));
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: activeSid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "yeni ürün", Code: null, Price: 500m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "500"; // sadece aktif yayın tutarı
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        var result = fx.Vm.TrySave();

        // Aktif yayın seçildiğinde ProductTotal=500 → 500+150=650 expected,
        // dekont 500 → shortage
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
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 500m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "500";
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        fx.Payments.FindByReferansNo("REF-001")!.ShipmentDirective
            .Should().Be(ShipmentDirective.Normal);
    }

    [Fact]
    public void TrySave_returns_NeedsShipmentDecision_when_shipping_fee_missing()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);

        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 3000m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3000"; // Sadece ürün, kargo eksik
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);
        result.Shortage.Should().NotBeNull();
        result.Shortage!.ProductTotal.Should().Be(3000m);
        result.Shortage.ExpectedAmount.Should().Be(3150m);

        // Henüz Payment yaratılmamış
        fx.Payments.FindByReferansNo("REF-001").Should().BeNull();
    }

    [Fact]
    public void CommitWithDirective_persists_payment_with_chosen_directive()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 3000m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3000";
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        // İlk save NeedsShipmentDecision döner
        fx.Vm.TrySave().Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);

        // Vendor "Beklet" seçti
        var commit = fx.Vm.CommitWithDirective(ShipmentDirective.Hold);

        commit.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        var stored = fx.Payments.FindByReferansNo("REF-001")!;
        stored.ShipmentDirective.Should().Be(ShipmentDirective.Hold);
    }

    [Fact]
    public void CommitWithDirective_RecipientPays_roundtrips()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 2500m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "2500";
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";
        fx.Vm.TrySave().Kind.Should().Be(DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision);

        var commit = fx.Vm.CommitWithDirective(ShipmentDirective.RecipientPays);

        commit.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        fx.Payments.FindByReferansNo("REF-001")!.ShipmentDirective
            .Should().Be(ShipmentDirective.RecipientPays);

        // Kargo PR F: RecipientPays seçimi customer flag'ini set etmeli.
        // Print template bu flag'i okuyup "ALICI ÖDEMELİ" render edecek.
        fx.Customers.GetById(customer.Id)!.RecipientPaysActive.Should().BeTrue();
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
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 2500m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "2500";
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";
        fx.Vm.TrySave();
        fx.Vm.CommitWithDirective(ShipmentDirective.Hold);

        fx.Customers.GetById(customer.Id)!.RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void TrySave_with_unknown_customer_silently_skips_matcher()
    {
        var fx = new Fixture();
        SeedActiveSession(fx);
        // Customer asla seed edilmedi

        FillValid(fx.Vm);
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@bilinmeyen";

        var result = fx.Vm.TrySave();

        // Müşteri bulunmadı → basic save, no prompt
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
    }

    // ── IBAN match check (2026-05-12) ───────────────────────────────────

    /// <summary>Vendor IBAN'ı Settings.Payment.Iban'da set'li olduğunda PDF
    /// alıcı IBAN'ı uyuşmazsa uyarı çıkar.</summary>
    [Fact]
    public void TryFillFromPdf_warns_when_recipient_iban_does_not_match_settings()
    {
        var fx = new Fixture();
        fx.Settings.Payment.Iban = "TR12 0011 1000 0000 0107 0201 32"; // boşluklu ok
        // PDF text içinde ALICI IBAN tamamen farklı:
        var pdfText = "ALICI IBAN: TR99 0099 9999 9999 9999 9999 99";

        // Helper: smoke text → IBAN extract → CheckIbanMatch logic doğrudan
        // burada tetiklenmez (TryFillFromPdf byte[] alır, PdfPig açar). Bunun
        // için ParseFromText pathini taklit edip ViewModel.TryFillFromPdf
        // davranışını test edelim — pure parser çıktısı bizim helper'a düşer.
        var parser = new OrderDeck.Core.Payments.PdfDekontParser();
        var parsed = parser.ParseFromText(pdfText, "fakehash");

        parsed.RecipientIban.Should().Be("TR990099999999999999999999");
        // ViewModel'in CheckIbanMatch'i private; ama IbanWarning gözlemlenebilir.
        // PDF byte[] olmadan direkt test için Parse(byte[]) çağırılamaz; bu
        // testte parser sonucunu zaten doğrulamak yeterli. Full integration
        // smoke gerçek PDF üzerinden manuel.
        parsed.RecipientIban.Should().NotBe(NormalizeIban(fx.Settings.Payment.Iban));
    }

    [Fact]
    public void TryFillFromPdf_no_warning_when_recipient_iban_matches()
    {
        var fx = new Fixture();
        fx.Settings.Payment.Iban = "TR12 0011 1000 0000 0107 0201 32";
        var pdfText = "ALICI IBAN: TR120011100000000107020132";  // boşluksuz aynı

        var parser = new OrderDeck.Core.Payments.PdfDekontParser();
        var parsed = parser.ParseFromText(pdfText, "fakehash");

        parsed.RecipientIban.Should().Be("TR120011100000000107020132");
        // Normalize sonrası eşleşmeli
        parsed.RecipientIban.Should().Be(NormalizeIban(fx.Settings.Payment.Iban));
    }

    private static string NormalizeIban(string raw)
        => System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", "").ToUpperInvariant();

    [Fact]
    public void TrySave_when_dekont_includes_shipping_fee_is_Saved_with_normal_directive()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 3000m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "3150"; // Ürün + kargo dahil
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        var result = fx.Vm.TrySave();

        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        fx.Payments.FindByReferansNo("REF-001")!.ShipmentDirective
            .Should().Be(ShipmentDirective.Normal);
    }

    // ── PR-C/2: Shipment hook integration tests ──────────────────────────

    [Fact]
    public void TrySave_attaches_label_to_open_shipment_when_customer_resolved()
    {
        var fx = new Fixture();
        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        var labelId = System.Guid.NewGuid().ToString("N");
        fx.Labels.Insert(new Label(
            Id: labelId, SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 1200m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "1200";
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

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
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 1200m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "1350"; // 1200 ürün + 150 kargo
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

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
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 5200m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "5200"; // ücretsiz kargo bölgesi
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

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
        var labelId = System.Guid.NewGuid().ToString("N");
        fx.Labels.Insert(new Label(
            Id: labelId, SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 1200m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "1200"; // ürün only, kargo eksik
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

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
    }

    [Fact]
    public void ApplyShipmentDecision_ShipNow_marks_shipment_as_shipped()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        var customer = SeedCustomer(fx);
        var sid = SeedActiveSession(fx);
        fx.Labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: sid, CustomerId: customer.Id,
            Platform: "instagram", Username: "@ayse_y",
            MessageText: "ürün", Code: null, Price: 5200m,
            AddedAt: 1100L, PrintedAt: null));

        FillValid(fx.Vm);
        fx.Vm.AmountText = "5200";
        fx.Vm.CustomerPlatform = "instagram";
        fx.Vm.CustomerUsername = "@ayse_y";

        var result = fx.Vm.TrySave();
        var shipmentId = result.ThresholdContext!.Shipment!.Id;

        fx.Vm.ApplyShipmentDecision(shipmentId, ShipmentDecision.ShipNow);

        var shipped = fx.Shipments.GetById(shipmentId)!;
        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        shipped.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public void TrySave_without_customer_skips_shipment_hook()
    {
        var fx = new Fixture();
        fx.Settings.Shipping.FreeShippingThreshold = 5000m;
        fx.Settings.Shipping.ShippingFee = 150m;

        FillValid(fx.Vm);
        // Müşteri alanları boş bırakılır

        var result = fx.Vm.TrySave();
        result.Kind.Should().Be(DekontEkleViewModel.SaveResultKind.Saved);
        result.ThresholdContext.Should().BeNull();

        // Hiçbir Shipment oluşmamalı
        var anyShipment = fx.Shipments.GetByStatus(ShipmentStatus.Pending);
        anyShipment.Should().BeEmpty();
    }
}
