using System.Collections.Generic;
using System.Linq;
using Dapper;
using OrderDeck.Core.Sales;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class LabelRepository
{
    private readonly IDbConnectionFactory _factory;
    public LabelRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(Label l)
    {
        using var conn = _factory.Open();
        // SQLite stores BOOLs as INTEGER — Dapper handles bool→0/1 conversion,
        // but we cast explicitly so the parameter type is unambiguous on
        // callers that pass an anonymous-typed projection.
        //
        // StockSyncedAt = SyncedAt: göç 030'daki backfill'in aynısı. Yeni
        // yazılan etikette ikisi de NULL; yedekten geri yüklenen, sunucuya
        // çoktan gitmiş bir satırda ikisi de dolu olmalı — yoksa geri yükleme
        // bakiyeden bir kez daha düşerdi. Label kaydına ayrı bir alan eklemeye
        // gerek yok: damganın kaynağı zaten SyncedAt.
        conn.Execute(
            @"INSERT INTO Label
              (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt, PrintedAt,
               IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt,
               StockSyncedAt, ProductId, ProductVariantId)
              VALUES
              (@Id, @SessionId, @CustomerId, @Platform, @Username, @DisplayName, @MessageText, @Code, @Price, @AddedAt, @PrintedAt,
               @IsBackupPromoted, @ParentLabelId, @IsTentativeBackup, @IsShippingFee, @ShipmentId, @SyncedAt,
               @SyncedAt, @ProductId, @ProductVariantId)",
            new
            {
                l.Id, l.SessionId, l.CustomerId, l.Platform, l.Username, l.DisplayName, l.MessageText,
                l.Code, l.Price, l.AddedAt, l.PrintedAt,
                IsBackupPromoted = l.IsBackupPromoted ? 1 : 0,
                l.ParentLabelId,
                IsTentativeBackup = l.IsTentativeBackup ? 1 : 0,
                IsShippingFee = l.IsShippingFee ? 1 : 0,
                l.ShipmentId,
                l.SyncedAt,
                l.ProductId,
                l.ProductVariantId
            });
    }

    public void Delete(string id)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM Label WHERE Id=@id", new { id });
    }

    public Label? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId
              FROM Label WHERE Id=@id",
            new { id });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<Label> GetUnprintedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        // Cancelled labels are NOT eligible for re-print — exclude them from the
        // queue snapshot the same way they're excluded from revenue totals.
        // Tentative backup labels DO appear here on purpose: the operator
        // wants to print them physically alongside normal queue items so they
        // can stick the spare sticker on the goods.
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NULL AND CancelledAt IS NULL
              ORDER BY AddedAt",
            new { sessionId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Tentative-backup labels for a given parent — used by the
    /// BackupTransferDialog after the parent is cancelled, and by the chip
    /// counter on the queue. Confirmed backups (IsTentativeBackup=0) are NOT
    /// returned because they're already real labels in their own right.
    /// </summary>
    public IReadOnlyList<Label> GetTentativeBackupsByParent(string parentLabelId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId
              FROM Label
              WHERE ParentLabelId=@parentLabelId AND IsTentativeBackup = 1 AND CancelledAt IS NULL
              ORDER BY AddedAt",
            new { parentLabelId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>One round-trip count of tentative backups grouped by parent —
    /// drives the chip badge on the queue UI.</summary>
    public IReadOnlyDictionary<string, int> GetTentativeBackupCounts(IEnumerable<string> parentLabelIds)
    {
        var idArray = parentLabelIds.ToArray();
        if (idArray.Length == 0)
            return new Dictionary<string, int>();

        using var conn = _factory.Open();
        var rows = conn.Query<(string ParentLabelId, int Count)>(
            @"SELECT ParentLabelId, COUNT(*) AS Count
              FROM Label
              WHERE ParentLabelId IN @ids AND IsTentativeBackup = 1 AND CancelledAt IS NULL
              GROUP BY ParentLabelId",
            new { ids = idArray });
        return rows.ToDictionary(r => r.ParentLabelId, r => r.Count);
    }

    /// <summary>Flips IsTentativeBackup→0 for the given label ids. Used when
    /// the operator confirms a backup after the original buyer cancels.
    /// Returns the labels affected so callers can update customer aggregates.</summary>
    public void ConfirmTentativeBackups(IEnumerable<string> labelIds, DbWrite? write = null)
    {
        // IsTentativeBackup STOK-İLGİLİ: geçici yedek sunucuda hareket üretmez,
        // onaylanmış olan üretir. StockSyncedAt düşüyor ki satır yeniden bekleyen
        // sayılsın, SyncedAt de düşüyor ki sunucu bu değişikliği GÖRSÜN — tek
        // outbox o. Yalnız StockSyncedAt düşerse satır bir daha push edilmez ve
        // sunucu yedeği ömür boyu "geçici" sanar (bkz. MarkCancelled).
        _factory.Execute(write,
            "UPDATE Label SET IsTentativeBackup = 0, SyncedAt=NULL, StockSyncedAt=NULL WHERE Id IN @ids AND IsTentativeBackup = 1",
            new { ids = labelIds.ToArray() });
    }

    /// <param name="write">
    /// Doluysa yazma çağıranın işlemine katılır (bkz. <see cref="DbWrite"/>).
    /// Boş bırakılırsa metot kendi bağlantısını açar — mevcut çağıranlar için
    /// davranış değişmiyor.
    /// </param>
    public void MarkCancelled(IEnumerable<string> ids, long cancelledAt, string reason, DbWrite? write = null)
    {
        // State değişikliği → SyncedAt NULL. CancelledAt stok-ilgili olduğu için
        // StockSyncedAt de düşüyor.
        _factory.Execute(write,
            "UPDATE Label SET CancelledAt=@cancelledAt, CancelReason=@reason, SyncedAt=NULL, StockSyncedAt=NULL WHERE Id IN @ids",
            new { cancelledAt, reason, ids = ids.ToArray() });
    }

    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void Uncancel(IEnumerable<string> ids, DbWrite? write = null)
    {
        _factory.Execute(write,
            "UPDATE Label SET CancelledAt=NULL, CancelReason=NULL, SyncedAt=NULL, StockSyncedAt=NULL WHERE Id IN @ids",
            new { ids = ids.ToArray() });
    }

    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void MarkPrinted(IEnumerable<string> ids, long printedAt, DbWrite? write = null)
    {
        // StockSyncedAt'e BİLEREK dokunulmuyor: yazdırmak satırı push kuyruğuna
        // geri alır ama stok açısından hiçbir şeyi değiştirmez — sunucunun
        // defterinde hareket zaten var. Silinirse aynı etiket ikinci kez düşülür.
        _factory.Execute(write,
            "UPDATE Label SET PrintedAt=@printedAt, SyncedAt=NULL WHERE Id IN @ids",
            new { printedAt, ids = ids.ToArray() });
    }

    /// <summary>Updates the Price of a single label. Used by
    /// <c>LabelService.ConfirmBackup</c> when the operator negotiates a
    /// different number while promoting a tentative backup.</summary>
    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void UpdatePrice(string id, decimal price, DbWrite? write = null)
    {
        // MarkPrinted'daki gerekçenin aynısı: fiyat stok-ilgili değil.
        _factory.Execute(write,
            "UPDATE Label SET Price=@price, SyncedAt=NULL WHERE Id=@id", new { id, price });
    }

    /// <summary>
    /// PR siparis-sync (2026-05-13): henüz LicenseServer'a push edilmemiş
    /// label'lar. Outbox query.
    /// </summary>
    public IReadOnlyList<Label> GetUnsynced(int limit = 100)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId
              FROM Label
              WHERE SyncedAt IS NULL
              ORDER BY AddedAt
              LIMIT @limit",
            new { limit }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Push başarılı sonrası SyncedAt'i set eder. StockSyncedAt de
    /// burada doluyor: push başarılıysa sunucu satırın stok-ilgili hâlini
    /// görmüş, defterini ona göre kurmuş demektir.</summary>
    public void MarkSynced(string id, long syncedAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Label SET SyncedAt=@syncedAt, StockSyncedAt=@syncedAt WHERE Id=@id",
            new { id, syncedAt });
    }

    public SessionTotals GetSessionTotals(string sessionId)
    {
        using var conn = _factory.Open();
        // Cancelled labels are excluded from revenue + count; the dialog still
        // surfaces them visually in the customer detail view (with an iptal
        // badge) so the audit trail isn't lost.
        // Tentative backup labels are also excluded — they're physically
        // printed sticker stand-ins for "if the original cancels", not
        // realised sales. Only after the operator confirms a backup
        // (IsTentativeBackup→0) does it count toward revenue.
        var row = conn.QueryFirstOrDefault<TotalsRow>(
            @"SELECT
                COUNT(*)               AS PrintedCount,
                COALESCE(SUM(Price),0) AS TotalAmount,
                COUNT(DISTINCT CustomerId) AS UniqueCustomers
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NOT NULL
                AND CancelledAt IS NULL AND IsTentativeBackup = 0",
            new { sessionId });

        return new SessionTotals(
            row?.PrintedCount ?? 0,
            row?.TotalAmount ?? 0m,
            row?.UniqueCustomers ?? 0);
    }

    /// <summary>
    /// Bir yayında belirli ürün kodundan kaç sipariş alındığı. Hero'daki
    /// "BU ÜRÜNDEN" sayacı.
    ///
    /// GetSessionTotals'tan iki farkı var, ikisi de bilinçli:
    ///  * PrintedAt filtresi YOK — operatör siparişi kuyruğa düştüğü anda
    ///    saymak istiyor, yazdırmayı beklemek sayacı geciktirirdi.
    ///  * Kod eşleşmesi harf duyarsız; hero girişi büyük harfe zorluyor ama
    ///    eski satırlar karışık olabilir.
    /// İptal ve onaylanmamış yedek dışlaması ise AYNI — iki sayaç birbirini
    /// tutmalı.
    /// </summary>
    public int CountSessionLabelsByCode(string sessionId, string code)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            """
            SELECT COUNT(*)
            FROM Label
            WHERE SessionId = @sessionId
              AND Code IS NOT NULL
              AND Code = @code COLLATE NOCASE
              AND CancelledAt IS NULL
              AND IsTentativeBackup = 0
            """,
            new { sessionId, code });
    }

    /// <summary>
    /// Yayın raporu için platform kırılımı: platform başına basılmış etiket
    /// adedi, ciro ve tekil müşteri. Filtre kuralları GetSessionTotals ile
    /// birebir aynı (iptal + tentative backup hariç) — iki sorgunun toplamları
    /// tutmalı; kuralı birinde değiştirirsen diğerini de güncelle.
    /// Ciro'ya göre azalan sıralı döner (rapor tablosu doğrudan bind eder).
    /// </summary>
    public IReadOnlyList<PlatformBreakdown> GetPlatformBreakdownBySession(string sessionId)
    {
        using var conn = _factory.Open();
        return conn.Query<PlatformBreakdownRow>(
            @"SELECT Platform,
                     COUNT(*)                   AS LabelCount,
                     COALESCE(SUM(Price),0)     AS TotalAmount,
                     COUNT(DISTINCT CustomerId) AS UniqueCustomers
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NOT NULL
                AND CancelledAt IS NULL AND IsTentativeBackup = 0
              GROUP BY Platform
              ORDER BY SUM(Price) DESC",
            new { sessionId })
            .Select(r => new PlatformBreakdown(r.Platform, r.LabelCount, r.TotalAmount, r.UniqueCustomers))
            .ToList();
    }

    public IReadOnlyList<TopCustomer> GetTopCustomersBySession(string sessionId, int limit = 10)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<TopCustomerRow>(
            @"SELECT c.Username,
                     c.DisplayName,
                     l.Platform,
                     COUNT(*)   AS LabelCount,
                     SUM(l.Price) AS TotalAmount
              FROM Label l
              JOIN Customer c ON c.Id = l.CustomerId
              WHERE l.SessionId=@sessionId AND l.PrintedAt IS NOT NULL
                AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0
              GROUP BY l.CustomerId
              ORDER BY SUM(l.Price) DESC
              LIMIT @limit",
            new { sessionId, limit }).ToList();
        return rows.Select(r => new TopCustomer(r.Username, r.Platform, r.LabelCount, r.TotalAmount, r.DisplayName)).ToList();
    }

    /// <summary>Returns the labels a customer added in a specific session, ordered
    /// oldest-first so the auctioneer sees them in the order they happened during
    /// the live. Cancelled rows are returned too — UI flags them visually.</summary>
    public IReadOnlyList<CustomerLabelRow> GetByCustomerAndSession(string customerId, string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, string SessionId, string MessageText, string? Code,
                                decimal Price, long AddedAt, long? PrintedAt,
                                long? CancelledAt, string? CancelReason)>(
            @"SELECT Id, SessionId, MessageText, Code, Price, AddedAt, PrintedAt,
                     CancelledAt, CancelReason
              FROM Label
              WHERE CustomerId=@customerId AND SessionId=@sessionId
              ORDER BY AddedAt",
            new { customerId, sessionId });

        return rows
            .Select(r => new CustomerLabelRow(
                r.Id, r.SessionId, r.MessageText, r.Code, r.Price, r.AddedAt,
                IsPrinted: r.PrintedAt is not null,
                CancelledAt: r.CancelledAt,
                CancelReason: r.CancelReason))
            .ToList();
    }

    /// <summary>
    /// Dönem raporu: verilen tarih aralığında BASILAN siparişlerin platform
    /// hesabı + GÜN bazında özeti. Ciro tanımı yayın raporuyla birebir aynı —
    /// basılmış, iptal edilmemiş, onaylanmamış backup olmayan satırlar.
    /// Kargo ücreti satırları toplama DAHİLDİR (muhasebe tek tutar istiyor).
    ///
    /// Gün kırılımı e-Fatura zorunluluğu: fatura günlük kesiliyor, aynı kişinin
    /// farklı günlerdeki alımları ayrı faturalar. Gün YEREL takvim gününe göre
    /// (<c>'localtime'</c>) hesaplanır — operatör hangi günü gördüyse o.
    ///
    /// Aralık [fromUnix, toUnix) — üst sınır dışarda, çağıran ayın ilk gününü
    /// verir. Zaman ekseni <c>PrintedAt</c>: sipariş kesinleştiği anda sayılır.
    ///
    /// Kişi bazına indirgeme burada YAPILMAZ; <see cref="PeriodReportBuilder"/>
    /// yapar (GroupId ile mükerrer kişi birleştirme).
    /// </summary>
    public IReadOnlyList<PeriodAccountRow> GetPeriodAccountRows(long fromUnix, long toUnix)
    {
        using var conn = _factory.Open();
        // Pozitif-parametreli record yerine DTO: SQLite aggregate kolonlarının
        // (COUNT/SUM) bildirilen tipi yok, Dapper ctor eşleştirmesinde byte[]'e
        // düşüyor. Property set'i tip dönüşümünü yapar — repo'nun diğer
        // rapor sorguları da bu kalıbı kullanıyor.
        var rows = conn.Query<PeriodAccountDto>(
            @"SELECT c.Id            AS CustomerId,
                     c.GroupId       AS GroupId,
                     l.Platform      AS Platform,
                     c.Username      AS Username,
                     c.DisplayName   AS DisplayName,
                     c.FullName      AS FullName,
                     c.Tckn          AS Tckn,
                     c.Phone         AS Phone,
                     c.Address       AS Address,
                     c.City          AS City,
                     c.District      AS District,
                     c.Email         AS Email,
                     c.LastSeenAt    AS LastSeenAt,
                     date(l.PrintedAt, 'unixepoch', 'localtime') AS Day,
                     MAX(l.PrintedAt) AS LastPrintedAt,
                     COUNT(*)        AS OrderCount,
                     SUM(l.Price)    AS TotalAmount
              FROM Label l
              JOIN Customer c ON c.Id = l.CustomerId
              WHERE l.PrintedAt >= @fromUnix AND l.PrintedAt < @toUnix
                AND l.CancelledAt IS NULL
                AND l.IsTentativeBackup = 0
              GROUP BY l.CustomerId, l.Platform, date(l.PrintedAt, 'unixepoch', 'localtime')
              ORDER BY SUM(l.Price) DESC",
            new { fromUnix, toUnix }).ToList();

        return rows.Select(r => new PeriodAccountRow(
            r.CustomerId, r.GroupId, r.Platform, r.Username, r.DisplayName,
            r.FullName, r.Tckn, r.Phone, r.Address, r.City, r.District, r.Email,
            r.LastSeenAt, r.Day, r.LastPrintedAt, r.OrderCount, r.TotalAmount)).ToList();
    }

    /// <summary>
    /// Kargo PR C (2026-05-11): müşterinin verilen yayındaki ürün satış toplamı.
    /// Kargo ücreti label'larını (IsShippingFee=1), iptal edilenleri ve henüz
    /// onaylanmamış backup'ları (IsTentativeBackup=1) dışarda bırakır. Print
    /// durumuna bakmaz — operatör basmadan önce de toplam doğru olmalı çünkü
    /// dekont matcher yayın anında bunu kullanır.
    /// </summary>
    public decimal GetCustomerSessionLabelTotal(string customerId, string sessionId)
    {
        using var conn = _factory.Open();
        var total = conn.ExecuteScalar<decimal?>(
            @"SELECT COALESCE(SUM(Price), 0)
              FROM Label
              WHERE CustomerId=@customerId AND SessionId=@sessionId
                AND CancelledAt IS NULL
                AND IsTentativeBackup = 0
                AND IsShippingFee = 0",
            new { customerId, sessionId });
        return total ?? 0m;
    }

    /// <summary>
    /// Kümülatif kargo PR-C/2 (2026-05-12): Bir müşterinin henüz herhangi bir
    /// Shipment dosyasına bağlanmamış aktif Label'ları. ShipmentService payment
    /// onayı sonrası bunları açık Shipment'a attach eder.
    ///
    /// Filter:
    /// - ShipmentId IS NULL (henüz Shipment'a bağlanmadı)
    /// - CancelledAt IS NULL (iptal edilmemiş)
    /// - IsTentativeBackup = 0 (onaylanmamış backup'lar dahil edilmesin)
    /// Cross-session: tüm yayınlardaki açık Label'lar.
    /// </summary>
    public IReadOnlyList<Label> GetUnattachedByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId
              FROM Label
              WHERE CustomerId=@customerId
                AND ShipmentId IS NULL
                AND CancelledAt IS NULL
                AND IsTentativeBackup = 0
              ORDER BY AddedAt",
            new { customerId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Full lifetime label history (every session). Used by the customer
    /// detail dialog when there's no active session to scope to.</summary>
    public IReadOnlyList<CustomerLabelRow> GetByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, string SessionId, string MessageText, string? Code,
                                decimal Price, long AddedAt, long? PrintedAt,
                                long? CancelledAt, string? CancelReason)>(
            @"SELECT Id, SessionId, MessageText, Code, Price, AddedAt, PrintedAt,
                     CancelledAt, CancelReason
              FROM Label
              WHERE CustomerId=@customerId
              ORDER BY AddedAt DESC",
            new { customerId });

        return rows
            .Select(r => new CustomerLabelRow(
                r.Id, r.SessionId, r.MessageText, r.Code, r.Price, r.AddedAt,
                IsPrinted: r.PrintedAt is not null,
                CancelledAt: r.CancelledAt,
                CancelReason: r.CancelReason))
            .ToList();
    }

    /// <summary>
    /// Yerelde yazılmış ama sunucuya <b>henüz gitmemiş</b> etiketleri
    /// (ürün, varyant) anahtarına göre sayar. Gösterilen bakiye
    /// <c>sunucu bakiyesi − bu sayı</c> olarak hesaplanır.
    ///
    /// <para>Filtre sunucudaki defter sayma kuralının <b>birebir aynası</b>:
    /// bir etiket stoktan düşer ancak ve ancak ürüne bağlıysa, kargo bedeli
    /// değilse, iptal edilmemişse ve geçici yedek değilse. Biri unutulursa
    /// gösterilen bakiye sunucununkiyle kalıcı olarak ayrışır.</para>
    ///
    /// <para><c>GROUP BY ProductVariantId</c> NULL'ları tek kovada topluyor —
    /// SQLite'ta GROUP BY, UNIQUE'in aksine NULL'ları eşit sayar. Ürün
    /// seviyesindeki (varyantsız) bekleyenler bu sayede tek satır olur.</para>
    ///
    /// <para>Süzgeç <c>SyncedAt</c> DEĞİL <c>StockSyncedAt</c>: ilki bir outbox
    /// bayrağı ve yazdırma/fiyat düzeltme onu yeniden NULL'a çekiyor, oysa o
    /// satır sunucunun defterinde çoktan sayılmış olur — aynı etiket ikinci kez
    /// düşülürdü. Ayrıntı göç 030'da.</para>
    /// </summary>
    public IReadOnlyList<PendingStockDelta> GetPendingStockDeltas(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<PendingRow>(
            """
            SELECT ProductVariantId, COUNT(*) AS PendingCount
            FROM Label
            WHERE StockSyncedAt IS NULL
              AND ProductId = @productId
              AND IsShippingFee = 0
              AND CancelledAt IS NULL
              AND IsTentativeBackup = 0
            GROUP BY ProductVariantId
            """,
            new { productId })
            .Select(r => new PendingStockDelta(
                productId, r.ProductVariantId, (int)r.PendingCount))
            .ToList();
    }

    // SQLite COUNT(*) Int64 döner; daraltma burada (bkz. ShipmentRepository.Row).
    private sealed class PendingRow
    {
        public string? ProductVariantId { get; init; }
        public long PendingCount { get; init; }
    }

    private static Label Map(Row r) =>
        new(r.Id, r.SessionId, r.CustomerId, r.Platform, r.Username, r.MessageText,
            r.Code, r.Price, r.AddedAt, r.PrintedAt, r.CancelledAt, r.CancelReason,
            IsBackupPromoted: r.IsBackupPromoted != 0,
            ParentLabelId: r.ParentLabelId,
            IsTentativeBackup: r.IsTentativeBackup != 0,
            DisplayName: r.DisplayName,
            IsShippingFee: r.IsShippingFee != 0,
            ShipmentId: r.ShipmentId,
            SyncedAt: r.SyncedAt,
            ProductId: r.ProductId,
            ProductVariantId: r.ProductVariantId);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string MessageText { get; init; } = "";
        public string? Code { get; init; }
        public decimal Price { get; init; }
        public long AddedAt { get; init; }
        public long? PrintedAt { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public int IsBackupPromoted { get; init; }
        public string? ParentLabelId { get; init; }
        public int IsTentativeBackup { get; init; }
        public int IsShippingFee { get; init; }
        public string? ShipmentId { get; init; }
        public long? SyncedAt { get; init; }
        public string? ProductId { get; init; }
        public string? ProductVariantId { get; init; }
    }

    private sealed class TotalsRow
    {
        public int PrintedCount { get; init; }
        public decimal TotalAmount { get; init; }
        public int UniqueCustomers { get; init; }
    }

    private sealed class TopCustomerRow
    {
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string Platform { get; init; } = "";
        public int LabelCount { get; init; }
        public decimal TotalAmount { get; init; }
    }

    private sealed class PeriodAccountDto
    {
        public string CustomerId { get; init; } = "";
        public string? GroupId { get; init; }
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? FullName { get; init; }
        public string? Tckn { get; init; }
        public string? Phone { get; init; }
        public string? Address { get; init; }
        public string? City { get; init; }
        public string? District { get; init; }
        public string? Email { get; init; }
        public long LastSeenAt { get; init; }
        public string Day { get; init; } = "";
        public long LastPrintedAt { get; init; }
        public int OrderCount { get; init; }
        public decimal TotalAmount { get; init; }
    }

    private sealed class PlatformBreakdownRow
    {
        public string Platform { get; init; } = "";
        public int LabelCount { get; init; }
        public decimal TotalAmount { get; init; }
        public int UniqueCustomers { get; init; }
    }
}

public sealed record SessionTotals(int PrintedCount, decimal TotalAmount, int UniqueCustomers);

/// <summary>
/// Yayın raporundaki platform kırılımı satırı (IG/TikTok/FB/YT başına
/// adet + ciro + tekil müşteri). <see cref="DisplayName"/> UI/Excel için
/// Türkçe platform adı üretir; bilinmeyen platform ham haliyle döner.
/// </summary>
public sealed record PlatformBreakdown(
    string Platform, int LabelCount, decimal TotalAmount, int UniqueCustomers)
{
    public string DisplayName => Platform.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "tiktok"    => "TikTok",
        "facebook"  => "Facebook",
        "youtube"   => "YouTube",
        _           => Platform
    };
}

/// <summary>
/// Bir yayında ürün alan müşteri (rapor + arama için). <see cref="Username"/> ham
/// platform kimliği (YouTube'da channel id); insan-okur gösterim için <see
/// cref="Display"/> kullan — DisplayName varsa onu, yoksa Username'e düşer.
/// </summary>
public sealed record TopCustomer(
    string Username, string Platform, int LabelCount, decimal TotalAmount, string? DisplayName = null)
{
    public string Display => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName!;
}

/// <summary>UI projection of a Label for the customer detail dialog.</summary>
public sealed record CustomerLabelRow(
    string Id,
    string SessionId,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    bool IsPrinted,
    long? CancelledAt = null,
    string? CancelReason = null)
{
    public bool IsCancelled => CancelledAt.HasValue;
}
