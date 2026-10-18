using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Payments;

public sealed class PaymentMatcherServiceTests
{
    private const string SessionId = "s1";
    private const string CustomerId = "c1";

    private static (PaymentMatcherService Svc, LabelRepository Labels, CustomerRepository Customers,
                    InMemorySqlite Db, AppSettings Settings) Build(decimal? threshold = null, decimal? fee = null)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        new SessionRepository(db).Insert(
            new StreamSession(SessionId, null, 1000, null, new[] { "instagram" }, null));

        var customers = new CustomerRepository(db);
        customers.Insert(new Customer(CustomerId, "instagram", "@ayse", "Ayşe Y", null,
            FirstSeenAt: 500, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null));

        var labels = new LabelRepository(db);
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = threshold;
        settings.Shipping.ShippingFee = fee;

        var svc = new PaymentMatcherService(labels, new ShippingSettingsProvider(new SalesSyncStateRepository(db), settings));
        return (svc, labels, customers, db, settings);
    }

    [Fact]
    public void Mod_On_eslestirme_yerel_ayari_degil_lisans_geneli_kargo_ayarini_kullanir()
    {
        // Spec 2026-10-13 §6.2 (Y4): yerel 500/60'ta 600 TL eşik üstü (kargosuz); lisans geneli 1000/80'de eşik altı —
        // beklenen 680, 600'lük dekont kargo eksik.
        var (svc, labels, _, db, _) = Build(threshold: 500m, fee: 60m);
        using var _2 = db;
        labels.Insert(NewProductLabel(600m));
        var state = new SalesSyncStateRepository(db);
        state.SaveServerState(3, 1000m, 80m, scrubOrderText: false);

        svc.Match(CustomerId, SessionId, dekontAmount: 600m).Outcome
            .Should().Be(PaymentMatcherService.MatchOutcome.Match, "mod Off: yerel ayar, eşik üstü");

        using (var conn = db.Open())
            Dapper.SqlMapper.Execute(conn, "UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        state.GetMode().Should().Be(1);
        var result = svc.Match(CustomerId, SessionId, dekontAmount: 600m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.ShippingShortage);
        result.ExpectedAmount.Should().Be(680m);
        result.ShippingFee.Should().Be(80m);
    }

    private static Label NewProductLabel(decimal price, long addedAt = 1100L) =>
        new(Id: System.Guid.NewGuid().ToString("N"),
            SessionId: SessionId,
            CustomerId: CustomerId,
            Platform: "instagram",
            Username: "@ayse",
            MessageText: "Ürün",
            Code: null,
            Price: price,
            AddedAt: addedAt,
            PrintedAt: null);

    [Fact]
    public void Match_when_shipping_disabled_only_compares_to_product_total()
    {
        var (svc, labels, _, db, _) = Build(threshold: null, fee: null);
        using var _2 = db;
        labels.Insert(NewProductLabel(200m));
        labels.Insert(NewProductLabel(300m));

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 500m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Match);
        result.ProductTotal.Should().Be(500m);
        result.ExpectedAmount.Should().Be(500m);
        result.ShippingFee.Should().BeNull();
    }

    [Fact]
    public void Match_when_shipping_disabled_and_amount_differs_returns_Mismatch()
    {
        var (svc, labels, _, db, _) = Build();
        using var _2 = db;
        labels.Insert(NewProductLabel(500m));

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 450m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Mismatch);
    }

    [Fact]
    public void Match_above_threshold_treats_as_free_shipping()
    {
        var (svc, labels, _, db, _) = Build(threshold: 5000m, fee: 150m);
        using var _2 = db;
        labels.Insert(NewProductLabel(5500m));

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 5500m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Match);
        result.ExpectedAmount.Should().Be(5500m, "eşik üstü, kargo eklenmemeli");
    }

    [Fact]
    public void Match_below_threshold_with_fee_included_is_Match()
    {
        var (svc, labels, _, db, _) = Build(threshold: 5000m, fee: 150m);
        using var _2 = db;
        labels.Insert(NewProductLabel(3000m));

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 3150m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Match);
        result.ExpectedAmount.Should().Be(3150m);
        result.ShippingFee.Should().Be(150m);
    }

    [Fact]
    public void Match_below_threshold_without_fee_is_ShippingShortage()
    {
        var (svc, labels, _, db, _) = Build(threshold: 5000m, fee: 150m);
        using var _2 = db;
        labels.Insert(NewProductLabel(3000m));

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 3000m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.ShippingShortage,
            "Müşteri ürün toplamını ödedi ama kargo eksik; vendor karar versin");
        result.ProductTotal.Should().Be(3000m);
        result.ExpectedAmount.Should().Be(3150m);
        result.ShippingFee.Should().Be(150m);
    }

    [Fact]
    public void Match_below_threshold_with_random_partial_is_Mismatch()
    {
        var (svc, labels, _, db, _) = Build(threshold: 5000m, fee: 150m);
        using var _2 = db;
        labels.Insert(NewProductLabel(3000m));

        // Müşteri ne 3000 ne 3150, tamamen farklı bir tutar yatırdı
        var result = svc.Match(CustomerId, SessionId, dekontAmount: 2500m);

        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Mismatch);
    }

    [Fact]
    public void Match_excludes_cancelled_labels_from_product_total()
    {
        var (svc, labels, _, db, _) = Build(threshold: 5000m, fee: 150m);
        using var _2 = db;
        var label1 = NewProductLabel(2000m);
        var label2 = NewProductLabel(1000m);
        labels.Insert(label1);
        labels.Insert(label2);
        labels.MarkCancelled(new[] { label2.Id }, cancelledAt: 1500L, reason: "test");

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 2150m);

        // Cancelled label hariç → 2000 + 150 = 2150
        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Match);
        result.ProductTotal.Should().Be(2000m);
    }

    [Fact]
    public void Match_excludes_shipping_fee_labels_from_product_total()
    {
        var (svc, labels, customers, db, _) = Build(threshold: 5000m, fee: 150m);
        using var _2 = db;
        // Ürün label
        labels.Insert(NewProductLabel(3000m));
        // Önceden eklenmiş kargo label (PR B helper'a paralel manuel insert)
        labels.Insert(new Label(
            Id: System.Guid.NewGuid().ToString("N"),
            SessionId: SessionId, CustomerId: CustomerId,
            Platform: "instagram", Username: "@ayse",
            MessageText: "Kargo", Code: null, Price: 150m,
            AddedAt: 1200L, PrintedAt: null,
            IsShippingFee: true));

        var result = svc.Match(CustomerId, SessionId, dekontAmount: 3150m);

        // ProductTotal kargo label'ını dahil etmez = 3000
        result.ProductTotal.Should().Be(3000m);
        result.Outcome.Should().Be(PaymentMatcherService.MatchOutcome.Match);
    }
}
