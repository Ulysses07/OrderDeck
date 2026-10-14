using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

/// <summary>
/// Ortak kuralların dosya durumu (<see cref="SalesShipmentStatus"/>) sunucunun <see cref="ShipmentStatus"/>'u ile
/// aynı ad ve değerde: sunucu kurala durumu adla ya da değerle çevirerek verir, kaymış bir enum "kargoya verildi"
/// kararını sessizce yanlış dosyaya uygulardı. Masaüstündeki karşılığı <c>LabelRulesCoreParityTests</c>'te.
/// </summary>
public sealed class SalesShipmentStatusParityTests
{
    [Fact]
    public void Sunucu_dosya_durumu_ortak_durumla_ayni_ad_ve_degerde()
    {
        Enum.GetNames<ShipmentStatus>().Should().Equal(Enum.GetNames<SalesShipmentStatus>());
        foreach (var s in Enum.GetValues<ShipmentStatus>())
            ((int)s).Should().Be((int)Enum.Parse<SalesShipmentStatus>(s.ToString()), $"durum {s}");
    }
}
