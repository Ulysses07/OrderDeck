using FluentAssertions;
using OrderDeck.App.Services.Sync;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>
/// Satış komutlarının Türkçe metinleri (spec 2026-10-13 §5.3, §5.7; W2-04): makine adı doğru bulunma ekiyle, saat yerel
/// SS:DD, bilinmeyen makine ya da saat cümleyi bozmaz.
/// </summary>
public sealed class SalesCommandMessagesTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.CreateCustomTimeZone("tr-test", TimeSpan.FromHours(3), "tr", "tr");
    private static readonly DateTimeOffset At = new(2026, 10, 10, 18, 14, 0, TimeSpan.Zero);   // 21:14 TR

    [Theory]
    [InlineData("PC-A", "PC-A'da")]
    [InlineData("PC-B", "PC-B'de")]
    [InlineData("PC", "PC'de")]
    [InlineData("KASA", "KASA'da")]
    [InlineData("SALON", "SALON'da")]
    [InlineData("OFIS", "OFIS'te")]
    [InlineData("DESKTOP", "DESKTOP'ta")]
    [InlineData("DESKTOP-4F2KJ3", "DESKTOP-4F2KJ3'te")]
    [InlineData("KASA-1", "KASA-1'de")]
    [InlineData("PC6", "PC6'da")]
    [InlineData("KASA-10", "KASA-10'da")]
    [InlineData("PC-40", "PC-40'ta")]
    [InlineData("PC-70", "PC-70'te")]
    [InlineData("PC-100", "PC-100'de")]
    [InlineData("PC-0", "PC-0'da")]
    [InlineData("PC-X", "PC-X'te")]
    [InlineData("depo", "depo'da")]
    [InlineData("ÇİÇEK", "ÇİÇEK'te")]
    public void Makine_adinin_bulunma_eki_son_hecenin_sesine_uyar(string machine, string expected)
        => SalesCommandMessages.Locative(machine).Should().Be(expected);

    [Fact]
    public void Oturum_canli_metni_makineyi_ve_yerel_saati_yazar()
    {
        SalesCommandMessages.SessionLive("PC-A", At, Istanbul).Should().Be("Bu yayın PC-A'da canlı (son sinyal 21:14)");
        SalesCommandMessages.SessionLive("PC-A", null, Istanbul).Should().Be("Bu yayın PC-A'da canlı");
        SalesCommandMessages.SessionLive(null, At, Istanbul).Should().Be("Bu yayın başka bir bilgisayarda canlı (son sinyal 21:14)");
    }

    [Fact]
    public void Sahip_sinyal_veriyor_metni()
    {
        SalesCommandMessages.OwnerActive("PC-A", At, Istanbul)
            .Should().Be("PC-A hâlâ sinyal veriyor (son 21:14); yayın buradan bitirilemez.");
        SalesCommandMessages.OwnerActive(null, null, Istanbul)
            .Should().Be("Yayın bilgisayarı hâlâ sinyal veriyor; yayın buradan bitirilemez.");
    }

    [Fact]
    public void Devrediliyor_metni_sayiyi_ve_makineyi_yazar()
    {
        SalesCommandMessages.HandingOver("PC-A", 3).Should().Be(
            "PC-A'da yayın bitti ama 3 etiket henüz gönderilmedi. PC-A açılıp internete bağlanınca yayın buradan düzenlenebilir olur.");
        SalesCommandMessages.HandingOver(null, null).Should().Be(
            "Yayın bitti ama etiketleri henüz gönderilmedi. Yayın bilgisayarı açılıp internete bağlanınca yayın buradan düzenlenebilir olur.");
    }

    [Fact]
    public void Cok_fazla_etiket_metni()
    {
        SalesCommandMessages.TooManyCandidates(12001)
            .Should().Be("Bu işlem çok fazla etiketi kapsıyor (12001); destek ile iletişime geç.");
        SalesCommandMessages.TooManyCandidates(null)
            .Should().Be("Bu işlem çok fazla etiketi kapsıyor; destek ile iletişime geç.");
    }

    [Fact]
    public void Saat_yerel_saat_dilimine_cevrilir()
    {
        SalesCommandMessages.Clock(At, Istanbul).Should().Be("21:14");
        SalesCommandMessages.Clock(At, TimeZoneInfo.Utc).Should().Be("18:14");
        SalesCommandMessages.Clock(At).Should().Be(At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }
}
