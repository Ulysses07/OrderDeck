using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>U17: kilit sırası değişmezlerinin DEBUG denetimleri (testler Debug derlemede koşar;
/// Release derlemede denetim yoktur ve testler bir şey yapmadan döner).</summary>
public sealed class WriteScopeGuardTests
{
    [Fact]
    public void DbWrite_acikken_ayni_akista_ikinci_baglanti_acilmaz()
    {
        if (!WriteScopeGuard.ChecksEnabled) return;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        using (var write = DbWrite.Begin(db))
        {
            var nested = () => { using var c = db.Open(); };
            nested.Should().Throw<InvalidOperationException>().WithMessage("*DbWrite*",
                "paket açıkken aynı akıştan yazma kendi kilidini bekleyip düşer; okuma commit edilmemiş satırı görmez");
            write.Commit();
        }

        using var after = db.Open();                          // paket kapandı → serbest
    }

    [Fact]
    public void SyncApplyScope_acikken_uretim_fabrikasi_da_ikinci_baglantiyi_reddeder()
    {
        if (!WriteScopeGuard.ChecksEnabled) return;
        var path = Path.Combine(Path.GetTempPath(), $"od-guard-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();
            using (SyncApplyScope.Begin(factory))
            {
                var nested = () => { using var c = factory.Open(); };
                nested.Should().Throw<InvalidOperationException>().WithMessage("*SyncApplyScope*");
            }
            using var after = factory.Open();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Baska_akis_kapsam_acikken_baglanti_acabilir()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        using var write = DbWrite.Begin(db);

        Task other;
        using (ExecutionContext.SuppressFlow())                 // başka akış: ör. senkron servisinin turu
            other = Task.Run(() => { using var c = db.Open(); });
        await other;                                              // fırlatmaz — denetim akış başınadır
    }
}
