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
        BankHasher.MaskIban(iban).Should().Be(iban[..4] + "…" + iban[^3..]);
    }

    [Fact]
    public void Maske_bosluklu_kucuk_harfli_girdiyi_normalize_ederek_maskeler()
    {
        // Üretilmiş, ardışık rakamlı IBAN biçimi (repo public — gerçek IBAN yazılmaz). Baş ve
        // son belirgin: yanlış pencere (ör. son 3 yerine son 4'ün ilk 3'ü) yakalanır.
        var iban = "TR" + string.Concat(Enumerable.Range(1, 24).Select(i => i % 10));
        var messy = " " + string.Join(" ", iban.Chunk(4).Select(c => new string(c))).ToLowerInvariant() + " ";

        BankHasher.MaskIban(messy).Should().Be("TR12…234");
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
    public void Maske_ham_girdiyi_asla_dondurmez()
    {
        BankHasher.MaskIban(null).Should().Be("");
        BankHasher.MaskIban("  ").Should().Be("", "boşluk ham hâliyle görünüme çıkmaz");
        BankHasher.MaskIban(" tr 12 ").Should().Be("TR12", "kısa değer normalize hâliyle döner");
    }

    [Fact]
    public void Anahtar_bos_ise_hasher_kurulamaz()
    {
        var act = () => new BankHasher(Options.Create(new BankOptions { HashKey = "" }));
        act.Should().Throw<InvalidOperationException>("anahtarsız HMAC = düz SHA, IBAN sözlük saldırısına açık");
    }

    [Fact]
    public void Anahtar_32_bayttan_kisaysa_reddedilir_32_bayt_kabul_edilir()
    {
        var otuzIkiBayt = Guid.NewGuid().ToString("N"); // 32 ASCII karakter = 32 bayt
        var kisa = otuzIkiBayt[..31];

        var reddedilen = () => new BankHasher(Options.Create(new BankOptions { HashKey = kisa }));
        reddedilen.Should().Throw<InvalidOperationException>("spec §7: anahtar 32+ bayt")
            .Which.Message.Should().NotContain(kisa, "anahtar hata mesajına (ve log'a) girmez");

        var kabul = () => new BankHasher(Options.Create(new BankOptions { HashKey = otuzIkiBayt }));
        kabul.Should().NotThrow();
    }

    [Fact]
    public void Vkn_hashinde_yalniz_rakamlar_sayilir_ayni_vkn_ayni_hash()
    {
        var h = NewHasher();
        var vkn = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
        var hash = h.HashTaxId(vkn);

        hash.Should().HaveLength(64, "SHA-256 hex");
        h.HashTaxId(vkn).Should().Be(hash, "aynı VKN aynı hash");
        h.HashTaxId($" {vkn[..3]} {vkn[3..]} ").Should().Be(hash, "boşluk atılır");
        h.HashTaxId($"VKN: {vkn}").Should().Be(hash, "harf ve noktalama atılır");
    }

    [Fact]
    public void Vkn_bos_rakamsiz_veya_kalinti_ise_hash_uretmez()
    {
        var h = NewHasher();
        h.HashTaxId(null).Should().BeNull();
        h.HashTaxId("").Should().BeNull();
        h.HashTaxId("   ").Should().BeNull();
        h.HashTaxId("VKN").Should().BeNull();
        h.HashTaxId("0").Should().BeNull("tek rakam kimlik değildir; hash'i olsaydı eşleştirici delil sanabilirdi");
        h.HashTaxId("-").Should().BeNull();
    }

    /// <summary>VKN 10, TCKN 11 rakamdır. Daha kısa kalıntı (9 rakam) hash'lenmez; aksi hâlde
    /// bozuk bir alan sonraki eşleştiricide kimlik kanıtı gibi eşleşebilirdi.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void Vkn_hashi_yalniz_10_veya_daha_fazla_rakamda_uretilir(int digitCount, bool expectHash)
    {
        var h = NewHasher();
        // Üretilmiş rakam dizisi (repo public — gerçek VKN/TCKN yazılmaz).
        var value = string.Concat(Enumerable.Range(0, digitCount).Select(_ => Random.Shared.Next(0, 10)));

        var hash = h.HashTaxId(value);

        if (expectHash) hash.Should().MatchRegex("^[0-9a-f]{64}$", "HMAC-SHA256 küçük hex");
        else hash.Should().BeNull();
    }
}
