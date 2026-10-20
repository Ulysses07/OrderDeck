using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>bank-data-cleanup argümanları katı (group-customers kalıbı): yazım hatalı --apply kuru koşuya dönüşmez,
/// tanınmayan, yinelenen ya da değeri eksik argüman reddedilir ve komut yapılandırmaya ve veritabanına dokunmadan 2 döner.
/// Komutun gövdesi gerçek SQL Server'da <see cref="BankDataCleanupRelationalTests"/>'te; kuralları BankDataCleanupJobTests'te.</summary>
public sealed class BankDataCleanupTests
{
    [Fact]
    public void Argumansiz_kuru_kosu_butun_lisanslar()
    {
        BankDataCleanup.TryParseArgs(["bank-data-cleanup"], out var o).Should().BeTrue();
        o.Should().Be(new BankDataCleanup.Options(License: null, Apply: false));
    }

    [Fact]
    public void Lisans_ve_apply_kabul_edilir_sira_onemsiz()
    {
        var license = Guid.NewGuid();
        BankDataCleanup.TryParseArgs(["bank-data-cleanup", "--license", license.ToString(), "--apply"], out var o).Should().BeTrue();
        o.Should().Be(new BankDataCleanup.Options(license, Apply: true));
        BankDataCleanup.TryParseArgs(["bank-data-cleanup", "--apply", "--license", license.ToString("N")], out var n).Should().BeTrue();
        n.Should().Be(new BankDataCleanup.Options(license, Apply: true));
    }

    public static TheoryData<string[]> UsageErrors()
    {
        var license = Guid.NewGuid().ToString();
        return new TheoryData<string[]>
        {
            // Tanınmayan argüman (--apply yazım hatası) kuru çalıştırma sanılmamalı.
            new[] { "bank-data-cleanup", "--aply" },
            new[] { "bank-data-cleanup", "--APPLY" },
            new[] { "bank-data-cleanup", "--license" },
            new[] { "bank-data-cleanup", "--license", "lisans-degil" },
            // Seçenek lisans değeri sanılmasın: "--license --apply" kuru çalıştırmaya dönüşmez.
            new[] { "bank-data-cleanup", "--license", "--apply" },
            new[] { "bank-data-cleanup", "--apply", "--apply" },
            new[] { "bank-data-cleanup", "--license", license, "--license", license },
            new[] { "bank-data-cleanup", "--all" },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Bozuk_arguman_reddedilir(string[] args)
        => BankDataCleanup.TryParseArgs(args, out _).Should().BeFalse();

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await BankDataCleanup.RunAsync(args)).Should().Be(2);
}
