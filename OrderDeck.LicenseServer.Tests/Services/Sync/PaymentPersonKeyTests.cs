using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// Sunucunun kişi anahtarı (spec 2026-10-13 §2 PersonKey, §6.4-b): GUID'li gruplu sahip <c>g:</c> + ham GroupId, GUID'li
/// gruplanmamış sahip <c>c:</c> + asıl kayıt "N" hex, GUID'siz sahip <c>r:</c> + ham Id. Gruplu sayılma kuralı yereldeki
/// <c>CustomerGroup.IsGrouped</c> ile aynı (yalnız boşluk = gruplanmamış). Özet metnin baytlarından: harf ya da sondaki
/// boşlukla ayrılan iki anahtar iki ayrı kişidir (§15-20).
/// </summary>
public sealed class PaymentPersonKeyTests
{
    [Fact]
    public void Gruplu_GUIDli_sahip_ham_GroupId_ile_g_oneki_alir()
    {
        var owner = Guid.NewGuid();
        var group = $" Grp-{Guid.NewGuid():N} ";

        PaymentPersonKey.ForCanonical(owner, group).Should().Be("g:" + group, "GroupId kırpılmaz, katlanmaz");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Gruplanmamis_GUIDli_sahip_c_oneki_ve_N_hex_alir(string? groupId)
    {
        var owner = Guid.NewGuid();

        PaymentPersonKey.ForCanonical(owner, groupId).Should().Be("c:" + owner.ToString("N"));
        PaymentPersonKey.IsGrouped(groupId).Should().BeFalse("yalnız boşluk gruplanmamış demektir (CustomerGroup.IsGrouped)");
    }

    [Fact]
    public void GUIDsiz_sahip_ham_Id_ile_r_oneki_alir()
    {
        var raw = $"Yerel-{Guid.NewGuid():N} ";

        PaymentPersonKey.ForRaw(raw).Should().Be("r:" + raw);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("yerel-1", false)]
    public void Yalniz_N_bicimli_Id_GUID_sayilir(string id, bool expected) =>
        PaymentPersonKey.TryParseGuid(id, out _).Should().Be(expected);

    [Fact]
    public void Ozet_anahtarin_UTF8_baytlarinin_SHA256_si()
    {
        var key = $"g:Kişi-{Guid.NewGuid():N}";

        PaymentPersonKey.Hash(key).Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        PaymentPersonKey.Hash(key).Should().HaveCount(32);
    }

    /// <summary>§15-20: SQL harf ve sondaki boşluk farkını yutabilir; özet yutmaz.</summary>
    [Theory]
    [InlineData("g:Ab", "g:ab")]
    [InlineData("r:x", "r:x ")]
    [InlineData("g:I", "g:ı")]
    public void Yalniz_harf_ya_da_sondaki_boslukla_ayrilan_anahtarlarin_ozeti_farklidir(string a, string b) =>
        PaymentPersonKey.Hash(a).Should().NotEqual(PaymentPersonKey.Hash(b));
}
