using FluentAssertions;
using OrderDeck.Core.Customers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// Sunucunun kabul edeceği müşteri satırı (<see cref="ServerCustomerRule"/>) — ortak bakiye spec §6.1-a, M14.
/// Müşteri senkronu ve ödeme sahibi kuralı aynı tanımı kullanır; sınırlar sunucunun kolon sınırlarıyla
/// (<c>LicensesWpfCustomersSyncController.IsAcceptable</c>: Platform 32, Username 128) aynı. Uzun değerler
/// <c>new string(c, n)</c> ile üretilir.
/// </summary>
public sealed class ServerCustomerRuleTests
{
    public static TheoryData<string?, string?, bool> Sinirlar => new()
    {
        { null, "u", false },
        { " ", "u", false },
        { "instagram", null, false },
        { "instagram", "", false },
        { new string('p', 32), "u", true },
        { new string('p', 33), "u", false },
        { "instagram", new string('u', 128), true },
        { "instagram", new string('u', 129), false },
    };

    [Theory] // §6.1-a: boş/boşluk ya da sınırı aşan Platform/Username sunucuya gönderilemez
    [MemberData(nameof(Sinirlar))]
    public void Platform_ve_kullanici_adi_sinirlari(string? platform, string? username, bool beklenen)
        => ServerCustomerRule.IsServerAcceptable(platform, username).Should().Be(beklenen,
            "platform uzunluğu {0}, kullanıcı adı uzunluğu {1}", platform?.Length, username?.Length);

    [Fact] // Sunucunun kolon sınırları: biri değişirse istemcideki eleme de değişmeli
    public void Sabitler_sunucu_sinirlariyla_ayni()
    {
        ServerCustomerRule.MaxPlatformLength.Should().Be(32);
        ServerCustomerRule.MaxUsernameLength.Should().Be(128);
    }
}
