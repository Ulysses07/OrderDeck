using System;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>R8-D01: servis testlerinin kullandığı taklit depo, gerçek SQLite
/// deposuyla AYNI sözleşmeyi uygulamak zorunda. Aksi hâlde servis testleri
/// yeşilken gerçek depo + servis birleşimi başka davranır — R6-03'te tam
/// olarak bu oldu: repository geri alma beklerken geç sonucu reddediyordu,
/// taklit kabul ediyordu. Bu küme her iki implementasyona da uygulanır;
/// depoya yeni aşama koruması eklerken iki tarafı da buradan doğrula.</summary>
public abstract class PaymentJobStoreContractTests
{
    protected abstract IPaymentJobStore CreateStore();

    /// <summary>250 uygulandı, sonra 50 hedefiyle geri alma niyeti yazıldı.</summary>
    private (IPaymentJobStore Store, string JobId, Guid Key) UygulanmisVeGeriAlmaBekleyen()
    {
        var store = CreateStore();
        var job = store.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        store.BeginApply(job.Id, key).Should().BeTrue();
        store.MarkApplied(job.Id, key, expectedRevision: 0, 250m).Should().BeTrue();
        store.BeginReversal(job.Id, 0, key, targetTotal: 50m).Should().BeTrue();
        return (store, job.Id, key);
    }

    [Fact]
    public void MarkApplied_GeriAlmaBeklerken_ReddederVeNiyetiKorur()
    {
        var (store, id, key) = UygulanmisVeGeriAlmaBekleyen();

        store.MarkApplied(id, key, expectedRevision: 0, 250m).Should().BeFalse(
            "niyet silinirse iade adımı bir daha çalışmaz");

        var job = store.Get(id)!;
        job.State.Should().Be(PaymentJobState.ReversePending);
        job.PendingTotal.Should().Be(50m);
        job.AppliedAmount.Should().Be(250m, "iade edilecek tutar korunmalı");
    }

    [Fact]
    public void MarkNoBalance_GeriAlmaBeklerken_ReddederVeNiyetiKorur()
    {
        var (store, id, key) = UygulanmisVeGeriAlmaBekleyen();

        store.MarkNoBalance(id, key, expectedRevision: 0).Should().BeFalse();

        var job = store.Get(id)!;
        job.State.Should().Be(PaymentJobState.ReversePending);
        job.AppliedAmount.Should().Be(250m);
    }

    [Fact]
    public void MarkUncertain_GeriAlmaBeklerken_ReddederVeNiyetiKorur()
    {
        var (store, id, key) = UygulanmisVeGeriAlmaBekleyen();

        store.MarkUncertain(id, key, expectedRevision: 0).Should().BeFalse();

        store.Get(id)!.State.Should().Be(PaymentJobState.ReversePending);
    }

    [Fact] // R4-01 korunuyor: koruma normal replay'i fazla sıkılaştıramaz
    public void SonucYazicilari_GeriAlmaYokken_ReplayYazmayaDevamEder()
    {
        var store = CreateStore();
        var job = store.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        store.BeginApply(job.Id, key).Should().BeTrue();

        store.MarkApplied(job.Id, key, 0, 100m).Should().BeTrue();
        store.MarkApplied(job.Id, key, 0, 100m).Should().BeTrue();
        store.MarkUncertain(job.Id, key, 0).Should().BeTrue();
        store.MarkNoBalance(job.Id, key, 0).Should().BeTrue();
        store.Get(job.Id)!.State.Should().Be(PaymentJobState.NoBalance);
    }
}

public sealed class SqlitePaymentJobStoreContractTests : PaymentJobStoreContractTests
{
    protected override IPaymentJobStore CreateStore()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx).Run();
        return new PaymentJobRepository(fx);
    }
}

public sealed class InMemoryPaymentJobStoreContractTests : PaymentJobStoreContractTests
{
    protected override IPaymentJobStore CreateStore() => new InMemoryPaymentJobStore();
}
