using FluentAssertions;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PaymentRequestTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// <c>already-requested</c>'ın öncül kümesi özeti (spec 2026-10-13 §6.4-e): Id kümesinin SHA-256'sı, okuma sırasından ve
/// yinelenen Id'den bağımsız. Kanonik sıra küçük harf "N" yazımının ordinal sırasıdır (RFC 9562 büyük uçlu baytların
/// sırası), .NET'in <see cref="Guid"/> bayt düzeni değil; telde <c>v1.</c> + dolgusuz base64url, tek yazım.
/// </summary>
public sealed class PaymentRequestPriorsDigestTests
{
    [Fact]
    public void Ozet_okuma_sirasindan_ve_yinelenen_Idden_bagimsiz()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        var shuffled = ids.OrderBy(_ => Random.Shared.Next()).Concat(ids.Take(5)).ToList();

        PaymentRequestPriorsDigest.Compute(shuffled).Should().Be(PaymentRequestPriorsDigest.Compute(ids));
    }

    /// <summary>Biçim ve kanonik sıra sunucu kodundan bağımsız türetilmiş özetle sabitlenir. .NET'in bayt düzeni
    /// (<c>ToByteArray()</c>, ilk üç alan küçük uçlu) kanonik sıradan ayrılan Id'ler seçilir: yanlış sıra fark edilir.</summary>
    [Fact]
    public void Ozet_kanonik_sirayla_ve_v1_bicimiyle_hesaplanir()
    {
        var first = Guid.ParseExact("00000001000040008000000000000001", "N");
        var second = Guid.ParseExact("01000000000040008000000000000002", "N");
        string.CompareOrdinal(first.ToString("N"), second.ToString("N")).Should().BeNegative();
        second.ToByteArray().AsSpan().SequenceCompareTo(first.ToByteArray()).Should().BeNegative(
            "küçük uçlu bayt düzeninde sıra kanonik sıranın tersi olmalı");

        var digest = PaymentRequestPriorsDigest.Compute([second, first]);

        digest.Should().Be(Kit.ExpectedPriorsDigest([first, second]));
        digest.Should().StartWith("v1.").And.HaveLength(46);
    }

    [Fact]
    public void Farkli_kume_farkli_ozet()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        PaymentRequestPriorsDigest.Compute([a, b]).Should().NotBe(PaymentRequestPriorsDigest.Compute([a]));
    }

    /// <summary>
    /// <c>Base64Url.TryDecodeFromChars</c> geçersiz karakterde ve artık bitleri sıfır olmayan son karakterde istisna atar;
    /// çözüm hiçbir girdide istisnaya (500) dönmemeli. Son karakterin 64 değerinin yalnız alt iki biti sıfır olanları (tek
    /// yazım) kabul edilir; alfabe dışı karakter, boşluk ve dolgu her konumda reddedilir.
    /// </summary>
    [Fact]
    public void Cozum_istisna_atmaz_yalniz_tek_yazimi_kabul_eder()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var digest = PaymentRequestPriorsDigest.Compute([Guid.NewGuid()]);

        for (var i = 0; i < alphabet.Length; i++)
            PaymentRequestPriorsDigest.TryParse(digest[..^1] + alphabet[i], out _).Should().Be(i % 4 == 0, $"son '{alphabet[i]}'");
        foreach (var bad in new[] { '+', '/', '=', ' ', '\n', '.', 'ı' })
        foreach (var at in new[] { 3, 20, digest.Length - 1 })
            PaymentRequestPriorsDigest.TryParse(string.Concat(digest.AsSpan(0, at), [bad], digest.AsSpan(at + 1)), out _)
                .Should().BeFalse($"'{bad}' konum {at}");
    }

    [Fact]
    public void Ozet_kendi_yazimindan_geri_okunur()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var digest = PaymentRequestPriorsDigest.Compute(ids);

        PaymentRequestPriorsDigest.TryParse(digest, out var hash).Should().BeTrue();
        hash.Should().Equal(PaymentRequestPriorsDigest.Hash(ids));
    }
}
