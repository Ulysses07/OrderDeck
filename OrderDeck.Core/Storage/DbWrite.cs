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
///
/// <para><b>Kilit sırası değişmezleri (U17):</b> (1) paket açıkken aynı akış İKİNCİ bir bağlantı
/// açmaz — her okuma ve yazma <see cref="Connection"/>/<see cref="Transaction"/> üstünden
/// (<c>LabelService.Add</c>'in <c>GetOrCreate</c> + etiket INSERT'i dahil: paketi alan aşırı
/// yüklemeler; iç içe <c>_factory.Open()</c> yazımı YASAK). (2) Paket açıkken
/// <c>CustomerBusySet</c> kilidi alınmaz — sıra her zaman önce küme, sonra SQLite yazma kilidi.
/// İkisini de <see cref="WriteScopeGuard"/> denetler (DEBUG derlemede ve testlerde — anahtar
/// <see cref="WriteScopeGuard.ChecksSwitch"/>; üretimde kapalı).</para>
/// </summary>
public sealed class DbWrite : IDisposable
{
    public IDbConnection Connection { get; }
    public IDbTransaction Transaction { get; }
    private readonly IDisposable _scopeMark;

    private DbWrite(IDbConnection connection, IDbTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
        _scopeMark = WriteScopeGuard.Enter(nameof(DbWrite));
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

    public void Commit()
    {
        Transaction.Commit();
        _scopeMark.Dispose();                 // işlem bitti: aynı akış yeniden bağlantı açabilir
    }

    /// <summary>
    /// Commit edilmemiş işlemi geri sarma işini <see cref="IDbTransaction"/>
    /// kendi Dispose'unda yapar — burada elle Rollback çağırmak, hata yolunda
    /// asıl istisnayı gizleyebilecek ikinci bir istisna kaynağı eklemekten
    /// başka bir işe yaramazdı.
    /// </summary>
    public void Dispose()
    {
        _scopeMark.Dispose();
        Transaction.Dispose();
        Connection.Dispose();
    }
}
