using System;
using FluentAssertions;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary><see cref="PersonScopePlan"/> ve <see cref="PaymentJob"/> teslim kurallarının saf testleri — depo
/// sözleşmesinin (<see cref="PaymentJobStoreContractTests"/>) depo yüzeyinden kurulamayan durumları.</summary>
public sealed class PersonScopePlanTests
{
    private static readonly string Satir = Guid.NewGuid().ToString("N");
    private static readonly string BaskaSatir = Guid.NewGuid().ToString("N");

    private static PaymentJob Taze(string customerId)
        => new(Guid.NewGuid().ToString("N"), customerId, "session:s1", 250m, 0, null, null,
            PaymentJobState.Created, 10, 10, null);

    /// <summary>Taşımanın indirdiği, teslim edilmiş, para taşıyan açık miras iş (250 − 100 = 150 istendi).</summary>
    private static PaymentJob TeslimliMiras(string customerId)
    {
        var id = Guid.NewGuid().ToString("N");
        var key = Guid.NewGuid();
        return new PaymentJob(id, customerId, $"legacy:{key:N}:{id}", 250m, 0, key, 100m,
            PaymentJobState.Applied, 5, 5, ClosedAt: null, PriorScopeKey: "session:s1",
            DeliveredAt: 6, DeliveredNet: 150m, Deliveries: 1, SendAttempts: 1);
    }

    // Test/arayüz incelemesi: aynı sahipteki miras taze kalan işe benimsenir (servis, miras adımı) — hiçbir
    // düşüm iade edilmez, aynı istek yeniden gider. Bu bir tekrar göndermedir; "birleştirilsin mi? önceki
    // düşümler iade edilir" sorusu operatörü yanıltırdı.
    [Fact]
    public void Benimsenecek_teslimli_miras_tekrar_gondermedir_iade_yok()
    {
        var miras = TeslimliMiras(Satir);

        var plan = PersonScopePlan.For(new[] { Taze(Satir) }, new[] { miras }, Satir);

        plan.AdoptedLegacy!.Id.Should().Be(miras.Id);
        plan.NeedsConfirmation.Should().BeTrue("istek müşteriye gitmişti");
        plan.Kind.Should().Be(PersonScopePromptKind.Resend);
        plan.RefundsDeductions.Should().BeFalse();
        plan.PromptJobs.Should().ContainSingle().Which.PromptNet.Should().Be(150m);
    }

    [Fact] // Kapsamın işi hiç yoksa iş sahip satırda taze açılır — aynı satırdaki miras yine benimsenir
    public void Is_yokken_sahip_satirdaki_miras_benimsenir()
    {
        var plan = PersonScopePlan.For(Array.Empty<PaymentJob>(), new[] { TeslimliMiras(Satir) }, Satir);

        plan.AdoptedLegacy.Should().NotBeNull();
        plan.Kind.Should().Be(PersonScopePromptKind.Resend);
    }

    [Fact] // Başka satırın mirası benimsenmez (sunucu anahtarı o asıl kayıtla tanır) — iade edilir, sorulur
    public void Baska_satirin_mirasi_iade_edilir_birlestirme_sorulur()
    {
        var plan = PersonScopePlan.For(new[] { Taze(Satir) }, new[] { TeslimliMiras(BaskaSatir) }, Satir);

        plan.AdoptedLegacy.Should().BeNull();
        plan.Kind.Should().Be(PersonScopePromptKind.Merge);
        plan.RefundsDeductions.Should().BeTrue();
        plan.RefundsToOtherRow.Should().BeTrue();
    }

    [Fact] // Kalan iş taze değilse (anahtarı var) miras benimsenmez: iade edilir
    public void Kalan_is_taze_degilse_miras_iade_edilir()
    {
        var kalan = Taze(Satir) with
        {
            ApplyKey = Guid.NewGuid(), AppliedAmount = 0m, State = PaymentJobState.NoBalance,
        };

        var plan = PersonScopePlan.For(new[] { kalan }, new[] { TeslimliMiras(Satir) }, Satir);

        plan.AdoptedLegacy.Should().BeNull();
        plan.Kind.Should().Be(PersonScopePromptKind.Merge);
    }

    [Fact] // Parasız (teslim edilmiş) kaybedenlerin birleşmesinde iade edilecek düşüm yok
    public void Parasiz_birlestirmede_iade_yok()
    {
        var a = Taze(Satir) with { State = PaymentJobState.NoBalance, AppliedAmount = 0m, DeliveredAt = 7, Deliveries = 1 };
        var b = Taze(BaskaSatir) with { State = PaymentJobState.NoBalance, AppliedAmount = 0m, DeliveredAt = 8, Deliveries = 1 };

        var plan = PersonScopePlan.For(new[] { a, b }, Satir);

        plan.Kind.Should().Be(PersonScopePromptKind.Merge);
        plan.RefundsDeductions.Should().BeFalse();
    }

    // Geri dönüş: bu sürüm teslim edip (DeliveredAt=100) önceki sürüm revize edip yeniden teslim ettiyse
    // (ClosedAt=200, teslim kaydı yok) en son teslim önceki sürümünkidir — soru eski saati ve eski tutarı
    // göstermez.
    [Fact]
    public void Onceki_surumun_sonraki_teslimi_son_teslim_sayilir()
    {
        var job = Taze(Satir) with
        {
            State = PaymentJobState.Applied, ApplyKey = Guid.NewGuid(), AppliedAmount = 40m, ProductTotal = 300m,
            ClosedAt = 200, DeliveredAt = 100, DeliveredNet = 210m, Deliveries = 1, SendAttempts = 1,
        };

        job.LastDeliveredAt.Should().Be(200);
        job.LastRequestedNet.Should().Be(260m, "kapalı işin alanları: 300 − 40");
        (job with { ClosedAt = 100 }).LastRequestedNet.Should().Be(210m, "bu sürümün teslimi: teslim kaydı");
    }

    [Fact] // Miras kapsamında kapanış uzlaştırmadır, teslim sayılmaz
    public void Miras_kapsaminda_kapanis_teslim_sayilmaz()
    {
        var miras = TeslimliMiras(Satir) with { ClosedAt = 50, DeliveredAt = null, DeliveredNet = null, Deliveries = 0, SendAttempts = 0 };

        miras.IsDelivered.Should().BeFalse();
        miras.LastDeliveredAt.Should().BeNull();
    }
}
