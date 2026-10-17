using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Durum satırının kalıcı uyarıları (D2): beş turda uygulanamayıp atlanan müşteri
/// akışı öğeleri (U10), uzlaştırma bekleyen anahtarlı açık miras ödeme işleri (U8) ve beş turda
/// uygulanamayıp atlanan satış akışı öğeleri (spec 2026-10-13 §5.7, U10).</summary>
public readonly record struct SyncAttention(int SkippedFeedItems, int OpenLegacyPaymentJobs, int SkippedSalesFeedItems = 0)
{
    public bool Any => SkippedFeedItems + OpenLegacyPaymentJobs + SkippedSalesFeedItems > 0;
}

/// <summary>
/// Henüz sunucuya gitmemiş kayıtların toplamı — kapanış uyarısı ve durum
/// satırı için. Her tablo kendi servisinin outbox ölçütüyle sayılır
/// (SyncedAt IS NULL; müşteri için SyncSeq > biçim-2 gönderim imleci). Yeni bir
/// gönderilen tablo eklenince BURAYA da eklenmeli — ve gönderim servisi durum izleyicisine
/// kaydolup başarılı turlarını yazmalı (<c>SyncStatusTracker.RegisterPush</c>/<c>MarkPushOk</c>,
/// D2 incelemesi I-3), yoksa düşen gönderimi durum satırı göremez.
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

    /// <summary>Operatörün bilmesi gereken kalıcı durumlar (bkz. <see cref="SyncAttention"/>).
    /// Anahtarlı açık miras iş ölçütü <see cref="CustomerSyncRepository.CountOpenKeyedLegacyJobs"/>
    /// ile aynı (ortak koşul); atlanan satış akışı öğesi <see cref="SalesFeedFailureRepository.CountSkipped"/>
    /// ile aynı.</summary>
    public SyncAttention CountAttention()
    {
        using var conn = _factory.Open();
        var (skipped, legacy, salesSkipped) = conn.QuerySingle<(long, long, long)>(@"
            SELECT (SELECT COUNT(*) FROM CustomerFeedFailure WHERE SkippedAt IS NOT NULL),
                   (SELECT COUNT(*) FROM PaymentJob WHERE " + CustomerSyncRepository.OpenKeyedLegacyJobCondition + @"),
                   (SELECT COUNT(*) FROM SalesFeedFailure WHERE SkippedAt IS NOT NULL)");
        return new SyncAttention((int)skipped, (int)legacy, (int)salesSkipped);
    }
}
