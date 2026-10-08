using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>group-customers</c> CLI'nin argüman ayrıştırması ve eşleşme dosyası
/// okuması — veritabanı istemez. Kullanım hataları 2 döner ve yapılandırmaya/
/// veritabanına HİÇ dokunmadan döner. Komutun gövdesi gerçek SQL Server'da
/// <see cref="GroupCustomersRelationalTests"/>'te; kuralları
/// CustomerGroupingJobTests'te.
/// </summary>
public sealed class GroupCustomersTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files) File.Delete(f);
    }

    private string WriteFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"group-customers-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        _files.Add(path);
        return path;
    }

    public static TheoryData<string[]> UsageErrors()
    {
        var license = Guid.NewGuid().ToString();
        const string pairs = "eslesmeler.txt";
        return new TheoryData<string[]>
        {
            new[] { "group-customers" },
            new[] { "group-customers", "--apply" },
            new[] { "group-customers", "--license", license },                       // --pairs yok
            new[] { "group-customers", "--pairs", pairs },                           // --license yok
            new[] { "group-customers", "--license", "--pairs", pairs },
            new[] { "group-customers", "--license", "lisans-degil", "--pairs", pairs },
            new[] { "group-customers", "--license", license, "--pairs" },
            new[] { "group-customers", "--license", license, "--pairs", " " },
            // Seçenek dosya adı sanılmasın: "--pairs --apply" kuru çalıştırmaya dönüşmez.
            new[] { "group-customers", "--license", license, "--pairs", "--apply" },
            new[] { "group-customers", "--license", license, "--license", license, "--pairs", pairs },
            new[] { "group-customers", "--license", license, "--pairs", pairs, "--pairs", pairs },
            new[] { "group-customers", "--license", license, "--pairs", pairs, "--apply", "--apply" },
            // Tanınmayan argüman (--apply yazım hatası) kuru çalıştırma sanılmamalı.
            new[] { "group-customers", "--license", license, "--pairs", pairs, "--aply" },
            new[] { "group-customers", "--all", "--pairs", pairs },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await GroupCustomers.RunAsync(args)).Should().Be(2);

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Ayristirici_kullanim_hatalarini_reddeder(string[] args)
        => GroupCustomers.TryParseArgs(args, out _, out _, out _).Should().BeFalse();

    [Fact]
    public void Gecerli_argumanlar_her_sirada_kabul_edilir()
    {
        var license = Guid.NewGuid();

        GroupCustomers.TryParseArgs(
                ["group-customers", "--license", license.ToString(), "--pairs", "a.txt"], out var l, out var p, out var apply)
            .Should().BeTrue();
        (l, p, apply).Should().Be((license, "a.txt", false));

        GroupCustomers.TryParseArgs(
                ["group-customers", "--apply", "--pairs", "b.txt", "--license", license.ToString("N")], out l, out p, out apply)
            .Should().BeTrue();
        (l, p, apply).Should().Be((license, "b.txt", true));
    }

    [Fact]
    public void Dosya_yorum_ve_bos_satirlari_atlar_N_ve_D_bicimini_okur()
    {
        var (b1, r1, b2, r2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var path = WriteFile(
            "# alıcı kayıtlı",
            "",
            $"{b1:N} {r1:N}",
            "   ",
            $"  \t{b2:D}\t\t{r2:N}  ",
            "   # girintili yorum");
        var error = new StringWriter();

        GroupCustomers.TryReadPairs(path, error, out var pairs).Should().BeTrue(error.ToString());

        pairs.Should().Equal(new CustomerGroupingJob.Pair(b1, r1), new CustomerGroupingJob.Pair(b2, r2));
        error.ToString().Should().BeEmpty();
    }

    public static TheoryData<string> MalformedLines() => new()
    {
        Guid.NewGuid().ToString("N"),                                                    // tek Id
        $"{Guid.NewGuid():N} {Guid.NewGuid():N} {Guid.NewGuid():N}",                     // üç Id
        $"{Guid.NewGuid():N} {Guid.NewGuid():N} # not",                                  // satır sonu yorum yok
        $"{Guid.NewGuid():N} kayitli-{Guid.NewGuid():N}",                                // GUID değil
        $"{Guid.NewGuid():B} {Guid.NewGuid():N}",                                        // yalnız N ve D
        $"{Guid.NewGuid():N},{Guid.NewGuid():N}",                                        // ayraç boşluk
    };

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void Bozuk_satir_dosyayi_reddeder_iletide_satir_numarasi_var_icerigi_yok(string bad)
    {
        var path = WriteFile("# yorum", $"{Guid.NewGuid():N} {Guid.NewGuid():N}", bad, $"{Guid.NewGuid():N} {Guid.NewGuid():N}");
        var error = new StringWriter();

        GroupCustomers.TryReadPairs(path, error, out var pairs).Should().BeFalse();

        pairs.Should().BeEmpty("yarım dosyayla yarım gruplama yapılmaz");
        var text = error.ToString();
        text.Should().Contain("satır 3");
        foreach (var token in bad.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
            text.Should().NotContain(token, "satırın içeriği yazılmaz");
        text.Should().NotContain(path, "dosya yolu yazılmaz");
    }

    [Fact]
    public void Okunamayan_dosya_reddedilir_yol_yazilmaz()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yok-{Guid.NewGuid():N}.txt");
        var error = new StringWriter();

        GroupCustomers.TryReadPairs(path, error, out _).Should().BeFalse();

        error.ToString().Should().Contain(nameof(FileNotFoundException)).And.NotContain(path);
    }

    [Fact]
    public async Task Bozuk_dosya_argumanlarla_da_2_doner()
    {
        var path = WriteFile($"{Guid.NewGuid():N}");
        (await GroupCustomers.RunAsync(["group-customers", "--license", Guid.NewGuid().ToString(), "--pairs", path]))
            .Should().Be(2);
    }
}
