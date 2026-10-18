using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Bekleyen ödeme kaydının türleri (spec 2026-10-13 §3.2, §4.7, §6.4). Göç 049'da CHECK kısıtı yok; türü
/// <see cref="PendingPaymentRecordRepository.Enqueue"/> denetler.</summary>
public static class PendingPaymentRecordKinds
{
    /// <summary>Kiranın sonucu, gönderilemedi (<c>POST payment-requests/{id}/finish</c>; §6.4 "Akışın sonu").</summary>
    public const string Finish = "finish";

    /// <summary>Kirasız kayıt (<c>POST payment-requests/backfill</c>): mod Off'taki teslim ya da belirsiz gönderim
    /// (§6.4 "Ödeme geçmişi").</summary>
    public const string Record = "record";

    public static bool IsKnown(string kind) => kind is Finish or Record;
}

/// <summary>Kuyruktaki bir ödeme kaydı.</summary>
/// <param name="RecordId">Sunucudaki kaydın Id'si (Guid, "N" biçimi); kuyruğun anahtarı.</param>
/// <param name="Kind"><see cref="PendingPaymentRecordKinds"/>.</param>
/// <param name="PayloadJson">Gönderilecek kaydın tamamı; biçimi yazanın (App'teki kodlayıcı) sözleşmesidir. Kopya kimliği
/// ve görülen kip yükte YOK: gönderim anının zarfından gelir (geri yüklemeden sonra yeni kimlik, §8.7).</param>
/// <param name="CreatedAt">Kuyruğa giriş anı (unix saniye); gönderim sırası.</param>
/// <param name="Attempts">Sonucu alınamamış gönderim sayısı.</param>
/// <param name="LastError">Son denemenin kısa nedeni (taşıma sonucu ya da ret kodu; kişisel veri ve tutar yok).</param>
public sealed record PendingPaymentRecordRow(
    string RecordId,
    string Kind,
    string PayloadJson,
    long CreatedAt,
    int Attempts,
    string? LastError);

/// <summary>
/// Bekleyen ödeme kayıtları (göç 049'un <c>PendingPaymentRecord</c> tablosu; spec 2026-10-13 §3.2, §4.7, §6.4): sunucuya
/// o an gönderilemeyen <c>finish</c>'ler ve mod Off'ta her teslim ya da belirsiz gönderimden sonra yazılan kirasız
/// kayıtlar. Gönderici bunları geri doldurma turunda (60 sn, iki kipte) ve mod On açılışının 1. adımında İLK sırada
/// gönderir (§8.4-1).
///
/// <para><b>Moddan bağımsız:</b> <c>PendingSalesCommand</c>'ın aksine kip kapısı yok — <c>finish</c>, liste ve
/// <c>backfill</c> iki kipte çalışır (§4.7). Satırın varlığı görülen kipi değiştirmez.</para>
/// </summary>
public sealed class PendingPaymentRecordRepository
{
    private const int MaxErrorLength = 200;

    private readonly IDbConnectionFactory _factory;

    public PendingPaymentRecordRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Kaydı kuyruğa yazar. <paramref name="recordId"/>'ye göre idempotent: aynı Id zaten kuyruktaysa satıra dokunulmaz
    /// (yük, deneme sayısı ve sıra değişmez) ve false döner; yeni satır yazıldıysa true.
    /// </summary>
    public bool Enqueue(string recordId, string kind, string payloadJson, long createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (!PendingPaymentRecordKinds.IsKnown(kind))
            throw new ArgumentException($"Bekleyen ödeme kaydı yalnız finish ya da record olabilir: {kind}.", nameof(kind));

        using var conn = _factory.Open();
        return conn.Execute(
            """
            INSERT OR IGNORE INTO PendingPaymentRecord (RecordId, Kind, PayloadJson, CreatedAt, Attempts, LastError)
            VALUES (@recordId, @kind, @payloadJson, @createdAt, 0, NULL)
            """,
            new { recordId, kind, payloadJson, createdAt }) == 1;
    }

    /// <summary>Kuyruk, gönderim sırasıyla (eskiden yeniye; eşit anda yazılış sırası).</summary>
    public IReadOnlyList<PendingPaymentRecordRow> List()
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
                """
                SELECT RecordId, Kind, PayloadJson, CreatedAt, Attempts, LastError
                FROM PendingPaymentRecord ORDER BY CreatedAt, rowid
                """)
            .Select(r => r.ToRow())
            .ToList();
    }

    /// <summary>Tek kayıt; yoksa null.</summary>
    public PendingPaymentRecordRow? Get(string recordId)
    {
        using var conn = _factory.Open();
        return conn.QuerySingleOrDefault<Row>(
            """
            SELECT RecordId, Kind, PayloadJson, CreatedAt, Attempts, LastError
            FROM PendingPaymentRecord WHERE RecordId = @recordId
            """,
            new { recordId })?.ToRow();
    }

    /// <summary>Kuyruktaki kayıt sayısı.</summary>
    public int Count()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM PendingPaymentRecord");
    }

    /// <summary>Sonucu alınamamış bir gönderimi sayar (<c>Attempts + 1</c>) ve nedenini yazar.</summary>
    public void RecordAttempt(string recordId, string? error) => RecordAttempt([recordId], error);

    /// <summary>Aynı gönderimdeki (geri doldurma partisi) kayıtların denemesini TEK işlemde sayar.</summary>
    public void RecordAttempt(IReadOnlyCollection<string> recordIds, string? error)
    {
        if (recordIds.Count == 0) return;
        var trimmed = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
        using var write = DbWrite.Begin(_factory);
        write.Connection.Execute(
            "UPDATE PendingPaymentRecord SET Attempts = Attempts + 1, LastError = @error WHERE RecordId = @recordId",
            recordIds.Distinct(StringComparer.Ordinal).Select(recordId => new { recordId, error = trimmed }).ToArray(),
            write.Transaction);
        write.Commit();
    }

    /// <summary>Kaydı kuyruktan siler. Satır yoksa false.</summary>
    public bool Delete(string recordId) => Delete([recordId]) > 0;

    /// <summary>Kayıtları TEK işlemde siler; silinen satır sayısı.</summary>
    public int Delete(IReadOnlyCollection<string> recordIds)
    {
        if (recordIds.Count == 0) return 0;
        using var write = DbWrite.Begin(_factory);
        var deleted = write.Connection.Execute(
            "DELETE FROM PendingPaymentRecord WHERE RecordId = @recordId",
            recordIds.Distinct(StringComparer.Ordinal).Select(recordId => new { recordId }).ToArray(),
            write.Transaction);
        write.Commit();
        return deleted;
    }

    private sealed class Row
    {
        public string RecordId { get; init; } = "";
        public string Kind { get; init; } = "";
        public string PayloadJson { get; init; } = "";
        public long CreatedAt { get; init; }
        public long Attempts { get; init; }
        public string? LastError { get; init; }

        public PendingPaymentRecordRow ToRow() =>
            new(RecordId, Kind, PayloadJson, CreatedAt, (int)Attempts, LastError);
    }
}
