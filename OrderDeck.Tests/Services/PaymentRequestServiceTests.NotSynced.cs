using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── W-havuz: 409 customer-not-synced → BalanceNotSynced (spec 2026-10-11 §6.1-b, K2, §8.4-2) ─────────────────────
//
// Havuz kipinde sunucu, kişinin bir üyesini henüz tanımıyorsa (birleştirme ya da ayırma sunucuya ulaşmadı) önizlemeye ve
// yeni anahtarlı apply'a 409 customer-not-synced döner. Bu "bakiye yok" DEĞİLDİR: MarkNoBalance yazılırsa iş kesinleşir
// ve sunucu eşitlendikten sonra da bakiye bir daha sorulmaz (K2'nin önlediği kalıcı kayıp). Mesaj gitmez, iş taze ya da
// anahtarlı-belirsiz kalır; sonraki tıklama kaldığı yerden sürdürür. Operatöre "Henüz eşitlenmedi" uyarısı; aynı kişide 5
// dakikadan uzun sürerse ayrı metin ve günlüğe kişinin satır Id'leri. Öbür yanıtlar bugünkü genel belirsiz kolda kalır:
// client-update-required (spec §6.1-c: genel BalanceUncertain yolu yeterli), member-ids-required (S2 saha notu 5 kesin
// sonuçtan ayrılmasını ister — genel kol MarkNoBalance yazmaz), balance-busy (S2 saha notu 5: eşlenmez).
public partial class PaymentRequestServiceTests
{
    [Fact] // §6.1-b (K2): önizlemenin customer-not-synced'i ayrı sonuç — MarkNoBalance yok, iş taze, mesaj gitmez
    public async Task Onizleme_customer_not_synced_BalanceNotSynced_is_taze_mesaj_gitmez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            outcome.Message.Should().BeNull("ilk görülme: kısa metni sunum katmanı kurar");
            outcome.OwnerId.Should().Be(b.Id, "banka W2 açısından BalanceUncertain gibi: sahip satırı taşınır");
            handler.PreviewCustomerIds.Should().ContainSingle("önizleme soruldu ve 409 aldı");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı — iş taze kaldı");
            job.ApplyKey.Should().BeNull("anahtar yazılmadı");
            handler.AppliedBalanceBodies.Should().BeEmpty();
            handler.SentBodies.Should().BeEmpty();
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj gönderilmedi");

            // Sunucu eşitlendi: aynı iş bakiyeyi düşer, mesaj gider ("bakiye yok" diye kesinleşmemişti).
            handler.PreviewResponse = null;
            var sonra = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            sonra.Result.Should().Be(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().ContainSingle();
            db.Jobs.Get(job.Id)!.State.Should().Be(PaymentJobState.Applied);
            _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("150%2C00", "250 − 100");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b (K2): apply'ın customer-not-synced'i — iş anahtarıyla belirsiz bekler, sonraki tıklama aynı anahtarla oynatır
    public async Task Apply_customer_not_synced_is_anahtarli_belirsiz_kalir_sonraki_tiklama_ayni_anahtarla_oynatir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            // İlk üç apply 409: ilk tıklamanın iki denemesi (SettleAsync yolu) + ikinci tıklamanın tek oynatması (belirsiz işin
            // 3. adımı). Dördüncü apply (üçüncü tıklama) başarılı.
            handler.ApplyResponse = n => n <= 3 ? WhatsAppStubHandler.Problem("customer-not-synced") : null;

            var ilk = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            ilk.Result.Should().Be(PaymentRequestResult.BalanceNotSynced, "SettleAsync yolu bayrağı taşır");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.ApplyUncertain, "anahtar diskte, sonuç bilinmiyor — MarkNoBalance yazılmadı");
            job.ApplyKey.Should().NotBeNull();
            handler.AppliedBalanceBodies.Should().HaveCount(2, "K3: bir deneme + bir anında tekrar, ikisi de 409");
            handler.AppliedBalanceBodies.Select(AppliedKey).Should().OnlyContain(k => k == job.ApplyKey!.Value,
                "anında tekrar aynı anahtarla");
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj gönderilmedi");

            // İkinci tıklama: belirsiz işin oynatması (3. adım, ReplayOnceMoreIfStaleAsync) yine 409 — stale değil, tek apply.
            var ikinci = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            ikinci.Result.Should().Be(PaymentRequestResult.BalanceNotSynced,
                "belirsiz işin oynatması da bayrağı taşır — 'Bakiye doğrulanamadı' değil");
            handler.AppliedBalanceBodies.Should().HaveCount(3, "belirsiz işin tek oynatması — stale değil, anında tekrar yok");
            AppliedKey(handler.AppliedBalanceBodies[2]).Should().Be(job.ApplyKey!.Value, "oynatma aynı anahtarla");
            db.Jobs.Get(job.Id)!.State.Should().Be(PaymentJobState.ApplyUncertain, "MarkNoBalance yine yazılmadı");
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj yine gönderilmedi");

            // Üçüncü tıklama: sunucu eşitlendi, aynı anahtarın oynatması düşer.
            var ucuncu = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            ucuncu.Result.Should().Be(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().HaveCount(4);
            AppliedKey(handler.AppliedBalanceBodies[3]).Should().Be(job.ApplyKey!.Value,
                "sonraki tıklama aynı anahtarla oynatır — çift düşüm yok");
            var son = db.Jobs.Get(job.Id)!;
            son.State.Should().Be(PaymentJobState.Applied);
            son.AppliedAmount.Should().Be(100m);
            _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("150%2C00", "250 − 100");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: miras işin oynatması (1. adım) da customer-not-synced alırsa ayrı sonuç — miras iş anahtarıyla bekler
    public async Task Miras_isin_oynatmasi_customer_not_synced_BalanceNotSynced()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (miras, anahtar) = MirasIs(db, a.Id);
            var (sut, handler) = KisiSut(db);
            handler.ApplyProblemTitle = "customer-not-synced";

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            db.Jobs.Get(miras.Id)!.State.Should().Be(PaymentJobState.ApplyUncertain, "miras iş anahtarıyla bekler");
            handler.AppliedBalanceBodies.Should().ContainSingle("miras işin tek oynatması — stale değil, anında tekrar yok");
            AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(anahtar);
            handler.PreviewCustomerIds.Should().BeEmpty("akış miras işte durdu — taze işin önizlemesi sorulmadı");
            _launcher.LaunchedUrls.Should().BeEmpty();
            Isler(db, "session:s1").Should().ContainSingle().Which.ApplyKey.Should().BeNull("taze iş anahtarsız bekler");
        }
        finally { Drop(db); }
    }

    /// <summary>Önizlemesi hep 409 <c>customer-not-synced</c> dönen sahte sunucu, günlük toplayıcı ve elle ilerletilen saat
    /// üstünde bir servis (5 dakika kuralı, spec §6.1-b). Testler <c>Handler.PreviewResponse</c>'u değiştirebilir.</summary>
    private (PaymentRequestService Sut, WhatsAppStubHandler Handler, CapturingLogger<PaymentRequestService> Log,
        ManualTimeProvider Saat) EsitlenmemisSut(SyncedDb db)
    {
        var handler = new WhatsAppStubHandler
        {
            PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced"),
        };
        var log = new CapturingLogger<PaymentRequestService>();
        var saat = new ManualTimeProvider();
        var sut = SutOn(handler, _store, _launcher, jobs: db.Jobs, customers: db.Customers, busy: db.Busy, log: log);
        sut.NotSyncedClock = saat;
        return (sut, handler, log, saat);
    }

    [Fact] // §6.1-b: 5 dakikadan UZUN süren eşitlenmeme ayrı metin + günlükte kişinin satır Id'leri (ad, telefon yok)
    public async Task Bes_dakika_dolmadan_kisa_metin_dolunca_uzun_metin_ve_gunluge_satir_idleri()
    {
        var db = FileDb();
        try
        {
            var adA = $"kisi-{Guid.NewGuid():N}";
            var adB = $"kisi-{Guid.NewGuid():N}";
            var a = Uye(db, adA, phone: false);
            var b = Uye(db, adB);
            Kisi(db, a, b);
            var (sut, _, log, saat) = EsitlenmemisSut(db);
            Task<PaymentRequestOutcome> Iste() => sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            var ilk = await Iste();
            ilk.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            ilk.Message.Should().BeNull("ilk görülme");

            saat.Advance(TimeSpan.FromMinutes(5));
            var tamBes = await Iste();
            tamBes.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            tamBes.Message.Should().BeNull("tam 5 dakika henüz 'uzun' değil (> 5 dk)");
            log.Warnings.Should().NotContain(w => w.Message.Contains("5 dakikadan uzun"));

            saat.Advance(TimeSpan.FromSeconds(1));
            var uzun = await Iste();
            uzun.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            uzun.Message.Should().Be(PaymentResultPresenter.NotSyncedPersistentText);

            var uyari = log.Warnings.Should().ContainSingle(w => w.Message.Contains("5 dakikadan uzun")).Subject;
            uyari.Full.Should().Contain(a.Id).And.Contain(b.Id, "günlüğe kişinin satır Id'leri yazılır");
            log.Entries.Should().NotContain(e => e.Full.Contains(adA) || e.Full.Contains(adB) || e.Full.Contains(b.Phone!),
                "günlükte ad, kullanıcı adı ve telefon yok — yalnız Id");
            Isler(db, "session:s1").Should().ContainSingle().Which.State.Should().Be(PaymentJobState.Created,
                "üç tıklamada da MarkNoBalance yazılmadı");
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: ilk görülme anı KİŞİ başına — bir kişinin uzun beklemesi öbür kişinin metnini değiştirmez
    public async Task Esitlenmeme_sayaci_kisi_basina()
    {
        var db = FileDb();
        try
        {
            var a1 = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var a2 = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a1, a2);
            var b1 = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b2 = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, b1, b2);
            var (sut, _, _, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(a2, _ => 250m, T, "session:s1")).Message.Should().BeNull();
            saat.Advance(TimeSpan.FromMinutes(6));
            (await sut.RequestPaymentAsync(a2, _ => 250m, T, "session:s1")).Message.Should()
                .Be(PaymentResultPresenter.NotSyncedPersistentText, "A kişisi 6 dakikadır eşitlenmedi");

            var bSonuc = await sut.RequestPaymentAsync(b2, _ => 250m, T, "session:s1");

            bSonuc.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            bSonuc.Message.Should().BeNull("B kişisi ilk kez görülüyor — sayaç kişi başına");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: kesin sonuç (sunucu kişiyi tanıdı) sayacı sıfırlar — sonraki eşitlenmeme yeniden kısa metinle başlar
    public async Task Kesin_sonuctan_sonra_sayac_sifirlanir()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler, _, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            saat.Advance(TimeSpan.FromMinutes(6));

            handler.PreviewResponse = null;
            handler.PreviewBalance = 0m;
            var kesin = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");
            kesin.Result.Should().Be(PaymentRequestResult.Opened, "sunucu kişiyi tanıdı: bakiye yok, mesaj gitti");
            Isler(db, "session:s1").Should().ContainSingle().Which.State.Should().Be(PaymentJobState.NoBalance);

            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var yeni = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s2");

            yeni.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            yeni.Message.Should().BeNull("kesin sonuç 6 dakikalık ilk görülmeyi sildi — sayaç yeniden başladı");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b "sürerse": art arda iki 409 arasında 30 dakikadan uzun ara sayacı yeniden başlatır (aynı üye kümesi aynı
           // anahtarı üretir — ayırıp yeniden birleştirmeden sonraki ilk 409 eski ilk görülmeyle uzun metin göstermez)
    public async Task Uzun_aradan_sonraki_ilk_esitlenmeme_kisa_metinle_baslar()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, _, log, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Message.Should().BeNull("ilk görülme");
            saat.Advance(TimeSpan.FromMinutes(40));

            var sonra = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            sonra.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            sonra.Message.Should().BeNull("40 dakikalık ara > NotSyncedStreakGap — ilk görülme yeniden başladı");
            log.Warnings.Should().NotContain(w => w.Message.Contains("5 dakikadan uzun"));

            saat.Advance(TimeSpan.FromMinutes(6));
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Message.Should()
                .Be(PaymentResultPresenter.NotSyncedPersistentText, "yeni ilk görülmeden bu yana 6 dakika, ara 6 dakika");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: ağ belirsizliği (sunucu cevap vermedi) sayacı SIFIRLAMAZ — kişinin eşitlendiği bilinmiyor
    public async Task Ag_belirsizligi_sayaci_sifirlamaz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler, _, saat) = EsitlenmemisSut(db);

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);

            handler.PreviewResponse = _ => throw new TaskCanceledException("stub timeout");
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceUncertain, "ağ hatası: bugünkü genel belirsiz kol");

            saat.Advance(TimeSpan.FromMinutes(6));
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var uzun = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            uzun.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            uzun.Message.Should().Be(PaymentResultPresenter.NotSyncedPersistentText,
                "ağ belirsizliği ilk görülmeyi silmedi — 6 dakikadır eşitlenmiyor");
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-c: önizlemenin client-update-required'ı genel BalanceUncertain yolu yeterli, metin değişmez — NoBalance yok
    public async Task Onizleme_client_update_required_belirsiz_NoBalance_yazilmaz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("client-update-required");

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain, "spec §6.1-c: metin değişmez");
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı");
            job.ApplyKey.Should().BeNull();
            handler.AppliedBalanceBodies.Should().BeEmpty();
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // S2 saha notu 5: member-ids-required kesin sonuçtan ayrılır — genel belirsiz kol, MarkNoBalance yok, iş taze
    public async Task Onizleme_member_ids_required_belirsiz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("member-ids-required", HttpStatusCode.BadRequest);

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.Created, "MarkNoBalance yazılmadı");
            job.ApplyKey.Should().BeNull();
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Theory] // S2 saha notu 5: apply'ın member-ids-required'ı (ayrılır), balance-busy'si ve 404'ü (eşlenmez) genel belirsiz kol — iş anahtarla bekler
    [InlineData("member-ids-required", HttpStatusCode.BadRequest)]
    [InlineData("balance-busy", HttpStatusCode.ServiceUnavailable)]
    [InlineData("Not Found", HttpStatusCode.NotFound)]
    public async Task Apply_member_ids_required_ve_balance_busy_is_anahtarla_bekler(string baslik, HttpStatusCode durum)
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ApplyResponse = _ => WhatsAppStubHandler.Problem(baslik, durum);

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.ApplyUncertain, "anahtar diskte, MarkNoBalance yazılmadı");
            job.ApplyKey.Should().NotBeNull();
            handler.AppliedBalanceBodies.Should().HaveCount(2, "K3: bir deneme + bir anında tekrar");
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-c: apply'ın client-update-required'ı genel belirsiz kol — iş anahtarıyla bekler
    public async Task Apply_client_update_required_belirsiz()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ApplyProblemTitle = "client-update-required";

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.BalanceUncertain);
            var job = Isler(db, "session:s1").Should().ContainSingle().Subject;
            job.State.Should().Be(PaymentJobState.ApplyUncertain, "MarkNoBalance yazılmadı");
            job.ApplyKey.Should().NotBeNull();
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // K7: havuz kipinde kural 1'in kesin kolu — önizleme 200 {balance: 0, personPool: true} kesin NoBalance, eşitlenmeme değil
    public async Task Havuz_onizlemesi_sifir_personPool_true_kesin_NoBalance()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"wpfCustomerId":"{{G(b)}}","balance":0,"updatedAt":"2030-01-01T00:00:00+00:00","personPool":true}""",
                    Encoding.UTF8, "application/json"),
            };

            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1");

            outcome.Result.Should().Be(PaymentRequestResult.Opened, "kişinin hiçbir üyesi sunucuda yok: para olamaz");
            outcome.Result.Should().NotBe(PaymentRequestResult.BalanceNotSynced);
            Isler(db, "session:s1").Should().ContainSingle().Which.State.Should().Be(PaymentJobState.NoBalance);
            handler.AppliedBalanceBodies.Should().BeEmpty("bakiye 0 — apply çağrılmaz");
            _launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("250%2C00", "bakiye düşülmeden tam tutar");
        }
        finally { Drop(db); }
    }
}
