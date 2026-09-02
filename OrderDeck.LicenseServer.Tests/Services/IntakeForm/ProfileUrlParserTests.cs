using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// Müşteri "kullanıcı adı" kutusuna çoğu zaman profil ADRESİNİ yapıştırıyor.
/// HandleValidator bunu bugün reddediyor ("sadece kullanıcı adını yaz"), yani
/// müşteri elle kırpmak zorunda kalıyor ve orada yanlış yazıyor. Parser adresi
/// kabul edip handle'ı kendisi çıkarır; çıkaramadığında ne yapılacağını söyler.
/// </summary>
public sealed class ProfileUrlParserTests
{
    /// <summary>Adres olmayan girdi olduğu gibi geçer — HandleValidator'ın işi bozulmasın.</summary>
    [Theory]
    [InlineData("bilalcanli")]
    [InlineData("@bilalcanli")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Duz_kullanici_adi_degistirilmeden_gecer(string? raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(raw);
    }

    /// <summary>YouTube'un @ biçimi: şema, www./m. öneki, sondaki yol ve sorgu atılır.</summary>
    [Theory]
    [InlineData("https://www.youtube.com/@orderdeck", "orderdeck")]
    [InlineData("http://youtube.com/@orderdeck", "orderdeck")]
    [InlineData("youtube.com/@orderdeck", "orderdeck")]
    [InlineData("www.youtube.com/@orderdeck/", "orderdeck")]
    [InlineData("https://m.youtube.com/@orderdeck/videos", "orderdeck")]
    [InlineData("https://www.youtube.com/@orderdeck?si=abc", "orderdeck")]
    public void YouTube_handle_adresinden_handle_cikar(string raw, string expected)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(expected);
    }

    /// <summary>
    /// /channel/UC… biçimi kanal kimliğini DOĞRUDAN veriyor. API'ye gitmeye gerek yok:
    /// eşleştirmede kullandığımız değer zaten bu. Yanlış yazılmış bir UC… hiçbir
    /// kanala denk gelmez, yani sessizce bir yabancıya bağlanma riski yok.
    /// </summary>
    [Theory]
    [InlineData("https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv")]
    [InlineData("youtube.com/channel/UCabcdefghijklmnopqrstuv/")]
    [InlineData("https://m.youtube.com/channel/UCabcdefghijklmnopqrstuv?si=x")]
    public void YouTube_channel_adresi_kanal_kimligi_olarak_donser(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.YouTubeChannelId);
        r.Value.Should().Be("UCabcdefghijklmnopqrstuv");
    }

    /// <summary>UC + 22 karakter dışındaki her şey kanal kimliği değildir.</summary>
    [Theory]
    [InlineData("https://www.youtube.com/channel/UCkisa")]
    [InlineData("https://www.youtube.com/channel/XXabcdefghijklmnopqrstuv")]
    public void YouTube_bozuk_kanal_kimligi_reddedilir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// /c/ ve /user/ eski biçimler; handle'a çevrilemiyorlar (API'de karşılığı yok).
    /// youtu.be bir VİDEO adresi, kanal değil. Üçünde de yapılacak iş aynı:
    /// müşteriyi kanal sayfasındaki @ adresine yönlendir.
    /// </summary>
    [Theory]
    [InlineData("https://www.youtube.com/c/OrderDeck")]
    [InlineData("https://www.youtube.com/user/OrderDeck")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void YouTube_cozulemeyen_adresler_yonlendirici_hata_verir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.YouTube, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().Contain("@");
    }

    /// <summary>Instagram: ?igsh= paylaşım eki ve sondaki eğik çizgi atılır.</summary>
    [Theory]
    [InlineData("https://instagram.com/bilalcanli", "bilalcanli")]
    [InlineData("https://www.instagram.com/bilalcanli/", "bilalcanli")]
    [InlineData("https://instagram.com/bilalcanli?igsh=MWx5", "bilalcanli")]
    [InlineData("instagram.com/bilalcanli", "bilalcanli")]
    public void Instagram_profil_adresinden_handle_cikar(string raw, string expected)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Instagram, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(expected);
    }

    /// <summary>
    /// Gönderi/reel/hikâye adresi profil DEĞİL — içindeki kod kullanıcı adı sanılırsa
    /// kayıt tamamen alakasız bir değere bağlanır. Ret.
    /// </summary>
    [Theory]
    [InlineData("https://instagram.com/p/Cxyz123")]
    [InlineData("https://www.instagram.com/reel/Cxyz123")]
    [InlineData("https://instagram.com/stories/bilalcanli/123456")]
    [InlineData("https://instagram.com/explore/tags/moda")]
    public void Instagram_gonderi_adresi_reddedilir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Instagram, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>TikTok: yol @ ile başlamalı; sondaki /video/… kırpılır.</summary>
    [Theory]
    [InlineData("https://www.tiktok.com/@edanur", "edanur")]
    [InlineData("https://tiktok.com/@edanur/video/7412345678901234567", "edanur")]
    [InlineData("tiktok.com/@edanur?lang=tr", "edanur")]
    public void TikTok_profil_adresinden_handle_cikar(string raw, string expected)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.TikTok, raw);

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be(expected);
    }

    /// <summary>
    /// vm./vt. kısa linkleri hedefi ancak HTTP isteğiyle açılır. Herkese açık bir
    /// formdan dışarı istek atmıyoruz (SSRF yüzeyi + yavaşlık). Müşteriye linki
    /// tarayıcıda açıp adres çubuğundakini yapıştırmasını söylüyoruz.
    /// </summary>
    [Theory]
    [InlineData("https://vm.tiktok.com/ZMabc123/")]
    [InlineData("https://vt.tiktok.com/ZSabc123/")]
    public void TikTok_kisa_link_cozulmez_ve_yonlendirici_hata_verir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.TikTok, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().Contain("adres çubuğ");
    }

    /// <summary>TikTok'ta @ olmayan yol profil değil (keşfet, etiket, müzik sayfası).</summary>
    [Theory]
    [InlineData("https://www.tiktok.com/tag/moda")]
    [InlineData("https://www.tiktok.com/foryou")]
    public void TikTok_profil_olmayan_adres_reddedilir(string raw)
    {
        var r = ProfileUrlParser.Parse(HandleValidator.TikTok, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Yanlış kutuya yapıştırma sık: Instagram kutusuna YouTube adresi. Sessizce
    /// handle çıkarsak kayıt yanlış platforma yazılır — hangi kutuya ait olduğunu söyle.
    /// </summary>
    [Theory]
    [InlineData(HandleValidator.Instagram, "https://www.youtube.com/@orderdeck", "YouTube")]
    [InlineData(HandleValidator.YouTube, "https://www.instagram.com/bilalcanli", "Instagram")]
    [InlineData(HandleValidator.TikTok, "https://www.instagram.com/bilalcanli", "Instagram")]
    public void Yanlis_kutuya_yapistirilan_adres_dogru_kutuyu_soyler(
        string platform, string raw, string expectedPlatformName)
    {
        var r = ProfileUrlParser.Parse(platform, raw);

        r.Kind.Should().Be(ProfileInputKind.Error);
        r.Error.Should().Contain(expectedPlatformName);
    }

    /// <summary>
    /// Tanımadığımız bir adres parser'a takılmaz; olduğu gibi geçer ve
    /// HandleValidator'ın mevcut "sadece kullanıcı adını yaz" mesajına düşer.
    /// Tek hata mesajı, tek yer.
    /// </summary>
    [Fact]
    public void Bilinmeyen_alan_adi_oldugu_gibi_gecer()
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Instagram, "https://ornek.com/bilal");

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be("https://ornek.com/bilal");
    }

    /// <summary>Facebook parser'a hiç girmiyor: FB eşleşmesi ada dayalı, elle girdi doğru.</summary>
    [Fact]
    public void Facebook_platformu_girdiyi_degistirmez()
    {
        var r = ProfileUrlParser.Parse(HandleValidator.Facebook, "https://facebook.com/bilal.canli");

        r.Kind.Should().Be(ProfileInputKind.Handle);
        r.Value.Should().Be("https://facebook.com/bilal.canli");
    }
}
