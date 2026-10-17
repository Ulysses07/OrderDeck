using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.Core.Sessions;

/// <summary>
/// Devir kapısı (spec 2026-10-13 §5.2, §12 risk 4, M2): süreç içi, oturum başına bir kilit. Kapanış gönderimi
/// "bu yayının etiket kuyruğu boş mu" okumasından o oturumun yanıtı uygulanana kadar kilidi tutar; böylece "boş"
/// okuması ile sunucudaki devir arasına kapıya uyan bir yerel düzenleme giremez (girseydi mod On'da push'ta K5 ile
/// reddedilir, değişiklik kaybolurdu).
///
/// <list type="bullet">
/// <item><see cref="EnterAsync"/>: gönderim ve geri doldurma — sırası gelene kadar bekler.</item>
/// <item><see cref="TryEnter"/>: sıfır bekleme. W2'nin düzenleme yönlendiricisinin yerel yolu, kendi bitmiş ama
/// devredilmemiş oturumuna yazmadan önce bunu dener; alamazsa operatöre "Yayın sunucuya devrediliyor; birkaç saniye
/// sonra tekrar dene." der. W1'de yerel yazımlar kapıdan geçmez: mod Off'ta devirden sonra gelen yerel değişiklik
/// sunucuda damgasız kuralla kabul edilir (K2), kayıp yoktur.</item>
/// </list>
///
/// <para>Bir oturumun kilidi başka oturumları etkilemez. Kilitler yalnız tutulurken ya da beklenirken bellekte
/// durur. Birden çok oturumu birlikte alan çağıran kimlik sırasıyla alır (kilitlenme olmasın).</para>
/// </summary>
public sealed class HandoverGate
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Tutan ve bekleyen sayısı; sıfıra inince giriş silinir.</summary>
        public int Users;
    }

    /// <summary>Oturumun kilidini sırası gelince alır. Dönen nesne bırakılınca kilit açılır.</summary>
    public async Task<IDisposable> EnterAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        Entry entry;
        lock (_lock)
        {
            entry = GetOrAdd(sessionId);
            entry.Users++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Leave(sessionId, entry, release: false);
            throw;
        }
        return new Lease(this, sessionId, entry);
    }

    /// <summary>Sıfır beklemeyle dener: kilit başkasındaysa <c>false</c>.</summary>
    public bool TryEnter(string sessionId, [NotNullWhen(true)] out IDisposable? lease)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            var entry = GetOrAdd(sessionId);
            if (!entry.Semaphore.Wait(0))
            {
                lease = null;
                return false;
            }
            entry.Users++;
            lease = new Lease(this, sessionId, entry);
            return true;
        }
    }

    /// <summary>Bellekteki giriş sayısı: tutulan ya da beklenen oturumlar (tanı; bırakılan kilit bellekte
    /// kalmaz).</summary>
    public int EntryCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    private Entry GetOrAdd(string sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var entry))
            _entries[sessionId] = entry = new Entry();
        return entry;
    }

    private void Leave(string sessionId, Entry entry, bool release)
    {
        lock (_lock)
        {
            if (release) entry.Semaphore.Release();
            if (--entry.Users == 0) _entries.Remove(sessionId);
        }
    }

    private sealed class Lease(HandoverGate gate, string sessionId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Leave(sessionId, entry, release: true);
        }
    }
}
