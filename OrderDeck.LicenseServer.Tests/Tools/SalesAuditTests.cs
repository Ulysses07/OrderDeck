using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-audit</c> CLI'nin argüman ayrıştırması — veritabanı istemez. Kullanım hataları 2 döner ve
/// yapılandırmaya/veritabanına HİÇ dokunmadan döner. Raporun kendisi gerçek SQL Server'da
/// <see cref="SalesAuditRelationalTests"/>'te.
/// </summary>
public sealed class SalesAuditTests
{
    public static TheoryData<string[]> UsageErrors()
    {
        var license = Guid.NewGuid().ToString();
        return new TheoryData<string[]>
        {
            new[] { "sales-audit" },
            new[] { "sales-audit", "--license" },
            new[] { "sales-audit", "--license", "lisans-degil" },
            new[] { "sales-audit", "--license", "--license", license },
            new[] { "sales-audit", "--license", license, "--license", license },
            // Rapor salt okunur: --apply yok; yazım hatası ya da alışkanlık "uygulandı" sanılmasın.
            new[] { "sales-audit", "--license", license, "--apply" },
            new[] { "sales-audit", "--apply", "--license", license },
            new[] { "sales-audit", "--all" },
            new[] { "sales-audit", license },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await SalesAudit.RunAsync(args)).Should().Be(2);

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Ayristirici_kullanim_hatalarini_reddeder(string[] args)
        => SalesAudit.TryParseArgs(args, out _).Should().BeFalse();

    [Fact]
    public void Gecerli_argumanlar_kabul_edilir()
    {
        var license = Guid.NewGuid();

        SalesAudit.TryParseArgs(["sales-audit", "--license", license.ToString()], out var parsed).Should().BeTrue();
        parsed.Should().Be(license);

        SalesAudit.TryParseArgs(["sales-audit", "--license", license.ToString("N")], out parsed).Should().BeTrue();
        parsed.Should().Be(license);
    }
}
