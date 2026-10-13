using FluentAssertions;
using OrderDeck.LicenseServer.Services.Sync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// İstemci sürümü ayrıştırıcısı ve sürüm kapısı (spec 2026-10-13 §4.0; ortak bakiye §7.2 aynı yardımcıyı
/// kullanacak). Kapı kapalı-hata: bilinmeyen, ayrıştırılamayan ya da alt sınırı tanımsız durum geçmez.
/// </summary>
public sealed class ClientVersionTests
{
    // ── User-Agent → ham metin (Activations.AppVersion'a bu yazılır) ─────────

    [Theory]
    [InlineData("OrderDeck-WPF/0.9.10", "0.9.10")]
    [InlineData("OrderDeck-WPF/9.8.7", "9.8.7")]
    [InlineData("Mozilla/5.0 OrderDeck-WPF/0.9.10 extra/1", "0.9.10")]
    [InlineData("OrderDeck-WPF/0.9.11-rc1", "0.9.11-rc1")]
    [InlineData("OrderDeck-WPF/0.9.10+abc123", "0.9.10")]
    public void RawFromUserAgent_istemci_surumunu_ceker(string userAgent, string expected)
        => ClientVersion.RawFromUserAgent(userAgent).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")]
    [InlineData("OrderDeck-Shopper/1.2.3")]
    [InlineData("XOrderDeck-WPF/1.2.3")]
    [InlineData("OrderDeck-WPF/")]
    public void RawFromUserAgent_desen_tutmazsa_null(string? userAgent)
        => ClientVersion.RawFromUserAgent(userAgent).Should().BeNull();

    // ── Ayrıştırma ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.9.10", "0.9.10.0", null)]
    [InlineData("0.9", "0.9.0.0", null)]
    [InlineData("1.2.3.4", "1.2.3.4", null)]
    [InlineData("0.9.11-rc1", "0.9.11.0", "rc1")]
    [InlineData("0.9.11-beta-2", "0.9.11.0", "beta-2")]
    [InlineData("0.9.10+abc", "0.9.10.0", null)]
    [InlineData("0.9.11-rc1+abc", "0.9.11.0", "rc1")]
    public void Parse_numarayi_ve_on_surum_ekini_ayirir(string raw, string number, string? preRelease)
    {
        var v = ClientVersion.Parse(raw)!;
        v.Should().NotBeNull();
        v.Number.Should().Be(Version.Parse(number));
        v.PreRelease.Should().Be(preRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("v0.9.10")]
    [InlineData("1")]
    [InlineData("0.9.x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("0.9.10-")]
    [InlineData("-rc1")]
    [InlineData("0.-1.2")]
    public void Parse_ayristirilamayan_surumde_null(string? raw)
        => ClientVersion.Parse(raw).Should().BeNull();

    // ── Kapı ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Sayisal_karsilastirma_metin_siralamasinin_yanildigi_yerde_dogru()
    {
        // Metin sırasıyla "0.9.10" < "0.9.9": kapı metinle kurulsaydı Ws'yi eski sayardı.
        string.CompareOrdinal("0.9.10", "0.9.9").Should().BeNegative();

        var client = ClientVersion.FromUserAgent("OrderDeck-WPF/0.9.10");
        ClientVersion.IsAtLeast(client, ClientVersion.Parse("0.9.9")).Should().BeTrue();
        ClientVersion.IsAtLeast(ClientVersion.Parse("0.9.9"), ClientVersion.Parse("0.9.10")).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.9.10", "0.9.10", true)]
    [InlineData("0.9.11", "0.9.10", true)]
    [InlineData("1.0.0", "0.9.99", true)]
    [InlineData("0.9.9", "0.9.10", false)]
    [InlineData("0.9.10.0", "0.9.10", true)]
    [InlineData("0.9.10", "0.9.10.0", true)]
    [InlineData("0.9", "0.9.0", true)]
    [InlineData("0.9.10.1", "0.9.10", true)]
    [InlineData("0.9.10+abc", "0.9.10", true)]
    [InlineData("0.9.11-rc1", "0.9.11", false)]
    [InlineData("0.9.11-rc1", "0.9.10", true)]
    [InlineData("0.9.11", "0.9.11-rc1", true)]
    [InlineData("0.9.11-rc1", "0.9.11-rc1", true)]
    [InlineData("0.9.11-RC1", "0.9.11-rc1", true)]
    [InlineData("0.9.11-rc2", "0.9.11-rc1", false)]
    [InlineData("0.9.10-rc1", "0.9.11-rc1", false)]
    public void IsAtLeast(string client, string min, bool expected)
        => ClientVersion.IsAtLeast(ClientVersion.Parse(client), ClientVersion.Parse(min)).Should().Be(expected);

    [Fact]
    public void IsAtLeast_kapali_hata_bilinmeyen_istemci_ve_tanimsiz_alt_sinir_gecmez()
    {
        var min = ClientVersion.Parse("0.9.10");
        ClientVersion.IsAtLeast(null, min).Should().BeFalse("UA'sız istemci eski sayılır");
        ClientVersion.IsAtLeast(ClientVersion.FromUserAgent(null), min).Should().BeFalse("UA yok");
        ClientVersion.IsAtLeast(ClientVersion.FromUserAgent("Mozilla/5.0"), min).Should().BeFalse("tarayıcı");
        ClientVersion.IsAtLeast(ClientVersion.FromUserAgent("OrderDeck-WPF/v1"), min).Should().BeFalse(
            "ayrıştırılamayan sürüm");
        ClientVersion.IsAtLeast(ClientVersion.Parse("9.9.9"), null).Should().BeFalse("alt sınır tanımsız");
        ClientVersion.IsAtLeast(ClientVersion.Parse("9.9.9"), ClientVersion.Parse("")).Should().BeFalse(
            "boş ayar = tanımsız alt sınır");
    }
}
