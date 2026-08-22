using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Storage;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// O-07: arayüz ipliği ile arka plan senkron servisleri aynı yerel
/// veritabanına eşzamanlı yazıyor. WAL sayesinde okumalar engellenmiyor ama
/// yazar-yazar çakışması SQLite'tan <c>SQLITE_BUSY</c> döndürüyor.
///
/// Ölçüm (bir yazar kilidi tutarken ikinci yazar bekliyor):
/// <code>
///   ayar yok                            → 30.033 ms sonra hata
///   PRAGMA busy_timeout = 3000          → 31.428 ms sonra hata
///   Default Timeout = 5                 →  5.047 ms sonra hata
///   busy_timeout = 3000 + Timeout = 5   →  6.910 ms sonra hata
/// </code>
/// Yani beklemeyi bağlayan şey SQLite'ın busy handler'ı değil,
/// Microsoft.Data.Sqlite'ın kendi yeniden deneme bütçesi. Denetim raporunun
/// önerdiği <c>busy_timeout</c> tek başına ölçülebilir biçimde etkisiz.
///
/// Gerçek dosya kullanılıyor: bellek-içi veritabanında yazma kilidi yok, yani
/// ölçülmek istenen durum hiç oluşmuyor.
/// </summary>
public sealed class SqliteContentionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteContentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"od-contention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "orderdeck.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SqliteConnectionFactory NewFactory()
    {
        var f = new SqliteConnectionFactory(_dbPath);
        using var conn = f.Open();
        conn.Execute("CREATE TABLE IF NOT EXISTS t (v INTEGER);");
        return f;
    }

    /// <summary>
    /// Asıl gerileme testi. Bütçe ayarlanmadan önce bu bekleme, sürücünün
    /// görünmez 30 sn'lik <c>CommandTimeout</c> varsayılanına düşüyordu:
    /// operatörün arayüzü kilit çakışmasında yarım dakika donuyordu.
    /// </summary>
    [Fact]
    public void Blocked_writer_gives_up_within_the_configured_budget()
    {
        var a = NewFactory();
        var b = NewFactory();

        using var holder = DbWrite.Begin(a);
        holder.Connection.Execute("INSERT INTO t (v) VALUES (1);", transaction: holder.Transaction);

        var sw = Stopwatch.StartNew();
        Action blocked = () =>
        {
            using var second = DbWrite.Begin(b);
            second.Connection.Execute("INSERT INTO t (v) VALUES (2);", transaction: second.Transaction);
            second.Commit();
        };
        blocked.Should().Throw<SqliteException>("kilidi bırakmayan bir yazar sonsuza dek bekletmemeli");
        sw.Stop();

        // Üst sınır cömert: sürücü bütçeyi ancak iki deneme arasında kontrol
        // edebiliyor, o yüzden birkaç saniyelik aşım normal. Ölçülen şey
        // 30 sn'lik varsayılana geri düşülmediği.
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(SqliteConnectionFactory.WriteContentionTimeoutSeconds + 8),
            "bekleme yapılandırılan bütçeye bağlı olmalı, 30 sn'lik gizli varsayılana değil");
        sw.Elapsed.Should().BeGreaterThan(
            TimeSpan.FromSeconds(1),
            "geçici çakışmada hemen pes edilmemeli");
    }

    /// <summary>
    /// Bütçenin diğer yüzü: kilit makul sürede bırakılırsa bekleyen yazar
    /// hata almadan geçmeli. Yerel yazmalar milisaniyeler sürdüğü için sahada
    /// beklenen davranış bu.
    /// </summary>
    [Fact]
    public async Task Writer_waits_for_a_short_lock_and_then_succeeds()
    {
        var a = NewFactory();
        var b = NewFactory();

        var holderReady = new TaskCompletionSource();
        var holder = Task.Run(() =>
        {
            using var w = DbWrite.Begin(a);
            w.Connection.Execute("INSERT INTO t (v) VALUES (1);", transaction: w.Transaction);
            holderReady.SetResult();
            Thread.Sleep(1500);
            w.Commit();
        });

        await holderReady.Task;

        using (var second = DbWrite.Begin(b))
        {
            second.Connection.Execute("INSERT INTO t (v) VALUES (2);", transaction: second.Transaction);
            second.Commit();
        }

        await holder;

        using var read = a.Open();
        read.QuerySingle<long>("SELECT COUNT(*) FROM t;").Should().Be(2,
            "kısa süreli kilit çakışması yazmayı kaybettirmemeli");
    }

    /// <summary>
    /// <c>busy_timeout</c> bilerek kurulmuyor — kurulursa beklemeyi bağlamak
    /// yerine bütçeyi aşırıyor. Birinin raporu okuyup "eksik PRAGMA"yı geri
    /// eklemesine karşı çit.
    /// </summary>
    [Fact]
    public void Connection_does_not_set_a_sqlite_level_busy_timeout()
    {
        using var conn = (SqliteConnection)NewFactory().Open();

        conn.ExecuteScalar<long>("PRAGMA busy_timeout;").Should().Be(0,
            "bekleme bütçesi sürücü tarafında (Default Timeout); iki mekanizma üst üste binmemeli");
    }
}
