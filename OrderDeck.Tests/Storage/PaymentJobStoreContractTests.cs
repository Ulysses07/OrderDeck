using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Customers;
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

    /// <summary>R6-02: 250'lik iş uygulanmış (100 düşülmüş), anahtar K, rev 0.</summary>
    private (IPaymentJobStore Store, string JobId, Guid Key) Uygulanmis()
    {
        var store = CreateStore();
        var job = store.FindOrCreate("c1", "session:s1", 250m);
        var key = Guid.NewGuid();
        store.BeginApply(job.Id, key).Should().BeTrue();
        store.MarkApplied(job.Id, key, expectedRevision: 0, 100m).Should().BeTrue();
        return (store, job.Id, key);
    }

    [Fact] // R6-02: dış iade benimseme — iş anahtarsız created'a döner, tutar korunur
    public void AdoptExternalReversal_UygulanmisIsi_AnahtarsizCreatedaDondurur()
    {
        var (store, id, key) = Uygulanmis();

        store.AdoptExternalReversal(id, key, expectedRevision: 0).Should().BeTrue();

        var job = store.Get(id)!;
        job.State.Should().Be(PaymentJobState.Created);
        job.ApplyKey.Should().BeNull("bir sonraki tıklama taze anahtarla başlamalı");
        job.AppliedAmount.Should().BeNull();
        job.Revision.Should().Be(1, "eski denemenin geç cevabı bayatlamalı");
        job.ProductTotal.Should().Be(250m, "dış iade satış tutarını değiştirmez");
        job.PendingTotal.Should().BeNull();
        job.ClosedAt.Should().BeNull();
    }

    [Fact] // R6-02: bayat anahtar/revizyon dokunamaz
    public void AdoptExternalReversal_BayatKimlik_ReddederVeDokunmaz()
    {
        var (store, id, key) = Uygulanmis();

        store.AdoptExternalReversal(id, Guid.NewGuid(), expectedRevision: 0)
            .Should().BeFalse("başka denemenin anahtarı işi sıfırlayamaz");
        store.AdoptExternalReversal(id, key, expectedRevision: 1)
            .Should().BeFalse("bayat revizyon işi sıfırlayamaz");

        var job = store.Get(id)!;
        job.State.Should().Be(PaymentJobState.Applied);
        job.AppliedAmount.Should().Be(100m);
        job.Revision.Should().Be(0);
    }

    [Fact] // R6-02 × R6-03: geri alma niyeti dış iade benimsemesiyle de ezilemez
    public void AdoptExternalReversal_GeriAlmaBeklerken_ReddederVeNiyetiKorur()
    {
        var (store, id, key) = UygulanmisVeGeriAlmaBekleyen();

        store.AdoptExternalReversal(id, key, expectedRevision: 0).Should().BeFalse(
            "niyet silinirse iade adımı bir daha çalışmaz");

        var job = store.Get(id)!;
        job.State.Should().Be(PaymentJobState.ReversePending);
        job.PendingTotal.Should().Be(50m);
        job.AppliedAmount.Should().Be(250m);
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

    [Fact] // U8 (Bölüm C): taşımanın mirasa çevirip kapattığı işe uçuştaki akış anahtar yazamaz
    public void BeginApply_KapaliIste_Reddeder()
    {
        var store = CreateStore();
        var job = store.FindOrCreate("c1", "session:s1", 250m);
        store.Close(job.Id);

        store.BeginApply(job.Id, Guid.NewGuid()).Should().BeFalse(
            "kapalı (ör. taşımanın mirasa çevirip kapattığı) işte düşüm hiç uzlaşmazdı (U8)");
        var sonra = store.Get(job.Id)!;
        sonra.ApplyKey.Should().BeNull();
        sonra.State.Should().Be(PaymentJobState.Created);
    }

    // ── Kişi × kapsam (FindOrCreateForPerson) ────────────────────────────
    //
    // Mantıksal kimlik (kişi, kapsam): iş, sahip satırın kişisindeki bütün üyeler taranarak bulunur.
    // İki depo da AYNI planı (PersonScopePlan) uygular; üye kümesi işlemin içinde yeniden okunur.

    /// <summary>Aynı kişide (tek grup) <paramref name="count"/> üye açar; dönen kişide tıklanan
    /// satır ilk üyedir.</summary>
    protected abstract PaymentPerson NewPerson(IPaymentJobStore store, int count);

    /// <summary>Kişiye sonradan üye ekler (elle birleştirme) — akışın anlık görüntüsü bayatlar.</summary>
    protected abstract string JoinNewMember(IPaymentJobStore store, PaymentPerson person);

    /// <summary>Kişiyi güncel üyeleriyle yeniden okur (akışın yeniden denemesi).</summary>
    protected abstract PaymentPerson Reload(IPaymentJobStore store, PaymentPerson person);

    private static PersonJobRequest Req(PaymentPerson p, string scope = "session:s1", decimal total = 100m,
        string? leasedId = null, string? token = null)
        => new(leasedId ?? p.LeasedId, p.MemberKey, p.MemberIds, p.OwnerId!, scope, total,
            token is null ? null : new HashSet<string> { token });

    private static string NotOwner(PaymentPerson p) => p.MemberIds.First(id => id != p.OwnerId);

    /// <summary><paramref name="customerId"/>'de uygulanmış (ve istenirse teslim edilmiş) iş.</summary>
    private static PaymentJob Applied(IPaymentJobStore store, string customerId, decimal applied, bool delivered,
        string scope = "session:s1")
    {
        var job = store.FindOrCreate(customerId, scope, 300m);
        var key = Guid.NewGuid();
        store.BeginApply(job.Id, key).Should().BeTrue();
        store.MarkApplied(job.Id, key, 0, applied).Should().BeTrue();
        if (delivered) store.MarkDelivered(job.Id, 300m - applied);
        return store.Get(job.Id)!;
    }

    [Fact]
    public void FindOrCreateForPerson_is_yokken_sahipte_olusturur()
    {
        var store = CreateStore();
        var person = NewPerson(store, 3);

        var r = store.FindOrCreateForPerson(Req(person));

        r.Status.Should().Be(PersonJobStatus.Ready);
        r.Job!.CustomerId.Should().Be(person.OwnerId, "yeni iş belirlenimli sahip satırda açılır");
        r.Job.State.Should().Be(PaymentJobState.Created);
        r.Job.ProductTotal.Should().Be(100m);
        r.Demoted.Should().BeEmpty();
    }

    [Fact] // Repro A: birincil kaydı — iş başka üyede açılmıştı, aynı iş bulunur
    public void FindOrCreateForPerson_baska_uyedeki_isi_bulur()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var onceki = store.FindOrCreate(NotOwner(person), "session:s1", 100m);

        var r = store.FindOrCreateForPerson(Req(person));

        r.Status.Should().Be(PersonJobStatus.Ready);
        r.Job!.Id.Should().Be(onceki.Id, "işin sahibi sahip kuralından farklı olsa da kişinin işidir");
        r.Job.CustomerId.Should().Be(NotOwner(person), "kalan iş sahip satırını korur — para anahtarı");
    }

    [Fact] // Sahip kararı 2: teslim edilmiş istek varsa ağ/para adımından önce sorulur
    public void FindOrCreateForPerson_kapali_isi_bulur_yeni_is_acmaz_once_sorar()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var teslim = Applied(store, NotOwner(person), 40m, delivered: true);

        var soru = store.FindOrCreateForPerson(Req(person));

        soru.Status.Should().Be(PersonJobStatus.ConfirmationRequired);
        soru.Plan!.Kind.Should().Be(PersonScopePromptKind.Resend);
        soru.Plan.PromptJobs.Should().ContainSingle().Which.Id.Should().Be(teslim.Id);
        store.Get(teslim.Id).Should().Be(teslim, "soru sorulurken hiçbir şey yazılmaz");

        var onay = store.FindOrCreateForPerson(Req(person, token: soru.Plan.Token));

        onay.Status.Should().Be(PersonJobStatus.Ready);
        onay.Job!.Id.Should().Be(teslim.Id, "tekrar paylaşım — yeni iş açılmaz");
    }

    [Fact] // Onay belirteci durumun parmak izidir: arada iş değişirse yeniden sorulur
    public void FindOrCreateForPerson_bayat_belirtecle_yeniden_sorar()
    {
        var store = CreateStore();
        var person = NewPerson(store, 1);
        var job = Applied(store, person.LeasedId, 40m, delivered: true);
        var token = store.FindOrCreateForPerson(Req(person)).Plan!.Token;

        store.BeginRevision(job.Id, 120m, Guid.NewGuid(), job.Revision).Should().BeTrue();
        store.MarkDelivered(job.Id, 120m);

        store.FindOrCreateForPerson(Req(person, token: token)).Status
            .Should().Be(PersonJobStatus.ConfirmationRequired, "operatör bu durumu görmedi");
    }

    // Eşzamanlılık incelemesi: tekrar paylaşım (aynı tutar) finansal hiçbir şey yazmaz — eskiden işin
    // parmak izi değişmiyordu (Close ilk ClosedAt'ı korur) ve teslimden ÖNCE alınmış ikinci onay hâlâ
    // tutuyordu: iki çekmeceden iki "Evet" aynı soruyla iki ücretli mesaj daha gönderiyordu. Her teslim
    // teslim kaydını yeniler; aynı saniyedeki iki teslimi de sayaç ayırır.
    [Fact]
    public void FindOrCreateForPerson_tekrar_paylasimdan_once_alinan_belirtec_tutmaz()
    {
        var store = CreateStore();
        var person = NewPerson(store, 1);
        var job = Applied(store, person.LeasedId, 40m, delivered: true);
        var ilkTeslim = store.Get(job.Id)!;
        var token = store.FindOrCreateForPerson(Req(person)).Plan!.Token;
        store.FindOrCreateForPerson(Req(person)).Plan!.Token.Should().Be(token, "durum değişmedikçe belirteç aynı");

        store.MarkDelivered(job.Id, 260m);                     // öbür çekmecenin onayıyla tekrar gönderildi

        var sonra = store.Get(job.Id)!;
        sonra.Deliveries.Should().Be(ilkTeslim.Deliveries + 1);
        sonra.DeliveredAt.Should().BeGreaterThanOrEqualTo(ilkTeslim.DeliveredAt!.Value, "soru SON gönderimi gösterir");
        sonra.ClosedAt.Should().Be(ilkTeslim.ClosedAt, "bakiye yaşam döngüsünün kapanışı değişmez");
        var soru = store.FindOrCreateForPerson(Req(person, token: token));
        soru.Status.Should().Be(PersonJobStatus.ConfirmationRequired, "bayat onay ikinci bir gönderime izin vermez");
        soru.Plan!.Token.Should().NotBe(token);
    }

    // Çevrimdışı gönderilen taze iş: teslim kaydı yazılır ama iş AÇIK kalır — kapalı işe anahtar
    // yazılmaz (U8); onaylı sonraki tıklama çevrimiçi düşümle sürdürür.
    [Fact]
    public void MarkDelivered_taze_isi_kapatmaz_teslimi_yazar()
    {
        var store = CreateStore();
        var job = store.FindOrCreate("c1", "session:s1", 250m);

        store.MarkDelivered(job.Id, 250m);

        var sonra = store.Get(job.Id)!;
        sonra.IsDelivered.Should().BeTrue();
        sonra.DeliveredNet.Should().Be(250m);
        sonra.ClosedAt.Should().BeNull("taze iş açık kalır");
        store.BeginApply(job.Id, Guid.NewGuid()).Should().BeTrue("onaylı sonraki tıklama düşüm yapabilir");
    }

    [Fact] // Kalan iş: para taşıyan önce — sahibin el değmemiş işi tercih edilmez; parasız kaybeden sessizce kapanır
    public void FindOrCreateForPerson_parali_isi_tutar_parasiz_kaybedeni_sorusuz_kapatir()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var bos = store.FindOrCreate(person.OwnerId!, "session:s1", 100m);   // sahibin el değmemiş işi
        var parali = Applied(store, NotOwner(person), 40m, delivered: false);

        var r = store.FindOrCreateForPerson(Req(person));

        r.Status.Should().Be(PersonJobStatus.Ready, "parasız ve teslim edilmemiş kaybeden soru gerektirmez");
        r.Job!.Id.Should().Be(parali.Id, "bakiye satır başına: düşüm yapılmış iş kalır");
        r.Demoted.Should().ContainSingle().Which.Should().Be(
            new DemotedJob(bos.Id, bos.CustomerId, "session:s1", KeptOpen: false));
        var indirilen = store.Get(bos.Id)!;
        indirilen.ScopeKey.Should().Be($"legacy:{bos.Id}:{bos.Id}");
        indirilen.PriorScopeKey.Should().Be("session:s1");
        indirilen.ClosedAt.Should().NotBeNull("parasız kaybeden kapanır");
        store.GetOpenLegacies(person.MemberIds).Should().BeEmpty();
    }

    [Fact] // Eşitlikte (ikisi de parasız, teslim edilmemiş) sahibin işi kalır — daha yeni olsa da
    public void FindOrCreateForPerson_esitlikte_sahibin_isini_tutar()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var eski = store.FindOrCreate(NotOwner(person), "session:s1", 100m);
        var sahibin = store.FindOrCreate(person.OwnerId!, "session:s1", 100m);

        var r = store.FindOrCreateForPerson(Req(person));

        r.Status.Should().Be(PersonJobStatus.Ready, "parasız ve teslim edilmemiş kaybeden sorusuz iner");
        r.Job!.Id.Should().Be(sahibin.Id, "iki bilgisayar aynı (WpfCustomerId, ScopeKey) anahtarında buluşsun");
        r.Demoted.Should().ContainSingle().Which.JobId.Should().Be(eski.Id);
    }

    [Fact] // Sahip kararı 4: para taşıyan/teslim edilmiş kaybeden varsa birleştirme sorulur; onayda açık mirasa iner
    public void FindOrCreateForPerson_iki_parali_is_sorar_onayda_kaybedeni_acik_legacy_yapar()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var eski = Applied(store, person.OwnerId!, 100m, delivered: true);
        var belirsiz = store.FindOrCreate(NotOwner(person), "session:s1", 200m);
        var belirsizKey = Guid.NewGuid();
        store.BeginApply(belirsiz.Id, belirsizKey).Should().BeTrue();

        var soru = store.FindOrCreateForPerson(Req(person));

        soru.Status.Should().Be(PersonJobStatus.ConfirmationRequired);
        soru.Plan!.Kind.Should().Be(PersonScopePromptKind.Merge);
        soru.Plan.PromptJobs.Select(j => j.Id).Should().Equal(new[] { eski.Id },
            "yalnız teslim edilmiş istek 'gitmiş' sayılır");
        soru.Plan.UnsentDeductions.Should().Be(1, "belirsiz düşüm gönderilmedi — ayrıca söylenir");
        soru.Plan.RefundsToOtherRow.Should().BeTrue("kaybedenin düşümü kalan işin satırında değil");
        store.GetOpenLegacies(person.MemberIds).Should().BeEmpty("onaydan önce indirme yok");

        var onay = store.FindOrCreateForPerson(Req(person, token: soru.Plan.Token));

        onay.Status.Should().Be(PersonJobStatus.Ready);
        onay.Job!.Id.Should().Be(eski.Id, "ikisi de para taşıyor: en son teslim edilen kalır");
        onay.Demoted.Should().ContainSingle().Which.Should().Be(
            new DemotedJob(belirsiz.Id, belirsiz.CustomerId, "session:s1", KeptOpen: true));
        var legacy = store.GetOpenLegacies(person.MemberIds).Should().ContainSingle().Subject;
        legacy.Id.Should().Be(belirsiz.Id);
        legacy.ScopeKey.Should().Be($"legacy:{belirsizKey:N}:{belirsiz.Id}");
        legacy.CustomerId.Should().Be(belirsiz.CustomerId, "miras iş kendi sahibiyle uzlaşır");
    }

    [Fact]
    public void FindOrCreateForPerson_legacy_kapsamlari_saymaz()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        // Miras işi depo yüzeyinden tohumlanamaz; kişi birleştirmesiyle oluşturulur (parasız kaybeden).
        var kalan = Applied(store, person.OwnerId!, 10m, delivered: false);
        var kaybeden = store.FindOrCreate(NotOwner(person), "session:s1", 100m);
        store.FindOrCreateForPerson(Req(person)).Demoted.Should().ContainSingle();
        store.Get(kaybeden.Id)!.ScopeKey.Should().StartWith("legacy:");

        var r = store.FindOrCreateForPerson(Req(person));

        r.Status.Should().Be(PersonJobStatus.Ready, "miras iş kişi × kapsamın işi sayılmaz — ikinci tur birleştirme yok");
        r.Job!.Id.Should().Be(kalan.Id);
        r.Demoted.Should().BeEmpty();
    }

    [Fact]
    public void FindOrCreateForPerson_tek_uyede_eski_FindOrCreate_ile_esdeger()
    {
        var store = CreateStore();
        var person = NewPerson(store, 1);

        var r = store.FindOrCreateForPerson(Req(person, total: 77m));
        var eski = store.FindOrCreate(person.LeasedId, "session:s1", 999m);

        eski.Id.Should().Be(r.Job!.Id);
        eski.ProductTotal.Should().Be(77m);
    }

    [Fact] // Tasarım 2 D-1: üye kümesi işlemin İÇİNDE yeniden okunur
    public void FindOrCreateForPerson_uye_kumesi_degistiyse_hicbir_sey_yazmaz()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var yeni = JoinNewMember(store, person);
        store.FindOrCreate(yeni, "session:s1", 50m);

        var r = store.FindOrCreateForPerson(Req(person));

        r.Status.Should().Be(PersonJobStatus.MembershipChanged);
        r.Job.Should().BeNull();

        // Akış kişiyi yeniden okur: bayat çağrı iş açmadığı için tek iş vardır, birleştirme yok.
        var guncel = Reload(store, person);
        guncel.MemberIds.Should().Contain(yeni);
        var tekrar = store.FindOrCreateForPerson(Req(guncel));
        tekrar.Status.Should().Be(PersonJobStatus.Ready);
        tekrar.Job!.CustomerId.Should().Be(yeni, "yeni üyenin işi bulunur");
        tekrar.Demoted.Should().BeEmpty("bayat çağrı ikinci bir iş açmadı");
    }

    [Fact]
    public void GetOpenLegacies_kisinin_butun_uyelerini_tarar()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        Applied(store, person.OwnerId!, 10m, delivered: true);
        var b = Applied(store, NotOwner(person), 20m, delivered: false);
        var plan = store.FindOrCreateForPerson(Req(person)).Plan!;
        store.FindOrCreateForPerson(Req(person, token: plan.Token)).Status.Should().Be(PersonJobStatus.Ready);

        store.GetOpenLegacies(person.MemberIds).Should().ContainSingle().Which.Id.Should().Be(b.Id);
        store.GetOpenLegacies(person.OwnerId!).Should().BeEmpty("tek satır biçimi yalnız o satırı tarar");
    }

    [Fact]
    public void ReassignOwner_yalniz_taze_iste()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        var taze = store.FindOrCreate(person.OwnerId!, "session:s1", 100m);

        store.ReassignOwner(taze.Id, NotOwner(person)).Should().BeTrue();
        store.Get(taze.Id)!.CustomerId.Should().Be(NotOwner(person));

        var anahtarli = Applied(store, person.OwnerId!, 10m, delivered: false, scope: "session:s2");
        store.ReassignOwner(anahtarli.Id, NotOwner(person)).Should().BeFalse("denenmiş işin para anahtarı değişmez");

        var cakisan = store.FindOrCreate(person.OwnerId!, "session:s1", 100m);
        store.ReassignOwner(cakisan.Id, NotOwner(person)).Should().BeFalse("hedefte aynı kapsamlı iş var");
    }

    [Fact] // Başka sahip satırın miras sonucu benimsenmez; legacy de kapanmaz
    public void AdoptLegacyResult_farkli_sahipte_hicbir_sey_yazmaz()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        Applied(store, person.OwnerId!, 10m, delivered: true);
        var b = Applied(store, NotOwner(person), 20m, delivered: false);
        var plan = store.FindOrCreateForPerson(Req(person)).Plan!;
        store.FindOrCreateForPerson(Req(person, token: plan.Token));
        var hedef = store.FindOrCreate(person.OwnerId!, "session:s9", 100m);

        store.AdoptLegacyResult(hedef.Id, b.Id);

        store.Get(hedef.Id)!.ApplyKey.Should().BeNull("başka satırın anahtarı benimsenmez");
        store.Get(b.Id)!.ClosedAt.Should().BeNull("geri alınmamış düşüm gömülmez");
    }

    /// <summary>Kişi birleştirmesiyle <paramref name="kaybeden"/>'i miras kapsamına indirir (onaylı).
    /// Kalan iş sahibinin teslim edilmiş, para taşıyan işi.</summary>
    private static PaymentJob Indir(IPaymentJobStore store, PaymentPerson person)
    {
        var plan = store.FindOrCreateForPerson(Req(person)).Plan!;
        var r = store.FindOrCreateForPerson(Req(person, token: plan.Token));
        r.Status.Should().Be(PersonJobStatus.Ready);
        return store.Get(r.Demoted.Single().JobId)!;
    }

    // Para incelemesi (kritik): asıl kapsamı bilinen miras iş yalnız KENDİ kapsamının işine benimsenir —
    // başka yayının düşümü bu satışa taşınıp revizyonla geri alınsaydı o yayının ürünleri için
    // kullanılan bakiye iade edilip burada yeniden harcanırdı.
    [Fact]
    public void AdoptLegacyResult_baska_kapsamin_isine_benimsenmez_kendi_kapsaminda_benimsenir()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        Applied(store, person.OwnerId!, 10m, delivered: true);
        Applied(store, NotOwner(person), 20m, delivered: false);
        var miras = Indir(store, person);
        miras.PriorScopeKey.Should().Be("session:s1");

        var baskaYayin = store.FindOrCreate(miras.CustomerId, "session:s2", 100m);
        store.AdoptLegacyResult(baskaYayin.Id, miras.Id);
        store.Get(baskaYayin.Id)!.ApplyKey.Should().BeNull("başka yayının düşümü bu satışa taşınmaz");
        store.Get(miras.Id)!.ClosedAt.Should().BeNull("geri alınmamış düşüm gömülmez");

        var ayniYayin = store.FindOrCreate(miras.CustomerId, "session:s1", 100m);
        store.AdoptLegacyResult(ayniYayin.Id, miras.Id);
        store.Get(ayniYayin.Id)!.ApplyKey.Should().Be(miras.ApplyKey, "kendi kapsamının taze işine benimsenir");
        store.Get(miras.Id)!.ClosedAt.Should().NotBeNull();
    }

    // Test/arayüz incelemesi: indirilen işin teslim izi korunur. Kapalı ama teslim edilmiş miras iş
    // (parasız kaybeden) kapsamın sıradaki tıklamasında "istek gitti" bilgisidir — kalan iş hiç teslim
    // edilmemiş olsa da tekrar göndermeden önce sorulur.
    [Fact]
    public void FindOrCreateForPerson_kapsamin_teslim_edilmis_miras_isi_tekrar_gondermeyi_sordurur()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        Applied(store, person.OwnerId!, 10m, delivered: false);          // kalan: para taşıyor, gitmedi
        var parasiz = store.FindOrCreate(NotOwner(person), "session:s1", 100m);
        store.MarkNoBalance(parasiz.Id, null, 0).Should().BeTrue();
        store.MarkDelivered(parasiz.Id, 100m);                          // gitti, parasız → kapanarak iner
        var miras = Indir(store, person);
        miras.Id.Should().Be(parasiz.Id);
        miras.ClosedAt.Should().NotBeNull();
        miras.IsDelivered.Should().BeTrue("teslim izi indirmede korunur");

        var soru = store.FindOrCreateForPerson(Req(person));

        soru.Status.Should().Be(PersonJobStatus.ConfirmationRequired);
        soru.Plan!.Kind.Should().Be(PersonScopePromptKind.Resend);
        soru.Plan.PromptJobs.Should().ContainSingle().Which.Id.Should().Be(miras.Id);
        store.FindOrCreateForPerson(Req(person, token: soru.Plan.Token)).Status.Should().Be(PersonJobStatus.Ready);
        store.FindOrCreateForPerson(Req(person, scope: "session:s2")).Status
            .Should().Be(PersonJobStatus.Ready, "başka kapsamın mirası bu kapsamda sorulmaz");
    }

    // Uzlaşmamış (açık) ve müşteriye gitmiş miras iş kapsamın tıklamasında birleştirme sorusunu tetikler
    // (geri alınacak); gitmemiş açık miras sorusuz uzlaşır.
    [Fact]
    public void FindOrCreateForPerson_acik_teslim_edilmis_miras_birlestirme_sordurur()
    {
        var store = CreateStore();
        var person = NewPerson(store, 2);
        Applied(store, person.OwnerId!, 10m, delivered: true);
        Applied(store, NotOwner(person), 20m, delivered: true);
        var miras = Indir(store, person);
        miras.ClosedAt.Should().BeNull("para taşıyan kaybeden açık kalır");

        var soru = store.FindOrCreateForPerson(Req(person));

        soru.Status.Should().Be(PersonJobStatus.ConfirmationRequired);
        soru.Plan!.Kind.Should().Be(PersonScopePromptKind.Merge);
        soru.Plan.PendingLegacies.Should().ContainSingle().Which.Id.Should().Be(miras.Id);
        soru.Plan.PromptJobs.Should().HaveCount(2, "iki istek de müşteriye gitti");
        var onay = store.FindOrCreateForPerson(Req(person, token: soru.Plan.Token));
        onay.Status.Should().Be(PersonJobStatus.Ready);
        onay.Demoted.Should().BeEmpty("miras iş yeniden indirilmez; uzlaştırma servisin işi");
    }
}

public sealed class SqlitePaymentJobStoreContractTests : PaymentJobStoreContractTests
{
    private InMemorySqlite? _fx;

    protected override IPaymentJobStore CreateStore()
    {
        _fx = new InMemorySqlite();
        new MigrationRunner(_fx).Run();
        return new PaymentJobRepository(_fx);
    }

    private CustomerRepository Customers => new(_fx!);

    private string Insert(string groupId)
    {
        var id = Guid.NewGuid().ToString("N");
        Customers.Insert(new Customer(id, "instagram", "u" + id[..10], null, null, 1, 1, false, null, null,
            0, 0m, null, null, null, GroupId: groupId));
        return id;
    }

    protected override PaymentPerson NewPerson(IPaymentJobStore store, int count)
    {
        var group = Guid.NewGuid().ToString("N");
        var ids = Enumerable.Range(0, count).Select(_ => Insert(group)).ToList();
        return Customers.GetPaymentPerson(ids[0]);
    }

    protected override string JoinNewMember(IPaymentJobStore store, PaymentPerson person)
    {
        var group = Customers.GetById(person.LeasedId)!.GroupId!;
        return Insert(group);
    }

    protected override PaymentPerson Reload(IPaymentJobStore store, PaymentPerson person)
        => Customers.GetPaymentPerson(person.LeasedId);
}

public sealed class InMemoryPaymentJobStoreContractTests : PaymentJobStoreContractTests
{
    private readonly Dictionary<string, List<string>> _people = new();

    protected override IPaymentJobStore CreateStore()
        => new InMemoryPaymentJobStore
        {
            MembersOf = id => _people.Values.FirstOrDefault(m => m.Contains(id)) ?? new List<string> { id },
        };

    protected override PaymentPerson NewPerson(IPaymentJobStore store, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString("N")).ToList();
        _people[ids[0]] = ids;
        return new PaymentPerson(ids[0], ids.Select(id => new PaymentPersonMember(id, null, false)).ToList());
    }

    protected override string JoinNewMember(IPaymentJobStore store, PaymentPerson person)
    {
        var id = Guid.NewGuid().ToString("N");
        _people.Values.First(m => m.Contains(person.LeasedId)).Add(id);
        return id;
    }

    protected override PaymentPerson Reload(IPaymentJobStore store, PaymentPerson person)
        => new(person.LeasedId, _people.Values.First(m => m.Contains(person.LeasedId))
            .Select(id => new PaymentPersonMember(id, null, false)).ToList());
}
