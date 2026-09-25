using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Ham hareket JSON'u saklanmadan önce kimlik alanları (adı IBAN/VKN/TCKN içeren) silinir (spec §7).
/// Hash + maske DTO'nun ham değerinden ÖNCE üretilir; bu yalnız saklanan kopyayı temizler.</summary>
public sealed class BankRawJsonRedactorTests
{
    [Fact]
    public void Adi_IBAN_VKN_TCKN_iceren_alanlarin_degeri_redakte_edilir_adlar_ve_diger_alanlar_kalir()
    {
        var iban = BankHasherTests.TestIban();
        var vkn = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        var tckn = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();
        var json = $$"""{"Id":"1","KarsiHesapIBAN":"{{iban}}","BorcluVKN":"{{vkn}}","AmirVKN":"","LehdarTCKN":"{{tckn}}","GonderenAdi":"Ad Soyad","Tutar":"10.00"}""";

        var redacted = BankRawJsonRedactor.Redact(json);

        redacted.Should().NotContain(iban).And.NotContain(vkn).And.NotContain(tckn);
        using var doc = JsonDocument.Parse(redacted);
        var root = doc.RootElement;
        root.GetProperty("KarsiHesapIBAN").GetString().Should().Be("[redakte]");
        root.GetProperty("BorcluVKN").GetString().Should().Be("[redakte]");
        root.GetProperty("AmirVKN").GetString().Should().Be("[redakte]", "boş değer de aynı kuralla — dallanma yok");
        root.GetProperty("LehdarTCKN").GetString().Should().Be("[redakte]");
        root.GetProperty("GonderenAdi").GetString().Should().Be("Ad Soyad", "ad zaten CounterpartyName'de");
        root.GetProperty("Id").GetString().Should().Be("1");
        root.GetProperty("Tutar").GetString().Should().Be("10.00");
    }

    [Fact]
    public void Alan_adi_buyuk_kucuk_harf_farki_gozetilmeden_eslesir()
    {
        var iban = BankHasherTests.TestIban();
        var vkn = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        var json = $$"""{"karsiIban":"{{iban}}","Vkn_No":"{{vkn}}","tckn":"x"}""";

        var redacted = BankRawJsonRedactor.Redact(json);

        using var doc = JsonDocument.Parse(redacted);
        foreach (var p in doc.RootElement.EnumerateObject())
            p.Value.GetString().Should().Be("[redakte]", $"{p.Name} kimlik alanıdır");
    }

    [Fact]
    public void Ic_ice_nesne_ve_dizilerdeki_kimlik_alanlari_da_redakte_edilir()
    {
        var iban = BankHasherTests.TestIban();
        var vkn = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        var json = $$"""{"Detay":{"KarsiHesapIBAN":"{{iban}}","Ad":"A"},"Liste":[{"AmirVKN":"{{vkn}}"},"duz",5,null,true]}""";

        var redacted = BankRawJsonRedactor.Redact(json);

        redacted.Should().NotContain(iban).And.NotContain(vkn);
        using var doc = JsonDocument.Parse(redacted);
        var root = doc.RootElement;
        root.GetProperty("Detay").GetProperty("KarsiHesapIBAN").GetString().Should().Be("[redakte]");
        root.GetProperty("Detay").GetProperty("Ad").GetString().Should().Be("A");
        var list = root.GetProperty("Liste");
        list[0].GetProperty("AmirVKN").GetString().Should().Be("[redakte]");
        list[1].GetString().Should().Be("duz");
        list[2].GetInt32().Should().Be(5);
        list[3].ValueKind.Should().Be(JsonValueKind.Null);
        list[4].GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Diger_degerlerin_JSON_turu_korunur_Turkce_karakterler_kacislanmaz()
    {
        // Saklanan kopya insan gözüyle okunur (admin tanısı): "Şükrü" Şükrü diye yazılmaz.
        var json = """{"Sayi":10.5,"Bos":null,"Evet":true,"GonderenAdi":"Şükrü Çağlar Öz"}""";

        var redacted = BankRawJsonRedactor.Redact(json);

        redacted.Should().Contain("Şükrü Çağlar Öz");
        using var doc = JsonDocument.Parse(redacted);
        var root = doc.RootElement;
        root.GetProperty("Sayi").GetDecimal().Should().Be(10.5m);
        root.GetProperty("Bos").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("Evet").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Kimlik_alani_olmayan_JSON_oldugu_gibi_kalir()
    {
        const string json = """{"Id":"1","Aciklama":"HAVALE","HesapId":"6"}""";
        BankRawJsonRedactor.Redact(json).Should().Be(json);
    }

    [Fact]
    public void Gecersiz_JSON_protokol_istisnasi()
    {
        // İstemci satırı zaten ayrıştırdı; buraya JSON olmayan metin gelmesi sözleşme ihlalidir — sessizce
        // (ham hâliyle) saklanmaz.
        var act = () => BankRawJsonRedactor.Redact("{not json");
        act.Should().Throw<ObifinProtocolException>();
    }
}
