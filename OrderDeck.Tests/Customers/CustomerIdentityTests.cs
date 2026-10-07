using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Customers;

public sealed class CustomerIdentityTests
{
    [Theory]
    [InlineData("  Ornek.MUSTERI ", "ornek.musteri")]
    [InlineData("İREM", "irem")]     // sunucudaki Replace('İ','i') ile aynı
    [InlineData("ırem", "ırem")]     // noktasız ı KORUNUR — sunucu IdentityKeyOf ı→i yapmaz
    [InlineData("ŞEYMA", "şeyma")]
    public void KeyOf_sunucudaki_IdentityKeyOf_ile_ayni_sonucu_verir(string username, string expected)
        => CustomerIdentity.KeyOf(username).Should().Be(expected);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]       // boş anahtar platformun bütün boş adlı satırlarını tek kişi sayardı
    [InlineData(" Ayse ", "ayse")]
    public void KeyOrNull_bos_anahtari_NULL_yapar(string? username, string? expected)
        => CustomerIdentity.KeyOrNull(username).Should().Be(expected);

    [Fact]
    public void SQL_fonksiyonu_ayni_anahtari_uretir()
    {
        using var db = new InMemorySqlite();
        using var c = db.Open();
        c.ExecuteScalar<string>("SELECT od_identity_key('  İrem.K ')")
            .Should().Be(CustomerIdentity.KeyOf("  İrem.K "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SQL_fonksiyonu_bos_adda_KeyOrNull_gibi_NULL_doner(string? username)
    {
        // Savunma derinliği: "SET IdentityKey = od_identity_key(Username)" biçimli bir onarım
        // boş adlı satırlara '' yazıp onları tek kişi saymasın.
        using var db = new InMemorySqlite();
        using var c = db.Open();
        c.ExecuteScalar<string?>("SELECT od_identity_key(@username)", new { username })
            .Should().BeNull();
    }
}
