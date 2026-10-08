using System.Data;
using Dapper;

namespace OrderDeck.Core.Storage;

/// <summary>
/// Senkron UYGULAMASI işlemi (göç 045). Sunucudan inen satırı yazmak, yerel yeniden
/// anahtarlama, miras satırı dönüştürme, akıştan inen KVKK silmesi ve taze bilgisayarın
/// form oynatması (U14) DÜZENLEME değildir: birim damgası
/// "şimdi" basılmamalı (yoksa başka bilgisayarın ESKİ değeri taze damgayla her yere
/// yayılır, henüz gönderilmemiş gerçek bir düzenlemeyi ezer) ve SyncSeq ilerlememeli
/// (yankı). İşlem açılışta <c>SyncApplyGuard</c>'a tek satır ekler, Commit'te siler;
/// damga tetikleyicileri ve SyncSeq güncelleme tetikleyicisi bu satır varken çalışmaz.
///
/// <para><b>Neden tablo satırı, neden TEMP değil:</b> TEMP olmayan tetikleyici temp
/// şemadaki tabloya başvuramaz. Kilit yine de fiilen bağlantı kapsamlı: SQLite tek
/// yazıcılı (bu işlem açıkken başka bağlantı yazamaz) ve WAL okuyucuları commit
/// edilmemiş satırı görmez. Satır işlemle birlikte geri alındığı için çöken uygulama
/// kilidi açık bırakmaz.</para>
///
/// <para><b>Commit kilidi silmeden işlemi kapatamaz.</b> Kalıcı bir kilit, bundan
/// sonraki BÜTÜN düzenlemeleri damgasız bırakır — hiçbiri hiçbir bilgisayara gitmezdi
/// ve belirtisi olmazdı. Aynı işlemde ikinci bir kilit birincil anahtara çarpar.</para>
///
/// <para>Microsoft.Data.Sqlite'ta <c>BeginTransaction()</c> (deferred: false)
/// <c>BEGIN IMMEDIATE</c> açar: yazma kilidi OKUMADAN önce alınır, oku-birleştir-yaz
/// arasına başka yazıcı giremez — operatörün o an kaydettiği not kaybolmaz.</para>
/// </summary>
public sealed class SyncApplyScope : IDisposable
{
    // internal (U15): işleme yalnız Core'daki senkron yazımları (CustomerRepository,
    // CustomerSyncRepository) doğrudan erişir. Dışarıdan Transaction.Commit() kilit satırını
    // silmeden commit ederdi; kalmış satır bundan sonraki bütün düzenlemeleri damgasız ve
    // gönderilmez bırakır — belirtisiz.
    internal IDbConnection Connection { get; }
    internal IDbTransaction Transaction { get; }

    // U17: bu akışta yazma kapsamı açık — kapsam açıkken aynı akış ikinci bağlantı açmaz ve
    // CustomerBusySet'in kilidini almaz (denetim: WriteScopeGuard — DEBUG derlemede ve testlerde).
    private readonly IDisposable _scopeMark;

    private SyncApplyScope(IDbConnection connection, IDbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
        _scopeMark = WriteScopeGuard.Enter(nameof(SyncApplyScope));
    }

    public static SyncApplyScope Begin(IDbConnectionFactory factory)
    {
        var conn = factory.Open();
        IDbTransaction? tx = null;
        try
        {
            tx = conn.BeginTransaction();
            conn.Execute("INSERT INTO SyncApplyGuard (Id) VALUES (1)", transaction: tx);
            return new SyncApplyScope(conn, tx);
        }
        catch
        {
            tx?.Dispose();
            conn.Dispose();
            throw;
        }
    }

    public void Commit()
    {
        var removed = Connection.Execute("DELETE FROM SyncApplyGuard", transaction: Transaction);
        if (removed != 1)
            throw new InvalidOperationException(
                "SyncApplyGuard satırı işlem içinde bulunamadı; işlem commit edilmeden geri alınıyor.");
        Transaction.Commit();
        _scopeMark.Dispose();                 // işlem bitti: aynı akış yeniden bağlantı açabilir
    }

    /// <summary>Kapsamın işleminde tek ifade — Core dışındaki çağıranlar (testler) için.</summary>
    public int Execute(string sql, object? param = null) => Connection.Execute(sql, param, Transaction);

    /// <summary>
    /// Kalmış (commit edilmiş) kilit satırlarını siler, sayısını döner. Kapsamlar satırı
    /// commit'ten ÖNCE sildiği için görünen her satır bir hatanın artığıdır: WAL okuyucusu
    /// commit edilmemiş satırı görmez, sürmekte olan bir kapsamın satırı silinemez (DELETE onun
    /// commit'ini bekler, sonra satır yoktur). AppHost açılışta, akış servisi her turda çağırır.
    /// </summary>
    public static int ClearStale(IDbConnectionFactory factory)
    {
        using var conn = factory.Open();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM SyncApplyGuard") == 0) return 0;
        return conn.Execute("DELETE FROM SyncApplyGuard");
    }

    /// <summary>Commit edilmediyse işlem (kilit satırı dahil) geri alınır.</summary>
    public void Dispose()
    {
        _scopeMark.Dispose();
        Transaction.Dispose();
        Connection.Dispose();
    }
}
