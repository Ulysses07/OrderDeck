using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync</c> CLI'nin argüman ayrıştırması — veritabanı istemez (spec 2026-10-13 §4.8, §4.10). S2'de tek alt
/// komut var: <c>shipping set</c>. Kullanım hataları (başka alt komut dahil) 2 döner ve yapılandırmaya/veritabanına
/// HİÇ dokunmadan döner. Komutun gövdesi gerçek SQL Server'da <see cref="SalesSyncRelationalTests"/>'te.
/// </summary>
public sealed class SalesSyncTests
{
    public static TheoryData<string[]> UsageErrors()
    {
        var license = Guid.NewGuid().ToString();
        return new TheoryData<string[]>
        {
            // Alt komut yok ya da S2'de olmayan alt komut (status, enable, disable, force-end, adopt-orders S3'te).
            new[] { "sales-sync" },
            new[] { "sales-sync", "status", "--license", license },
            new[] { "sales-sync", "enable", "--license", license },
            new[] { "sales-sync", "shipping" },
            new[] { "sales-sync", "shipping", "get", "--license", license },
            new[] { "sales-sync", "shipping", "--license", license, "--off" },
            new[] { "sales-sync", "set", "shipping", "--license", license, "--off" },
            // Lisans eksik, bozuk ya da iki kez.
            new[] { "sales-sync", "shipping", "set", "--threshold", "500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", "--threshold", "500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", "lisans-degil", "--off" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--license", license, "--off" },
            // Değer yok: ne eşik/ücret ne --off.
            new[] { "sales-sync", "shipping", "set", "--license", license },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--apply" },
            // Yalnız biri.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--fee", "60" },
            // --off ile değer birlikte.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--threshold", "500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--fee", "60" },
            // Geçersiz tutar: sıfır, negatif, kuruştan ince, Türkçe virgül, binlik ayraç, üs, boşluk, sayı değil.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "0", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--fee", "0" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "-500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500.005", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500,5", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "1,500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "5e2", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", " 500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "beşyüz", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "99999999999999999", "--fee", "60" },
            // Değeri eksik ya da değer yerine seçenek: "--threshold --apply" kuru koşu ya da tutar sanılmasın.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--fee" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--fee", "--apply" },
            // İki kez verilen seçenek.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--threshold", "600", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--off" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--apply", "--apply" },
            // Tanınmayan argüman (--apply yazım hatası) kuru çalıştırma sanılmasın.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--aply" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "evet" },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await SalesSync.RunAsync(args)).Should().Be(2);

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Ayristirici_kullanim_hatalarini_reddeder(string[] args)
        => SalesSync.TryParseArgs(args, out _).Should().BeFalse();

    [Fact]
    public void Esik_ve_ucret_kabul_edilir_kuru_kosu_varsayilan()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(
                ["sales-sync", "shipping", "set", "--license", license.ToString(), "--threshold", "500", "--fee", "59.90"],
                out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new SalesSync.ShippingSetArgs(license, 500m, 59.90m, Apply: false));
    }

    [Fact]
    public void Secenek_sirasi_serbest_apply_ve_N_bicimi_kabul_edilir()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(
                ["sales-sync", "shipping", "set", "--apply", "--fee", "60", "--license", license.ToString("N"), "--threshold", "750.5"],
                out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new SalesSync.ShippingSetArgs(license, 750.5m, 60m, Apply: true));
    }

    [Fact]
    public void Off_iki_degeri_bos_birakir()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(["sales-sync", "shipping", "set", "--license", license.ToString(), "--off"], out var dry)
            .Should().BeTrue();
        dry.Should().Be(new SalesSync.ShippingSetArgs(license, null, null, Apply: false));

        SalesSync.TryParseArgs(["sales-sync", "shipping", "set", "--off", "--apply", "--license", license.ToString()], out var apply)
            .Should().BeTrue();
        apply.Should().Be(new SalesSync.ShippingSetArgs(license, null, null, Apply: true));
    }
}
