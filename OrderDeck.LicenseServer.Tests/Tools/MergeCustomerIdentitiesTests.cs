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
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await MergeCustomerIdentities.RunAsync(args)).Should().Be(2);
}
