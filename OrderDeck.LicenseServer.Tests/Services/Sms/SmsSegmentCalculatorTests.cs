using FluentAssertions;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;
using static OrderDeck.LicenseServer.Services.Sms.SmsSegmentCalculator;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class SmsSegmentCalculatorTests
{
    [Fact]
    public void Empty_or_null_is_one_gsm7_segment()
    {
        Calculate(null).Should().Be(new Result(Encoding.Gsm7, 1));
        Calculate("").Should().Be(new Result(Encoding.Gsm7, 1));
    }

    [Fact]
    public void Short_ascii_is_single_gsm7_segment()
    {
        var r = Calculate("Merhaba dunya");
        r.Encoding.Should().Be(Encoding.Gsm7);
        r.Segments.Should().Be(1);
    }

    [Theory]
    [InlineData(160, 1)]   // tam sınır → tek
    [InlineData(161, 2)]   // sınır+1 → çok parçalı (153/parça)
    [InlineData(306, 2)]   // 2×153
    [InlineData(307, 3)]
    public void Gsm7_segment_boundaries(int length, int expectedSegments)
    {
        var msg = new string('a', length);
        var r = Calculate(msg);
        r.Encoding.Should().Be(Encoding.Gsm7);
        r.Segments.Should().Be(expectedSegments);
    }

    [Fact]
    public void Turkish_lowercase_chars_force_ucs2()
    {
        // ş ğ ı GSM-7 temel alfabede yok → UCS-2
        var r = Calculate("şğı");
        r.Encoding.Should().Be(Encoding.Ucs2);
        r.Segments.Should().Be(1);
    }

    [Theory]
    [InlineData(70, 1)]
    [InlineData(71, 2)]
    [InlineData(134, 2)]   // 2×67
    [InlineData(135, 3)]
    public void Ucs2_segment_boundaries(int turkishCharCount, int expectedSegments)
    {
        // 'ş' UCS-2 tetikler; her biri 1 UTF-16 kod birimi.
        var msg = new string('ş', turkishCharCount);
        var r = Calculate(msg);
        r.Encoding.Should().Be(Encoding.Ucs2);
        r.Segments.Should().Be(expectedSegments);
    }

    [Fact]
    public void Gsm7_extension_chars_count_as_two_septets()
    {
        // '€' genişletme tablosunda → 2 septet. 80 adet = 160 septet = tek mesaj.
        Calculate(new string('€', 80)).Should().Be(new Result(Encoding.Gsm7, 1));
        // 81 adet = 162 septet > 160 → 2 parça.
        Calculate(new string('€', 81)).Segments.Should().Be(2);
    }

    [Fact]
    public void Gsm7_basic_turkish_uppercase_stays_gsm7()
    {
        // Ç Ö Ü Ä Ñ GSM-7 temel alfabede VAR → GSM-7 kalır.
        var r = Calculate("ÇÖÜ");
        r.Encoding.Should().Be(Encoding.Gsm7);
    }

    [Fact]
    public void Segments_shortcut_matches_calculate()
    {
        Segments("şğı").Should().Be(Calculate("şğı").Segments);
        Segments(new string('a', 200)).Should().Be(2);
    }
}
