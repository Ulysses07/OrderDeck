using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.Core.Storage;

namespace OrderDeck.Core.Customers;

/// <summary>
/// U13 — ödeme akışı süren müşteriler (süreç içi, singleton). Bakiye akışı müşteriyi
/// kiralar (<see cref="EnterAsync"/>); senkron deposu taşıma/dönüştürme işlemini
/// <see cref="RunLocked{T}"/> içinde, kaynak ve hedef kiralı değilse koşar.
///
/// <para><b>Neden kilit taşıma işlemini kapsıyor:</b> kiralama ile taşıma aynı kilitten geçer.
/// Taşıma sürerken gelen kiralama onun commit'ini bekler; ödeme akışı kiraladıktan sonra Id'yi
/// yeniden çözer (C9) — arada taşınmışsa güncel Id'yi kiralar. Kilit yalnız bir öğenin işlemi
/// kadar tutulur (milisaniyeler); kiralama asenkron bekler, arayüz iş parçacığı bloklanmaz.</para>
///
/// <para>Kira bırakma kilitsizdir: bir taşıma "meşgul" görüp vazgeçtikten sonra kiranın bitmesi
/// zararsız (sonraki tur yeniden dener).</para>
///
/// <para><b>TEK paylaşılan örnek:</b> senkron deposu ile <c>PaymentRequestService</c> aynı örneği
/// almalı (DI tekil kaydı — C6); ayrı örnekler birbirinin kirasını görmez, U13 sessizce
/// çalışmaz.</para>
///
/// <para><b>Kilit sırası değişmezi (U17):</b> önce bu kümenin kilidi, SONRA SQLite yazma kilidi —
/// asla tersi. <see cref="RunLocked{T}"/>'in gövdesi <c>SyncApplyScope</c> açar (küme → yazma);
/// ödeme akışı kiralamayı hiçbir veritabanı işlemi açıkken yapmaz. Açık bir
/// <c>DbWrite</c>/<c>SyncApplyScope</c> içinden kiralamak ya da <see cref="RunLocked{T}"/>
/// çağırmak, kilidi tutan taşımayla karşılıklı beklemeye girebilirdi: denetim açıkken
/// (DEBUG derleme ve testler) <see cref="WriteScopeGuard"/> reddeder.</para>
/// </summary>
public sealed class CustomerBusySet
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _leases = new(StringComparer.Ordinal);

    /// <summary>Müşteriyi kiralar; dönen nesnenin Dispose'u kirayı bırakır (iki kez çağrılabilir).
    /// Aynı müşteri birden çok kez kiralanabilir — son kira bitene kadar meşgul sayılır.</summary>
    public async Task<IDisposable> EnterAsync(string customerId, CancellationToken ct = default)
    {
        WriteScopeGuard.AssertNoActiveScope("CustomerBusySet.EnterAsync");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { _leases.AddOrUpdate(customerId, 1, static (_, n) => n + 1); }
        finally { _gate.Release(); }
        return new Lease(this, customerId);
    }

    /// <summary>Gövde kümenin kilidi altında koşar; gövdeye verilen fonksiyon "bu Id kiralı mı"
    /// sorusunu o an için doğru cevaplar (kilit tutulurken yeni kira başlayamaz).</summary>
    public T RunLocked<T>(Func<Func<string, bool>, T> body)
    {
        WriteScopeGuard.AssertNoActiveScope("CustomerBusySet.RunLocked");
        _gate.Wait();
        try { return body(id => _leases.TryGetValue(id, out var n) && n > 0); }
        finally { _gate.Release(); }
    }

    private void Exit(string customerId)
    {
        _leases.AddOrUpdate(customerId, 0, static (_, n) => n - 1);
        // Yalnız sayaç hâlâ 0 ise silinir: arada başlayan yeni kira (1) korunur.
        _leases.TryRemove(KeyValuePair.Create(customerId, 0));
    }

    private sealed class Lease(CustomerBusySet owner, string customerId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Exit(customerId);
        }
    }
}
