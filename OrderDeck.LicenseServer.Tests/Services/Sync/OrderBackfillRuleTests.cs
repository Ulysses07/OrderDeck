using FluentAssertions;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.Shared.Sales;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// Geri doldurmanın mevcut sipariş kuralı — spec 2026-10-13 §4.3, §8.2-3/-4, M20. Sunucuda dolu bir alan ya da damgalı
/// bir birim yazılmaz; yalnız boş yeni alanlar dolar. Birimler (fiyat, iptal, yedek, baskı) yerel değerle
/// karşılaştırılır, yazılmaz: farklı olanlar ayrışma olarak döner.
/// </summary>
public sealed class OrderBackfillRuleTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 18, 30, 0, TimeSpan.Zero);

    private static OrderUnitValues Server() => new()
    {
        Price = 100m,
        PrintedAt = At,
    };

    private static OrderServerRow Row(OrderUnitValues values) => new() { Values = values, Version = 3 };

    [Fact]
    public void Ayni_birimde_bos_yeni_alanlar_doldurulur_ayrisma_yok()
    {
        var parent = Guid.NewGuid();
        var shipment = Guid.NewGuid();
        var local = Server() with
        {
            PriceCorrectedAt = At.AddMinutes(5),
            PaidPrice = 120m,
            PrintBatchId = "b1",
            PrintSeq = 2,
            ParentOrderId = parent,
            ShipmentId = shipment,
        };

        var result = OrderBackfillRule.Apply(Row(Server()), local);

        result.Values.Should().Be(local, "boş alanların hepsi yerelden dolar");
        result.Diverged.Should().BeEmpty();
        result.DivergedStamped.Should().BeFalse();
    }

    [Fact]
    public void Dolu_alan_ezilmez()
    {
        var server = Server() with
        {
            PriceCorrectedAt = At.AddMinutes(1),
            PaidPrice = 90m,
            PrintBatchId = "sunucu",
            PrintSeq = 1,
            ParentOrderId = Guid.NewGuid(),
            ShipmentId = Guid.NewGuid(),
        };
        var local = server with
        {
            PriceCorrectedAt = At.AddMinutes(7),
            PaidPrice = 95m,
            PrintBatchId = "yerel",
            PrintSeq = 9,
            ParentOrderId = Guid.NewGuid(),
            ShipmentId = Guid.NewGuid(),
        };

        var result = OrderBackfillRule.Apply(Row(server), local);

        result.Values.Should().Be(server);
        result.Diverged.Should().BeEquivalentTo(new[] { OrderUnit.Price, OrderUnit.Print },
            "dolu birim alanı farklıysa ayrışmadır; ebeveyn ve kargo bağı birim karşılaştırmasına girmez");
    }

    /// <summary>Fiyat birimi birlikte yazılır: sunucunun fiyatı yereldekinden farklıysa düzeltme damgası ve ödenen fiyat
    /// yerelden doldurulmaz — sunucu satırı "düzeltilmiş" görünüp eski fiyatı taşımasın. Fark ayrışmadır.</summary>
    [Fact]
    public void Fiyat_farkliysa_fiyat_biriminin_yeni_alanlari_doldurulmaz()
    {
        var local = Server() with { Price = 80m, PriceCorrectedAt = At.AddMinutes(5), PaidPrice = 100m };

        var result = OrderBackfillRule.Apply(Row(Server()), local);

        result.Values.PriceCorrectedAt.Should().BeNull();
        result.Values.PaidPrice.Should().BeNull();
        result.Values.Price.Should().Be(100m, "birim yazılmaz");
        result.Diverged.Should().Equal(OrderUnit.Price);
        result.DivergedStamped.Should().BeFalse();
    }

    [Fact]
    public void Damgali_fiyat_biriminin_yeni_alanlari_doldurulmaz_ayrisma_damgali()
    {
        var local = Server() with { PriceCorrectedAt = At.AddMinutes(5), PaidPrice = 100m };

        var result = OrderBackfillRule.Apply(Row(Server()) with { PriceSetAt = At.AddHours(1) }, local);

        result.Values.PriceCorrectedAt.Should().BeNull();
        result.Values.PaidPrice.Should().BeNull();
        result.Diverged.Should().Equal(OrderUnit.Price);
        result.DivergedStamped.Should().BeTrue();
    }

    /// <summary>K3'ün kuralı: baskı grubu yalnız sunucuda basılmış satıra dolar. Sunucuda basılmamış, yerelde basılmış
    /// satır (F05 öncesi kayıp baskı) ayrışmadır; baskı grubu da yazılmaz.</summary>
    [Fact]
    public void Baski_grubu_yalniz_basilmis_satira_dolar()
    {
        var server = Server() with { PrintedAt = null };
        var local = server with { PrintedAt = At, PrintBatchId = "b1", PrintSeq = 1 };

        var result = OrderBackfillRule.Apply(Row(server), local);

        result.Values.Should().Be(server);
        result.Diverged.Should().Equal(OrderUnit.Print);
    }

    [Fact]
    public void Kargo_bagi_yalniz_bos_ve_damgasizsa_dolar()
    {
        var local = Server() with { ShipmentId = Guid.NewGuid() };

        OrderBackfillRule.Apply(Row(Server()) with { ShipmentSetAt = At }, local)
            .Values.ShipmentId.Should().BeNull("komutun dokunduğu bağ yalnız komutla değişir");
        OrderBackfillRule.Apply(Row(Server()), local)
            .Values.ShipmentId.Should().Be(local.ShipmentId);
    }

    [Fact]
    public void Iptal_ve_yedek_ayrismasi_damgayla_birlikte_doner()
    {
        var local = Server() with { CancelledAt = At.AddHours(1), CancelReason = "vazgeçti" };

        var unstamped = OrderBackfillRule.Apply(Row(Server()), local);
        unstamped.Diverged.Should().Equal(OrderUnit.Cancel);
        unstamped.DivergedStamped.Should().BeFalse();
        unstamped.Values.CancelledAt.Should().BeNull("birim yazılmaz");

        var stamped = OrderBackfillRule.Apply(Row(Server()) with { CancelSetAt = At.AddHours(2) }, local);
        stamped.DivergedStamped.Should().BeTrue();

        var backup = OrderBackfillRule.Apply(
            Row(Server() with { IsTentativeBackup = true }) with { BackupSetAt = At }, Server());
        backup.Diverged.Should().Equal(OrderUnit.Backup);
        backup.DivergedStamped.Should().BeTrue();
    }

    /// <summary>Damga yalnız ayrışan birime bakar: başka birimin damgası ayrışmayı damgalı yapmaz.</summary>
    [Fact]
    public void Ayrismayan_birimin_damgasi_sayilmaz()
    {
        var local = Server() with { CancelledAt = At.AddHours(1) };

        var result = OrderBackfillRule.Apply(Row(Server()) with { PriceSetAt = At }, local);

        result.Diverged.Should().Equal(OrderUnit.Cancel);
        result.DivergedStamped.Should().BeFalse();
    }

    /// <summary>Masaüstü zamanı Unix saniyesi olarak saklar; sunucunun saniye altı kesri ayrışma değildir
    /// (<see cref="OrderUnitMerge"/> ile aynı karşılaştırma).</summary>
    [Fact]
    public void Zaman_damgalari_saniyeye_yuvarlanarak_karsilastirilir()
    {
        var server = Server() with { PrintedAt = At.AddMilliseconds(400), CancelledAt = At.AddMilliseconds(900) };
        var local = Server() with { PrintedAt = At, CancelledAt = At };

        OrderBackfillRule.Apply(Row(server), local).Diverged.Should().BeEmpty();
        OrderBackfillRule.Apply(Row(server), local with { CancelledAt = At.AddSeconds(1) })
            .Diverged.Should().Equal(OrderUnit.Cancel);
    }
}
