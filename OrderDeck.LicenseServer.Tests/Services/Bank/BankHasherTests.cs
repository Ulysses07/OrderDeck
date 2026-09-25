using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>IBAN/VKN düz saklanmaz; HMAC anahtarı olmadan hash'ten geri dönülemez,
/// aynı değer aynı hash'i verir (hafıza araması), maskeleme yalnız görüntü içindir.</summary>
public sealed class BankHasherTests
{
    private static BankHasher NewHasher(string? key = null)
        => new(Options.Create(new BankOptions { HashKey = key ?? $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    /// <summary>Test IBAN'ı üretir — repo public, gerçek IBAN yazılmaz.</summary>
    public static string TestIban()
        => "TR" + Random.Shared.NextInt64(10_000_000_000_000_000, 99_999_999_999_999_999).ToString() + "0000000";

    [Fact]
    public void Ayni_iban_ayni_anahtarla_ayni_hashi_verir_bosluk_ve_kucuk_harf_farki_yok()
    {
        var h = NewHasher();
        var iban = TestIban();
        var spaced = string.Join(" ", Enumerable.Range(0, iban.Length / 4 + 1)
            .Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - i * 4))));

        h.HashIban(iban).Should().Be(h.HashIban(spaced.ToLowerInvariant()));
        h.HashIban(iban).Should().HaveLength(64, "SHA-256 hex");
    }

    [Fact]
    public void Farkli_anahtar_farkli_hash_verir()
    {
        var iban = TestIban();
        NewHasher().HashIban(iban).Should().NotBe(NewHasher().HashIban(iban));
    }

    [Fact]
    public void Maske_yalniz_ilk_dort_ve_son_uc_karakteri_gosterir()
    {
        var iban = TestIban();
        var masked = BankHasher.MaskIban(iban);
        masked.Should().StartWith(iban[..4]).And.EndWith(iban[^3..]).And.Contain("…");
        masked.Length.Should().BeLessThan(iban.Length);
    }

    [Fact]
    public void Bos_veya_kisa_deger_hash_ve_maske_uretmez()
    {
        var h = NewHasher();
        h.HashIban("").Should().BeNull();
        h.HashIban("  ").Should().BeNull();
        BankHasher.MaskIban("TR12").Should().Be("TR12");
    }

    [Fact]
    public void Anahtar_bos_ise_hasher_kurulamaz()
    {
        var act = () => new BankHasher(Options.Create(new BankOptions { HashKey = "" }));
        act.Should().Throw<InvalidOperationException>("anahtarsız HMAC = düz SHA, IBAN sözlük saldırısına açık");
    }
}
