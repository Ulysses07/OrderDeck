using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

// ── customer-not-synced'in tetiklediği müşteri senkron turu (spec 2026-10-11 §6.1-b, §8.4-2, §14 #18, M14) ──
//
// Havuz kipinde sunucu kişinin bir üyesini henüz tanımıyorsa (409 customer-not-synced) akış BalanceNotSynced döner ve
// arka planda bir müşteri senkron turu başlatır (CustomerChangesPullService.PullOnceAsync: önce gönderir, sonra çeker).
// Akış turu BEKLEMEZ; tur koşarken gelen tetik bir tur sıraya alır, bekleyen bir tur varken yenisi eklenmez; turun
// hatası yutulur ve günlüğe yazılır. Tur kişi kilitleri ve müşteri kirası bırakıldıktan SONRA başlar. Burada CustomerTour'a sahte tur bağlanır; DI'ın gerçek
// servisi bağladığını CustomerSyncDiTests.Odeme_servisi_musteri_turunu_DIdan_alinan_akis_servisine_baglar kanıtlar.
public partial class PaymentRequestServiceTests
{
    /// <summary><see cref="PaymentRequestService.CustomerTour"/>'a bağlanan sahte tur: çağrıları sayar, başladığını
    /// bildirir, test <see cref="Bitsin"/>'i bırakana kadar sürer; <see cref="Firlat"/> doluysa sonunda fırlatır.</summary>
    private sealed class TurSayaci
    {
        public int Cagri;
        public TaskCompletionSource Baslasin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Bitsin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Firlat { get; set; }

        public async Task Tur(CancellationToken ct)
        {
            Interlocked.Increment(ref Cagri);
            Baslasin.TrySetResult();
            await Bitsin.Task;
            if (Firlat is not null) throw Firlat;
        }
    }

    [Fact] // §6.1-b kabul 1: akış turu beklemez (kira iddiası yalnız duman denetimi; tetiğin yeri Adım 5'in Edit'iyle sabit)
    public async Task Esitlenmeme_musteri_turunu_tetikler_akis_turu_beklemez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var sayac = new TurSayaci();
            bool? kiraTurBasindaTutuluyordu = null;
            sut.CustomerTour = ct =>
            {
                kiraTurBasindaTutuluyordu = Leased(db, a.Id) || Leased(db, b.Id);
                return sayac.Tur(ct);
            };

            // Bitsin bırakılmadı: akış turu bekleseydi burada asılı kalırdı (üst sınır yalnız asılı kalan testi düşürür).
            var outcome = await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1").WaitAsync(TestPatience.Upper);

            outcome.Result.Should().Be(PaymentRequestResult.BalanceNotSynced);
            _launcher.LaunchedUrls.Should().BeEmpty("mesaj gönderilmedi");
            await sayac.Baslasin.Task.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(1);
            sut.LastCustomerTour.IsCompleted.Should().BeFalse("tur test bırakana kadar sürüyor; akış onu beklemeden döndü");
            kiraTurBasindaTutuluyordu.Should().BeFalse("duman denetimi: tur başında kira tutulmuyor");

            sayac.Bitsin.SetResult();
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b kabul 2: tur koşarken gelen tetik bir tur sıraya alır; bekleyen tur varken yenisi eklenmez;
           // turlar bitince yeni 409 yeniden tetikler
    public async Task Bekleyen_tur_varken_ikinci_esitlenmeme_yeni_tur_eklemez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");
            var sayac = new TurSayaci();
            sut.CustomerTour = sayac.Tur;

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sayac.Baslasin.Task.WaitAsync(TestPatience.Upper);
            var ilkTur = sut.LastCustomerTour;

            // Tur sürerken üç tıklama daha: hepsi eşitlenmedi. İlki bir tur sıraya alır (koşan tur gönderimini geçmiş
            // olabilir — bu PC'nin yeni birleştirmesini sıradaki tur gönderir); sıra doluyken öbür ikisi tur EKLEMEZ.
            for (int i = 0; i < 3; i++)
            {
                (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                    .Should().Be(PaymentRequestResult.BalanceNotSynced);
            }
            ReferenceEquals(sut.LastCustomerTour, ilkTur).Should().BeTrue("sıradaki tur koşan turun görevinde koşar");
            sayac.Cagri.Should().Be(1, "tur kilidi üst üste binmeyi önler: sıradaki tur koşan bitmeden başlamaz");

            sayac.Bitsin.SetResult();
            await ilkTur.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(2, "dört eşitlenmeme: koşan tur + bir sıradaki tur; bekleyen varken yenisi eklenmedi");

            // Turlar bitti (sayaç boşta): yeni bir 409 yeniden tetikler. Bitsin tamam — üçüncü tur hemen biter.
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            ReferenceEquals(sut.LastCustomerTour, ilkTur).Should().BeFalse("turlar bitince yeni 409 yeni bir tur başlatır");
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(3);
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b: apply'ın customer-not-synced'i de tetikler; tetik koşu başına tek (SettleAsync'in anında tekrarının
           // iki apply'ı tek sonuç)
    public async Task Apply_customer_not_synced_de_turu_tetikler()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 100m;
            handler.ApplyProblemTitle = "customer-not-synced";
            var sayac = new TurSayaci();
            sut.CustomerTour = sayac.Tur;

            // Bitsin akış boyunca TUTULUR: apply başına bir tetik olsaydı ilki turu başlatır (0→1), ikincisi turu
            // belirlenimci olarak sıraya alırdı (1→2) ve Bitsin bırakılınca Cagri 2 olurdu.
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sayac.Baslasin.Task.WaitAsync(TestPatience.Upper);
            handler.AppliedBalanceBodies.Should().HaveCount(2, "SettleAsync'in anında tekrarı: bir deneme + bir tekrar");
            sayac.Cagri.Should().Be(1);

            sayac.Bitsin.SetResult();
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(1, "iki apply tek sonuç: koşu başına tek tetik, sıraya tur girmedi");
            _launcher.LaunchedUrls.Should().BeEmpty();
        }
        finally { Drop(db); }
    }

    [Fact] // §6.1-b kabul 3: turun istisnası yutulur ve günlüğe yazılır; sayaç boşa döner, sonraki 409 yeniden tetikler
    public async Task Tur_hatasi_yutulur_gunluge_yazilir_sayac_bosa_doner()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var log = new CapturingLogger<PaymentRequestService>();
            var handler = new WhatsAppStubHandler
            {
                PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced"),
            };
            var sut = SutOn(handler, _store, _launcher, jobs: db.Jobs, customers: db.Customers, busy: db.Busy, log: log);
            var sayac = new TurSayaci { Firlat = new InvalidOperationException("tur-hatasi") };
            sayac.Bitsin.SetResult();
            sut.CustomerTour = sayac.Tur;

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);   // fırlatmaz: hata turun içinde yutuldu

            sut.LastCustomerTour.IsCompletedSuccessfully.Should().BeTrue();
            log.Warnings.Should().ContainSingle(e => e.Message.Contains("müşteri senkron turu başarısız"))
                .Which.Exception.Should().Contain("tur-hatasi");

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);
            sayac.Cagri.Should().Be(2, "hatalı tur sayacı boşa döndürdü: sonraki 409 yeniden tetikler");
        }
        finally { Drop(db); }
    }

    [Theory] // yalnız customer-not-synced tetikler: öbür belirsizler (client-update-required) ve kesin sonuç tur başlatmaz
    [InlineData("client-update-required", PaymentRequestResult.BalanceUncertain)]
    [InlineData(null, PaymentRequestResult.Opened)]
    public async Task Esitlenmeme_disinda_tur_tetiklenmez(string? onizlemeHatasi, PaymentRequestResult beklenen)
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewBalance = 0m;  // hata yoksa önizleme kesin NoBalance
            if (onizlemeHatasi is not null)
            {
                string baslik = onizlemeHatasi;
                handler.PreviewResponse = _ => WhatsAppStubHandler.Problem(baslik);
            }
            var sayac = new TurSayaci();
            sayac.Bitsin.SetResult();     // yanlışlıkla başlayan tur hemen biter: aşağıdaki bekleme asılı kalmaz
            sut.CustomerTour = sayac.Tur;

            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result.Should().Be(beklenen);
            // Tetik yoksa LastCustomerTour tamamlanmış görevdir; tetik olsaydı bu bekleme turu koşturur ve sayaç 1 olurdu.
            await sut.LastCustomerTour.WaitAsync(TestPatience.Upper);

            sayac.Cagri.Should().Be(0);
        }
        finally { Drop(db); }
    }

    [Fact] // CustomerTour null (DI'sız kurulum, testlerin varsayılanı): akış aynı sonucu verir, istisna yok
    public async Task Tur_yoksa_akis_degismez()
    {
        var db = FileDb();
        try
        {
            var a = Uye(db, $"kisi-{Guid.NewGuid():N}", phone: false);
            var b = Uye(db, $"kisi-{Guid.NewGuid():N}");
            Kisi(db, a, b);
            var (sut, handler) = KisiSut(db);
            handler.PreviewResponse = _ => WhatsAppStubHandler.Problem("customer-not-synced");

            sut.CustomerTour.Should().BeNull("customerPull verilmedi");
            (await sut.RequestPaymentAsync(b, _ => 250m, T, "session:s1")).Result
                .Should().Be(PaymentRequestResult.BalanceNotSynced);
            sut.LastCustomerTour.IsCompleted.Should().BeTrue("tur yok: başlatılan bir şey yok");
        }
        finally { Drop(db); }
    }
}
