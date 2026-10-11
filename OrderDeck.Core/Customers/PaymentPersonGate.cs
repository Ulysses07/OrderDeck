using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.Core.Storage;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Ödeme akışlarının kişi başına ÖZEL (exclusive) kilidi — süreç içi, ödeme servisinin tek örneğinde.
///
/// <para><b>Neden <see cref="CustomerBusySet"/> yetmiyor:</b> oradaki kiralar paylaşımlı sayaçtır; senkronu
/// durdurur ama iki ödeme akışını sıraya sokmaz. Rapor ile Müşteriler penceresi aynı kişinin farklı
/// üyelerinden aynı anda "Ödeme iste" derse ikisi de aynı işi Settle eder ya da biri öbürünün düşümünü
/// birleştirmede geri alırken öbürü onu mesaja yazar. Bu kilit aynı kişinin akışlarını sıraya sokar.</para>
///
/// <para><b>Neden genel bir kapı değil:</b> akış Cloud API gönderimi boyunca sürer; genel kapı öbür
/// penceredeki BÜTÜN müşterilerin tıklamasını bekletirdi. Kilit yalnız kişinin üye Id'lerine alınır.</para>
///
/// <para><b>Atomik çoklu alım:</b> bütün üyeler TEK adımda alınır (hepsi boşsa); kısmi alım ve sıra
/// gerekmez, iki akış birbirini karşılıklı bekleyemez. Bekleme süresi sınırlıdır: dolarsa <c>null</c> döner
/// ve çağıran "başka ödeme akışı sürüyor" der.</para>
///
/// <para><b>U17:</b> hiçbir veritabanı işlemi açıkken alınmaz (denetim <see cref="WriteScopeGuard"/>).
/// Ödeme akışında sıra: tıklanan satırın kirası → bu kilit → öbür üyelerin kiraları → SQLite yazma kilidi.
/// Senkron bu kilidi hiç almaz; kilidi tutan akışın beklediği küme kilidi bu kilidi beklemez.</para>
/// </summary>
public sealed class PaymentPersonGate
{
    private readonly object _sync = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private TaskCompletionSource _released = NewSignal();

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Verilen Id'lerin hepsini birlikte alır; <paramref name="timeout"/> içinde alınamazsa
    /// <c>null</c>. Dönen nesnenin Dispose'u bırakır (iki kez çağrılabilir).</summary>
    public async Task<IDisposable?> TryEnterAsync(
        IReadOnlyCollection<string> ids, TimeSpan timeout, CancellationToken ct = default)
    {
        WriteScopeGuard.AssertNoActiveScope("PaymentPersonGate.TryEnterAsync");
        var keys = ids.Distinct(StringComparer.Ordinal).ToArray();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Task released;
            lock (_sync)
            {
                if (!keys.Any(_held.Contains))
                {
                    foreach (var k in keys) _held.Add(k);
                    return new Hold(this, keys);
                }
                released = _released.Task;
            }

            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(remaining, delayCts.Token);
            // Süre dolsa da döngü bir kez daha dener (bırakma tam sınırda olmuş olabilir), sonra null.
            await Task.WhenAny(released, delay).ConfigureAwait(false);
            delayCts.Cancel();
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Id şu an bir ödeme akışında mı (tanı ve testler için).</summary>
    public bool IsHeld(string id)
    {
        lock (_sync) return _held.Contains(id);
    }

    private void Exit(string[] keys)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            foreach (var k in keys) _held.Remove(k);
            signal = _released;
            _released = NewSignal();
        }
        signal.TrySetResult();
    }

    private sealed class Hold(PaymentPersonGate owner, string[] keys) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Exit(keys);
        }
    }
}
