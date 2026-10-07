using System.Data;
using Dapper;

namespace OrderDeck.Core.Storage;

/// <summary>
/// <c>Customer.SyncSeq</c>'in AÇIK ilerletmesi (göç 045'in <c>SyncSeqCounter</c>'ı). Satırı gönderim
/// kuyruğuna geri koymanın tek yolu: yeniden anahtarlama ve miras satırı dönüştürmesi asıl kaydı,
/// taze bilgisayarın form oynatması değişen satırı kilit altında (SyncSeq tetikleyicisi çalışmazken)
/// bununla ilerletir.
///
/// <para><b>Neden MAX(SyncSeq)+1 değil:</b> en büyük satır silinince (yeniden anahtarlama yeni
/// gönderilmiş kopyayı siler) MAX+1 gönderim imlecinin ALTINA düşer — satır hiç gönderilmez. Sayaç
/// yalnız artar; tetikleyiciler de numarayı ondan alır. MAX+1 yazan tek bir yol bile sayaçla aynı
/// numarayı üretip F07 benzersizliğini bozar.</para>
///
/// <para><b>İki ifade, çağıranın yazma işleminin İÇİNDE:</b> sayacı artır, satıra yaz. İşlemsiz
/// (autocommit) her ifade yazma kilidini ayrı alıp bırakır; arasına giren başka bir yazıcı aynı
/// sayaç değerini okur ve iki satır AYNI SyncSeq'i alır. Bu yüzden işlem parametresi zorunlu.</para>
///
/// <para>Çağrı başına TEK satır. Birden çok satır = her satır için ayrı çağrı (tek UPDATE'le
/// birden çok satıra yazılsaydı hepsi aynı numarayı alırdı).</para>
/// </summary>
public static class CustomerSyncSeq
{
    private const string BumpSql =
        "UPDATE SyncSeqCounter SET Value = Value + 1 WHERE Id = 1; " +
        "UPDATE Customer SET SyncSeq = (SELECT Value FROM SyncSeqCounter WHERE Id = 1) WHERE Id = @id;";

    /// <summary><paramref name="id"/>'li satırın SyncSeq'ini sayacın bir sonraki değerine
    /// taşır (satır yoksa yalnız sayaç ilerler — zararsız boşluk).</summary>
    public static void Bump(IDbConnection conn, IDbTransaction tx, string id)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        conn.Execute(BumpSql, new { id }, tx);
    }
}
