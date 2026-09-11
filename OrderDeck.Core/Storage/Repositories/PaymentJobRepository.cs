using System;
using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>PaymentJob durumları. String sabitler — SQLite'ta TEXT saklanır,
/// enum sıra değişikliği veri bozamasın.</summary>
public static class PaymentJobState
{
    /// <summary>İş var, henüz anahtar diske inmedi — sunucuya hiçbir şey gitmedi.</summary>
    public const string Created = "created";
    /// <summary>Anahtar diskte; sunucu cevabı KESİNLEŞMEDİ. Bu durumda mesaj gönderilmez.</summary>
    public const string ApplyUncertain = "apply_uncertain";
    /// <summary>Düşüm uygulandı; AppliedAmount dolu.</summary>
    public const string Applied = "applied";
    /// <summary>Sunucu kesin cevap verdi: bakiye yok / uygulanacak şey yok. AppliedAmount = 0.</summary>
    public const string NoBalance = "no_balance";
}

/// <summary>Bir "Ödeme iste" satışının kalıcı kimliği ve düşüm sonucu.
/// Kapsam: (CustomerId, ScopeKey) — "session:{id}" | "cumulative" | "legacy".
/// İşler ASLA silinmez; teslimatta ClosedAt dolar.</summary>
public sealed record PaymentJob(
    string Id,
    string CustomerId,
    string ScopeKey,
    decimal ProductTotal,
    int Revision,
    Guid? ApplyKey,
    decimal? AppliedAmount,
    string State,
    long CreatedAt,
    long UpdatedAt,
    long? ClosedAt);

/// <summary>R2-01..04: ödeme işi yaşam döngüsünün disk katmanı. Tüm geçişler
/// koşullu UPDATE'lerle yarışa dayanıklı; servis katmanı false dönüşünde
/// satırı yeniden okuyup kazananın yazdığını kullanır.</summary>
public interface IPaymentJobStore
{
    /// <summary>Atomik find-or-create: UNIQUE (CustomerId, ScopeKey) +
    /// INSERT OR IGNORE. İki eşzamanlı çağrı aynı satırı görür (R2-04).</summary>
    PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal);

    PaymentJob? Get(string id);

    /// <summary>033'ten taşınan, henüz kapanmamış 'legacy' işi; yoksa null.</summary>
    PaymentJob? GetOpenLegacy(string customerId);

    /// <summary>Anahtarı diske indirir ve işi apply_uncertain'e geçirir —
    /// yalnız anahtar HENÜZ yoksa. false = yarışı kaybettik; yeniden oku,
    /// kazananın anahtarıyla devam et.</summary>
    bool BeginApply(string id, Guid applyKey);

    /// <summary>Çağıran, id'yi aynı akışta <c>FindOrCreate</c>/<c>Get</c>'ten almış olmalıdır;
    /// var olmayan id sessiz no-op'tur, hata değil.</summary>
    void MarkApplied(string id, decimal appliedAmount);

    /// <summary>Kesin "bakiye yok" cevabı — AppliedAmount 0 yazılır.
    /// Çağıran, id'yi aynı akışta <c>FindOrCreate</c>/<c>Get</c>'ten almış olmalıdır;
    /// var olmayan id sessiz no-op'tur, hata değil.</summary>
    void MarkNoBalance(string id);

    /// <summary>Sonuç yeniden belirsizleşti (ör. geri alma ağda kayboldu).
    /// Çağıran, id'yi aynı akışta <c>FindOrCreate</c>/<c>Get</c>'ten almış olmalıdır;
    /// var olmayan id sessiz no-op'tur, hata değil.</summary>
    void MarkUncertain(string id);

    /// <summary>Mesaj müşteriye ulaştı — iş kapanır. Idempotent (ilk zaman korunur).</summary>
    void Close(string id);

    /// <summary>Revizyon: yeni toplam + yeni anahtar + Revision+1, eski sonuç
    /// sıfırlanır, durum apply_uncertain. Yalnız Revision == expectedRevision
    /// ise — false = eşzamanlı revizyon kazandı, yeniden oku.</summary>
    bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision);

    /// <summary>Legacy işin sonucunu (ApplyKey/AppliedAmount/State/ProductTotal)
    /// TAZE hedefe kopyalar ve legacy'yi kapatır — tek transaction. Hedef taze
    /// değilse (anahtarı varsa) hedefe dokunmaz, yine de legacy'yi kapatır.</summary>
    void AdoptLegacyResult(string targetId, string legacyId);
}

public sealed class PaymentJobRepository : IPaymentJobStore
{
    private readonly IDbConnectionFactory _factory;
    public PaymentJobRepository(IDbConnectionFactory factory) => _factory = factory;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal)
    {
        using var conn = _factory.Open();
        var now = Now();
        conn.Execute(
            @"INSERT OR IGNORE INTO PaymentJob
              (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
               AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt)
              VALUES (@id, @customerId, @scopeKey, @total, 0, NULL,
                      NULL, @state, @now, @now, NULL)",
            new
            {
                id = Guid.NewGuid().ToString("N"),
                customerId,
                scopeKey,
                total = Dec(productTotal),
                state = PaymentJobState.Created,
                now,
            });
        var row = conn.QuerySingle<Row>(
            SelectSql + " WHERE CustomerId=@customerId AND ScopeKey=@scopeKey",
            new { customerId, scopeKey });
        return Map(row);
    }

    public PaymentJob? Get(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(SelectSql + " WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public PaymentJob? GetOpenLegacy(string customerId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            SelectSql + " WHERE CustomerId=@customerId AND ScopeKey='legacy' AND ClosedAt IS NULL",
            new { customerId });
        return row is null ? null : Map(row);
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ApplyKey=@key, State=@state, UpdatedAt=@now
              WHERE Id=@id AND ApplyKey IS NULL",
            new
            {
                id,
                key = applyKey.ToString("N"),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
            }) == 1;
    }

    public void MarkApplied(string id, decimal appliedAmount)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET State=@state, AppliedAmount=@amt, UpdatedAt=@now WHERE Id=@id",
            new { id, state = PaymentJobState.Applied, amt = Dec(appliedAmount), now = Now() });
    }

    public void MarkNoBalance(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET State=@state, AppliedAmount=@amt, UpdatedAt=@now WHERE Id=@id",
            new { id, state = PaymentJobState.NoBalance, amt = Dec(0m), now = Now() });
    }

    public void MarkUncertain(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET State=@state, UpdatedAt=@now WHERE Id=@id",
            new { id, state = PaymentJobState.ApplyUncertain, now = Now() });
    }

    public void Close(string id)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentJob SET ClosedAt=COALESCE(ClosedAt,@now), UpdatedAt=@now WHERE Id=@id",
            new { id, now = Now() });
    }

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE PaymentJob
              SET ProductTotal=@total, Revision=Revision+1, ApplyKey=@key,
                  AppliedAmount=NULL, State=@state, UpdatedAt=@now, ClosedAt=NULL
              WHERE Id=@id AND Revision=@expectedRevision",
            new
            {
                id,
                total = Dec(newProductTotal),
                key = newApplyKey.ToString("N"),
                state = PaymentJobState.ApplyUncertain,
                now = Now(),
                expectedRevision,
            }) == 1;
    }

    public void AdoptLegacyResult(string targetId, string legacyId)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        var now = Now();
        conn.Execute(
            @"UPDATE PaymentJob SET
                  ProductTotal  = (SELECT ProductTotal  FROM PaymentJob WHERE Id=@legacyId),
                  ApplyKey      = (SELECT ApplyKey      FROM PaymentJob WHERE Id=@legacyId),
                  AppliedAmount = (SELECT AppliedAmount FROM PaymentJob WHERE Id=@legacyId),
                  State         = (SELECT State         FROM PaymentJob WHERE Id=@legacyId),
                  UpdatedAt     = @now
              WHERE Id=@targetId AND ApplyKey IS NULL AND State=@created",
            new { targetId, legacyId, now, created = PaymentJobState.Created },
            tx);
        conn.Execute(
            "UPDATE PaymentJob SET ClosedAt=COALESCE(ClosedAt,@now), UpdatedAt=@now WHERE Id=@legacyId",
            new { legacyId, now },
            tx);
        tx.Commit();
    }

    private const string SelectSql =
        @"SELECT Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
                 AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt
          FROM PaymentJob";

    private static PaymentJob Map(Row r) => new(
        r.Id,
        r.CustomerId,
        r.ScopeKey,
        decimal.Parse(r.ProductTotal, CultureInfo.InvariantCulture),
        r.Revision,
        r.ApplyKey is null ? null : Guid.ParseExact(r.ApplyKey, "N"),
        r.AppliedAmount is null
            ? null
            : decimal.Parse(r.AppliedAmount, CultureInfo.InvariantCulture),
        r.State,
        r.CreatedAt,
        r.UpdatedAt,
        r.ClosedAt);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string ScopeKey { get; init; } = "";
        public string ProductTotal { get; init; } = "0";
        public int Revision { get; init; }
        public string? ApplyKey { get; init; }
        public string? AppliedAmount { get; init; }
        public string State { get; init; } = "";
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; init; }
        public long? ClosedAt { get; init; }
    }
}
