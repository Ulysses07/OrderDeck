using System;
using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Diske inmiş, henüz sonuçlanmamış bir bakiye düşüm işi.
/// <see cref="ProductTotal"/> kargo dahil, bakiye öncesi toplamdır — yeni
/// denemenin "aynı satış mı?" karşılaştırması bu değerle yapılır.</summary>
public sealed record PendingBalanceApply(
    Guid IdempotencyKey,
    string CustomerId,
    decimal ProductTotal,
    long CreatedAt);

/// <summary>
/// N02 (2026-09-10 denetimi): bakiye düşümünün kalıcı ödeme-işi kimliği.
///
/// Sunucunun apply ucu idempotent (anahtar = ledger PK) ama anahtar yalnız
/// bellekteyse LaunchFailed sonrası ikinci tıklama yeni anahtar üretir ve
/// bakiye ikinci kez düşer. Anahtar burada diske iner; mesaj müşteriye
/// ulaşana kadar (Sent ya da wa.me açıldı) kayıt çözülmemiş kalır ve yeni
/// deneme aynı anahtarı yeniden kullanır.
/// </summary>
public interface IPendingBalanceApplyStore
{
    /// <summary>Müşterinin çözülmemiş işi; yoksa null. Akış gereği müşteri
    /// başına en fazla bir çözülmemiş kayıt olur (yenisi ancak eskisi
    /// çözüldükten sonra yaratılır).</summary>
    PendingBalanceApply? GetUnresolved(string customerId);

    /// <summary>Apply çağrısından HEMEN ÖNCE yazılır — süreç apply ile mesaj
    /// arasında ölürse bile anahtar kaybolmaz.</summary>
    void Create(string customerId, Guid idempotencyKey, decimal productTotal);

    /// <summary>Mesaj müşteriye ulaştı (ya da anahtarın sunucuda hiç
    /// kullanılmadığı kanıtlandı) — iş kapanır, sonraki deneme yeni anahtar üretir.</summary>
    void MarkResolved(Guid idempotencyKey);
}

public sealed class PendingBalanceApplyRepository : IPendingBalanceApplyStore
{
    private readonly IDbConnectionFactory _factory;
    public PendingBalanceApplyRepository(IDbConnectionFactory factory) => _factory = factory;

    public PendingBalanceApply? GetUnresolved(string customerId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT IdempotencyKey, CustomerId, ProductTotal, CreatedAt
              FROM PendingBalanceApply
              WHERE CustomerId=@customerId AND ResolvedAt IS NULL
              ORDER BY CreatedAt DESC
              LIMIT 1",
            new { customerId });
        return row is null
            ? null
            : new PendingBalanceApply(
                Guid.ParseExact(row.IdempotencyKey, "N"),
                row.CustomerId,
                decimal.Parse(row.ProductTotal, CultureInfo.InvariantCulture),
                row.CreatedAt);
    }

    public void Create(string customerId, Guid idempotencyKey, decimal productTotal)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO PendingBalanceApply
              (IdempotencyKey, CustomerId, ProductTotal, CreatedAt, ResolvedAt)
              VALUES (@key, @customerId, @productTotal, @createdAt, NULL)",
            new
            {
                key = idempotencyKey.ToString("N"),
                customerId,
                productTotal = productTotal.ToString(CultureInfo.InvariantCulture),
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public void MarkResolved(Guid idempotencyKey)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PendingBalanceApply SET ResolvedAt=@now WHERE IdempotencyKey=@key AND ResolvedAt IS NULL",
            new { key = idempotencyKey.ToString("N"), now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    private sealed class Row
    {
        public string IdempotencyKey { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string ProductTotal { get; init; } = "0";
        public long CreatedAt { get; init; }
    }
}
