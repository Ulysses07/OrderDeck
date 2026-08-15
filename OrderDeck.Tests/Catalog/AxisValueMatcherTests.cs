using FluentAssertions;
using OrderDeck.Core.Catalog;
using Xunit;

namespace OrderDeck.Tests.Catalog;

public class AxisValueMatcherTests
{
    private static readonly string[] Bedenler = { "S", "M", "L", "XL" };

    private static AxisMatchResult M(string comment, string code = "Ateş", string[]? values = null)
        => AxisValueMatcher.Match(comment, code, values ?? Bedenler);

    // --- Kabul kriteri 5 ---
    [Fact]
    public void Tek_tam_eslesme_cekmece_actirmaz()
    {
        var r = M("ateş m");

        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("M");
        r.NeedsPicker.Should().BeFalse();
    }

    // --- Kabul kriteri 6: substring eşleştirme YOK ---
    [Fact]
    public void Xl_tek_eslesmedir_x_ve_l_diye_bolunmez()
    {
        // Eksende "L" değeri "XL"den ÖNCE geliyor; substring arasaydık "XL"
        // içindeki "L" önce yakalanır ve yanlış beden yazılırdı.
        var r = M("ateş xl");

        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("XL");
        r.NeedsPicker.Should().BeFalse();
    }

    // --- Kabul kriteri 7 ---
    [Fact]
    public void Bitisik_yazim_kombinasyon_olarak_cozulur()
    {
        // Kombinasyon TAHMİNDİR; operatör onaylamadan sipariş yazılmaz.
        var r = M("ateş ml");

        r.Kind.Should().Be(AxisMatchKind.Combination);
        r.Values.Should().Equal("M", "L");
        r.NeedsPicker.Should().BeTrue();
    }

    // --- Kabul kriteri 8 ---
    [Fact]
    public void Hicbir_sey_eslesmezse_bos_sonuc_doner()
    {
        var r = M("bana da");

        r.Kind.Should().Be(AxisMatchKind.None);
        r.Values.Should().BeEmpty();
        r.NeedsPicker.Should().BeTrue();
    }

    // --- Kabul kriteri 9 ---
    [Fact]
    public void Iki_ayri_token_iki_deger_verir()
    {
        var r = M("ateş m l");

        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("M", "L");
        // Birden çok değer → hangisi olduğunu bilemeyiz, onay istenir.
        r.NeedsPicker.Should().BeTrue();

        // Sonuç EKSEN sırasında çıkar, yorumdaki yazım sırasında değil.
        M("ateş l m").Values.Should().Equal("M", "L");
    }

    [Fact]
    public void Tek_degere_inen_kombinasyon_bile_onay_ister()
    {
        // "mm" → M + M → tekilleşip tek değere iniyor, ama yine de TAHMİN;
        // operatör "M" mi yoksa yazım hatası mı bilmiyoruz.
        var r = M("ateş mm");

        r.Kind.Should().Be(AxisMatchKind.Combination);
        r.Values.Should().Equal("M");
        r.NeedsPicker.Should().BeTrue();
    }

    [Fact]
    public void Belirsiz_bolme_hicbir_seyi_isaretlemez()
    {
        // "LSM" hem L+S+M hem LS+M diye bölünebiliyor → tahmin etmeyiz.
        var r = M("ateş lsm", values: new[] { "S", "M", "L", "LS" });

        r.Kind.Should().Be(AxisMatchKind.None);
        r.Values.Should().BeEmpty();
    }

    [Fact]
    public void Dolgu_kelimeleri_atilir()
    {
        M("ateş beden m").Values.Should().Equal("M");
        M("ateş bedeni m").Values.Should().Equal("M");
        M("ateş numara 38", values: new[] { "36", "38" }).Values.Should().Equal("38");
    }

    [Fact]
    public void Es_anlamlilar_tam_tarama_basarisiz_olunca_devreye_girer()
    {
        M("ateş medium").Values.Should().Equal("M");
        M("ateş küçük").Values.Should().Equal("S");
        M("ateş büyük").Values.Should().Equal("L");
    }

    [Fact]
    public void Eksende_ORTA_varsa_es_anlamli_devreye_girmez()
    {
        // Eş anlamlı geçişi tam taramadan SONRA çalışıyor; aksi hâlde ekseninde
        // gerçekten "Orta" yazan ürün eşleşmeyi kaybederdi.
        var r = M("ateş orta", values: new[] { "Orta", "Büyük" });

        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("Orta");
    }

    [Fact]
    public void Cok_kelimeli_yayin_kodu_metinden_silinir()
    {
        AxisValueMatcher.Match("güzel elbise m", "Güzel Elbise", Bedenler)
            .Values.Should().Equal("M");
    }

    [Fact]
    public void Cok_kelimeli_eksen_degeri_eslesir()
    {
        var r = M("ateş 50 ml", values: new[] { "50 ML", "100 ML" });

        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("50 ML");
    }

    [Fact]
    public void Noktalama_ve_turkce_harf_onemsiz()
    {
        AxisValueMatcher.Match("ATEŞ, m!", "ateş", Bedenler)
            .Values.Should().Equal("M");
    }

    [Fact]
    public void Tireli_deger_bozulmaz()
    {
        // Noktalama token'ın yalnız UCUNDAN kırpılıyor; içindeki tire duruyor.
        M("ateş 36-38", values: new[] { "36-38", "40-42" })
            .Values.Should().Equal("36-38");
    }

    [Fact]
    public void Eksen_degeri_yoksa_bos_doner()
    {
        AxisValueMatcher.Match("ateş m", "Ateş", Array.Empty<string>())
            .Kind.Should().Be(AxisMatchKind.None);
    }

    [Fact]
    public void Cozulemeyen_kelime_kombinasyonu_iptal_etmez()
    {
        // Gerçek sohbet nezaket sözcükleriyle dolu. "lütfen" hiçbir bedene
        // bölünemiyor ama bu, bir önceki token'da bulunan M+L'yi çöpe atmayı
        // gerektirmez — bölünemeyen kelime yalnızca sohbet gürültüsüdür.
        // Belirsizlik ise başka bir şey: orada YANLIŞ beden seçme riski var.
        var r = M("ateş ml lütfen");

        r.Kind.Should().Be(AxisMatchKind.Combination);
        r.Values.Should().Equal("M", "L");
    }
}
