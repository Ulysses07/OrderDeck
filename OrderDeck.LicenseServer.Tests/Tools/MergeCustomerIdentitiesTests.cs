using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>merge-customer-identities</c> CLI'nin kullanım hataları: hepsi 2 döner ve
/// yapılandırmaya/veritabanına HİÇ dokunmadan döner (bu yüzden testler bağlantı
/// dizesi istemez). Komut prod'da elle yazılır; yazım hatası sessizce başka bir
/// kapsama (ör. tüm lisanslara) dönüşmemeli. Geçerli argümanlı yol
/// veritabanına bağlanır — o yolun mantığı CustomerIdentityMergeJobTests'te.
/// </summary>
public class MergeCustomerIdentitiesTests
{
    public static TheoryData<string[]> UsageErrors()
    {
        var license = Guid.NewGuid().ToString();
        return new TheoryData<string[]>
        {
            new[] { "merge-customer-identities" },
            new[] { "merge-customer-identities", "--apply" },
            new[] { "merge-customer-identities", "--all", "--license", license },
            new[] { "merge-customer-identities", "--license" },
            new[] { "merge-customer-identities", "--license", "lisans-degil" },
            // --all yanında bozuk --license: tüm lisanslara düşmemeli.
            new[] { "merge-customer-identities", "--all", "--license", "lisans-degil", "--apply" },
            // Tanınmayan argüman (--apply yazım hatası) kuru çalıştırma sanılmamalı.
            new[] { "merge-customer-identities", "--license", license, "--aply" },
            new[] { "merge-customer-identities", "--license", license, "--license", license },
            // --at-twins iki kez: öteki seçenekler gibi yinelenen argüman.
            new[] { "merge-customer-identities", "--all", "--at-twins", "--at-twins" },
            new[] { "merge-customer-identities", "--at-twins" },
            new[] { "merge-customer-identities", "--at-twins", "--apply" },
            new[] { "merge-customer-identities", "--all", "--at-twin" },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await MergeCustomerIdentities.RunAsync(args)).Should().Be(2);

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Ayristirici_kullanim_hatalarini_reddeder(string[] args)
        => MergeCustomerIdentities.TryParseArgs(args, out _, out _, out _, out _).Should().BeFalse();

    [Fact]
    public void At_twins_all_ve_license_ile_apply_ile_kabul_edilir()
    {
        var license = Guid.NewGuid();

        MergeCustomerIdentities.TryParseArgs(
                ["merge-customer-identities", "--all", "--at-twins"], out var all, out var single, out var apply, out var atTwins)
            .Should().BeTrue();
        (all, single, apply, atTwins).Should().Be((true, (Guid?)null, false, true));

        MergeCustomerIdentities.TryParseArgs(
                ["merge-customer-identities", "--at-twins", "--license", license.ToString(), "--apply"],
                out all, out single, out apply, out atTwins)
            .Should().BeTrue();
        (all, single, apply, atTwins).Should().Be((false, (Guid?)license, true, true));

        MergeCustomerIdentities.TryParseArgs(
                ["merge-customer-identities", "--license", license.ToString()], out _, out _, out _, out atTwins)
            .Should().BeTrue();
        atTwins.Should().BeFalse("bayraksız olağan kip");
    }
}
