using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OrderDeck.Shared.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales.Rules;

/// <summary>
/// Ortak kurallar (<see cref="LabelRules"/>) ortak vektörlere karşı. Bu sınıf İKİ test projesinde birden koşar
/// (OrderDeck.Tests ve OrderDeck.LicenseServer.Tests; bağlantılı dosya): sunucu Linux'ta, masaüstü Windows'ta.
/// Kod karşılaştırması harf katlamasına dayandığı için iki platformun aynı sonucu vermesi de burada sabitlenir.
/// Masaüstünün bugünkü koduyla eşliği ayrıca <c>LabelRulesCoreParityTests</c> (yalnız OrderDeck.Tests) sabitler.
/// </summary>
public sealed class LabelRulesVectorTests
{
    public static IEnumerable<object[]> SameCodeNames() => Names("sameCode");
    public static IEnumerable<object[]> CodeKeyNames() => Names("codeKey");
    public static IEnumerable<object[]> ClassifyNames() => Names("classify");
    public static IEnumerable<object[]> CodePriceNames() => Names("codePrice");
    public static IEnumerable<object[]> ContributionNames() => Names("contribution");
    public static IEnumerable<object[]> PaidPriceNames() => Names("paidPriceOnCorrection");
    public static IEnumerable<object[]> OrderUnitMergeNames() => Names("orderUnitMerge");
    public static IEnumerable<object[]> ShipmentAmountNames() => Names("shipmentAmount");

    private static IEnumerable<object[]> Names(string section)
        => LabelRulesVectors.Names(section).Select(n => new object[] { n });

    [Fact]
    public void Dosyadaki_her_bolum_bu_kosucuda_kosar_ve_adlar_tekildir()
    {
        LabelRulesVectors.Sections().Should().BeEquivalentTo(LabelRulesVectors.KnownSections,
            "yeni bir kural bölümü iki test projesinde de koşucu ister");
        foreach (var section in LabelRulesVectors.KnownSections)
            LabelRulesVectors.Names(section).Should().OnlyHaveUniqueItems($"bölüm {section}")
                .And.NotBeEmpty($"bölüm {section}");
    }

    [Theory]
    [MemberData(nameof(SameCodeNames))]
    public void SameCode(string name)
    {
        var c = LabelRulesVectors.SameCode().Single(v => v.Name == name);
        LabelRules.SameCode(c.A, c.B).Should().Be(c.Expected, $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(CodeKeyNames))]
    public void CodeKey(string name)
    {
        var c = LabelRulesVectors.CodeKey().Single(v => v.Name == name);
        LabelRules.CodeKey(c.Code).Should().Be(c.Expected, $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(ClassifyNames))]
    public void Classify(string name)
    {
        var c = LabelRulesVectors.Classify().Single(v => v.Name == name);
        var statusOf = LabelRulesVectors.StatusOf(c.Shipments);
        foreach (var l in c.Labels)
            LabelRules.Classify(l.Facts, statusOf).Should().Be(l.Expected, $"vektör {name}, etiket {l.Facts.Id}");
    }

    [Theory]
    [MemberData(nameof(CodePriceNames))]
    public void CodePrice(string name)
    {
        var c = LabelRulesVectors.CodePrice().Single(v => v.Name == name);
        // Vektörün ön koşulu: etiketler sorgu kodunun etiketleri (kodla süzmek çağıranın işi).
        c.Labels.All(l => LabelRules.SameCode(l.Code, LabelRules.CodeKey(c.Code)))
            .Should().BeTrue($"vektör {name} yalnız kodun etiketlerini taşımalı");

        LabelRules.CodePrice(c.Labels, LabelRulesVectors.StatusOf(c.Shipments))
            .Should().Be(c.Expected, $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(ContributionNames))]
    public void Contribution(string name)
    {
        var c = LabelRulesVectors.Contribution().Single(v => v.Name == name);
        LabelRules.Contribution(c.Label).Should().Be((c.ExpectedCount, c.ExpectedAmount), $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(PaidPriceNames))]
    public void PaidPriceOnCorrection(string name)
    {
        var c = LabelRulesVectors.PaidPriceOnCorrection().Single(v => v.Name == name);
        LabelRules.PaidPriceOnCorrection(c.PaidPrice, c.PriceBefore, c.LinkedToShipment, c.PaidFromBalance)
            .Should().Be(c.Expected, $"vektör {name}");
    }

    [Theory]
    [MemberData(nameof(OrderUnitMergeNames))]
    public void OrderUnitMerge(string name)
    {
        var c = LabelRulesVectors.OrderUnitMerge().Single(v => v.Name == name);
        var result = Shared.Sales.OrderUnitMerge.Merge(c.Server, c.Push, c.Context);

        result.Merged.Should().Be(c.ExpectedMerged, $"vektör {name}: yazılacak değerler");
        result.ChangedUnits.Should().Equal(c.ExpectedChanged, $"vektör {name}: değişen birimler");
        result.Ignored.Should().Equal(c.ExpectedIgnored, $"vektör {name}: yok sayılan birimler");
        result.Reason.Should().Be(c.ExpectedReason, $"vektör {name}: neden");
        if (c.ExpectedStatus is { } status)
            result.Status.Should().Be(status, $"vektör {name}: satırın durumu");
    }

    [Theory]
    [MemberData(nameof(ShipmentAmountNames))]
    public void ShipmentAmount(string name)
    {
        var c = LabelRulesVectors.ShipmentAmount().Single(v => v.Name == name);
        LabelRules.DerivedShipmentAmount(c.ShipmentId, c.Labels).Should().Be(c.Expected, $"vektör {name}");
    }

    [Fact]
    public void CodePrice_fiyat_kaynagi_bos_ise_null()
        => LabelRules.CodePrice(Array.Empty<decimal>()).Should().BeNull();
}
