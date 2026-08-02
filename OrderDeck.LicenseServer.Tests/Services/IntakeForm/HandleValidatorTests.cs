using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// Kayıt formuna girilen kullanıcı adlarının platform kurallarına uyduğunu
/// doğrular. NEDEN: Instagram scraper'ı sohbet yazarını
/// <c>^@?[a-z0-9._]+$</c> ile süzüyor; forma "Musa Sevinç" gibi bir değer
/// girilirse kayıt sohbetteki kişiyle hiçbir zaman eşleşemez. Prod verisinde
/// 924 Instagram kaydının 29'u bu yüzden ölü kalmıştı.
/// </summary>
public class HandleValidatorTests
{
    [Theory]
    [InlineData("bilalcanli", "bilalcanli")]
    [InlineData("  bilalcanli  ", "bilalcanli")]
    [InlineData("@bilalcanli", "bilalcanli")]
    [InlineData("@@bilalcanli", "bilalcanli")]
    [InlineData("@ bilalcanli", "bilalcanli")]
    public void Normalize_strips_at_prefix_and_outer_whitespace(string raw, string expected)
        => HandleValidator.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@")]
    public void Normalize_maps_empty_input_to_null(string? raw)
        => HandleValidator.Normalize(raw).Should().BeNull();

    /// <summary>
    /// İç boşluk KORUNMALI. Sessizce silinirse "Musa Sevinc" → "MusaSevinc"
    /// olur; bu geçerli görünen ama var olmayan bir hesaba işaret eder ve
    /// kullanıcı hatasını hiç fark etmez.
    /// </summary>
    [Fact]
    public void Normalize_keeps_inner_whitespace_so_validation_can_reject_it()
    {
        var normalized = HandleValidator.Normalize("Musa Sevinç");

        normalized.Should().Be("Musa Sevinç");
        HandleValidator.Validate(HandleValidator.Instagram, normalized)
            .Should().Contain("boşluk");
    }

    [Theory]
    [InlineData("bilalcanli")]
    [InlineData("bilal.canli")]
    [InlineData("bilal_canli")]
    [InlineData("Bilal123")]
    public void Instagram_accepts_valid_handles(string handle)
        => HandleValidator.Validate(HandleValidator.Instagram, handle).Should().BeNull();

    [Theory]
    [InlineData("Musa Sevinç")]      // ad soyad — asıl şikâyet
    [InlineData("musasevinç")]       // Türkçe karakter
    [InlineData("musa@gmail.com")]   // e-posta
    [InlineData("instagram.com/musa")] // profil bağlantısı
    [InlineData(".musa")]            // nokta ile başlıyor
    [InlineData("musa.")]            // nokta ile bitiyor
    [InlineData("mu..sa")]           // art arda nokta
    [InlineData("musa-sevinc")]      // Instagram tireye izin vermez
    public void Instagram_rejects_invalid_handles(string handle)
        => HandleValidator.Validate(HandleValidator.Instagram, handle).Should().NotBeNull();

    [Fact]
    public void Instagram_rejects_handle_longer_than_30_chars()
        => HandleValidator.Validate(HandleValidator.Instagram, new string('a', 31))
            .Should().NotBeNull();

    [Theory]
    [InlineData("edanurs")]
    [InlineData("eda.nur")]
    [InlineData("eda_nur")]
    public void TikTok_accepts_valid_handles(string handle)
        => HandleValidator.Validate(HandleValidator.TikTok, handle).Should().BeNull();

    [Theory]
    [InlineData("a")]                // 2 karakterden kısa
    [InlineData("_eda")]             // alt çizgi ile başlıyor
    [InlineData("eda_")]             // alt çizgi ile bitiyor
    [InlineData("eda-nur")]          // TikTok tireye izin vermez
    [InlineData("Eda Nur")]
    public void TikTok_rejects_invalid_handles(string handle)
        => HandleValidator.Validate(HandleValidator.TikTok, handle).Should().NotBeNull();

    [Fact]
    public void TikTok_rejects_handle_longer_than_24_chars()
        => HandleValidator.Validate(HandleValidator.TikTok, new string('a', 25))
            .Should().NotBeNull();

    [Theory]
    [InlineData("orderdeck")]
    [InlineData("order-deck")]       // YouTube tireye izin verir
    [InlineData("order.deck")]
    public void YouTube_accepts_valid_handles(string handle)
        => HandleValidator.Validate(HandleValidator.YouTube, handle).Should().BeNull();

    [Theory]
    [InlineData("ab")]               // 3 karakterden kısa
    [InlineData("-orderdeck")]
    [InlineData("orderdeck-")]
    [InlineData("order deck")]
    public void YouTube_rejects_invalid_handles(string handle)
        => HandleValidator.Validate(HandleValidator.YouTube, handle).Should().NotBeNull();

    /// <summary>
    /// Facebook BİLEREK serbest: canlı yorumlarda DOM'dan gelen şey kullanıcı
    /// adı değil, kişinin görünen adı. Prod'daki 80 Facebook kaydının 37'sinde
    /// boşluk, 46'sında Türkçe karakter var ve bunlar DOĞRU veri — buraya
    /// handle kuralı koymak Facebook eşleşmesini tamamen kırar.
    /// </summary>
    [Theory]
    [InlineData("Musa Sevinç")]
    [InlineData("musa.sevinc")]
    [InlineData("Ayşe Öztürk")]
    public void Facebook_accepts_display_names(string handle)
        => HandleValidator.Validate(HandleValidator.Facebook, handle).Should().BeNull();

    [Fact]
    public void Facebook_still_rejects_over_64_chars()
        => HandleValidator.Validate(HandleValidator.Facebook, new string('a', 65))
            .Should().NotBeNull();

    [Theory]
    [InlineData(HandleValidator.Instagram)]
    [InlineData(HandleValidator.TikTok)]
    [InlineData(HandleValidator.YouTube)]
    [InlineData(HandleValidator.Facebook)]
    public void Empty_handle_is_valid_because_platform_is_simply_not_filled(string platform)
    {
        HandleValidator.Validate(platform, null).Should().BeNull();
        HandleValidator.Validate(platform, "").Should().BeNull();
    }
}
