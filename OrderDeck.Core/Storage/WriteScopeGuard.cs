using System;
using System.Threading;

namespace OrderDeck.Core.Storage;

/// <summary>
/// U17 — kilit sırası değişmezlerinin denetimi. <see cref="DbWrite"/> ve
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
/// <para><b>Ne zaman fırlatır:</b> çalışma zamanı anahtarı <see cref="ChecksSwitch"/> açıksa HER
/// derlemede; anahtar yoksa yalnız DEBUG derlemede. Test süreci anahtarı <c>TestAssemblyInit</c>'te
/// açar — CI testleri Release koşar, denetim derlemeye (<c>[Conditional("DEBUG")]</c>) bağlı olsaydı
/// orada hiç çalışmazdı. Üretimde (Release, anahtar yok) denetim yoktur; işaretleme her zaman
/// (maliyetsiz).</para>
/// <para><b>İşaret bir tutucudur</b> (<c>Owner</c>, <c>Active</c>): kapsamın içinde kuyruğa alınan
/// iş (Task.Run, ThreadPool, Dispatcher, CancellationToken.Register…) AsyncLocal'ı miras alır. Kapsam
/// kapanınca tutucu pasifleşir; sonradan koşan o iş yanlış alarm vermez. Başka akış (ör. arka plan
/// servisinin turu) zaten etkilenmez: AsyncLocal akış başınadır.</para>
/// </summary>
public static class WriteScopeGuard
{
    /// <summary>AppContext anahtarı: true → denetim her derlemede açık, false → kapalı; anahtar
    /// yoksa derlemeye göre (DEBUG açık, Release kapalı).</summary>
    public const string ChecksSwitch = "OrderDeck.WriteScopeGuard.Checks";

    private static readonly AsyncLocal<ScopeMark?> Current = new();

    /// <summary>Denetimler açık mı. Her erişimde hesaplanır, önbelleğe alınmaz: anahtarın ne zaman
    /// kurulduğu (başlatma sırası) sonucu değiştirmesin.</summary>
    public static bool ChecksEnabled =>
        AppContext.TryGetSwitch(ChecksSwitch, out var on) ? on : IsDebugBuild();

    /// <summary>Bu akışta AÇIK yazma kapsamının sahibi ("DbWrite", "SyncApplyScope") ya da null.</summary>
    public static string? ActiveScope => Current.Value is { Active: true } mark ? mark.Owner : null;

    /// <summary>Kapsamı işaretler; dönen nesnenin Dispose'u işareti pasifleştirir ve önceki değeri
    /// geri koyar (iki kez çağrılabilir: Commit ve Dispose).</summary>
    internal static IDisposable Enter(string owner)
    {
        var previous = Current.Value;
        var mark = new ScopeMark(owner);
        Current.Value = mark;
        return new Restore(mark, previous);
    }

    /// <summary>Denetim açıksa ve bu akışta açık bir yazma kapsamı varsa fırlatır; aksi hâlde hiçbir
    /// şey yapmaz.</summary>
    public static void AssertNoActiveScope(string operation)
    {
        // Ucuz yol önce: kapsam yokken anahtara hiç bakılmaz (her bağlantı açılışında koşar).
        if (Current.Value is not { Active: true } mark) return;
        if (!ChecksEnabled) return;
        throw new InvalidOperationException(
            $"{operation}: aynı akışta açık bir yazma kapsamı var ({mark.Owner}). Kapsam açıkken yalnız " +
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

    /// <summary>Kapsamın işareti. AsyncLocal'a değer değil BU NESNE konur: kapsamın içinde kuyruğa
    /// alınan işler aynı nesneyi taşır, kapanışta <see cref="Close"/> hepsinde birden görünür.</summary>
    private sealed class ScopeMark(string owner)
    {
        private volatile bool _active = true;

        public string Owner { get; } = owner;

        public bool Active => _active;

        public void Close() => _active = false;
    }

    private sealed class Restore(ScopeMark mark, ScopeMark? previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            mark.Close();
            Current.Value = previous;
        }
    }
}
