using System.Data;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Yok sayılan ya da reddedilen satış değişikliklerinin izi (göç 049'un <c>SalesConflictLog</c> tablosu; spec
/// 2026-10-13 §3.2, §5.2). Yalnız kimlik taşır: ad, kullanıcı adı ya da mesaj metni YAZILMAZ (§9).
///
/// <para><b>Oturum düzeyindeki kayıt:</b> <c>LabelId</c> boştur, oturumun kimliği <c>Reason</c>'da
/// <see cref="SessionReason"/> biçimiyle durur (tablonun oturum kolonu yok; göç yalnız ekleyici).</para>
/// </summary>
public static class SalesConflictLog
{
    /// <summary>Sunucu, bu kopyanın kapanışını ya da devir isteğini başka kopyanın oturumu diye yok saydı
    /// (§4.1, §8.7: sahip olmayanın kapanışı). Son durum: oturum yeniden gönderilmez, <c>server</c> olmaz.</summary>
    public const string SessionOwnedElsewhere = "session-owned-elsewhere";

    /// <summary>Oturum düzeyindeki kaydın <c>Reason</c>'ı: <c>session:{id}</c>.</summary>
    public static string SessionReason(string sessionId) => $"session:{sessionId}";

    /// <summary>Etiket gönderiminin bir ya da birkaç birimi sunucuda yok sayıldı; sunucunun değeri
    /// (<c>current</c>) uygulandı (§5.2, W1-08).</summary>
    public const string PushIgnored = "push-ignored";

    /// <summary>Etiket gönderimi sunucuda reddedildi (devredilmiş oturum, kargolanmış dosya, başka kopyanın canlı
    /// yayını); sunucunun değeri (<c>current</c>) uygulandı ya da — reddedilen eklemede — satır son durumda
    /// onaylandı (§5.2, W1-08).</summary>
    public const string PushRejected = "push-rejected";

    /// <summary>Gönderim kaydının <c>Reason</c>'ı: sunucunun nedeni ve yanıttaki birimler,
    /// <c>{neden}; units={birim,…}</c>. Yalnız kod adları — kişisel veri yok (§9).</summary>
    public static string PushReason(string? reason, IEnumerable<string> units)
        => $"{(string.IsNullOrEmpty(reason) ? "?" : reason)}; units={string.Join(",", units)}";

    /// <summary>Geri doldurmada birim ayrışması, yerel kazanır (§4.3, §8.2-4; W1-09): satır olağan kuyruğa yeniden
    /// alındı ve v2 push'uyla gidecek. Geri doldurmanın tamamlanması (<c>backfillCompleted</c>, §8.2-5) etiketin bu
    /// satırdan sonraki ilk gönderiminin <c>applied</c> olmasını bekler.</summary>
    public const string BackfillRequeued = "backfill-requeued";

    /// <summary>Geri doldurmada birim ayrışması, sunucu kazanır (geri yüklenmiş ya da kopyalanmış veritabanı,
    /// damgalı birim; §4.3, §8.7): yalnız iz — satır kuyruğa alınmaz, sunucunun değeri mod On'un ilk turunda
    /// iner.</summary>
    public const string BackfillDiverged = "backfill-diverged";

    /// <summary>Mod On'un ilk yetişmesinde (<c>FirstCatchUpAt</c> boş) bu bilgisayarın kendi etiketinde sunucudaki
    /// birim yereldekinden farklıydı; sunucunun değeri yazıldı (§5.4, §8.4). Reason <see cref="UnitsReason"/>
    /// biçiminde, yalnız birim adları. Geri doldurmadaki onarımdan (§8.2-4) sonra yalnız geri yüklenmiş veritabanında
    /// görülmesi beklenir.</summary>
    public const string CatchUpOverwrite = "catchup-overwrite";

    /// <summary>Geri doldurma kaydının <c>Reason</c>'ı: ayrışan birimler, <c>units={birim,…}</c>.</summary>
    public static string UnitsReason(IEnumerable<string> units) => $"units={string.Join(",", units)}";

    /// <summary>Çağıranın işleminde tek satır ekler.</summary>
    internal static void Insert(
        IDbConnection connection, IDbTransaction? transaction, long at, string? labelId, string kind, string? reason)
        => connection.Execute(
            "INSERT INTO SalesConflictLog (At, LabelId, Kind, Reason) VALUES (@at, @labelId, @kind, @reason)",
            new { at, labelId, kind, reason }, transaction);
}
