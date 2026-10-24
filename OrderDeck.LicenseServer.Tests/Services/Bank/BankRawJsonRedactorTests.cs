using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK Ö-2: ham hareket kaydının yalnız beyaz listedeki alanları saklanır; açıklama alanları maskelenir. Değerler
/// üretilir (repo public).</summary>
public sealed class BankRawJsonRedactorTests
{
    /// <summary>Gizlilik politikasındaki "bankanın gönderdiği kaydın kopyası" listesi, metin olarak sabit. Hukuken riskli yön
    /// listeye alan EKLEMEK (metin saklananı eksik anlatır): liste değişince bu test kırılır; o zaman politikanın iki kopyası ve
    /// avukata giden metin de değişmeli (<see cref="BankRawJsonRedactor"/> özeti).</summary>
    private const string PolicyFieldList = "Aciklama,GonderenBanka,IslemAciklama,IslemKodu,MakbuzNo,ReferansNo";

    private static string Kept(string redacted)
        => string.Join(",", JsonDocument.Parse(redacted).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    [Fact]
    public void Beyaz_liste_politikadaki_alan_listesiyle_ayni()
    {
        string.Join(",", BankRawJsonRedactor.AllowedFields.Order(StringComparer.Ordinal))
            .Should().Be(PolicyFieldList, "politikadaki 'kaydın kopyası' listesiyle aynı olmalı");
    }

    [Fact]
    public void Yalniz_beyaz_listedeki_alanlar_kalir()
    {
        var iban = BankHasherTests.TestIban();
        var vkn = TestPhone.Digits(10);
        var json = $$"""
        {"Id":"1","HesapId":"6","IslemNo":"X1","Aciklama":"HAVALE ornek_musteri34","IslemAciklama":"EFT","GonderenAdi":"Ad Soyad",
         "GonderenBanka":"Banka","GonderenSube":"Sube","AliciAdi":"Alici","MusteriAciklamasi":"not","EkDetay1":"a","EkDetay2":"b",
         "EkDetay3":"c","EkDetay4":"d","Bakiye":"123.45","KarsiHesapIBAN":"{{iban}}","BorcluVKN":"{{vkn}}","IslemKodu":"FT",
         "ReferansNo":"R1","MakbuzNo":"M1","Tutar":"10.00","TutarEksiArti":"10.00"}
        """;

        var redacted = BankRawJsonRedactor.Redact(json);

        Kept(redacted).Should().Be(PolicyFieldList, "politikadaki 'kaydın kopyası' listesiyle aynı olmalı");
        redacted.Should().NotContain(iban).And.NotContain(vkn).And.NotContain("123.45").And.NotContain("Alici").And.NotContain("Sube")
            .And.NotContain("Ad Soyad", "gönderen adı kopyaya da girmez (KVKK veri küçültme)");
    }

    [Fact]
    public void Aciklama_alanlarindaki_IBAN_ve_on_bir_haneli_numara_maskelenir()
    {
        var iban = BankHasherTests.TestIban();
        var tckn = TestTckn.NewValid();
        var json = $$"""{"Aciklama":"HAVALE {{iban}}","IslemAciklama":"FAST {{tckn}}","GonderenAdi":"Ad Soyad"}""";

        var root = JsonDocument.Parse(BankRawJsonRedactor.Redact(json)).RootElement;

        root.GetProperty("Aciklama").GetString().Should().Be($"HAVALE {BankHasher.MaskIban(iban)}");
        root.GetProperty("IslemAciklama").GetString().Should().Be($"FAST {BankDescriptionMasker.ElevenDigitMask}");
        root.TryGetProperty("GonderenAdi", out _).Should().BeFalse("gönderen adı beyaz listede değil");
    }

    [Fact]
    public void Sayi_olarak_gelen_aciklamadaki_on_bir_haneli_numara_da_maskelenir()
    {
        // İstemci açıklamayı sayı gelse de metne çevirip okur (ObifinClient.Str): sütun maskelenirken kopya düz kalmasın.
        var tckn = TestTckn.NewValid();
        var json = $$"""{"Aciklama":{{tckn}},"IslemAciklama":42}""";

        var redacted = BankRawJsonRedactor.Redact(json);

        redacted.Should().NotContain(tckn);
        var root = JsonDocument.Parse(redacted).RootElement;
        root.GetProperty("Aciklama").GetString().Should().Be(BankDescriptionMasker.ElevenDigitMask);
        root.GetProperty("IslemAciklama").GetInt32().Should().Be(42, "maskelenecek bir şey yoksa sayı aynen kalır");
        BankRawJsonRedactor.Redact(redacted).Should().Be(redacted);
    }

    [Fact]
    public void Ic_ice_nesne_ve_dizi_alinmaz_null_ve_sayi_aynen_kalir()
    {
        var json = """{"Aciklama":{"x":"y"},"GonderenBanka":["a"],"ReferansNo":null,"MakbuzNo":42}""";

        var root = JsonDocument.Parse(BankRawJsonRedactor.Redact(json)).RootElement;

        root.TryGetProperty("Aciklama", out _).Should().BeFalse();
        root.TryGetProperty("GonderenBanka", out _).Should().BeFalse();
        root.GetProperty("ReferansNo").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("MakbuzNo").GetInt32().Should().Be(42);
    }

    [Fact]
    public void Turkce_karakterler_kacislanmaz_ve_islem_idempotent()
    {
        const string json = """{"Aciklama":"ÖDEME çğışü","GonderenBanka":"Güneş Bankası"}""";

        var once = BankRawJsonRedactor.Redact(json);

        once.Should().Be(json);
        BankRawJsonRedactor.Redact(once).Should().Be(once);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"metin\"")]
    public void Json_nesne_degilse_protokol_hatasi(string raw)
    {
        var act = () => BankRawJsonRedactor.Redact(raw);
        act.Should().Throw<ObifinProtocolException>();
    }
}
