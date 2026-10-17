using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Sales;

/// <summary>Etiket birimlerinin teldeki adları (spec 2026-10-13 §2, §4.2 <c>ignoredUnits</c>; sunucuda
/// <c>OrderUnitMapping.WireName</c>).</summary>
public static class SalesPushUnits
{
    /// <summary>Fiyat {Price, PriceCorrectedAt, PaidPrice}.</summary>
    public const string Price = "price";
    /// <summary>İptal {CancelledAt, CancelReason}.</summary>
    public const string Cancel = "cancel";
    /// <summary>Yedek {IsTentativeBackup}.</summary>
    public const string Backup = "backup";
    /// <summary>Kargo bağı {ShipmentId}.</summary>
    public const string Shipment = "shipment";
    /// <summary>Baskı {PrintedAt, PrintBatchId, PrintSeq}.</summary>
    public const string Print = "print";

    /// <summary>Bütün birimler — <c>rejected</c>'da <c>current</c>'tan hepsi yazılır.</summary>
    public static IReadOnlyList<string> All { get; } = [Price, Cancel, Backup, Shipment, Print];
}

/// <summary>Satırın v2 yanıtındaki durumu: yok sayıldı ya da reddedildi (<c>applied</c> bu yoldan geçmez).</summary>
public enum LabelPushVerdictKind
{
    Ignored,
    Rejected,
}

/// <summary>
/// Sunucunun güncel satırının (<c>current</c>, akış öğesi biçimi) birim alanları, yerel biçimde: unix saniye,
/// "N" biçimli kimlik. Oluşum alanları ve CustomerId bilerek YOK: yanıt uygulaması onları hiç yazmaz (DM4).
/// </summary>
/// <param name="Version">Satırın sunucudaki sürümü — yerelde <c>ServerVersion</c> olur, sonraki gönderimin
/// <c>baseVersion</c>'ı.</param>
public sealed record LabelServerState(
    int Version,
    decimal Price,
    long? PriceCorrectedAt,
    decimal? PaidPrice,
    long? CancelledAt,
    string? CancelReason,
    bool IsTentativeBackup,
    string? ShipmentId,
    long? PrintedAt,
    string? PrintBatchId,
    long? PrintSeq);

/// <summary>Gönderilen bir etiketin <c>ignored</c> ya da <c>rejected</c> yanıtı.</summary>
/// <param name="Revision">Gönderilen satırın <c>Revision</c>'ı (F05): uygulama yalnız satır o hâldeyse yazar.</param>
/// <param name="IgnoredUnits">Sunucunun yazmadığı birimler (teldeki adlarıyla, <see cref="SalesPushUnits"/>).</param>
/// <param name="Reason">Satırın nedeni (<c>unit-stamped</c>, <c>cancelled-elsewhere</c>, <c>session-released</c>, …).</param>
/// <param name="Current">Sunucunun güncel satırı; reddedilen eklemede (sunucuda satır yok) boş.</param>
public sealed record LabelPushVerdict(
    string LabelId,
    long Revision,
    LabelPushVerdictKind Kind,
    IReadOnlyList<string> IgnoredUnits,
    string? Reason,
    LabelServerState? Current);

/// <summary><see cref="SalesPushApply.Apply"/>'ın sonucu.</summary>
public enum LabelPushApplyStatus
{
    /// <summary><c>current</c> uygulandı, satır onaylandı, iz yazıldı.</summary>
    Applied,

    /// <summary><c>current</c> yok (reddedilen ekleme): iz yazıldı ve satır onaylandı — son durum, yeniden
    /// gönderilmez.</summary>
    Terminal,

    /// <summary>Satır gönderimden sonra değişti (F05): hiçbir şey yazılmadı, yeni hâli sonraki turda gider.</summary>
    Stale,

    /// <summary>Satır zaten onaylanmış (aynı yanıt ikinci kez): hiçbir şey yazılmadı.</summary>
    AlreadyAcknowledged,

    /// <summary>Etiket yerelde yok: hiçbir şey yazılmadı.</summary>
    Missing,
}

/// <param name="ChangedUnits">Yerel değeri değişen birimler (teldeki adlarıyla).</param>
/// <param name="PrintDiscard">Basılmış kâğıt sunucuda alınmadı çünkü sipariş başka bilgisayarda komutla iptal
/// edilmişti (K3 <c>cancelled-elsewhere</c>): operatöre "kâğıdı imha et" denmeli.</param>
/// <param name="Shipments">Y5 dosya bloğunun sonucu (§7.4); blok çalışmadıysa boş.</param>
public sealed record LabelPushApplyResult(
    LabelPushApplyStatus Status,
    IReadOnlyList<string> ChangedUnits,
    bool PrintDiscard,
    ShipmentAdjustResult Shipments)
{
    internal static LabelPushApplyResult Of(LabelPushApplyStatus status) =>
        new(status, Array.Empty<string>(), false, ShipmentAdjustResult.None);
}

/// <summary>
/// v2 <c>orders/sync</c> yanıtının <c>ignored</c> ve <c>rejected</c> satırlarını yerele uygular (spec 2026-10-13
/// §5.2 "Yanıtın işlenmesi", §4.2 Yanıt, §7.4, §6.1; W1-08). Akışın uygulanmadığı mod Off'ta da yerel değer böylece
/// sunucuyla ayrışık kalmaz (§15-26).
///
/// <para><b>Ne yazılır.</b> <c>ignored</c>: yalnız yok sayılan birimler (<c>ignoredUnits</c>) <c>current</c>'tan —
/// öbür birimleri sunucu push'tan aldı, yerel değer zaten doğrudur. <c>rejected</c>: bütün birimler
/// <c>current</c>'tan ("zorla": <c>ignoredUnits</c>'te listelenmeyen birim de; reddedilen push'un hiçbir değeri
/// sunucuda değildir). Oluşum alanları ve CustomerId hiç yazılmaz (DM4; <see cref="LabelServerState"/>'te yoklar).
/// Kargo bağı GUID olarak karşılaştırılır: aynı dosyanın başka yazımı bağ değişikliği sayılmaz.</para>
///
/// <para><b>Aynı işlemde</b> (kilit sırası U17: tek bağlantı, kapsamın işlemi):</para>
/// <list type="bullet">
/// <item>Etiket kilit altında (<see cref="SyncApplyScope"/>) yazılır: 049 tetikleyicileri susar — mod 1'deki kargo
/// bağı ve <c>Authority = 'server'</c> oturumun etiketi de yazılır, kuyruğa alma tetikleyicisi Revision'ı artırmaz.
/// Satır <c>SyncedAt</c>, <c>StockSyncedAt</c> ve <c>ServerVersion = current.Version</c> ile onaylanır; Revision
/// değişmez (DM8).</item>
/// <item>Y5 dosya bloğu (<see cref="ShipmentAmountAdjuster"/>, §7.4): etiketin dosyadaki payı (bağlı ∧ iptal
/// edilmemiş ∧ geçici yedek değil → fiyat; <c>LabelRules.DerivedShipmentAmount</c>) önce ve sonra hesaplanır, fark
/// dosyaya yazılır; bağ değiştiyse eski dosyadan düşülür, yenisine eklenir. Mod 1'de, kargolanmış ve yerelde olmayan
/// dosyada blok yazmaz (adjuster'ın kuralları). Fark okunan satırla <c>current</c> arasındadır: aynı yanıtı ikinci
/// kez uygulamak sıfır fark üretir.</item>
/// <item>Müşteri toplamı etiketlerden türetilir (<see cref="CustomerAggregateSql"/>, §6.1). CustomerId yazılmadığı
/// için eski ve yeni müşteri aynıdır.</item>
/// <item>Tek <c>SalesConflictLog</c> satırı: tür <see cref="SalesConflictLog.PushIgnored"/> ya da
/// <see cref="SalesConflictLog.PushRejected"/>, etiket kimliği, neden ve yanıttaki birimler — ad yok (§9).</item>
/// </list>
///
/// <para><b>Karşılaştır-ve-yaz (F05).</b> Satır gönderilen <c>Revision</c>'da ve hâlâ onaysızsa (<c>SyncedAt IS
/// NULL</c>) uygulanır. Gönderimden sonra değiştiyse hiçbir şey yazılmaz (ne birim, ne dosya, ne iz): yeni değişiklik
/// sonraki turda gider, sunucunun yanıtı yine gelir. Onaylanmış satır aynı yanıtı ikinci kez almaz.</para>
///
/// <para><b>Reddedilen ekleme</b> (<c>current</c> yok, ör. <c>session-owned-elsewhere</c>): iz ve onay; son durum —
/// satır yeniden gönderilmez, birimlerine dokunulmaz.</para>
///
/// <para><b>Kâğıt imhası (K3).</b> Baskı birimi yok sayıldıysa ve yerelde basılmış satırın <c>current</c>'ında
/// <c>PrintedAt</c> boşsa sipariş başka bilgisayarda komutla iptal edilmiştir (<c>cancelled-elsewhere</c>; sunucuda
/// PrintedAt boşken baskının yok sayılmasının tek nedeni budur). Satırın nedeni ret nedeni olsa bile
/// (<c>session-released</c>) bu böyle anlaşılır.</para>
/// </summary>
public sealed class SalesPushApply
{
    private readonly IDbConnectionFactory _factory;

    public SalesPushApply(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>Tek etiketin yanıtını kendi <see cref="SyncApplyScope"/> işleminde uygular.</summary>
    /// <param name="now">Onay ve iz anı (unix saniye).</param>
    public LabelPushApplyResult Apply(LabelPushVerdict verdict, long now)
    {
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        // BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı — okunan satır yazılana kadar değişemez.
        var row = conn.QuerySingleOrDefault<Row>(
            @"SELECT CustomerId, Price, PriceCorrectedAt, PaidPrice, CancelledAt, CancelReason, IsTentativeBackup,
                     ShipmentId, PrintedAt, PrintBatchId, PrintSeq, SyncedAt, Revision
              FROM Label WHERE Id = @id",
            new { id = verdict.LabelId }, tx);
        if (row is null) return LabelPushApplyResult.Of(LabelPushApplyStatus.Missing);
        if (row.Revision != verdict.Revision) return LabelPushApplyResult.Of(LabelPushApplyStatus.Stale);
        if (row.SyncedAt is not null) return LabelPushApplyResult.Of(LabelPushApplyStatus.AlreadyAcknowledged);

        var kind = verdict.Kind == LabelPushVerdictKind.Rejected
            ? SalesConflictLog.PushRejected
            : SalesConflictLog.PushIgnored;
        var reason = SalesConflictLog.PushReason(verdict.Reason, verdict.IgnoredUnits);

        if (verdict.Current is not { } current)
        {
            WriteLabel(conn, tx, verdict.LabelId, verdict.Revision, Units.Of(row), [], now, serverVersion: null);
            SalesConflictLog.Insert(conn, tx, now, verdict.LabelId, kind, reason);
            scope.Commit();
            return LabelPushApplyResult.Of(LabelPushApplyStatus.Terminal);
        }

        var write = verdict.Kind == LabelPushVerdictKind.Rejected
            ? SalesPushUnits.All.ToHashSet(StringComparer.Ordinal)
            : verdict.IgnoredUnits.ToHashSet(StringComparer.Ordinal);
        var before = Units.Of(row);
        var after = before.Take(current, write);
        var changed = before.ChangedUnits(after);
        var printDiscard = verdict.IgnoredUnits.Contains(SalesPushUnits.Print, StringComparer.Ordinal)
                           && before.PrintedAt is not null
                           && current.PrintedAt is null;

        WriteLabel(conn, tx, verdict.LabelId, verdict.Revision, after, changed, now, current.Version);
        var deltas = ShipmentDeltas(before, after);
        var shipments = deltas.Count == 0
            ? ShipmentAdjustResult.None
            : ShipmentAmountAdjuster.Apply(deltas, scope);
        CustomerAggregateSql.Recompute(conn, tx, [row.CustomerId]);
        SalesConflictLog.Insert(conn, tx, now, verdict.LabelId, kind, reason);
        scope.Commit();
        return new LabelPushApplyResult(LabelPushApplyStatus.Applied, changed, printDiscard, shipments);
    }

    /// <summary>Değişen birimlerin alanları ve onay. Değişmeyen birim yazılmaz (REAL kolonun değeri decimal'den
    /// geri dönüşte oynamasın). F05 koşulu yazımda da (okumayla aynı işlemde zaten tutar).</summary>
    private static void WriteLabel(
        IDbConnection conn, IDbTransaction tx, string id, long revision, Units u, IReadOnlyCollection<string> changed,
        long now, int? serverVersion)
    {
        var written = conn.Execute(
            @"UPDATE Label SET
                     Price            = CASE WHEN @wPrice = 1 THEN @Price ELSE Price END,
                     PriceCorrectedAt = CASE WHEN @wPrice = 1 THEN @PriceCorrectedAt ELSE PriceCorrectedAt END,
                     PaidPrice        = CASE WHEN @wPrice = 1 THEN @PaidPrice ELSE PaidPrice END,
                     CancelledAt      = CASE WHEN @wCancel = 1 THEN @CancelledAt ELSE CancelledAt END,
                     CancelReason     = CASE WHEN @wCancel = 1 THEN @CancelReason ELSE CancelReason END,
                     IsTentativeBackup = CASE WHEN @wBackup = 1 THEN @IsTentativeBackup ELSE IsTentativeBackup END,
                     ShipmentId       = CASE WHEN @wShipment = 1 THEN @ShipmentId ELSE ShipmentId END,
                     PrintedAt        = CASE WHEN @wPrint = 1 THEN @PrintedAt ELSE PrintedAt END,
                     PrintBatchId     = CASE WHEN @wPrint = 1 THEN @PrintBatchId ELSE PrintBatchId END,
                     PrintSeq         = CASE WHEN @wPrint = 1 THEN @PrintSeq ELSE PrintSeq END,
                     SyncedAt = @now, StockSyncedAt = @now, ServerVersion = COALESCE(@serverVersion, ServerVersion)
              WHERE Id = @id AND Revision = @revision",
            new
            {
                id, revision, now, serverVersion,
                wPrice = changed.Contains(SalesPushUnits.Price) ? 1 : 0,
                wCancel = changed.Contains(SalesPushUnits.Cancel) ? 1 : 0,
                wBackup = changed.Contains(SalesPushUnits.Backup) ? 1 : 0,
                wShipment = changed.Contains(SalesPushUnits.Shipment) ? 1 : 0,
                wPrint = changed.Contains(SalesPushUnits.Print) ? 1 : 0,
                Price = (double)u.Price,
                u.PriceCorrectedAt,
                PaidPrice = (double?)u.PaidPrice,
                u.CancelledAt,
                u.CancelReason,
                IsTentativeBackup = u.IsTentativeBackup ? 1 : 0,
                u.ShipmentId,
                u.PrintedAt,
                u.PrintBatchId,
                u.PrintSeq,
            }, tx);
        if (written != 1)
            throw new InvalidOperationException("Etiket aynı işlemde okunduğu hâlde bulunamadı; yanıt uygulanmadı.");
    }

    /// <summary>Etiketin dosyadaki payının farkı: önceki dosyadan önceki pay düşülür, sonraki dosyaya sonraki pay
    /// eklenir (aynı dosyaysa net fark). Sıfır farklar atılır.</summary>
    private static Dictionary<string, decimal> ShipmentDeltas(Units before, Units after)
    {
        var deltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (before.ShipmentShare() is { } b) deltas[b.Id] = deltas.GetValueOrDefault(b.Id) - b.Amount;
        if (after.ShipmentShare() is { } a) deltas[a.Id] = deltas.GetValueOrDefault(a.Id) + a.Amount;
        foreach (var id in deltas.Where(kv => kv.Value == 0m).Select(kv => kv.Key).ToList()) deltas.Remove(id);
        return deltas;
    }

    /// <summary>Etiketin birim alanları (yerel biçim).</summary>
    private sealed record Units(
        decimal Price, long? PriceCorrectedAt, decimal? PaidPrice,
        long? CancelledAt, string? CancelReason,
        bool IsTentativeBackup,
        string? ShipmentId,
        long? PrintedAt, string? PrintBatchId, long? PrintSeq)
    {
        public static Units Of(Row r) => new(
            r.Price, r.PriceCorrectedAt, r.PaidPrice, r.CancelledAt, r.CancelReason, r.IsTentativeBackup != 0,
            r.ShipmentId, r.PrintedAt, r.PrintBatchId, r.PrintSeq);

        /// <summary>Verilen birimleri <paramref name="s"/>'ten alır; öbürleri olduğu gibi kalır.</summary>
        public Units Take(LabelServerState s, IReadOnlySet<string> units)
        {
            var u = this;
            if (units.Contains(SalesPushUnits.Price))
                u = u with { Price = s.Price, PriceCorrectedAt = s.PriceCorrectedAt, PaidPrice = s.PaidPrice };
            if (units.Contains(SalesPushUnits.Cancel))
                u = u with { CancelledAt = s.CancelledAt, CancelReason = s.CancelReason };
            if (units.Contains(SalesPushUnits.Backup))
                u = u with { IsTentativeBackup = s.IsTentativeBackup };
            if (units.Contains(SalesPushUnits.Shipment) && !SameShipment(ShipmentId, s.ShipmentId))
                u = u with { ShipmentId = s.ShipmentId };
            if (units.Contains(SalesPushUnits.Print))
                u = u with { PrintedAt = s.PrintedAt, PrintBatchId = s.PrintBatchId, PrintSeq = s.PrintSeq };
            return u;
        }

        public IReadOnlyList<string> ChangedUnits(Units after)
        {
            var changed = new List<string>();
            if (Price != after.Price || PriceCorrectedAt != after.PriceCorrectedAt || PaidPrice != after.PaidPrice)
                changed.Add(SalesPushUnits.Price);
            if (CancelledAt != after.CancelledAt || !string.Equals(CancelReason, after.CancelReason, StringComparison.Ordinal))
                changed.Add(SalesPushUnits.Cancel);
            if (IsTentativeBackup != after.IsTentativeBackup)
                changed.Add(SalesPushUnits.Backup);
            if (!string.Equals(ShipmentId, after.ShipmentId, StringComparison.Ordinal))
                changed.Add(SalesPushUnits.Shipment);
            if (PrintedAt != after.PrintedAt || !string.Equals(PrintBatchId, after.PrintBatchId, StringComparison.Ordinal)
                || PrintSeq != after.PrintSeq)
                changed.Add(SalesPushUnits.Print);
            return changed;
        }

        /// <summary>Dosyadaki pay (DM7): bağlı ∧ iptal edilmemiş ∧ geçici yedek değil → fiyat.</summary>
        public (string Id, decimal Amount)? ShipmentShare() =>
            ShipmentId is { } id && CancelledAt is null && !IsTentativeBackup ? (id, Price) : null;

        private static bool SameShipment(string? a, string? b) =>
            a is null || b is null
                ? a is null && b is null
                : string.Equals(a, b, StringComparison.Ordinal)
                  || (Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb) && ga == gb);
    }

    private sealed class Row
    {
        public string CustomerId { get; set; } = "";
        public decimal Price { get; set; }
        public long? PriceCorrectedAt { get; set; }
        public decimal? PaidPrice { get; set; }
        public long? CancelledAt { get; set; }
        public string? CancelReason { get; set; }
        public long IsTentativeBackup { get; set; }
        public string? ShipmentId { get; set; }
        public long? PrintedAt { get; set; }
        public string? PrintBatchId { get; set; }
        public long? PrintSeq { get; set; }
        public long? SyncedAt { get; set; }
        public long Revision { get; set; }
    }
}
