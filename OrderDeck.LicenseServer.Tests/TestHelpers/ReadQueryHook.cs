using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Sıralı okuma kancası (gerçek SQL Server testleri): <c>when</c>'i tutan ilk okuma komutunun hemen öncesinde
/// <c>before</c>'u bir kez koşturur. Yarış zamanlamaya değil sıraya dayanır (<see cref="RaceSaveHook"/>'un okuma
/// karşılığı; InMemory'de komut kancası yok). Yeniden girmez: kanca gövdesinin sorguları onu tetiklemez. Gövde
/// içinden <see cref="Arm"/> çağrılırsa sonraki adım kurulur (zincir) ve gövdeden sonraki komutlarda denenir.
/// </summary>
public sealed class ReadQueryHook : DbCommandInterceptor
{
    private Func<string, bool>? _when;
    private Func<Task>? _before;
    private bool _running;

    public void Arm(Func<string, bool> when, Func<Task> before)
    {
        _when = when;
        _before = before;
    }

    public void Disarm() => _before = null;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (_before is { } hook && !_running && _when!(command.CommandText))
        {
            _before = null;
            _running = true;
            try { await hook(); }
            finally { _running = false; }
        }
        return result;
    }
}

/// <summary><see cref="RelationalApiFactory"/> + paylaşılan bir <see cref="ReadQueryHook"/>.</summary>
public sealed class ReadHookRelationalApiFactory : RelationalApiFactory
{
    public ReadHookRelationalApiFactory(string connectionString) : base(connectionString) { }

    public ReadQueryHook Queries { get; } = new();

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Queries);
}
