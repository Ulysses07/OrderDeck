using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>Faz 0 (D5): "gönder ve kapat" — gönderim adımları sırayla, toplam süre sınırıyla,
/// çağıranın iş parçacığı dışında koşar; hiçbir adımın hatası kapanışı engellemez. Tek partide
/// gönderen servisler kuyruğu boşaltana dek yinelenir; müşteri adımı bütçenin en çok yarısını alır.</summary>
public sealed class SyncFlushServiceTests
{
    private sealed class FixedLicense(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => key;
    }

    private static FlushStep Step(Func<CancellationToken, Task> run, double? maxShare = null)
        => new("adım", run, maxShare);

    [Fact]
    public async Task Adimlar_sirayla_kosar()
    {
        var calls = new List<string>();
        var svc = new SyncFlushService(new[]
        {
            Step(_ => { lock (calls) calls.Add("a"); return Task.CompletedTask; }),
            Step(_ => { lock (calls) calls.Add("b"); return Task.CompletedTask; }),
        });

        await svc.FlushAsync(TimeSpan.FromSeconds(5));

        calls.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Sure_dolunca_kalan_adimlar_atlanir_hata_firlamaz()
    {
        // İlk adımın payı (maxShare: 1.0) TAM OLARAK kalan bütçeye eşittir — "adımın payı bitti"
        // ile "GENEL bütçe de bitti" kararı aynı anda verilir. Bu, FlushAsync'in tek saatinin
        // (Stopwatch) kendisiyle bile sıfır marjlı bir karşılaştırmaydı: CancelAfter'ın kendi
        // zamanlayıcısı milisaniyenin kesirli kısmını kırptığı için adım, Stopwatch'a göre <1 ms
        // ÖNCE iptal olabiliyordu — döngü bunu "bütçe bitmedi" sanıp sıradaki adımı YANLIŞLIKLA
        // başlatıyordu (son inceleme: 400 turluk bir iç döngüde ~%12 görüldü, saf bir tesadüf
        // değildi). FlushAsync'teki BudgetExhaustionMargin bu sıfır marjı güvenli bir marja taşıdı
        // (2000 turluk iç döngüde bu TAM 100 ms'lik bütçeyle sıfır hataya indi) — test bu yüzden
        // GERÇEK bir saatle, dar görünen ama artık üretim kodundaki marjla korunan bütçesinde kalır.
        var skippedRan = false;
        var svc = new SyncFlushService(new[]
        {
            Step(ct => Task.Delay(TimeSpan.FromSeconds(10), ct), maxShare: 1.0),   // bütün bütçeyi alabilir
            Step(_ => { skippedRan = true; throw new InvalidOperationException("çalışmamalı"); }),
        });

        var act = () => svc.FlushAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(11));

        await act.Should().NotThrowAsync();
        skippedRan.Should().BeFalse("süre doldu — kalan adım sonraki açılışta gider");
    }

    [Fact]
    public async Task Bir_adimin_hatasi_sonrakileri_engellemez()
    {
        var ran = false;
        var svc = new SyncFlushService(new[]
        {
            Step(_ => throw new InvalidOperationException("enjekte adım hatası (senkron)")),
            Step(_ => Task.FromException(new InvalidOperationException("enjekte adım hatası (görev)"))),
            Step(_ => { ran = true; return Task.CompletedTask; }),
        });

        await svc.FlushAsync(TimeSpan.FromSeconds(5));

        ran.Should().BeTrue("tek gönderimin hatası diğerlerini engellemez");
    }

    [Fact]
    public async Task Adimin_eszamanli_kismi_cagiranin_is_parcaciginda_kosmaz_sure_siniri_kesindir()
    {
        // C4/C6 incelemesi: müşteri gönderimi ilk gerçek await'ine kadar (kapı, lisans, GetForPush,
        // taşımanın CustomerBusySet beklemesi) çağıranın iş parçacığında koşar. Kapanış gönderimi
        // arayüz iş parçacığından başlar: adım orada koşsaydı pencere donardı. Belirteci dinlemeyen
        // (eşzamanlı bekleyen) bir adım da 30 sn bütçesini aşmamalı.
        // Elden çıkarılmaz: bırakılan adım Set'ten hemen sonra hâlâ Wait'ten dönüyor olabilir.
        var release = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var stepThread = -1;
        var svc = new SyncFlushService(new[]
        {
            Step(_ =>
            {
                Volatile.Write(ref stepThread, Environment.CurrentManagedThreadId);
                release.Wait(TimeSpan.FromSeconds(30));      // belirteci dinlemez
                return Task.CompletedTask;
            }),
        });

        try
        {
            var sw = Stopwatch.StartNew();
            var flush = svc.FlushAsync(TimeSpan.FromMilliseconds(200));
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "çağrı adımın eşzamanlı kısmını beklemeden döner");

            await flush.WaitAsync(TimeSpan.FromSeconds(10));  // bütçe dolunca adımı beklemeden biter
            flush.IsCompletedSuccessfully.Should().BeTrue();
            SpinWait.SpinUntil(() => Volatile.Read(ref stepThread) != -1, TimeSpan.FromSeconds(10));
            stepThread.Should().NotBe(callerThread);
        }
        finally
        {
            release.Set();
        }
    }

    // ── bütçe payı (inceleme küçük 5) ───────────────────────────────────

    [Theory]
    [InlineData(30, 30, 0.5, 4, 15)]    // müşteri: toplamın yarısı
    [InlineData(30, 10, 0.5, 4, 10)]    // pay kalan süreyi aşamaz
    [InlineData(30, 15, null, 3, 5)]    // kalanlar kalan süreyi eşit paylaşır
    [InlineData(30, 12, null, 2, 6)]    // önceki adım erken bittiyse arta kalan sonrakilere
    [InlineData(30, 7, null, 1, 7)]     // son adım kalanın hepsini alır
    [InlineData(30, 0, null, 2, 0)]
    public void Adimin_payi(int budgetSec, int remainingSec, double? maxShare, int stepsLeft, int expectedSec)
        => SyncFlushService.StepAllowance(TimeSpan.FromSeconds(budgetSec), TimeSpan.FromSeconds(remainingSec), maxShare, stepsLeft)
            .Should().Be(TimeSpan.FromSeconds(expectedSec));

    [Fact]
    public async Task Musteri_adimi_butcenin_en_cok_yarisini_alir_sonraki_adimlar_kosar()
    {
        // D5b'den ya da ilk biçim-2 gönderiminden sonra müşteri kuyruğu çok büyük olabilir: bütün
        // bütçeyi o yerse oturum, etiket, ödeme ve kargo kapanışta hiç gitmezdi.
        //
        // Davranış sınanır, dar bir zaman penceresi değil (yük altında kararlı olsun — bir CI
        // koşucusunun gecikmesi test düşürmesin). StepAllowance'ın KESİN aritmetiği zaten
        // Adimin_payi'de deterministik (wall-clock'suz) sınanıyor; burada yalnız üçü doğrulanır:
        // müşteri adımı payını (sıfıra yakın değil) bitirmeden bırakılmıyor, SIRADAKİ adım koşuyor,
        // ve bütün akış cömert bir üst sınırın içinde bitiyor (sonsuza kalmıyor).
        //
        // FlushAsync bırakılan adımı BEKLEMEZ (abandoned = true, devam eder) — bu yüzden
        // customerCancelledAt'in finally'de yazıldığından FlushAsync'in dönüşünden SONRA da emin
        // olmak gerekir; aksi hâlde yük altında assert, finally henüz koşmadan "Zero" bulup düşer
        // (gerçek bir yarış, dar zaman penceresinin perdelediği bir kusur). customerStepFinished bu
        // yarışı kapatır: finally koşana kadar açıkça bekleriz.
        //
        // Aynı yarış SIRADAKİ adımda da var: iş parçacığı havuzu çok yüklüyse (ör. tüm takımla
        // eşzamanlı koşu) o adımın Task.Run'ı payı dolana kadar hiç başlamayabilir — FlushAsync onu
        // da bırakır (abandoned). Ama bu adımın kendi gövdesi belirteci HİÇ dinlemiyor: er ya da geç
        // bir iş parçacığı bulunca yine çalışıp nextRan'ı true yapar — yalnız FlushAsync'in dönüşünden
        // SONRA olabilir. nextStepRan aynı şekilde açıkça beklenir.
        var budget = TimeSpan.FromSeconds(4);
        var expectedCustomerShare =
            SyncFlushService.StepAllowance(budget, remaining: budget, SyncFlushService.CustomerShare, stepsLeft: 2);
        var generousCeiling = budget + TimeSpan.FromSeconds(10);
        var customerCancelledAt = TimeSpan.Zero;
        var customerStepFinished = new TaskCompletionSource();
        var nextRan = false;
        var nextStepRan = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();
        var svc = new SyncFlushService(new[]
        {
            Step(async ct =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    customerCancelledAt = sw.Elapsed;
                    customerStepFinished.TrySetResult();
                }
            }, maxShare: SyncFlushService.CustomerShare),
            Step(_ => { nextRan = true; nextStepRan.TrySetResult(); return Task.CompletedTask; }),
        });

        await svc.FlushAsync(budget).WaitAsync(generousCeiling);   // cömert üst sınır: akış sonsuza kalmasın
        await customerStepFinished.Task.WaitAsync(generousCeiling); // bırakılan adımın finally'si de aynı sınırda
        await nextStepRan.Task.WaitAsync(generousCeiling);          // sıradaki adım da bırakılmış olabilir

        nextRan.Should().BeTrue("müşteri adımı payını bitirince sıradaki adım kalan süreyle koşar");
        // Yarı pay kadar (ya da fazlası) geçmeden bırakılmamalı — "hemen iptal edildi" gibi bir
        // regresyonu yakalar; kesin değeri Adimin_payi zaten sınıyor, o yüzden burada bol tolerans var.
        customerCancelledAt.Should().BeGreaterThan(expectedCustomerShare / 2,
            "müşteri adımı payını bitirmeden bırakılmamalı");
    }

    // ── tek partide gönderenler kuyruğu boşaltır (inceleme önemli 1) ─────

    [Fact]
    public async Task Bosaltma_gonderdikce_yineler_bos_turda_durur()
    {
        var queue = new Queue<int>(new[] { 50, 50, 3, 0, 99 });
        var calls = 0;

        await SyncFlushService.Drain(_ => { calls++; return Task.FromResult(queue.Dequeue()); })(CancellationToken.None);

        calls.Should().Be(4, "boş tur kuyruğun bittiği demek");
    }

    [Fact]
    public async Task Bosaltma_en_cok_yirmi_tur_kosar()
    {
        // Onayı hep ıskalayan bir satır (MarkSynced Revision CAS'i tutmuyor) her turda yeniden gider.
        var calls = 0;

        await SyncFlushService.Drain(_ => { calls++; return Task.FromResult(1); })(CancellationToken.None);

        calls.Should().Be(SyncFlushService.MaxDrainRounds).And.Be(20);
    }

    [Fact]
    public async Task Bosaltma_sure_dolunca_durur()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        await SyncFlushService.Drain(_ => { if (++calls == 2) cts.Cancel(); return Task.FromResult(10); })(cts.Token);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Gonder_ve_kapat_uc_yuz_etiketi_sure_icinde_gonderir()
    {
        // Oturum+etiket servisi turda TEK parti gönderir (50 oturum / 100 etiket): yinelenmeseydi
        // kapanışta 100 etiket gider, 200'ü sonraki açılışa kalırdı.
        var key = $"lisans-{Guid.NewGuid():N}";
        var licenseId = Guid.NewGuid();
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var sessions = new SessionRepository(db);
        var labels = new LabelRepository(db);
        var customerId = Guid.NewGuid().ToString("N");
        new CustomerRepository(db).Insert(new Customer(customerId, "tiktok", "ornek_musteri", "Örnek Müşteri",
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
        var sessionId = Guid.NewGuid().ToString("N");
        sessions.Insert(new StreamSession(sessionId, "Örnek yayın", 1_700_000_000L, null, new[] { "tiktok" }, null));
        for (var i = 0; i < 300; i++)
            labels.Insert(new Label(Guid.NewGuid().ToString("N"), sessionId, customerId, "tiktok", "ornek_musteri",
                "alıyorum", null, 100m, 1_700_000_100L + i, null, DisplayName: "Örnek Müşteri"));

        var ordersSent = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, $"[{{\"id\":\"{licenseId}\",\"licenseKey\":\"{key}\"}}]");
            if (path.EndsWith("/sessions/sync")) return FakeHttpMessageHandler.Json(200, "[]");
            if (path.EndsWith("/orders/sync"))
            {
                using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Interlocked.Add(ref ordersSent, body.RootElement.GetProperty("orders").GetArrayLength());
                return FakeHttpMessageHandler.Json(200, "[]");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var api = new LicenseApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.local") },
            new LicenseTokenStore());
        var license = new FixedLicense(key);
        var clock = new SystemClock();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"od-flush-{Guid.NewGuid():N}.json");
        var flush = SyncFlushService.ForClose(
            new WpfCustomerProjectionSyncService(api, new CustomerSyncRepository(db), new SyncCursorRepository(db),
                license, clock, NullLogger<WpfCustomerProjectionSyncService>.Instance),
            new SessionOrderSyncService(api, sessions, labels, license, clock, NullLogger<SessionOrderSyncService>.Instance),
            new PaymentSyncService(api, new PaymentRepository(db), new SyncCursorRepository(db), license, clock,
                NullLogger<PaymentSyncService>.Instance),
            new ShipmentSyncService(api, new ShipmentRepository(db), new SettingsStore(settingsPath), new AppSettings(),
                license, clock, NullLogger<ShipmentSyncService>.Instance));

        try
        {
            await flush.FlushAsync(SyncFlushService.CloseBudget).WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            File.Delete(settingsPath);
        }

        ordersSent.Should().Be(300);
        labels.GetUnsynced(1000).Should().BeEmpty();
    }
}
