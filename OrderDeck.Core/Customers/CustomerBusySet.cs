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
/// yeniden çözer (C9) — arada taşınmışsa güncel Id'yi kiralar.</para>
///
/// <para><b>Bekleme ne kadar sürebilir (M-4):</b> kilit bir akış öğesinin işlemi boyunca tutulur;
/// o işlem SQLite yazma kilidini bekliyorsa (başka bir bağlantı yazıyorsa) kiralama da yazma
/// kilidi bütçesi kadar (<see cref="SqliteConnectionFactory.WriteContentionTimeoutSeconds"/> sn)
/// bekleyebilir — milisaniyelerle sınırlı değildir. <see cref="RunLocked{T}"/> eşzamanlı (bloklayarak)
/// bekler: arayüz iş parçacığından çağrılmaz. <see cref="EnterAsync"/> arayüz iş parçacığında
/// yalnız <c>await</c> ile beklenir, asla bloklanarak (<c>.Result</c>, <c>.Wait()</c>) değil.</para>
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
///
/// <para><b>Yeniden giriş HER ZAMAN reddedilir:</b> kilit yeniden girişli değildir;
/// <see cref="RunLocked{T}"/> gövdesinin içinden (aynı akış) kiralamak ya da yeniden
/// <see cref="RunLocked{T}"/> çağırmak kilidi sonsuza dek beklerdi. Bu denetim anahtara bağlı
/// değil, üretimde de açık: kalıcı bir askıda kalma yerine açık bir hata.</para>
/// </summary>
public sealed class CustomerBusySet
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _leases = new(StringComparer.Ordinal);

    // Kilidi tutan akışın işareti (AsyncLocal — iş parçacığı değil, mantıksal akış). Değer değil
    // NESNE konur: gövdenin içinde kuyruğa alınan iş aynı nesneyi miras alır; gövde bitince
    // nesne kapanır ve sonradan koşan o iş yanlış alarm vermez (WriteScopeGuard'daki desen).
    private readonly AsyncLocal<GateHold?> _hold = new();

    /// <summary>Müşteriyi kiralar; dönen nesnenin Dispose'u kirayı bırakır (iki kez çağrılabilir).
    /// Aynı müşteri birden çok kez kiralanabilir — son kira bitene kadar meşgul sayılır.</summary>
    public async Task<IDisposable> EnterAsync(string customerId, CancellationToken ct = default)
    {
        ThrowIfHeldByThisFlow("CustomerBusySet.EnterAsync");
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
        ThrowIfHeldByThisFlow("CustomerBusySet.RunLocked");
        WriteScopeGuard.AssertNoActiveScope("CustomerBusySet.RunLocked");
        _gate.Wait();
        var hold = new GateHold();
        var previous = _hold.Value;
        _hold.Value = hold;
        try { return body(id => _leases.TryGetValue(id, out var n) && n > 0); }
        finally
        {
            hold.Close();
            _hold.Value = previous;
            _gate.Release();
        }
    }

    private void ThrowIfHeldByThisFlow(string operation)
    {
        if (_hold.Value is { Active: true })
            throw new CustomerBusySetReentrancyException(
                $"{operation}: bu akış CustomerBusySet kilidini zaten tutuyor (RunLocked gövdesinin içi). " +
                "Kilit yeniden girişli değildir; iç içe çağrı sonsuza dek beklerdi.");
    }

    private void Exit(string customerId)
    {
        _leases.AddOrUpdate(customerId, 0, static (_, n) => n - 1);
        // Yalnız sayaç hâlâ 0 ise silinir: arada başlayan yeni kira (1) korunur.
        _leases.TryRemove(KeyValuePair.Create(customerId, 0));
    }

    private sealed class GateHold
    {
        private volatile bool _active = true;

        public bool Active => _active;

        public void Close() => _active = false;
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

/// <summary>
/// <see cref="CustomerBusySet"/> kilidine aynı akıştan yeniden girme denemesi — bir programlama
/// hatasının işareti (kilit yeniden girişli değildir). Belirli bir müşterinin verisine bağlı
/// değildir: müşteri akışı bunu uygulanamayan öğe (U10) saymaz, öğeyi atlamaz (C7 incelemesi M-1).
/// </summary>
public sealed class CustomerBusySetReentrancyException(string message) : InvalidOperationException(message);
