using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Customers;

public sealed class CustomerIdentityTests
{
    [Theory]
    [InlineData("  Ayse.KAYA ", "ayse.kaya")]
    [InlineData("İREM", "irem")]     // sunucudaki Replace('İ','i') ile aynı
    [InlineData("ırem", "ırem")]     // noktasız ı KORUNUR — sunucu IdentityKeyOf ı→i yapmaz
    [InlineData("ŞEYMA", "şeyma")]
    public void KeyOf_sunucudaki_IdentityKeyOf_ile_ayni_sonucu_verir(string username, string expected)
        => CustomerIdentity.KeyOf(username).Should().Be(expected);

    [Fact]
    public void SQL_fonksiyonu_ayni_anahtari_uretir()
    {
        using var db = new InMemorySqlite();
        using var c = db.Open();
        c.ExecuteScalar<string>("SELECT od_identity_key('  İrem.K ')")
            .Should().Be(CustomerIdentity.KeyOf("  İrem.K "));
    }
}
