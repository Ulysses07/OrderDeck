using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Bekleyen komutun türleri (göç 049'un CHECK kısıtı; spec 2026-10-13 §3.2, §5.5).</summary>
public static class PendingSalesCommandKinds
{
    /// <summary>Kalıntı kuyruk etiketinin iptali (<c>cancel</c>).</summary>
    public const string Cancel = "cancel";

    /// <summary>Kalıntı kuyruk etiketinin ilk baskısı (<c>mark-printed</c>).</summary>
    public const string MarkPrinted = "mark-printed";

    public static bool IsKnown(string kind) => kind is Cancel or MarkPrinted;
}

/// <summary>Kuyruktaki bir bekleyen komut ve örttüğü etiketler.</summary>
/// <param name="CommandId">Komutun sabit kimliği (Guid, "N" biçimi): her gönderimde aynı.</param>
/// <param name="PayloadJson">Gönderilecek niyetin kaydı; biçimi yazanın (App'teki kodlayıcı) sözleşmesidir.</param>
/// <param name="CreatedAt">Kuyruğa giriş anı (unix saniye); gönderim sırası.</param>
/// <param name="Attempts">Sonucu alınamamış gönderim sayısı.</param>
/// <param name="LastError">Son denemenin kısa nedeni (durum kodu adı; kişisel veri yok).</param>
/// <param name="LabelIds">Örtülen etiketler (sıralı).</param>
public sealed record PendingSalesCommandRow(
    string CommandId,
    string Kind,
    string PayloadJson,
    long CreatedAt,
    int Attempts,
    string? LastError,
    IReadOnlyList<string> LabelIds);

/// <summary>
/// Bekleyen satış komutları (göç 049'un <c>PendingSalesCommand</c> ve <c>PendingSalesCommandLabel</c> tabloları; spec
/// 2026-10-13 §3.2, §5.5, §5.8 "Kalıntı kuyruk"). Bu bilgisayarın DEVREDİLMİŞ kendi yayınlarından kalan kuyruk
/// etiketlerinin iptali ve ilk baskısı, başka bilgisayarın devredilmiş yayınındaki satırın ilk baskısı (sahip kararı
/// Y11) komutla yapılır; sunucuya o an ulaşılamazsa (ya da kâğıt basıldıysa, önce kalıcı kayıt olarak) burada bekler
/// ve satış akışı turu onu akıştan ÖNCE, aynı kimlikle gönderir.
///
/// <para><b>Örtü:</b> bir komutun örttüğü etiket yerel okuyucularda uygulanmış sayılır — kuyruk
/// (<see cref="LabelRepository.GetUnprintedBySession"/>), kalıntı teklifi
/// (<see cref="LabelRepository.GetUnprintedOutsideSession"/>) ve baskı öncesi yeniden okuma
/// (<see cref="LabelRepository.GetPrintableByIds"/>) onu dışarıda bırakır: aynı kâğıt ikinci kez teklif edilmez. Etiket
/// satırının kendisine dokunulmaz (yayını sunucu otoritesinde; yerel yazım tetikleyiciyle düşerdi); komut uygulanınca
/// sonucu akış öğesi gibi yazılır ve satır silinir, örtü kalkar.</para>
///
/// <para><b>Mod kapısı:</b> yalnız yerel <c>SalesSyncState.Mode = 1</c> iken yazılır (aynı işlemde okunur): mod Off'ta
/// (bugün her lisans) hiçbir satır doğmaz. Satırın varlığı görülen kipi 1 tutar
/// (<see cref="SalesSyncStateRepository.GetSeenMode"/>, değişmedi).</para>
/// </summary>
public sealed class PendingSalesCommandRepository
{
    private const int MaxErrorLength = 200;

    private readonly IDbConnectionFactory _factory;

    public PendingSalesCommandRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Komutu ve örttüğü etiketleri TEK işlemde kuyruğa yazar. Aynı kimlik zaten kuyruktaysa dokunmaz (yeniden deneme
    /// aynı niyettir) ve true döner. Yerel kip On değilse hiçbir şey yazmaz, false döner.
    /// </summary>
    public bool Enqueue(string commandId, string kind, string payloadJson, IReadOnlyCollection<string> labelIds,
        long createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (!PendingSalesCommandKinds.IsKnown(kind))
            throw new ArgumentException($"Bekleyen komut yalnız cancel ya da mark-printed olabilir: {kind}.", nameof(kind));
        var ids = labelIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) throw new ArgumentException("Bekleyen komut en az bir etiketi örtmeli.", nameof(labelIds));

        using var write = DbWrite.Begin(_factory);
        var inserted = write.Connection.Execute(
            """
            INSERT INTO PendingSalesCommand (CommandId, Kind, PayloadJson, CreatedAt, Attempts, LastError)
            SELECT @commandId, @kind, @payloadJson, @createdAt, 0, NULL
            WHERE COALESCE((SELECT Mode FROM SalesSyncState WHERE Id = 1), 0) = 1
              AND NOT EXISTS (SELECT 1 FROM PendingSalesCommand WHERE CommandId = @commandId)
            """,
            new { commandId, kind, payloadJson, createdAt }, write.Transaction);
        if (inserted == 0)
        {
            var exists = write.Connection.ExecuteScalar<long>(
                "SELECT EXISTS (SELECT 1 FROM PendingSalesCommand WHERE CommandId = @commandId)",
                new { commandId }, write.Transaction) == 1;
            return exists;                                  // işlem geri sarılır: yazılan bir şey yok
        }

        write.Connection.Execute(
            "INSERT OR IGNORE INTO PendingSalesCommandLabel (LabelId, CommandId) VALUES (@labelId, @commandId)",
            ids.Select(labelId => new { labelId, commandId }).ToArray(), write.Transaction);
        write.Commit();
        return true;
    }

    /// <summary>Kuyruk, gönderim sırasıyla (eskiden yeniye).</summary>
    public IReadOnlyList<PendingSalesCommandRow> List()
    {
        using var conn = _factory.Open();
        // İki okuma arasında kuyruğa giren komut bu listede yok (bağları yok sayılır); komut ve bağları tek işlemde
        // yazıldığı için listelenen komutun bağları eksik okunmaz.
        var commands = conn.Query<Row>(
            """
            SELECT CommandId, Kind, PayloadJson, CreatedAt, Attempts, LastError
            FROM PendingSalesCommand ORDER BY CreatedAt, rowid
            """).ToList();
        if (commands.Count == 0) return [];
        var links = conn.Query<(string LabelId, string CommandId)>(
                "SELECT LabelId, CommandId FROM PendingSalesCommandLabel ORDER BY CommandId, LabelId")
            .ToLookup(l => l.CommandId, l => l.LabelId, StringComparer.Ordinal);
        return commands
            .Select(c => new PendingSalesCommandRow(c.CommandId, c.Kind, c.PayloadJson, c.CreatedAt, (int)c.Attempts,
                c.LastError, links[c.CommandId].ToList()))
            .ToList();
    }

    /// <summary>Tek komut; yoksa null.</summary>
    public PendingSalesCommandRow? Get(string commandId) =>
        List().FirstOrDefault(c => string.Equals(c.CommandId, commandId, StringComparison.Ordinal));

    /// <summary>
    /// Sonucu alınamamış bir gönderimi sayar (<c>Attempts + 1</c>) ve nedenini yazar. <paramref name="payloadJson"/>
    /// verilirse kayıt onunla değişir: son gönderilen niyet (taze beklenen sürümlerle yeniden denenmiş olabilir) —
    /// sonraki gönderim sunucunun kaydettiği içeriği AYNEN yinelesin.
    /// </summary>
    public void RecordAttempt(string commandId, string? error, string? payloadJson = null)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            UPDATE PendingSalesCommand
            SET Attempts = Attempts + 1, LastError = @error, PayloadJson = COALESCE(@payloadJson, PayloadJson)
            WHERE CommandId = @commandId
            """,
            new
            {
                commandId,
                error = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error,
                payloadJson,
            });
    }

    /// <summary>Komutu ve etiket bağlarını (örtü) siler; örtü kalkar. Satır yoksa false.</summary>
    public bool Delete(string commandId)
    {
        using var write = DbWrite.Begin(_factory);
        // Bağlar şemada ON DELETE CASCADE; yabancı anahtar denetimi kapalı bir bağlantıda da örtü kalmasın diye açıkça.
        write.Connection.Execute("DELETE FROM PendingSalesCommandLabel WHERE CommandId = @commandId",
            new { commandId }, write.Transaction);
        var deleted = write.Connection.Execute("DELETE FROM PendingSalesCommand WHERE CommandId = @commandId",
            new { commandId }, write.Transaction);
        write.Commit();
        return deleted > 0;
    }

    /// <summary>Verilen etiketlerden bekleyen bir komutun örttükleri.</summary>
    public IReadOnlySet<string> GetCoveredLabelIds(IReadOnlyCollection<string> labelIds)
    {
        if (labelIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        using var conn = _factory.Open();
        return conn.Query<string>(
                "SELECT DISTINCT LabelId FROM PendingSalesCommandLabel WHERE LabelId IN @ids",
                new { ids = labelIds.Distinct(StringComparer.Ordinal).ToArray() })
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Verilen müşterilerin (bir ödeme kişisinin bütün üyeleri) etiketlerinden bekleyen bir komutun örttükleri — kapsam
    /// <paramref name="sessionId"/> verilirse o yayın (<c>session:{id}</c>), verilmezse bütün yayınlar
    /// (<c>cumulative</c>). Etiketin durumu süzülmez: bekleyen ilk baskı basılmamış, bekleyen iptal basılmış satırı
    /// örter; ikisi de ödeme isteğinin tutarını değiştirecek gönderilmemiş bir karardır (spec 2026-10-13 §6.4-1).
    /// </summary>
    public IReadOnlySet<string> GetCoveredLabelIdsOfCustomers(
        IReadOnlyCollection<string> customerIds, string? sessionId)
    {
        if (customerIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        using var conn = _factory.Open();
        return conn.Query<string>(
                @"SELECT DISTINCT p.LabelId
                  FROM PendingSalesCommandLabel p
                  JOIN Label l ON l.Id = p.LabelId
                  WHERE l.CustomerId IN @customerIds
                    AND (@sessionId IS NULL OR l.SessionId = @sessionId)",
                new { customerIds = customerIds.Distinct(StringComparer.Ordinal).ToArray(), sessionId })
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed class Row
    {
        public string CommandId { get; init; } = "";
        public string Kind { get; init; } = "";
        public string PayloadJson { get; init; } = "";
        public long CreatedAt { get; init; }
        public long Attempts { get; init; }
        public string? LastError { get; init; }
    }
}
