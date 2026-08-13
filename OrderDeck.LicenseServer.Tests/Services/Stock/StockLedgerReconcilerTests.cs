using FluentAssertions;
using OrderDeck.LicenseServer.Services.Stock;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Stock;

public class StockLedgerReconcilerTests
{
    private static readonly Guid P = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid V1 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid V2 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static LedgerOrderState Order(
        Guid? productId = null,
        Guid? variantId = null,
        bool shippingFee = false,
        bool cancelled = false,
        bool tentative = false)
        => new(Guid.NewGuid(), productId ?? P, variantId, shippingFee, cancelled, tentative);

    private static Dictionary<StockKey, int> None() => new();

    [Fact]
    public void New_sale_emits_minus_one_at_variant_level()
    {
        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), None());

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
        });
    }

    [Fact]
    public void New_sale_without_variant_deducts_at_product_level()
    {
        var deltas = StockLedgerReconciler.Reconcile(Order(), None());

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, null), -1),
        });
    }

    [Fact]
    public void Repush_of_already_recorded_sale_emits_nothing()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), existing);

        deltas.Should().BeEmpty();
    }

    [Fact]
    public void Cancelling_a_recorded_sale_emits_plus_one()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(
            Order(variantId: V1, cancelled: true), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), 1),
        });
    }

    [Fact]
    public void Uncancelling_emits_minus_one_again()
    {
        // İptal sonrası defter: -1 (satış) + 1 (iptal) = 0
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = 0 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
        });
    }

    [Fact]
    public void Shipping_fee_row_never_touches_stock()
    {
        StockLedgerReconciler.Reconcile(Order(shippingFee: true), None())
            .Should().BeEmpty();
    }

    [Fact]
    public void Tentative_backup_writes_no_movement()
    {
        StockLedgerReconciler.Reconcile(Order(variantId: V1, tentative: true), None())
            .Should().BeEmpty();
    }

    [Fact]
    public void Promoting_a_tentative_backup_emits_the_sale()
    {
        // Yedek onaylandı: artık tentative değil, ilk kez düşülüyor.
        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), None());

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
        });
    }

    [Fact]
    public void Order_without_product_emits_nothing()
    {
        var order = new LedgerOrderState(Guid.NewGuid(), null, null, false, false, false);

        StockLedgerReconciler.Reconcile(order, None()).Should().BeEmpty();
    }

    [Fact]
    public void Rebinding_to_another_variant_returns_the_old_and_takes_the_new()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V2), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V2), -1),
            new LedgerDelta(new StockKey(P, V1), 1),
        });
    }

    [Fact]
    public void Binding_a_variant_to_a_product_level_sale_moves_the_deduction_down()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, null)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
            new LedgerDelta(new StockKey(P, null), 1),
        });
    }

    [Fact]
    public void Cancelled_order_with_already_balanced_ledger_emits_nothing()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = 0 };

        StockLedgerReconciler.Reconcile(Order(variantId: V1, cancelled: true), existing)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Reconcile_is_idempotent_when_applied_repeatedly(int rounds)
    {
        var ledger = new Dictionary<StockKey, int>();
        var order = Order(variantId: V1);

        for (var i = 0; i < rounds; i++)
        {
            foreach (var d in StockLedgerReconciler.Reconcile(order, ledger))
            {
                ledger[d.Key] = ledger.TryGetValue(d.Key, out var cur)
                    ? cur + d.QuantityDelta
                    : d.QuantityDelta;
            }
        }

        ledger.Should().BeEquivalentTo(
            new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 });
    }
}
