using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Payments;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Kirasız ödeme kaydının saf kuralları (spec 2026-10-13 §6.4 "Ödeme geçmişi"; W3-06): belirlenimli ve iki ad uzaylı
/// kayıt Id'si, sunucunun kapsam, üye ve tutar sınırlarının istemcideki aynası (sunucunun 400'ü partiyi ve
/// tamamlanmayı kalıcı olarak kilitlerdi) ve imlecin kodlaması.
/// </summary>
public sealed class PaymentHistoryRecordsTests
{
    [Fact]
    public void Kayit_Idsi_belirlenimli_ve_teslim_ile_belirsiz_ayri_ad_uzaylarinda()
    {
        var job = Guid.NewGuid().ToString("N");

        PaymentRecordIds.Delivered(job, 2).Should().Be(PaymentRecordIds.Delivered(job, 2), "yineleme zararsız");
        PaymentRecordIds.Delivered(job, 2).Should().NotBe(PaymentRecordIds.Delivered(job, 3));
        PaymentRecordIds.Unconfirmed(job, 2).Should().NotBe(PaymentRecordIds.Delivered(job, 2),
            "belirsiz denemeden sonraki teslim aynı sayıyı taşır; Id'si ayrı olmalı");
        PaymentRecordIds.Delivered(job, 1).Should().NotBe(PaymentRecordIds.Delivered(Guid.NewGuid().ToString("N"), 1));
        PaymentRecordIds.Delivered(job, 1).Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("cumulative", true, true)]
    [InlineData("session:0123456789abcdef0123456789abcdef", true, true)]
    [InlineData("session:01234567-89ab-cdef-0123-456789abcdef", false, false)]   // yalnız "N" biçimi
    [InlineData("session:s1", false, false)]
    [InlineData("legacy", false, true)]
    [InlineData("legacy:anahtar", false, true)]
    [InlineData("legacy:", false, false)]
    [InlineData("cumulative ", false, false)]
    [InlineData("", false, false)]
    public void Kapsam_sunucunun_kira_ve_kayit_kurallarinin_aynasi(string scope, bool lease, bool record)
    {
        PaymentHistoryRecords.IsLeaseScope(scope).Should().Be(lease);
        PaymentHistoryRecords.IsRecordScope(scope).Should().Be(record);
    }

    [Fact]
    public void Uzun_miras_kapsami_kayit_bicimine_uymaz()
    {
        PaymentHistoryRecords.IsRecordScope("legacy:" + new string('a', PaymentRequestLimits.MaxScopeKeyLength))
            .Should().BeFalse();
    }

    [Fact]
    public void Tel_uyeleri_sahip_once_tekil_sinirli_sahip_sigmazsa_kayit_yok()
    {
        var owner = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        var tooLong = new string('x', PaymentRequestLimits.MaxMemberIdLength + 1);

        PaymentHistoryRecords.WireMembers(owner, new[] { other, owner, other, " ", tooLong })
            .Should().Equal(owner, other);
        PaymentHistoryRecords.WireMembers(tooLong, new[] { owner }).Should().BeNull();

        var many = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid().ToString("N")).ToList();
        var wire = PaymentHistoryRecords.WireMembers(owner, many)!;
        wire.Should().HaveCount(PaymentRequestLimits.MaxMembers);
        wire[0].Should().Be(owner);
    }

    [Fact]
    public void Tel_siparisleri_Guid_olanlar_tekil_tutar_iki_ondalik_negatif_degil()
    {
        var a = Guid.NewGuid();
        PaymentHistoryRecords.WireOrders(new[] { a.ToString("N"), "etiket-guid-degil", a.ToString("D") })
            .Should().Equal(a);
        PaymentHistoryRecords.Money(12.345m).Should().Be(12.35m);
        PaymentHistoryRecords.Money(-5m).Should().Be(0m);
    }

    [Fact]
    public void Imlec_kodlamasi_gidip_gelir_bitmis_asamada_rowid_tasimaz()
    {
        var cursor = PaymentHistoryCursor.Start.After(42);
        PaymentHistoryCursor.Decode(cursor.Encode()).Should().Be(cursor);
        PaymentHistoryCursor.Decode(PaymentHistoryCursor.Done.Encode()).Should().Be(PaymentHistoryCursor.Done);
        PaymentHistoryCursor.Decode(PaymentHistoryCursor.Completed.Encode()).Should().Be(PaymentHistoryCursor.Completed);
        PaymentHistoryCursor.Decode(PaymentHistoryCursor.Done.Encode() + 1).Should().BeNull();
        PaymentHistoryCursor.Decode(3 * PaymentHistoryCursor.PhaseFactor).Should().BeNull();
        PaymentHistoryCursor.Decode(-1).Should().BeNull();
    }
}
