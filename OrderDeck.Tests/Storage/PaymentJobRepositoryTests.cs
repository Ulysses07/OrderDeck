using System;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public sealed class PaymentJobRepositoryTests
{
    private static (InMemorySqlite Fx, PaymentJobRepository Repo) Fx()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx).Run();
        return (fx, new PaymentJobRepository(fx));
    }

    [Fact]
    public void FindOrCreate_IlkCagri_CreatedIsYaratir()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250.75m);

        job.CustomerId.Should().Be("c1");
        job.ScopeKey.Should().Be("session:s1");
        job.ProductTotal.Should().Be(250.75m); // TEXT gidiş-dönüş kayıpsız
        job.Revision.Should().Be(0);
        job.ApplyKey.Should().BeNull();
        job.AppliedAmount.Should().BeNull();
        job.State.Should().Be(PaymentJobState.Created);
        job.ClosedAt.Should().BeNull();
    }

    [Fact]
    public void FindOrCreate_AyniKapsamIkinciCagri_AyniSatiriDondurur()
    {
        var (_, repo) = Fx();
        var ilk = repo.FindOrCreate("c1", "session:s1", 100m);
        var ikinci = repo.FindOrCreate("c1", "session:s1", 999m); // farklı toplam bile olsa

        ikinci.Id.Should().Be(ilk.Id);
        ikinci.ProductTotal.Should().Be(100m); // INSERT OR IGNORE — mevcut satır kazanır
    }

    [Fact]
    public void FindOrCreate_FarkliKapsam_AyriIsYaratir()
    {
        var (_, repo) = Fx();
        var oturum = repo.FindOrCreate("c1", "session:s1", 100m);
        var kumulatif = repo.FindOrCreate("c1", "cumulative", 100m);
        kumulatif.Id.Should().NotBe(oturum.Id);
    }

    [Fact]
    public void BeginApply_IlkYazan_KazanirVeBelirsizeGecer()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        var kazanan = Guid.NewGuid();
        var kaybeden = Guid.NewGuid();

        repo.BeginApply(job.Id, kazanan).Should().BeTrue();
        repo.BeginApply(job.Id, kaybeden).Should().BeFalse(); // anahtar zaten diskte

        var guncel = repo.Get(job.Id)!;
        guncel.ApplyKey.Should().Be(kazanan); // kaybedenin anahtarı ASLA yazılmaz
        guncel.State.Should().Be(PaymentJobState.ApplyUncertain); // anahtar diskte = belirsiz
    }

    [Fact]
    public void MarkApplied_SonucuVeTutariYazar()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.BeginApply(job.Id, Guid.NewGuid());
        repo.MarkApplied(job.Id, 42.5m);

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Applied);
        guncel.AppliedAmount.Should().Be(42.5m);
    }

    [Fact]
    public void MarkNoBalance_SifirTutarlaKesinlesir()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.MarkNoBalance(job.Id);

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.NoBalance);
        guncel.AppliedAmount.Should().Be(0m);
    }

    [Fact]
    public void Close_Idempotent_IlkKapanisZamaniKorunur()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.Close(job.Id);
        var ilkKapanis = repo.Get(job.Id)!.ClosedAt;
        ilkKapanis.Should().NotBeNull();

        repo.Close(job.Id); // ikinci çağrı zamanı EZMEZ
        repo.Get(job.Id)!.ClosedAt.Should().Be(ilkKapanis);
    }

    [Fact]
    public void BeginRevision_BeklenenRevizyonTutuyorsa_YeniAnahtarVeToplamYazar()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.BeginApply(job.Id, Guid.NewGuid());
        repo.MarkApplied(job.Id, 40m);

        repo.Close(job.Id); // teslim edilmişti; tutar düzeltmesi işi yeniden açar (R2-02)

        var yeniKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 150m, yeniKey, expectedRevision: 0).Should().BeTrue();

        var guncel = repo.Get(job.Id)!;
        guncel.Revision.Should().Be(1);
        guncel.ProductTotal.Should().Be(150m);
        guncel.ApplyKey.Should().Be(yeniKey);
        guncel.AppliedAmount.Should().BeNull();
        guncel.State.Should().Be(PaymentJobState.ApplyUncertain); // yeni anahtar diskte = belirsiz
        guncel.ClosedAt.Should().BeNull("revizyon kapanmış işi yeniden açar");
    }

    [Fact]
    public void BeginRevision_RevizyonKaymissa_ReddederVeHicbirSeyiDegistirmez()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        var kazananKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 150m, kazananKey, expectedRevision: 0).Should().BeTrue();

        repo.BeginRevision(job.Id, 200m, Guid.NewGuid(), expectedRevision: 0).Should().BeFalse();

        var guncel = repo.Get(job.Id)!;
        guncel.Revision.Should().Be(1);
        guncel.ProductTotal.Should().Be(150m);
        guncel.ApplyKey.Should().Be(kazananKey);
    }

    [Fact]
    public void GetOpenLegacies_YalnizAcikLegacyIsleriniDondurur()
    {
        var (_, repo) = Fx();
        repo.GetOpenLegacies("c1").Should().BeEmpty();

        var legacy = repo.FindOrCreate("c1", "legacy", 100m);
        repo.GetOpenLegacies("c1").Should().ContainSingle().Which.Id.Should().Be(legacy.Id);

        repo.Close(legacy.Id);
        repo.GetOpenLegacies("c1").Should().BeEmpty(); // kapalı legacy görünmez
    }

    // R4-04: 034 artık her çözülmemiş anahtarı kendi 'legacy:{anahtar}' işine
    // taşıyor. Depo müşterinin TÜM açık miras işlerini görmek zorunda; birini
    // gözden kaçırmak, sunucudaki fazla düşümü yerelde izsiz bırakırdı.
    [Fact]
    public void GetOpenLegacies_AyniMusterininBirdenFazlaMirasIsiniDondurur()
    {
        var (_, repo) = Fx();
        var eski = repo.FindOrCreate("c1", $"legacy:{Guid.NewGuid():N}", 100m);
        var yeni = repo.FindOrCreate("c1", $"legacy:{Guid.NewGuid():N}", 250m);
        repo.FindOrCreate("c1", "session:s1", 40m);   // miras değil
        repo.FindOrCreate("c2", $"legacy:{Guid.NewGuid():N}", 10m); // başka müşteri

        repo.GetOpenLegacies("c1").Select(j => j.Id)
            .Should().BeEquivalentTo(new[] { eski.Id, yeni.Id });

        repo.Close(eski.Id);
        repo.GetOpenLegacies("c1").Should().ContainSingle().Which.Id.Should().Be(yeni.Id);
    }

    [Fact]
    public void AdoptLegacyResult_TazeHedefeSonucuKopyalarVeLegacyyiKapatir()
    {
        var (_, repo) = Fx();
        var legacy = repo.FindOrCreate("c1", "legacy", 250.75m);
        var legacyKey = Guid.NewGuid();
        repo.BeginApply(legacy.Id, legacyKey);
        repo.MarkApplied(legacy.Id, 50m);

        var hedef = repo.FindOrCreate("c1", "session:s1", 250.75m);
        repo.AdoptLegacyResult(hedef.Id, legacy.Id);

        var guncelHedef = repo.Get(hedef.Id)!;
        guncelHedef.ApplyKey.Should().Be(legacyKey);
        guncelHedef.AppliedAmount.Should().Be(50m);
        guncelHedef.State.Should().Be(PaymentJobState.Applied);
        guncelHedef.ProductTotal.Should().Be(250.75m);

        repo.Get(legacy.Id)!.ClosedAt.Should().NotBeNull();
        repo.GetOpenLegacies("c1").Should().BeEmpty();
    }

    [Fact]
    public void AdoptLegacyResult_HedefTazeDegilse_HedefiEzmezAmaLegacyyiKapatir()
    {
        var (_, repo) = Fx();
        var legacy = repo.FindOrCreate("c1", "legacy", 100m);
        repo.BeginApply(legacy.Id, Guid.NewGuid());

        var hedef = repo.FindOrCreate("c1", "session:s1", 100m);
        var hedefKey = Guid.NewGuid();
        repo.BeginApply(hedef.Id, hedefKey); // hedef kendi anahtarını yazmış

        repo.AdoptLegacyResult(hedef.Id, legacy.Id);

        repo.Get(hedef.Id)!.ApplyKey.Should().Be(hedefKey); // dokunulmadı
        repo.Get(legacy.Id)!.ClosedAt.Should().NotBeNull();
    }
}
