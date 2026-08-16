using FluentAssertions;
using OrderDeck.Labeling;
using Xunit;

namespace OrderDeck.Tests.Labeling;

public class BarcodeLabelDocumentTests
{
    [Fact]
    public void Modul_dizisi_sessiz_bolgeyle_cevrelenir()
    {
        var modules = BarcodeLabelDocument.EncodeWithQuietZone("0000000001");

        // Sessiz bölge okuyucunun barkodun NEREDE bittiğini anlamasını
        // sağlıyor; ZXing'in encode'u onu vermiyor, biz ekliyoruz.
        modules.Take(BarcodeLabelDocument.QuietZoneModules)
            .Should().OnlyContain(m => m == false);
        modules.TakeLast(BarcodeLabelDocument.QuietZoneModules)
            .Should().OnlyContain(m => m == false);
    }

    [Fact]
    public void Ilk_cizgi_sessiz_bolgeden_hemen_sonra_baslar()
    {
        var modules = BarcodeLabelDocument.EncodeWithQuietZone("0000000001");

        modules[BarcodeLabelDocument.QuietZoneModules].Should().BeTrue();
    }

    [Fact]
    public void On_haneli_sayi_makul_genislikte()
    {
        var modules = BarcodeLabelDocument.EncodeWithQuietZone("0000000001");

        // Code128-C 10 haneyi 5 çift olarak sıkıştırıyor: start + 5 veri +
        // checksum + stop ≈ 90 modül, + 20 sessiz bölge. 60 mm etikete
        // 0.4 mm modülle (≈44 mm) rahat sığıyor.
        modules.Length.Should().BeInRange(100, 130);
    }

    [Fact]
    public void Bos_yuk_reddedilir()
    {
        var act = () => BarcodeLabelDocument.EncodeWithQuietZone("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MmToHundredths_LabelPrintDocument_ile_ayni()
    {
        // İki belge aynı yazıcıya, aynı kâğıda basıyor. Ölçü dönüşümü
        // ayrışsaydı biri kâğıda otururken diğeri kayardı.
        BarcodeLabelDocument.MmToHundredths(60)
            .Should().Be(LabelPrintDocument.MmToHundredths(60));
    }
}
