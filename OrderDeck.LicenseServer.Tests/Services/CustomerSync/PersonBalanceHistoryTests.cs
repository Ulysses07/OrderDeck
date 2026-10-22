using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary><see cref="PersonBalance.AggregateHistory"/> — spec 2026-10-11 §4.6 toplama kuralı (saf).</summary>
public sealed class PersonBalanceHistoryTests
{
    private static readonly Guid License = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static CustomerBalanceTransaction Row(
        Guid row, decimal amount, string kind, Guid? group = null, Guid? id = null, string? reason = null,
        decimal? original = null, DateTimeOffset? at = null) => new()
    {
        Id = id ?? Guid.NewGuid(), LicenseId = License, WpfCustomerId = row, Amount = amount, Kind = kind,
        DrawGroupId = group, Reason = reason, OriginalAmount = original, CreatedAt = at ?? At,
    };

    [Fact]
    public void Ayni_gruptaki_bu_satirdaki_bacaklar_tek_satir_olur_alanlar_capadan()
    {
        var a = Guid.NewGuid();
        var other = Guid.NewGuid();
        var anchorId = Guid.NewGuid();
        var anchor = Row(other, -10m, "purchase-deduction", anchorId, anchorId, original: 120m);
        var leg1 = Row(a, -30m, "purchase-deduction", anchorId);
        var leg2 = Row(a, -5m, "purchase-deduction", anchorId);
        var refund = Row(a, 50m, "refund-full", reason: "iade", at: At.AddMinutes(-1));

        var list = PersonBalance.AggregateHistory(
            [leg1, leg2, refund], new Dictionary<Guid, CustomerBalanceTransaction> { [anchorId] = anchor, [refund.Id] = refund });

        list.Should().HaveCount(2);
        list[0].Should().Be(new PersonBalance.HistoryEntry(
            anchorId, other, -35m, "purchase-deduction", 120m, null, null, null, At));
        list[1].Should().Be(new PersonBalance.HistoryEntry(
            refund.Id, a, 50m, "refund-full", null, null, "iade", null, At.AddMinutes(-1)));
        list.Sum(e => e.Amount).Should().Be(15m, "listedeki tutarlar satırın bakiyesini verir");
    }

    [Fact]
    public void Capasi_bulunmayan_yetim_bacak_kendi_alanlariyla_listelenir()
    {
        var a = Guid.NewGuid();
        var missingAnchor = Guid.NewGuid();
        var orphan = Row(a, -7m, "purchase-deduction", missingAnchor);

        var list = PersonBalance.AggregateHistory([orphan], new Dictionary<Guid, CustomerBalanceTransaction>());

        list.Should().ContainSingle().Which.Should().Be(new PersonBalance.HistoryEntry(
            missingAnchor, a, -7m, "purchase-deduction", null, null, null, null, At));
    }
}
