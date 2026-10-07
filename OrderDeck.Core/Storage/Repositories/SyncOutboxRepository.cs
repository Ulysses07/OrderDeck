using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Durum satırının kalıcı uyarıları (D2): beş turda uygulanamayıp atlanan müşteri
/// akışı öğeleri (U10) ve uzlaştırma bekleyen anahtarlı açık miras ödeme işleri (U8).</summary>
public readonly record struct SyncAttention(int SkippedFeedItems, int OpenLegacyPaymentJobs)
{
    public bool Any => SkippedFeedItems + OpenLegacyPaymentJobs > 0;
}

/// <summary>
/// Henüz sunucuya gitmemiş kayıtların toplamı — kapanış uyarısı ve durum
/// satırı için. Her tablo kendi servisinin outbox ölçütüyle sayılır
/// (SyncedAt IS NULL; müşteri için SyncSeq > biçim-2 gönderim imleci). Yeni bir
/// gönderilen tablo eklenince BURAYA da eklenmeli.
///
/// <para>Müşteri sayımı bir fark değil <c>COUNT(*)</c>'tır — SyncSeq ardışık değildir (C1).
/// Silinmiş (<c>PurgedAt</c> dolu) satır hiç gönderilmez (C6) ama gönderim turu onu geçene dek
/// imlecin üstünde kalır; sayılmaz.</para>
/// </summary>
public sealed class SyncOutboxRepository
{
    private readonly IDbConnectionFactory _factory;
    public SyncOutboxRepository(IDbConnectionFactory factory) => _factory = factory;

    public int CountPending(long customerPushCursor)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(@"
            SELECT (SELECT COUNT(*) FROM StreamSession WHERE SyncedAt IS NULL)
                 + (SELECT COUNT(*) FROM Label         WHERE SyncedAt IS NULL)
                 + (SELECT COUNT(*) FROM Payment       WHERE SyncedAt IS NULL)
                 + (SELECT COUNT(*) FROM Shipment      WHERE SyncedAt IS NULL)
                 + (SELECT COUNT(*) FROM Customer      WHERE SyncSeq > @c AND PurgedAt IS NULL)",
            new { c = customerPushCursor });
    }

    /// <summary>Operatörün bilmesi gereken iki kalıcı durum (bkz. <see cref="SyncAttention"/>).
    /// Anahtarlı açık miras iş ölçütü <see cref="CustomerSyncRepository.CountOpenKeyedLegacyJobs"/>
    /// ile aynı (ortak koşul).</summary>
    public SyncAttention CountAttention()
    {
        using var conn = _factory.Open();
        var (skipped, legacy) = conn.QuerySingle<(long, long)>(@"
            SELECT (SELECT COUNT(*) FROM CustomerFeedFailure WHERE SkippedAt IS NOT NULL),
                   (SELECT COUNT(*) FROM PaymentJob WHERE " + CustomerSyncRepository.OpenKeyedLegacyJobCondition + ")");
        return new SyncAttention((int)skipped, (int)legacy);
    }
}
