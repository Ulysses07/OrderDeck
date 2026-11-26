using FluentAssertions;
using OrderDeck.Shared.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales.Rules;

/// <summary>
/// Kargo ücreti ve eşik kuralının (<see cref="ShippingRules"/>) sınır tablosu. Bu sınıf İKİ test projesinde birden koşar
/// (OrderDeck.Tests ve OrderDeck.LicenseServer.Tests; bağlantılı dosya).
/// </summary>
public sealed class ShippingRulesTests
{
    /// <summary>(durum, eşik, ücret, etkin mi)</summary>
    public static TheoryData<string, decimal?, decimal?, bool> Settings => new()
    {
        { "ikisi de dolu", 500m, 60m, true },
        { "ikisi de bir kuruş", 0.01m, 0.01m, true },
        { "ikisi de boş", null, null, false },
        { "eşik boş", null, 60m, false },
        { "ücret boş", 500m, null, false },
        { "eşik sıfır", 0m, 60m, false },
        { "ücret sıfır", 500m, 0m, false },
        { "ikisi de sıfır", 0m, 0m, false },
        { "eşik eksi", -0.01m, 60m, false },
        { "ücret eksi", 500m, -0.01m, false },
        { "ikisi de eksi", -500m, -60m, false },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void IsEnabled(string @case, decimal? threshold, decimal? fee, bool expected)
        => ShippingRules.IsEnabled(threshold, fee).Should().Be(expected, @case);

    /// <summary>(durum, tutar, eşik, ulaştı mı)</summary>
    public static TheoryData<string, decimal, decimal, bool> Amounts => new()
    {
        { "eşiğin bir kuruş altı", 499.99m, 500m, false },
        { "tam eşik", 500m, 500m, true },
        { "tam eşik, ondalık yazılmış", 500.00m, 500m, true },
        { "eşiğin bir kuruş üstü", 500.01m, 500m, true },
        { "sıfır tutar", 0m, 500m, false },
        { "kuruşlu eşik, tam", 499.99m, 499.99m, true },
    };

    [Theory]
    [MemberData(nameof(Amounts))]
    public void ReachesThreshold(string @case, decimal amount, decimal threshold, bool expected)
        => ShippingRules.ReachesThreshold(amount, threshold).Should().Be(expected, @case);

    /// <summary>(durum, eşik, ücret, ürün toplamı, alıcı ödemeli, istenecek kargo)</summary>
    public static TheoryData<string, decimal?, decimal?, decimal, bool, decimal> Fees => new()
    {
        { "eşik altı", 500m, 60m, 490m, false, 60m },
        { "eşiğin bir kuruş altı", 500m, 60m, 499.99m, false, 60m },
        { "kuruşlu ücret", 500m, 59.9m, 490m, false, 59.9m },
        { "ürün toplamı sıfır", 500m, 60m, 0m, false, 60m },
        { "tam eşik", 500m, 60m, 500m, false, 0m },
        { "eşik üstü", 500m, 60m, 500.01m, false, 0m },
        { "alıcı ödemeli, eşik altı", 500m, 60m, 490m, true, 0m },
        { "alıcı ödemeli, eşik üstü", 500m, 60m, 600m, true, 0m },
        { "alıcı ödemeli, ayar kapalı", null, null, 490m, true, 0m },
        { "ayar kapalı", null, null, 490m, false, 0m },
        { "eşik boş", null, 60m, 490m, false, 0m },
        { "ücret boş", 500m, null, 490m, false, 0m },
        { "eşik sıfır", 0m, 60m, -1m, false, 0m },
        { "ücret sıfır", 500m, 0m, 490m, false, 0m },
        { "eşik eksi", -500m, 60m, -501m, false, 0m },
        { "ücret eksi", 500m, -60m, 490m, false, 0m },
    };

    [Theory]
    [MemberData(nameof(Fees))]
    public void FeeToRequest(
        string @case, decimal? threshold, decimal? fee, decimal productTotal, bool recipientPays, decimal expected)
        => ShippingRules.FeeToRequest(threshold, fee, productTotal, recipientPays).Should().Be(expected, @case);
}
