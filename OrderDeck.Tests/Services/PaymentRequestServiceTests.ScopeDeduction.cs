using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── W3-11: kapsam başına tek düşüm — apply'ın scope-has-deduction'ı ve kilit zaman aşımı (spec 2026-10-13 §6.6) ──
//
// Mod On'da sunucu, anahtarı yeni ve kapsamlı bir apply'ı asıl kayıt × kapsam kilidi altında yürütür: kapsamda geri
// alınmamış düşüm varsa 409 scope-has-deduction {transactionId, appliedAmount, productTotal} döner, hiçbir şey yazmaz;
// kilit 5 sn'de alınamazsa 409 stale (yine hiçbir şey yazılmadı). İstemci düşümü benimser ve revizyon adımına döner;
// miras işte benimsemez, #506'nın birleştirme sorusuna düşer. Stale bir kez yeniden denenir (en çok iki apply).
public partial class PaymentRequestServiceTests
{
    private static decimal AppliedAmountOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("amount").GetDecimal();
    }

    /// <summary>Önceki tıklamadan kalmış belirsiz iş (apply_uncertain, anahtar diskte) ya da taze kişi.</summary>
    private Guid? BelirsizIs(bool belirsiz, string customerId, decimal total)
    {
        if (!belirsiz) return null;
        var key = Guid.NewGuid();
        SeedJob(customerId, PaymentJobState.ApplyUncertain, total, null, key);
        return key;
    }

    [Fact]
    public async Task Apply_kapsamda_dusum_varsa_ayni_toplamda_benimser_tek_apply_mesaj_dusumle_gider()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var tx = Guid.NewGuid();
        handler.ApplyResponse = n => n == 1 ? WhatsAppStubHandler.ScopeHasDeduction(tx, 100m, 600m) : null;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 600m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle("kapsamdaki düşüm benimsendi — ikinci düşüm yazılmaz");
        handler.ReverseCalls.Should().BeEmpty("düşüm bu toplama ait");
        handler.TxStatusQueries.Should().BeEmpty("kilit altındaki güncel cevap — tekrar paylaşım doğrulaması gereksiz");
        _jobs.AdoptRemoteCalls.Should().Be(1);
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.Applied);
        job.ApplyKey.Should().Be(tx, "düşüm sunucudaki işlemin kimliğiyle tanınır");
        job.AppliedAmount.Should().Be(100m);
        job.ProductTotal.Should().Be(600m);
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().Contain("500%2C00", "600 − benimsenen 100");
    }

    [Fact]
    public async Task Apply_kapsamda_baska_toplamin_dusumu_varsa_benimser_geri_alir_yeni_anahtarla_uygular()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var tx = Guid.NewGuid();
        handler.ApplyResponse = n => n == 1 ? WhatsAppStubHandler.ScopeHasDeduction(tx, 100m, 600m) : null;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 650m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(tx,
            "benimsenen düşüm 600'e aitti — revizyon onu geri alır");
        handler.AppliedBalanceBodies.Should().HaveCount(2);
        var ilk = AppliedKey(handler.AppliedBalanceBodies[0]);
        var yeni = AppliedKey(handler.AppliedBalanceBodies[1]);
        yeni.Should().NotBe(ilk, "revizyon YENİ anahtarla taze düşümdür").And.NotBe(tx);
        AppliedAmountOf(handler.AppliedBalanceBodies[1]).Should().Be(650m);
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.Applied);
        job.ApplyKey.Should().Be(yeni);
        job.ProductTotal.Should().Be(650m);
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().Contain("550%2C00", "650 − taze 100");
    }

    [Fact] // Dönüş bir kez: aynı tıklamada ikinci benimseme başka toplamınsa mesaj durur (ping-pong yok)
    public async Task Apply_ayni_tiklamada_ikinci_kez_kapsamda_dusum_bulursa_toplam_tutmuyorsa_durur()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var tx1 = Guid.NewGuid();
        var tx2 = Guid.NewGuid();
        handler.ApplyResponse = n => n switch
        {
            1 => WhatsAppStubHandler.ScopeHasDeduction(tx1, 100m, 600m),
            2 => WhatsAppStubHandler.ScopeHasDeduction(tx2, 100m, 600m),
            _ => null,
        };
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 650m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        handler.AppliedBalanceBodies.Should().HaveCount(2, "ikinci benimsemeden sonra üçüncü apply yok");
        handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(tx1);
        _launcher.LaunchedUrls.Should().BeEmpty("650 istenirken 600'ün düşümü mesaja yazılmaz");
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.Applied);
        job.ApplyKey.Should().Be(tx2, "ikinci düşüm de benimsendi — sıradaki tıklama onu revize eder");
    }

    [Fact] // §10.3: miras iş scope-has-deduction aldığında benimsemez, birleştirme sorusuna düşer
    public async Task Miras_isin_replayi_kapsamda_dusum_bulursa_benimsemez_birlestirme_sorar_hicbir_sey_yazmaz()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var tx = Guid.NewGuid();
        handler.ApplyResponse = _ => WhatsAppStubHandler.ScopeHasDeduction(tx, 100m, 600m);
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        var legacyKey = Guid.NewGuid();
        var legacyId = Guid.NewGuid().ToString("N");
        _jobs.Seed(new PaymentJob(
            legacyId, customer.Id, $"legacy:{legacyKey:N}:{legacyId}", 600m, 0,
            legacyKey, null, PaymentJobState.ApplyUncertain, 1, 1, null, PriorScopeKey: "session:s1"));

        var outcome = await sut.RequestPaymentAsync(customer, _ => 600m, T, "session:s1");

        outcome.Result.Should().Be(PaymentRequestResult.MergeRequired);
        outcome.Prompt!.Kind.Should().Be(PersonScopePromptKind.Merge);
        outcome.Prompt.RemoteDeductions.Should().Equal(100m);
        outcome.Prompt.Requests.Should().BeEmpty("sunucu teslimi bilmez — iz gitmiş istek diye anlatılmaz");
        _jobs.AdoptRemoteCalls.Should().Be(0, "miras iş düşümü sahiplenmez — iki iş aynı düşümü sahiplenirdi");
        _launcher.LaunchedUrls.Should().BeEmpty();
        handler.AppliedBalanceBodies.Should().ContainSingle()
            .Which.Should().Contain(legacyKey.ToString(), "yalnız miras anahtarının replay'i");
        handler.ReverseCalls.Should().BeEmpty();
        var miras = _jobs.Get(legacyId)!;
        miras.State.Should().Be(PaymentJobState.ApplyUncertain);
        miras.ApplyKey.Should().Be(legacyKey);
        miras.ClosedAt.Should().BeNull();
        var kalan = _jobs.Snapshot.Single(j => j.ScopeKey == "session:s1");
        kalan.State.Should().Be(PaymentJobState.Created);
        kalan.ApplyKey.Should().BeNull();
    }

    [Fact] // Onay: miras anahtarı sunucuda hiç yazılmadı — iş kapanır, kapsamdaki düşüm kalan işe kapsamdan benimsenir
    public async Task Miras_birlestirmesi_onaylaninca_miras_kapanir_kalan_is_dusumu_kapsamdan_benimser()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var tx = Guid.NewGuid();
        handler.ApplyResponse = _ => WhatsAppStubHandler.ScopeHasDeduction(tx, 100m, 600m);
        handler.ScopeResponseJson = ScopeJson(tx, applied: 100m, total: 600m);
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        var legacyKey = Guid.NewGuid();
        var legacyId = Guid.NewGuid().ToString("N");
        _jobs.Seed(new PaymentJob(
            legacyId, customer.Id, $"legacy:{legacyKey:N}:{legacyId}", 600m, 0,
            legacyKey, null, PaymentJobState.ApplyUncertain, 1, 1, null, PriorScopeKey: "session:s1"));
        var dialogs = new FakeDialogService();

        (await EvetAsync(sut, customer, 600m, "session:s1", dialogs))
            .Should().Be(PaymentRequestResult.Opened);

        dialogs.Confirmations.Should().ContainSingle()
            .Which.Message.Should().Contain("bakiye düşümü bulundu").And.Contain("100,00 TL");
        handler.AppliedBalanceBodies.Should().HaveCount(2).And.OnlyContain(b => b.Contains(legacyKey.ToString()),
            "iki turda da yalnız miras anahtarı denendi — taze düşüm yok");
        handler.ReverseCalls.Should().BeEmpty("miras anahtarı yazılmadı, kapsamdaki düşüm kalır");
        var miras = _jobs.Get(legacyId)!;
        miras.ClosedAt.Should().NotBeNull();
        miras.State.Should().Be(PaymentJobState.NoBalance, "anahtarın düşümü yok");
        var kalan = _jobs.Snapshot.Single(j => j.ScopeKey == "session:s1");
        kalan.ApplyKey.Should().Be(tx);
        kalan.AppliedAmount.Should().Be(100m);
        _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("500%2C00");
    }

    [Theory] // §6.6: kilit zaman aşımı hiçbir şey yazmadı — hemen bir kez yeniden, en çok iki apply
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_stale_sonra_basari_iki_apply_mesaj_gider(bool belirsizIs)
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        handler.ApplyResponse = n => n == 1 ? WhatsAppStubHandler.ApplyStale() : null;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        var seeded = BelirsizIs(belirsizIs, customer.Id, 250m);

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().HaveCount(2);
        var keys = handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().ToList();
        keys.Should().ContainSingle("aynı denemenin tekrarı — yeni anahtar yok");
        if (seeded is { } key) keys[0].Should().Be(key);
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.Applied);
        job.AppliedAmount.Should().Be(100m);
        _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("150%2C00");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_stale_sonra_kapsamda_dusum_benimsenir(bool belirsizIs)
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var tx = Guid.NewGuid();
        handler.ApplyResponse = n => n switch
        {
            1 => WhatsAppStubHandler.ApplyStale(),
            2 => WhatsAppStubHandler.ScopeHasDeduction(tx, 40m, 250m),
            _ => null,
        };
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        BelirsizIs(belirsizIs, customer.Id, 250m);

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().HaveCount(2);
        handler.ReverseCalls.Should().BeEmpty();
        handler.TxStatusQueries.Should().BeEmpty();
        var job = _jobs.Snapshot.Single();
        job.ApplyKey.Should().Be(tx);
        job.AppliedAmount.Should().Be(40m);
        _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("210%2C00", "250 − benimsenen 40");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_iki_kez_stale_BalanceUncertain_is_anahtariyla_belirsiz_kalir_sonraki_tiklama_oynatir(
        bool belirsizIs)
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        handler.ApplyResponse = _ => WhatsAppStubHandler.ApplyStale();
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        var seeded = BelirsizIs(belirsizIs, customer.Id, 250m);

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        _launcher.LaunchedUrls.Should().BeEmpty("sonuç kesinleşmeden mesaj gitmez (fail-closed)");
        handler.AppliedBalanceBodies.Should().HaveCount(2, "bir deneme + bir kez yeniden — üçüncüsü yok");
        var key = AppliedKey(handler.AppliedBalanceBodies[0]);
        AppliedKey(handler.AppliedBalanceBodies[1]).Should().Be(key);
        if (seeded is { } s) key.Should().Be(s);
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.ApplyUncertain);
        job.ApplyKey.Should().Be(key, "anahtar diskte kalır — sıradaki tıklama aynı denemeyi oynatır");

        handler.ApplyResponse = null;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        handler.AppliedBalanceBodies.Should().HaveCount(3);
        AppliedKey(handler.AppliedBalanceBodies[2]).Should().Be(key);
    }

    [Fact] // Miras işin tek replay'i de kilit zaman aşımında bir kez yeniden denenir
    public async Task Miras_isin_replayi_stale_alirsa_bir_kez_yeniden_denenir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        handler.ApplyResponse = n => n == 1 ? WhatsAppStubHandler.ApplyStale() : null;
        var customer = MakeCustomer(Telefon, id: Guid.NewGuid().ToString("N"));
        var legacyKey = Guid.NewGuid();
        _jobs.Seed(new PaymentJob(
            Guid.NewGuid().ToString("N"), customer.Id, "legacy", 250m, 0,
            legacyKey, null, PaymentJobState.ApplyUncertain, 1, 1, null));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Select(AppliedKey).Should().Equal(legacyKey, legacyKey);
        _jobs.Snapshot.Single(j => j.ScopeKey == "legacy").ClosedAt.Should().NotBeNull();
        _jobs.Snapshot.Single(j => j.ScopeKey == "session:s1").ApplyKey.Should().Be(legacyKey);
    }
}
