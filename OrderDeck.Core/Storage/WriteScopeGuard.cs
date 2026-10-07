using System;
using System.Diagnostics;
using System.Threading;

namespace OrderDeck.Core.Storage;

/// <summary>
/// U17 — kilit sırası değişmezlerinin hata ayıklama denetimi. <see cref="DbWrite"/> ve
/// <see cref="SyncApplyScope"/> açıkken bu akış (AsyncLocal — iş parçacığı değil, mantıksal akış)
/// işaretlenir. İki değişmez:
/// <list type="number">
/// <item>Kapsam açıkken aynı akış İKİNCİ bağlantı açmaz: her okuma ve yazma kapsamın bağlantısıyla
/// (DbWrite belgesinin kuralı). Paket dışından bir yazma kendi akışının tuttuğu kilidi 10 sn
/// bekleyip SQLITE_BUSY ile düşer (kendini kilitleme); paket dışından bir okuma paketin commit
/// edilmemiş satırını görmez.</item>
/// <item>Kapsam açıkken <c>CustomerBusySet</c> kilidi alınmaz: sıra her zaman önce küme, sonra
/// SQLite yazma kilidi.</item>
/// </list>
/// Denetim yalnız DEBUG derlemede fırlatır (testler Debug koşar); işaretleme her zaman (maliyetsiz).
/// Başka akış (ör. arka plan servisinin turu) etkilenmez: AsyncLocal akış başınadır.
/// </summary>
public static class WriteScopeGuard
{
    private static readonly AsyncLocal<string?> Owner = new();

    /// <summary>Denetimler bu derlemede etkin mi (testler Release'te bir şey yapmadan döner).</summary>
    public static bool ChecksEnabled { get; } = IsDebugBuild();

    /// <summary>Bu akışta açık yazma kapsamının sahibi ("DbWrite", "SyncApplyScope") ya da null.</summary>
    public static string? ActiveScope => Owner.Value;

    /// <summary>Kapsamı işaretler; dönen nesnenin Dispose'u önceki değeri geri koyar (iki kez
    /// çağrılabilir: Commit ve Dispose).</summary>
    internal static IDisposable Enter(string owner)
    {
        var previous = Owner.Value;
        Owner.Value = owner;
        return new Restore(previous);
    }

    /// <summary>DEBUG: bu akışta açık bir yazma kapsamı varsa fırlatır.</summary>
    [Conditional("DEBUG")]
    public static void AssertNoActiveScope(string operation)
    {
        if (Owner.Value is { } owner)
            throw new InvalidOperationException(
                $"{operation}: aynı akışta açık bir yazma kapsamı var ({owner}). Kapsam açıkken yalnız " +
                "kapsamın bağlantısı kullanılır; kilit sırası önce CustomerBusySet, sonra SQLite yazma kilidi (U17).");
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Owner.Value = previous;
        }
    }
}
