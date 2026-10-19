using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>IBAN düz saklanmaz; HMAC anahtarı olmadan özetten IBAN elde edilemez, aynı değer aynı özeti verir (hafıza
/// araması), maskeleme yalnız görüntü içindir. VKN/TCKN artık özetlenmez (KVKK Ö-2).</summary>
public sealed class BankHasherTests
{
    private static BankHasher NewHasher(string? key = null)
        => new(Options.Create(new BankOptions { HashKey = key ?? $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    /// <summary>Mod-97 geçerli TR IBAN üretir — repo public, gerçek IBAN yazılmaz. Gerçek bir hesaba denk gelemez: TR IBAN'da
    /// 5 haneli banka kodundan sonraki rezerv hanesi bugün hep 0'dır, burada 1–9 (kontrol haneleri yine tutar, biçim yine TR
    /// IBAN: 26 karakter, TR + 24 rakam).</summary>
    public static string TestIban()
        => WithCheckDigits("TR", TestPhone.Digits(5) + Random.Shared.Next(1, 10) + TestPhone.Digits(16));

    /// <summary>ISO 13616 kontrol haneleriyle IBAN üretir. Üretim kodundan bağımsız hesap: test doğrulayıcının kendisini sınar.
    /// BBAN büyük harf ve rakamdan oluşmalı.</summary>
    public static string WithCheckDigits(string country, string bban)
    {
        var remainder = 0;
        foreach (var c in bban + country + "00")
            remainder = char.IsAsciiDigit(c) ? (remainder * 10 + (c - '0')) % 97 : (remainder * 100 + (c - 'A' + 10)) % 97;
        return country + (98 - remainder).ToString("00") + bban;
    }

    /// <summary>Kontrol hanelerini bozar (her zaman geçersiz: geçerli kontrol hanesi tektir, 02–98 aralığında başka bir
    /// değer kalanı değiştirir).</summary>
    public static string WrongCheckDigits(string iban)
    {
        var check = int.Parse(iban[2..4]);
        return iban[..2] + (check == 98 ? 2 : check + 1).ToString("00") + iban[4..];
    }

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
        BankHasher.MaskIban("TR12").Should().Be(BankHasher.ShortValueMask, "kısa değer maske sütununda açık kalmaz (KVKK Ö-6)");
    }

    [Fact]
    public void Maske_ham_girdiyi_asla_dondurmez()
    {
        BankHasher.MaskIban(null).Should().Be("");
        BankHasher.MaskIban("  ").Should().Be("", "boşluk ham hâliyle görünüme çıkmaz");
        BankHasher.MaskIban(" tr 12 ").Should().Be("…", "kısa değer hiçbir karakteriyle görünmez");
        BankHasher.MaskIban(TestPhone.Digits(7)).Should().Be("…", "7 haneli hesap numarası da");
        BankHasher.MaskIban(TestPhone.Digits(10)).Should().Be("…", "10 haneli hesap numarasının 7 hanesi açık kalmaz");
        BankHasher.MaskIban(TestPhone.Digits(14)).Should().Be("…", "en kısa geçerli IBAN 15 karakter");
        var shortest = WithCheckDigits("NO", TestPhone.Digits(11));
        BankHasher.MaskIban(shortest).Should().Be(shortest[..4] + "…" + shortest[^3..], "15 karakterlik geçerli IBAN (Norveç) kısa değer sayılmaz");
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
    public void Anahtar_gecerliligi_bayt_sayar_bos_ve_bosluk_gecersiz()
    {
        // Program.cs açılışta aynı denetimle banka modülünü açar/kapatır; kurucu da aynısını kullanır.
        BankHasher.IsValidKey(null).Should().BeFalse();
        BankHasher.IsValidKey("").Should().BeFalse();
        BankHasher.IsValidKey(new string(' ', 40)).Should().BeFalse("boşluk anahtar değildir");
        BankHasher.IsValidKey(new string('k', 31)).Should().BeFalse();
        BankHasher.IsValidKey(new string('k', 32)).Should().BeTrue();
        // 16 karakter × 2 bayt (UTF-8 'ş') = 32 bayt: karakter değil bayt sayılır.
        BankHasher.IsValidKey(new string((char)0x15F, 16)).Should().BeTrue();
        BankHasher.IsValidKey(new string((char)0x15F, 15)).Should().BeFalse("15 × 2 = 30 bayt");
    }

    [Fact]
    public void Anahtar_gecersizse_kurucu_banka_modulu_kapali_mesajiyla_duser()
    {
        var act = () => new BankHasher(Options.Create(new BankOptions { HashKey = new string('k', 31) }));
        act.Should().Throw<InvalidOperationException>().WithMessage(BankHasher.DisabledMessage);
    }

    [Fact]
    public void Kontrol_hanesi_denetimi_gecerli_IBANi_kabul_eder_bozugu_reddeder()
    {
        var iban = TestIban();
        BankHasher.HasValidIbanChecksum(iban).Should().BeTrue();
        BankHasher.HasValidIbanChecksum(WrongCheckDigits(iban)).Should().BeFalse();
        BankHasher.HasValidIbanChecksum(iban[..10] + "******" + iban[16..]).Should().BeFalse("harf/rakam dışı karakter");
        BankHasher.HasValidIbanChecksum(iban.ToLowerInvariant()).Should().BeFalse("büyük harfli, boşluksuz değer bekler");
        BankHasher.HasValidIbanChecksum("TR1").Should().BeFalse("kısa değer");
    }
}
