using System.Data;

namespace OrderDeck.Core.Storage;

/// <summary>
/// Birden çok depoya yayılan yazmaları <b>tek bağlantı üzerinde tek işleme</b>
/// bağlar: ya hepsi kalıcı olur ya hiçbiri.
///
/// Neden gerekli: her depo metodu kendi bağlantısını açar
/// (<c>using var conn = _factory.Open()</c>). Bu, tek başına çalışan yazmalar
/// için doğru ve basit — ama iki depoya sırayla yazan bir iş akışında araya
/// giren hata yarısı uygulanmış bir durum bırakır. SQLite'ta işlem tek
/// bağlantıya bağlıdır; iki ayrı bağlantıdaki iki yazmayı hiçbir işlem
/// kapsayamaz. Paylaşılan bağlantıyı taşıyan şey bu tip.
///
/// Bunun yerini tutmaya çalışan "hata olursa elle geri al" telafi kodu
/// güvenilmez: telafinin başarısızlık sebebi çoğu zaman asıl hatanınkiyle
/// aynıdır (kilitli dosya, dolu disk), yani tam gerektiği anda çalışmaz.
/// Süreç ortada ölürse hiç çalışmaz. İşlem her iki durumda da geri sarar,
/// çünkü geri sarmayı yapan uygulama değil veritabanının kendisi.
///
/// <b>Kullanım kuralı:</b> paket açıkken yalnızca aynı paketi alan yazmalar
/// çağrılmalı. Paket açıkken ikinci bir bağlantıdan yazmaya kalkmak SQLite'ta
/// kilide düşer (<c>SQLITE_BUSY</c>). Okumalar paket açılmadan önce bitirilir.
/// </summary>
public sealed class DbWrite : IDisposable
{
    public IDbConnection Connection { get; }
    public IDbTransaction Transaction { get; }

    private DbWrite(IDbConnection connection, IDbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public static DbWrite Begin(IDbConnectionFactory factory)
    {
        var conn = factory.Open();
        try
        {
            return new DbWrite(conn, conn.BeginTransaction());
        }
        catch
        {
            // BeginTransaction patlarsa bağlantı sahipsiz kalmasın.
            conn.Dispose();
            throw;
        }
    }

    public void Commit() => Transaction.Commit();

    /// <summary>
    /// Commit edilmemiş işlemi geri sarma işini <see cref="IDbTransaction"/>
    /// kendi Dispose'unda yapar — burada elle Rollback çağırmak, hata yolunda
    /// asıl istisnayı gizleyebilecek ikinci bir istisna kaynağı eklemekten
    /// başka bir işe yaramazdı.
    /// </summary>
    public void Dispose()
    {
        Transaction.Dispose();
        Connection.Dispose();
    }
}
