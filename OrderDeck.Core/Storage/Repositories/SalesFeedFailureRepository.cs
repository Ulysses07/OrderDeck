using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Uygulanamayan satış akışı öğesinin anahtarı: tür (<c>OrderDeck.Core.Sales.SalesFeedItemKind</c>) ve Id.
/// Aynı Id iki türde çakışmaz.</summary>
public readonly record struct SalesFeedItemKey(string Kind, string ItemId);

/// <summary>
/// Uygulanamayan satış akışı öğesinin kalıcı deneme sayacı (göç 049 <c>SalesFeedFailure</c>; spec 2026-10-13 §3.2,
/// §5.4, U10) — <see cref="CustomerSyncRepository.RecordFeedFailure"/> ve kardeşlerinin ikizi. Akış servisi beş turda
/// uygulanamayan öğeyi atlar (<see cref="MarkSkipped"/>); ortam hataları ve "bekliyor" durumları (U5, ödeme akışı)
/// deneme sayılmaz, buraya hiç yazılmaz. Bekleyen müşteri yalnız müşteri akışı öğeden sonra yetiştikçe sayılır (hata
/// metni <c>PendingCustomer</c>): projeksiyonu olmayan Id'ye yönlendirme hiç gelmez.
///
/// <para>Hata metni kişisel veri taşımamalı (çağıranın sorumluluğu: yalnız tür ve Id); 500 karakterde kesilir.</para>
/// </summary>
public sealed class SalesFeedFailureRepository
{
    private const int MaxErrorLength = 500;

    private readonly IDbConnectionFactory _factory;

    public SalesFeedFailureRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>Başarısız denemeyi kalıcı sayar, güncel deneme sayısını döner. Aynı öğenin DAHA YENİ bir değişikliği
    /// (başka <paramref name="changeSeq"/>) başarısız olursa sayaç baştan başlar — içerik değişti; ilk başarısızlık anı
    /// da o değişikliğinki olur. Yeniden kayıt atlanmış işaretini kaldırır.</summary>
    public int Record(string kind, string itemId, long changeSeq, string error, long nowUnix)
    {
        using var conn = _factory.Open();
        // SET ifadelerinin hepsi satırın ESKİ değerlerini görür (SQLite): ChangeSeq karşılaştırması atama sırasından
        // bağımsız.
        return conn.ExecuteScalar<int>(@"
            INSERT INTO SalesFeedFailure (Kind, ItemId, ChangeSeq, Attempts, LastError, FirstFailedAt)
            VALUES (@kind, @itemId, @changeSeq, 1, @error, @now)
            ON CONFLICT(Kind, ItemId) DO UPDATE SET
                Attempts      = CASE WHEN SalesFeedFailure.ChangeSeq = excluded.ChangeSeq
                                     THEN SalesFeedFailure.Attempts + 1 ELSE 1 END,
                FirstFailedAt = CASE WHEN SalesFeedFailure.ChangeSeq = excluded.ChangeSeq
                                     THEN SalesFeedFailure.FirstFailedAt ELSE excluded.FirstFailedAt END,
                ChangeSeq     = excluded.ChangeSeq,
                LastError     = excluded.LastError,
                SkippedAt     = NULL
            RETURNING Attempts",
            new
            {
                kind, itemId, changeSeq, now = nowUnix,
                error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error,
            });
    }

    /// <summary>Öğe atlandı (U10): akış onu geçer, durum satırı "M öğe atlandı" der.</summary>
    public void MarkSkipped(string kind, string itemId, long nowUnix)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE SalesFeedFailure SET SkippedAt = @now WHERE Kind = @kind AND ItemId = @itemId",
            new { kind, itemId, now = nowUnix });
    }

    /// <summary>Öğe sonunda uygulandı: kaydı siler.</summary>
    public void Clear(string kind, string itemId)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM SalesFeedFailure WHERE Kind = @kind AND ItemId = @itemId", new { kind, itemId });
    }

    /// <summary>Kaydı olan öğeler (uygulanınca silinecekler).</summary>
    public IReadOnlySet<SalesFeedItemKey> GetIds()
    {
        using var conn = _factory.Open();
        return conn.Query<(string Kind, string ItemId)>("SELECT Kind, ItemId FROM SalesFeedFailure")
            .Select(r => new SalesFeedItemKey(r.Kind, r.ItemId))
            .ToHashSet();
    }

    /// <summary>Bütün kayıtları siler, sayısını döner. Kayıtlar lisansa bağlı değil: akış servisi lisans değişince
    /// çağırır — önceki lisansın atlanan öğeleri yeni lisansın durum satırında kalmasın.</summary>
    public int ClearAll()
    {
        using var conn = _factory.Open();
        return conn.Execute("DELETE FROM SalesFeedFailure");
    }

    /// <summary>Atlanmış öğe sayısı (durum satırı ve dikkat sayacı, §5.7).</summary>
    public int CountSkipped()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SalesFeedFailure WHERE SkippedAt IS NOT NULL");
    }
}
