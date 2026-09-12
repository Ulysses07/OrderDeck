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
        var key = Guid.NewGuid();
        repo.BeginApply(job.Id, key);
        repo.MarkApplied(job.Id, key, expectedRevision: 0, 42.5m).Should().BeTrue();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Applied);
        guncel.AppliedAmount.Should().Be(42.5m);
    }

    [Fact]
    public void MarkNoBalance_SifirTutarlaKesinlesir()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 100m);
        repo.MarkNoBalance(job.Id, expectedKey: null, expectedRevision: 0).Should().BeTrue();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.NoBalance);
        guncel.AppliedAmount.Should().Be(0m);
    }

    [Fact] // R4-01
    public void MarkApplied_GecikmisEskiCevap_YeniRevizyonunSonucunuEzemez()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var eskiKey = Guid.NewGuid();
        repo.BeginApply(job.Id, eskiKey);            // rev 0 — cevabı yolda takıldı

        // Üçüncü çağrı satışı 50'ye revize etti ve sonucu yazdı.
        var yeniKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 50m, yeniKey, expectedRevision: 0).Should().BeTrue();
        repo.MarkApplied(job.Id, yeniKey, expectedRevision: 1, 50m).Should().BeTrue();

        // Şimdi ilk çağrının 250'lik cevabı serbest kalıyor.
        repo.MarkApplied(job.Id, eskiKey, expectedRevision: 0, 250m).Should().BeFalse();

        var guncel = repo.Get(job.Id)!;
        guncel.Revision.Should().Be(1);
        guncel.ProductTotal.Should().Be(50m);
        guncel.AppliedAmount.Should().Be(50m,
            "gecikmiş cevap yeni revizyonun düşümünü ezerse net eksiye düşer");
    }

    [Fact] // R4-01 — aynı denemenin sonucunu ikinci kez yazmak zararsız tekrardır
    public void MarkApplied_AyniDenemeIkinciKez_YazmayaDevamEder()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        repo.BeginApply(job.Id, key);

        repo.MarkApplied(job.Id, key, expectedRevision: 0, 100m).Should().BeTrue();
        repo.MarkApplied(job.Id, key, expectedRevision: 0, 100m).Should().BeTrue();
        repo.Get(job.Id)!.AppliedAmount.Should().Be(100m);
    }

    [Fact] // R4-01 — "bakiye yok" cevabı da denemeye bağlı (anahtarsız iş dahil)
    public void MarkNoBalance_AnahtarsizIste_Calisir_BayatCevabi_Reddeder()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);

        // Önizleme 0 döndü: iş hâlâ anahtarsız (created) — koşul null anahtarla kurulur.
        repo.MarkNoBalance(job.Id, expectedKey: null, expectedRevision: 0).Should().BeTrue();
        repo.Get(job.Id)!.State.Should().Be(PaymentJobState.NoBalance);

        // Araya revizyon girdikten sonra aynı gözlem artık bayattır.
        repo.BeginRevision(job.Id, 300m, Guid.NewGuid(), expectedRevision: 0).Should().BeTrue();
        repo.MarkNoBalance(job.Id, expectedKey: null, expectedRevision: 0).Should().BeFalse();
        repo.Get(job.Id)!.State.Should().Be(PaymentJobState.ApplyUncertain);
    }

    [Fact] // R4-01 — bayat akış, güncel sonucu "bilinmiyor"a düşüremez
    public void MarkUncertain_BayatAkis_GuncelSonucuBozamaz()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var eskiKey = Guid.NewGuid();
        repo.BeginApply(job.Id, eskiKey);
        var yeniKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 50m, yeniKey, expectedRevision: 0).Should().BeTrue();
        repo.MarkApplied(job.Id, yeniKey, expectedRevision: 1, 50m).Should().BeTrue();

        repo.MarkUncertain(job.Id, eskiKey, expectedRevision: 0).Should().BeFalse();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Applied);
        guncel.AppliedAmount.Should().Be(50m);
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
        var ilkKey = Guid.NewGuid();
        repo.BeginApply(job.Id, ilkKey);
        repo.MarkApplied(job.Id, ilkKey, expectedRevision: 0, 40m);

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
        repo.MarkApplied(legacy.Id, legacyKey, expectedRevision: 0, 50m);

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

    // ── R4-02: geri alma niyeti ─────────────────────────────────────────────

    [Fact]
    public void BeginReversal_NiyetiYazar_AnahtarVeEskiToplamKorunur()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        repo.BeginApply(job.Id, key);
        repo.MarkApplied(job.Id, key, 0, 250m);

        repo.BeginReversal(job.Id, 0, key, targetTotal: 50m).Should().BeTrue();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.ReversePending);
        guncel.PendingTotal.Should().Be(50m);
        guncel.ProductTotal.Should().Be(250m, "eski düşümün hangi tutara ait olduğu korunur");
        guncel.ApplyKey.Should().Be(key, "geri alınacak işlem odur");
        guncel.AppliedAmount.Should().Be(250m);
    }

    [Fact]
    public void CompleteReversal_IsiTazeyeDondurur_AnahtariDusurur()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        repo.BeginApply(job.Id, key);
        repo.MarkApplied(job.Id, key, 0, 250m);
        repo.BeginReversal(job.Id, 0, key, 50m);

        repo.CompleteReversal(job.Id, 0, key).Should().BeTrue();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Created);
        guncel.ProductTotal.Should().Be(50m);
        guncel.PendingTotal.Should().BeNull();
        guncel.ApplyKey.Should().BeNull("geri alınmış anahtar bir daha replay edilemez");
        guncel.AppliedAmount.Should().BeNull();
        guncel.Revision.Should().Be(1, "uçuştaki eski cevaplar bayatlamalı");
    }

    [Fact]
    public void CompleteReversal_NiyetYokken_HicbirSeyYapmaz()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        repo.BeginApply(job.Id, key);
        repo.MarkApplied(job.Id, key, 0, 250m);

        // BeginReversal hiç çağrılmadı: iş applied. Uzlaştırma yokken tamamlama
        // çağrısı düşümü sessizce silerdi.
        repo.CompleteReversal(job.Id, 0, key).Should().BeFalse();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Applied);
        guncel.AppliedAmount.Should().Be(250m);
        guncel.ApplyKey.Should().Be(key);
    }

    [Fact]
    public void BeginReversal_BayatAkis_GuncelDenemeyiGeriAlmayaSokamaz()
    {
        var (_, repo) = Fx();
        var job = repo.FindOrCreate("c1", "session:s1", 250m);
        var eskiKey = Guid.NewGuid();
        repo.BeginApply(job.Id, eskiKey);

        var yeniKey = Guid.NewGuid();
        repo.BeginRevision(job.Id, 50m, yeniKey, expectedRevision: 0).Should().BeTrue();
        repo.MarkApplied(job.Id, yeniKey, 1, 50m).Should().BeTrue();

        repo.BeginReversal(job.Id, 0, eskiKey, 999m).Should().BeFalse();

        var guncel = repo.Get(job.Id)!;
        guncel.State.Should().Be(PaymentJobState.Applied);
        guncel.PendingTotal.Should().BeNull();
    }
}
