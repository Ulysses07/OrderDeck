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
}
