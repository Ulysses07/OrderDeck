using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Uzak öncüllerin onay belirteci (spec 2026-10-13 §6.4 "409'ların istemcideki karşılığı", PR #523 eki; W3-08):
/// sorunun belirteci olarak taşınır, onaylanan belirteç kümesinden geri okunur ve <c>begin</c>'e özet + en çok 500 Id ile
/// gider.
/// </summary>
public sealed class PaymentLeaseConfirmationTests
{
    private static string Digest() => $"v1.{Guid.NewGuid():N}";

    [Fact]
    public void Belirtecten_ozet_ve_Id_listesi_aynen_geri_okunur()
    {
        var digest = Digest();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var token = PaymentLeaseConfirmation.Create(digest, ids).Token;

        var read = PaymentLeaseConfirmation.From(new HashSet<string>(StringComparer.Ordinal) { "yerel-soru", token });

        read.PriorsDigest.Should().Be(digest);
        read.PriorIds.Should().Equal(ids);
        read.WirePriorIds.Should().Equal(ids);
    }

    [Fact]
    public void Belirtec_yoksa_onay_alanlari_bos()
    {
        PaymentLeaseConfirmation.From(null).Should().BeSameAs(PaymentLeaseConfirmation.None);
        var read = PaymentLeaseConfirmation.From(new HashSet<string>(StringComparer.Ordinal) { "uzak:abc" });
        read.PriorsDigest.Should().BeNull();
        read.PriorIds.Should().BeEmpty();
    }

    [Fact]
    public void Birden_cok_uzak_onayda_en_yenisi_gecerli()
    {
        var older = PaymentLeaseConfirmation.Create(Digest(), [Guid.NewGuid()]);
        var newer = PaymentLeaseConfirmation.Create(Digest(), [Guid.NewGuid(), Guid.NewGuid()]);

        var read = PaymentLeaseConfirmation.From(
            new HashSet<string>(StringComparer.Ordinal) { newer.Token, older.Token });

        read.PriorsDigest.Should().Be(newer.PriorsDigest, "yeni already-requested'in özeti öncekinin yerini alır");
        read.PriorIds.Should().Equal(newer.PriorIds);
    }

    [Fact]
    public void Ozetsiz_sunucunun_onayi_yalniz_Id_listesi_tasir()
    {
        var ids = new[] { Guid.NewGuid() };
        var read = PaymentLeaseConfirmation.From(new HashSet<string>(StringComparer.Ordinal)
        {
            PaymentLeaseConfirmation.Create(null, ids).Token,
        });

        read.PriorsDigest.Should().BeNull("#523 öncesi sunucu priorsDigest döndürmez; telde alan hiç yazılmaz");
        read.PriorIds.Should().Equal(ids);
    }

    [Fact]
    public void Telde_en_cok_500_Id_fazlasi_kesilir()
    {
        var ids = Enumerable.Range(0, PaymentRequestLimits.MaxConfirmedPriorIds + 37).Select(_ => Guid.NewGuid()).ToList();
        var confirmation = PaymentLeaseConfirmation.Create(Digest(), ids);

        confirmation.WirePriorIds.Should().Equal(ids.Take(PaymentRequestLimits.MaxConfirmedPriorIds),
            "500'ü aşan liste özet tutsa da 400 invalid-confirmed-priors alırdı; bütün kümeyi özet onaylar");
    }

    [Fact] // W3-09: belirteç o 409'un en çok 500 öncül Id'sini taşır (sorunun sırasıyla ilkleri)
    public void Belirtec_en_cok_500_Id_tasir()
    {
        var digest = Digest();
        var ids = Enumerable.Range(0, 600).Select(_ => Guid.NewGuid()).ToList();

        var read = PaymentLeaseConfirmation.From(new HashSet<string>(StringComparer.Ordinal)
        {
            PaymentLeaseConfirmation.Create(digest, ids).Token,
        });

        read.PriorIds.Should().Equal(ids.Take(PaymentRequestLimits.MaxConfirmedPriorIds));
        read.PriorsDigest.Should().Be(digest);
    }

    [Theory]
    [InlineData("kira-onay:")]
    [InlineData("kira-onay:0:v1.abc:")]
    [InlineData("kira-onay:x:v1.abc:")]
    [InlineData("kira-onay:3:v1.abc")]
    [InlineData("kira-onay:3:v1.abc:bozuk-id")]
    public void Bozuk_belirtec_onay_sayilmaz(string token)
    {
        var read = PaymentLeaseConfirmation.From(new HashSet<string>(StringComparer.Ordinal) { token });

        read.Should().BeSameAs(PaymentLeaseConfirmation.None);
    }
}
