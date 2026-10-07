using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>Faz 0 (D5): "gönder ve kapat" — gönderim adımları sırayla, toplam süre sınırıyla,
/// çağıranın iş parçacığı dışında koşar; hiçbir adımın hatası kapanışı engellemez.</summary>
public sealed class SyncFlushServiceTests
{
    [Fact]
    public async Task Adimlar_sirayla_kosar()
    {
        var calls = new List<string>();
        var svc = new SyncFlushService(new Func<CancellationToken, Task>[]
        {
            _ => { lock (calls) calls.Add("a"); return Task.CompletedTask; },
            _ => { lock (calls) calls.Add("b"); return Task.CompletedTask; },
        });

        await svc.FlushAsync(TimeSpan.FromSeconds(5));

        calls.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Sure_dolunca_kalan_adimlar_atlanir_hata_firlamaz()
    {
        var skippedRan = false;
        var svc = new SyncFlushService(new Func<CancellationToken, Task>[]
        {
            ct => Task.Delay(TimeSpan.FromSeconds(10), ct),
            _ => { skippedRan = true; throw new InvalidOperationException("çalışmamalı"); },
        });

        var act = () => svc.FlushAsync(TimeSpan.FromMilliseconds(100));

        await act.Should().NotThrowAsync();
        skippedRan.Should().BeFalse("süre doldu — kalan adım sonraki açılışta gider");
    }

    [Fact]
    public async Task Bir_adimin_hatasi_sonrakileri_engellemez()
    {
        var ran = false;
        var svc = new SyncFlushService(new Func<CancellationToken, Task>[]
        {
            _ => throw new InvalidOperationException("enjekte adım hatası (senkron)"),
            _ => Task.FromException(new InvalidOperationException("enjekte adım hatası (görev)")),
            _ => { ran = true; return Task.CompletedTask; },
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
        var svc = new SyncFlushService(new Func<CancellationToken, Task>[]
        {
            _ =>
            {
                Volatile.Write(ref stepThread, Environment.CurrentManagedThreadId);
                release.Wait(TimeSpan.FromSeconds(30));      // belirteci dinlemez
                return Task.CompletedTask;
            },
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
}
