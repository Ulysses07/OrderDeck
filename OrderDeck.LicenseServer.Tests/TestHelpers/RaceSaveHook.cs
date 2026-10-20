using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Sıralı yarış kancası (gerçek SQL Server testleri): bir <c>SaveChanges</c>'in SQL'i gitmeden HEMEN ÖNCE rakip bir
/// yazımı koşturur — yarış zamanlamaya değil sıraya dayanır (<see cref="SaveHookInterceptor"/> gerekçesi).
/// <see cref="Once"/> tek sefer, <see cref="Always"/> her kayıtta (üst üste çakışma). Kancanın kendi yazımları kancayı
/// yeniden tetiklemez. Jeton çakışmalarını (<see cref="DbUpdateConcurrencyException"/>) sayar.
/// </summary>
public sealed class RaceSaveHook : SaveChangesInterceptor
{
    private Func<Task>? _action;
    private bool _repeat;
    private int _running;
    private int _concurrencyFailures;

    public int ConcurrencyFailures => Volatile.Read(ref _concurrencyFailures);

    public void Once(Func<Task> action)
    {
        _repeat = false;
        Volatile.Write(ref _action, action);
    }

    public void Always(Func<Task> action)
    {
        _repeat = true;
        Volatile.Write(ref _action, action);
    }

    public void Disarm() => Volatile.Write(ref _action, null);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return result;
        try
        {
            var action = _repeat ? Volatile.Read(ref _action) : Interlocked.Exchange(ref _action, null);
            if (action is not null) await action();
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
        return result;
    }

    /// <summary>
    /// Jeton çakışmasını sayar. <c>SaveChangesFailedAsync</c> değil: EF <see cref="DbUpdateConcurrencyException"/>'ı
    /// o olaya hiç uğratmaz (yalnız iyimser eşzamanlılık olayına), sayaç orada hep 0 kalırdı.
    /// </summary>
    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _concurrencyFailures);
        return ValueTask.FromResult(result);
    }
}

/// <summary><see cref="RelationalApiFactory"/> + paylaşılan bir <see cref="RaceSaveHook"/>.</summary>
public sealed class RaceRelationalApiFactory : RelationalApiFactory
{
    public RaceRelationalApiFactory(string connectionString) : base(connectionString) { }

    public RaceSaveHook Hook { get; } = new();

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
}
