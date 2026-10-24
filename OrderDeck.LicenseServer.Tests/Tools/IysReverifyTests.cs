using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>iys-reverify argümanları katı (bank-data-cleanup kalıbı): yazım hatalı --apply kuru koşuya dönüşmez, tanınmayan,
/// yinelenen ya da değeri eksik argüman reddedilir ve komut yapılandırmaya ve veritabanına dokunmadan 2 döner. Komutun gövdesi
/// gerçek SQL Server'da <see cref="IysReverifyRelationalTests"/>'te.</summary>
public sealed class IysReverifyTests
{
    private static string NewBrand() => Random.Shared.Next(100_000, 999_999).ToString();

    [Fact]
    public void Argumansiz_kuru_kosu_butun_markalar()
    {
        IysReverify.TryParseArgs(["iys-reverify"], out var o).Should().BeTrue();
        o.Should().Be(new IysReverify.Options(Brand: null, Apply: false));
    }

    [Fact]
    public void Marka_ve_apply_kabul_edilir_sira_onemsiz()
    {
        var brand = NewBrand();
        IysReverify.TryParseArgs(["iys-reverify", "--brand", brand, "--apply"], out var o).Should().BeTrue();
        o.Should().Be(new IysReverify.Options(brand, Apply: true));
        IysReverify.TryParseArgs(["iys-reverify", "--apply", "--brand", brand], out var n).Should().BeTrue();
        n.Should().Be(new IysReverify.Options(brand, Apply: true));
        IysReverify.TryParseArgs(["iys-reverify", "--apply"], out var a).Should().BeTrue();
        a.Should().Be(new IysReverify.Options(null, Apply: true));
    }

    public static TheoryData<string[]> UsageErrors()
    {
        var brand = Random.Shared.Next(100_000, 999_999).ToString();
        return new TheoryData<string[]>
        {
            // Tanınmayan argüman (--apply yazım hatası) kuru çalıştırma sanılmamalı.
            new[] { "iys-reverify", "--aply" },
            new[] { "iys-reverify", "--APPLY" },
            new[] { "iys-reverify", "apply" },
            new[] { "iys-reverify", "--apply", "--apply" },
            new[] { "iys-reverify", "--brand" },
            // Seçenek marka değeri sanılmasın: "--brand --apply" kuru çalıştırmaya dönüşmez.
            new[] { "iys-reverify", "--brand", "--apply" },
            new[] { "iys-reverify", "--brand", "" },
            new[] { "iys-reverify", "--brand", "marka" },
            new[] { "iys-reverify", "--brand", " " + brand },
            new[] { "iys-reverify", "--brand", new string('7', 17) },   // sütun 16 karakter
            new[] { "iys-reverify", "--brand", brand, "--brand", brand },
            new[] { "iys-reverify", "--license", Guid.NewGuid().ToString() },
            new[] { "iys-reverify", "--all" },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Bozuk_arguman_reddedilir(string[] args)
        => IysReverify.TryParseArgs(args, out _).Should().BeFalse();

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await IysReverify.RunAsync(args)).Should().Be(2);
}
