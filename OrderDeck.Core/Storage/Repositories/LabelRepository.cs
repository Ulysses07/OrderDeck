using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class LabelRepository
{
    private readonly IDbConnectionFactory _factory;
    public LabelRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <param name="write">Doluysa çağıranın paketinde (<c>LabelService.Add</c>: müşteri + etiket
    /// tek işlem, U12). Boşsa kendi bağlantısı — davranış değişmez.</param>
    public void Insert(Label l, DbWrite? write = null)
    {
        // SQLite stores BOOLs as INTEGER — Dapper handles bool→0/1 conversion,
        // but we cast explicitly so the parameter type is unambiguous on
        // callers that pass an anonymous-typed projection.
        //
        // StockSyncedAt = SyncedAt: göç 030'daki backfill'in aynısı. Yeni
        // yazılan etikette ikisi de NULL; yedekten geri yüklenen, sunucuya
        // çoktan gitmiş bir satırda ikisi de dolu olmalı — yoksa geri yükleme
        // bakiyeden bir kez daha düşerdi. Label kaydına ayrı bir alan eklemeye
        // gerek yok: damganın kaynağı zaten SyncedAt.
        //
        // U12: CustomerId yönlendirmeden YAZIMLA AYNI İFADEDE çözülür — bayat bir Customer
        // nesnesiyle gelen yazım (AddShippingFee, açık pencere) taşınmış Id'de FK hatası
        // vermez, asıl kayda iner.
        _factory.Execute(write,
            @"INSERT INTO Label
              (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt, PrintedAt,
               IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt,
               StockSyncedAt, ProductId, ProductVariantId)
              VALUES
              (@Id, @SessionId, " + CustomerIdSql.Resolve("@CustomerId") + @", @Platform, @Username, @DisplayName,
               @MessageText, @Code, @Price, @AddedAt, @PrintedAt,
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

    // N07: Delete(id) kasıtlı olarak YOK. Fiziksel DELETE, sunucuya bir kez
    // push edilmiş satırın mezar taşını da yok eder — sunucu kopyası ömür
    // boyu aktif kalır. Kaldırma ihtiyacı MarkCancelled'dan geçmeli.

    public Label? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
              FROM Label WHERE Id=@id",
            new { id });
        return row is null ? null : Map(row);
    }

    /// <summary>
    /// Bekleyen komut örtüsü (spec 2026-10-13 §5.5; <see cref="PendingSalesCommandRepository"/>): devredilmiş kendi
    /// yayınının kalıntı etiketi için iptal ya da ilk baskı komutu kuyrukta bekliyorsa etiket yerel okuyucularda
    /// uygulanmış sayılır — kuyrukta ve kalıntı teklifinde yeniden görünmez, ikinci kâğıt çıkmaz. Bağ tablosunun
    /// birincil anahtarı (LabelId, CommandId) ile tek arama; kuyruk boşken (mod Off, bugün her lisans) hiçbir satırı
    /// düşürmez.
    /// </summary>
    internal const string NotCoveredByPendingCommand =
        "NOT EXISTS (SELECT 1 FROM PendingSalesCommandLabel p WHERE p.LabelId = Label.Id)";

    /// <summary>Yayının kuyruğu: basılmamış, iptal edilmemiş ve bekleyen komut örtüsünde olmayan etiketler
    /// (<see cref="NotCoveredByPendingCommand"/>).</summary>
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
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NULL AND CancelledAt IS NULL
                AND " + NotCoveredByPendingCommand + @"
              ORDER BY AddedAt",
            new { sessionId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>R7-04 (PO-02): verilen oturum DIŞINDA kalan basılmamış,
    /// iptal edilmemiş etiketler. Kuyruk yalnız aktif oturumdan yüklendiği
    /// için önceki yayından basılmadan kalan etiketler ekranda görünmez —
    /// StartStream bunları operatöre sorar ve isterse yeni kuyruğa taşır
    /// (SessionId DEĞİŞMEZ: satış hâlâ eski yayının; taşınan yalnız
    /// görünürlük). Geçici yedekler dahil:
    /// <see cref="GetUnprintedBySession"/> ile aynı seçim kuralı.
    /// <para>Yalnız bu bilgisayarda açılmış oturumlar (<c>Origin = 'local'</c>, §5.5): başka
    /// bilgisayarın basılmamış kuyruğu akışla inse de burada teklif edilmez — aynı satış iki yazıcıdan
    /// çıkabilirdi. Oturum birincil anahtarıyla bulunur; Label.SessionId yabancı anahtar olduğu için
    /// oturumsuz etiket yoktur, süzgeç bugünkü (hepsi yerel) veride hiçbir satırı düşürmez.</para>
    /// <para>Bekleyen komut örtüsündeki etiket (çevrimdışı iptal ya da basılmış kâğıdı sunucuya henüz işlenmemiş
    /// kalıntı; <see cref="NotCoveredByPendingCommand"/>) teklif edilmez: yeniden açılışta da aynı kâğıt ikinci kez
    /// çıkmaz.</para></summary>
    public IReadOnlyList<Label> GetUnprintedOutsideSession(string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
              FROM Label
              WHERE SessionId<>@sessionId AND PrintedAt IS NULL AND CancelledAt IS NULL
                AND EXISTS (SELECT 1 FROM StreamSession s WHERE s.Id = Label.SessionId AND s.Origin = 'local')
                AND " + NotCoveredByPendingCommand + @"
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
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
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
            "UPDATE Label SET IsTentativeBackup = 0, SyncedAt=NULL, StockSyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids AND IsTentativeBackup = 1",
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
            "UPDATE Label SET CancelledAt=@cancelledAt, CancelReason=@reason, SyncedAt=NULL, StockSyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids",
            new { cancelledAt, reason, ids = ids.ToArray() });
    }

    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void Uncancel(IEnumerable<string> ids, DbWrite? write = null)
    {
        _factory.Execute(write,
            "UPDATE Label SET CancelledAt=NULL, CancelReason=NULL, SyncedAt=NULL, StockSyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids",
            new { ids = ids.ToArray() });
    }

    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void MarkPrinted(IEnumerable<string> ids, long printedAt, DbWrite? write = null)
    {
        // StockSyncedAt'e BİLEREK dokunulmuyor: yazdırmak satırı push kuyruğuna
        // geri alır ama stok açısından hiçbir şeyi değiştirmez — sunucunun
        // defterinde hareket zaten var. Silinirse aynı etiket ikinci kez düşülür.
        _factory.Execute(write,
            "UPDATE Label SET PrintedAt=@printedAt, SyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids",
            new { printedAt, ids = ids.ToArray() });
    }

    /// <summary>Göç 048: bir "Yazdır" basışını damgalar — <see cref="MarkPrinted"/>'ın aynısı, ek olarak
    /// her satıra basışın kimliği (<paramref name="batchId"/>) ve kâğıttaki konumu (1'den) yazılır.
    /// "Basılanlar" sekmesi bu ikisiyle gruplar ve sıralar.</summary>
    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void MarkPrintedBatch(
        IReadOnlyList<(string Id, int Seq)> items, long printedAt, string batchId, DbWrite? write = null)
    {
        if (items.Count == 0) return;
        // StockSyncedAt'e dokunulmaz — MarkPrinted ile aynı gerekçe.
        _factory.Execute(write,
            @"UPDATE Label SET PrintedAt=@printedAt, PrintBatchId=@batchId, PrintSeq=@seq,
                     SyncedAt=NULL, Revision = Revision + 1
              WHERE Id=@id",
            items.Select(i => new { printedAt, batchId, seq = i.Seq, id = i.Id }).ToArray());
    }

    /// <summary>Fiyat düzeltmesinin (LabelService.CorrectPrices) satır yazımı — KOŞULLU: yalnız satır
    /// hâlâ okunduğu hâldeyse (aynı eski fiyat, iptal edilmemiş, basılmışlık ve onay durumu aynı)
    /// yazar. 0 dönerse çağıran paketi geri sarar: müşteri toplamı ve kargo farkı okunan hâle göre
    /// hesaplandı, değişmiş bir satıra uygulanamaz.
    ///
    /// <para><see cref="UpdatePrice"/> gibi: SyncedAt=NULL (yeni fiyat mevcut senkronla sunucuya gider),
    /// Revision+1, StockSyncedAt'e DOKUNULMAZ (fiyat stok-ilgili değil). PriceCorrectedAt (yeniden
    /// baskıdaki "DÜZELTME" işareti) YALNIZ basılmış etikette damgalanır: işaret "yığında eski fiyatlı
    /// bir kâğıt var" demek. Kuyrukta düzeltilen etiketin ilk kâğıdı zaten yeni fiyatla çıkar — onu
    /// işaretlemek paketleyene ortada olmayan yanlış kâğıdı aratırdı.</para>
    ///
    /// <para>PaidPrice (göç 048): etiket ÖDENMİŞSE — bir kargo dosyasına bağlı (dekont kaydı bağlar) ya da
    /// <paramref name="paidFromBalance"/> (onu içeren teslim edilmiş istek tamamen bakiyeden düşülmüş) — ve ödenmişken
    /// ilk kez düzeltiliyorsa düzeltmeden ÖNCEKİ fiyatı, yani ödendiği fiyat yazılır; sonraki düzeltmeler dokunmaz.
    /// "Ödendi, fark" bununla hesaplanır (<see cref="GetPaidPrices"/>). SQLite atamanın sağ tarafını satırın ESKİ
    /// değerleriyle değerlendirir: <c>Price</c> burada eski fiyat.</para></summary>
    public int UpdatePriceForCorrection(
        string id, decimal expectedPrice, bool expectPrinted, bool expectTentative, bool paidFromBalance,
        decimal newPrice, long correctedAt, DbWrite write)
        => write.Connection.Execute(
            @"UPDATE Label SET Price=@newPrice,
                     PriceCorrectedAt = CASE WHEN PrintedAt IS NOT NULL THEN @correctedAt ELSE PriceCorrectedAt END,
                     PaidPrice = CASE WHEN PaidPrice IS NULL AND (ShipmentId IS NOT NULL OR @paidFromBalance = 1)
                                      THEN Price ELSE PaidPrice END,
                     SyncedAt=NULL, Revision = Revision + 1
              WHERE Id=@id AND abs(Price - @expectedPrice) < 0.00001 AND CancelledAt IS NULL
                AND (PrintedAt IS NOT NULL) = @expectPrinted
                AND IsTentativeBackup = @expectTentative",
            new
            {
                id, expectedPrice, newPrice, correctedAt,
                expectPrinted = expectPrinted ? 1 : 0,
                expectTentative = expectTentative ? 1 : 0,
                paidFromBalance = paidFromBalance ? 1 : 0,
            }, write.Transaction);

    /// <summary>Verilen etiketlerden fiyatı "Fiyatı düzelt" ile değiştirilmiş olanlar (göç 048) —
    /// yeniden baskının "DÜZELTME" işareti.</summary>
    public IReadOnlySet<string> GetPriceCorrectedIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return new HashSet<string>();
        using var conn = _factory.Open();
        return conn.Query<string>(
                "SELECT Id FROM Label WHERE Id IN @ids AND PriceCorrectedAt IS NOT NULL",
                new { ids = ids.ToArray() })
            .ToHashSet(System.StringComparer.Ordinal);
    }

    /// <summary>Verilen etiketlerden ödenmişken düzeltilmiş olanların ÖDENDİĞİ fiyatı (göç 048, PaidPrice — ilk
    /// düzeltme yazar, <see cref="UpdatePriceForCorrection"/>). Listede olmayan etiket ödenmişken hiç düzeltilmedi:
    /// ödendiği fiyat düzeltmeden önceki fiyatıdır.</summary>
    public IReadOnlyDictionary<string, decimal> GetPaidPrices(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return new Dictionary<string, decimal>(System.StringComparer.Ordinal);
        using var conn = _factory.Open();
        return conn.Query<(string Id, decimal PaidPrice)>(
                "SELECT Id, PaidPrice FROM Label WHERE Id IN @ids AND PaidPrice IS NOT NULL",
                new { ids = ids.ToArray() })
            .ToDictionary(r => r.Id, r => r.PaidPrice, System.StringComparer.Ordinal);
    }

    /// <summary>"Basılanlar" sekmesi (K1): yayının basılmış, iptal edilmemiş etiketleri, bağlı kargo
    /// dosyasının durumuyla. Gruplama ve sıralama çağıranda (<see cref="PrintedLabelRow.BatchKey"/>);
    /// burada yalnız en yeni basış önce gelir. Geçici yedekler ve kargo ücreti satırları dahildir —
    /// kâğıtları da yığında.</summary>
    public IReadOnlyList<PrintedLabelRow> GetPrintedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<PrintedRow>(
            @"SELECT l.Id, l.SessionId, l.CustomerId, l.Platform, l.Username, l.DisplayName, l.MessageText, l.Code,
                     l.Price, l.AddedAt, l.PrintedAt, l.CancelledAt, l.CancelReason, l.IsBackupPromoted, l.ParentLabelId,
                     l.IsTentativeBackup, l.IsShippingFee, l.ShipmentId, l.SyncedAt, l.ProductId, l.ProductVariantId,
                     l.Revision, l.PrintBatchId, l.PrintSeq, l.PriceCorrectedAt, s.Status AS ShipmentStatus
              FROM Label l
              LEFT JOIN Shipment s ON s.Id = l.ShipmentId
              WHERE l.SessionId=@sessionId AND l.PrintedAt IS NOT NULL AND l.CancelledAt IS NULL
              ORDER BY l.PrintedAt DESC, l.PrintBatchId, l.PrintSeq, l.AddedAt, l.Id",
            new { sessionId }).ToList();
        return rows.Select(r => new PrintedLabelRow(
                Map(r), r.PrintBatchId, r.PrintSeq is long seq ? (int)seq : null, r.PriceCorrectedAt,
                r.ShipmentStatus is { } st && System.Enum.TryParse<ShipmentStatus>(st, out var parsed)
                    ? parsed
                    : null))
            .ToList();
    }

    /// <summary>Bir yayında bir kodun iptal edilmemiş etiketleri (kargo ücreti hariç; kuyruk, basılmış ve
    /// yedekler dahil), yazılış sırasıyla. "Bir yayında bir kodun TEK fiyatı" kuralının kaynağı (sahip kararı
    /// F1–F3): FİYAT kutusu kodun fiyatını buradan okur, kod düzeyinde düzeltme bu etiketleri değiştirir. Kod
    /// eşleşmesi <see cref="SameCode"/> — "BU ÜRÜNDEN" sayacıyla (<see cref="CountSessionLabelsByCode"/>) aynı.</summary>
    public IReadOnlyList<Label> GetActiveBySessionAndCode(string sessionId, string code)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
              FROM Label
              WHERE SessionId=@sessionId AND Code IS NOT NULL AND length(Code) = length(@code)
                AND CancelledAt IS NULL AND IsShippingFee = 0
              ORDER BY AddedAt, rowid",
            new { sessionId, code }).ToList();
        return rows.Where(r => SameCode(r.Code, code)).Select(Map).ToList();
    }

    /// <summary>
    /// İki etiket kodu aynı ürün mü: harf duyarsız, Türkçe harfler dahil (<see cref="StringComparison.OrdinalIgnoreCase"/>
    /// — kod kutusunun etkin kod karşılaştırması ve düzeltmenin kod gruplaması da bu). SQLite'ın NOCASE harmanı
    /// yalnız ASCII katlar: "çanta1" ile "Çanta1"i kabuk aynı kod sayarken fiyat kaynağı ayrı sayıyordu — şerit
    /// açılmadan aynı ürüne iki fiyat yazılıyor, "Fiyatı düzelt" öbür yazımı atlıyordu. Bu yüzden eşleşme
    /// SQL'de değil burada; SQL adayları uzunlukla süzer (harf katlama uzunluğu değiştirmez).
    /// </summary>
    public static bool SameCode(string? a, string b)
        => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="GetPrintedBySession"/>'ın satır sayısı — sekme kapalıyken başlıktaki sayı (liste
    /// kurulmaz).</summary>
    public int CountPrintedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Label WHERE SessionId=@sessionId AND PrintedAt IS NOT NULL AND CancelledAt IS NULL",
            new { sessionId });
    }

    /// <summary>Verilen etiketler, verilen sırayla (eksik olanlar atlanır).</summary>
    /// <param name="write">Doluysa çağıranın paketinde okur (U17): <c>LabelService.Cancel</c>/<c>Uncancel</c>
    /// dosya payını yazımla aynı işlemde okunan hâlden hesaplar. Boşsa kendi bağlantısı.</param>
    public IReadOnlyList<Label> GetByIds(IReadOnlyList<string> ids, DbWrite? write = null)
    {
        if (ids.Count == 0) return System.Array.Empty<Label>();
        var byId = _factory.Query<Row>(write,
                @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                         Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
                  FROM Label WHERE Id IN @ids",
                new { ids = ids.Distinct().ToArray() })
            .ToDictionary(r => r.Id, Map, System.StringComparer.Ordinal);
        return ids.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    /// <summary>
    /// Baskıdan hemen önceki yeniden okuma (spec 2026-10-13 §5.6-1): verilen etiketlerden kâğıda gidebilecekler,
    /// verilen sırayla — basılmamış, iptal edilmemiş ve bekleyen komut örtüsünde olmayan
    /// (<see cref="NotCoveredByPendingCommand"/>). Kuyruğun seçim kuralı (<see cref="GetUnprintedBySession"/>) ile aynı.
    /// </summary>
    public IReadOnlyList<Label> GetPrintableByIds(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return System.Array.Empty<Label>();
        using var conn = _factory.Open();
        var byId = conn.Query<Row>(
                @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                         Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
                  FROM Label
                  WHERE Id IN @ids AND PrintedAt IS NULL AND CancelledAt IS NULL
                    AND " + NotCoveredByPendingCommand,
                new { ids = ids.Distinct().ToArray() })
            .ToDictionary(r => r.Id, Map, System.StringComparer.Ordinal);
        return ids.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    /// <summary>
    /// Verilen etiketlerin sunucudaki sürümü (<c>ServerVersion</c>, göç 049): satış komutunun beklenen sürümü (spec
    /// 2026-10-13 §4.6 adım 4; <c>expected</c>). Yerelde olmayan etiket sözlükte yoktur; sürümü hiç bilinmeyen etiket
    /// (onaylanmamış ya da eski sunucunun sürümsüz onayı) null değerle döner.
    /// </summary>
    public IReadOnlyDictionary<string, int?> GetServerVersions(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return new Dictionary<string, int?>(System.StringComparer.Ordinal);
        using var conn = _factory.Open();
        return conn.Query<(string Id, long? ServerVersion)>(
                "SELECT Id, ServerVersion FROM Label WHERE Id IN @ids",
                new { ids = ids.Distinct().ToArray() })
            .ToDictionary(r => r.Id, r => r.ServerVersion is { } v ? (int?)checked((int)v) : null,
                System.StringComparer.Ordinal);
    }

    /// <summary>Updates the Price of a single label. Used by
    /// <c>LabelService.ConfirmBackup</c> when the operator negotiates a
    /// different number while promoting a tentative backup.</summary>
    /// <param name="write"><inheritdoc cref="MarkCancelled" path="/param[@name='write']"/></param>
    public void UpdatePrice(string id, decimal price, DbWrite? write = null)
    {
        // MarkPrinted'daki gerekçenin aynısı: fiyat stok-ilgili değil.
        _factory.Execute(write,
            "UPDATE Label SET Price=@price, SyncedAt=NULL, Revision = Revision + 1 WHERE Id=@id", new { id, price });
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
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
              FROM Label
              WHERE SyncedAt IS NULL
              ORDER BY AddedAt
              LIMIT @limit",
            new { limit }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Push başarılı sonrası SyncedAt'i set eder. StockSyncedAt de
    /// burada doluyor: push başarılıysa sunucu satırın stok-ilgili hâlini
    /// görmüş, defterini ona göre kurmuş demektir.
    ///
    /// F05: compare-and-set — <paramref name="revision"/> push'a giden satırdan
    /// okunan değer. Push uçuştayken bir mutasyon Revision'ı artırdıysa bu
    /// UPDATE 0 satır etkiler; satır bekleyen kalır ve bir sonraki tick'te
    /// güncel hâliyle tekrar gider. Aksi hâlde onay, uçuş sırasındaki
    /// değişikliği (ör. iptal) sessizce ezerdi.</summary>
    public void MarkSynced(string id, long syncedAt, long revision)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Label SET SyncedAt=@syncedAt, StockSyncedAt=@syncedAt WHERE Id=@id AND Revision=@revision",
            new { id, syncedAt, revision });
    }

    /// <summary>Gönderim kuyruğu boş mu — kısmi <c>IX_Label_SyncedAt</c> dizininden tek satır okur. Gönderim
    /// turu (yayında ≈5 sn'de bir, Y9) HTTP'den, lisans çözümü dahil, önce buna bakar.</summary>
    public bool HasUnsynced()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM Label WHERE SyncedAt IS NULL)") != 0;
    }

    /// <summary>Bir yayının gönderilmemiş etiket sayısı (spec 2026-10-13 §5.2): kapanışın <c>Release</c>'i yalnız
    /// bu 0 iken gönderilir, değilse sayı <c>UnsentLabelCount</c> olarak gider. Devir kapısı altında okunur.</summary>
    public int CountUnsyncedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Label WHERE SessionId = @sessionId AND SyncedAt IS NULL", new { sessionId });
    }

    /// <summary>
    /// v2 gönderiminin kuyruğu (spec 2026-10-13 §4.2, §5.2): <see cref="GetUnsynced"/>'in sırası ve süzgeci, ek olarak
    /// 048 alanları, yedek ebeveyni, kargo bağı ve <c>ServerVersion</c> (<c>baseVersion</c>). <see cref="Label"/>
    /// yerine ayrı satır tipi: bu alanlar yalnız gönderimin girdisi.
    /// </summary>
    public IReadOnlyList<LabelPushRow> GetUnsyncedForPush(int limit)
    {
        using var conn = _factory.Open();
        return conn.Query<PushRow>(
                @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                         Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsShippingFee, IsBackupPromoted,
                         IsTentativeBackup, ProductId, ProductVariantId, ParentLabelId, ShipmentId,
                         PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice, ServerVersion, Revision
                  FROM Label
                  WHERE SyncedAt IS NULL
                  ORDER BY AddedAt
                  LIMIT @limit",
                new { limit })
            .Select(r => new LabelPushRow(
                r.Id, r.SessionId, r.CustomerId, r.Platform, r.Username, r.DisplayName, r.MessageText, r.Code,
                r.Price, r.AddedAt, r.PrintedAt, r.CancelledAt, r.CancelReason,
                IsShippingFee: r.IsShippingFee != 0,
                IsBackupPromoted: r.IsBackupPromoted != 0,
                IsTentativeBackup: r.IsTentativeBackup != 0,
                r.ProductId, r.ProductVariantId, r.ParentLabelId, r.ShipmentId,
                r.PrintBatchId, r.PrintSeq, r.PriceCorrectedAt, r.PaidPrice, r.ServerVersion, r.Revision))
            .ToList();
    }

    /// <summary>
    /// Gönderim onayı, tek kısa yazma işleminde (spec 2026-10-13 §5.2): <c>SyncedAt</c> ve <c>StockSyncedAt</c>
    /// <see cref="MarkSynced"/>'in F05 karşılaştır-ve-yaz kuralıyla — <c>WHERE Revision = gönderilen</c>. Uçuş
    /// sırasında değişen satır onaylanmaz; bekleyen kalır ve sonraki tur güncel hâliyle gönderir.
    ///
    /// <para><c>ServerVersion</c> yalnız sürümü bilinen onayda yazılır (<see cref="LabelPushAck.ServerVersion"/>
    /// dolu: v2 <c>applied</c>); eski sunucunun onayı mevcut değeri korur. Sürüm Revision koşuluna BAĞLI DEĞİL:
    /// <c>applied</c>'ın sürümü bu istemcinin kendi yazımının sonucudur, satır uçuşta yeniden düzenlense de sunucu o
    /// sürümdedir. Yazılmasaydı sonraki gönderim eski <c>baseVersion</c> ile gider, damgalı birimde (K2/K4) sunucu
    /// onu bayat sayar ve operatörün yeni düzenlemesi <c>current</c> ile sessizce geri alınırdı. Araya başka bir
    /// yazım (komut) girdiyse sunucunun sürümü daha ileridedir ve sonraki gönderim yine doğru biçimde yok sayılır.
    /// Sürüm geri gitmez (daha yeni bir <c>current</c> ya da geri doldurma sürümü ezilmez).</para>
    ///
    /// <para><c>SyncedAt</c>, <c>StockSyncedAt</c> ve <c>ServerVersion</c> 049 tetikleyicilerinin hiçbir listesinde
    /// değil: yazım kuyruğa almaz, <c>Revision</c> değişmez (DM8) ve her kipte geçer. Mod On'da sunucunun farklı
    /// kargo bağı da aynı işlemde yazılır (<see cref="MarkSyncedWithServerLinks"/>); mod Off'ta bağın yazıcısı bu
    /// bilgisayardır, dokunulmaz.</para>
    /// </summary>
    /// <returns>Onaylanan satır sayısı.</returns>
    public int MarkSyncedBatch(IReadOnlyList<LabelPushAck> acks, long syncedAt)
    {
        if (acks.Count == 0) return 0;
        if (acks.Any(a => a.ServerShipmentKnown) && IsSalesModeOn())
            return MarkSyncedWithServerLinks(acks, syncedAt);

        using var write = DbWrite.Begin(_factory);
        var marked = WriteAcks(write.Connection, write.Transaction, acks, syncedAt);
        write.Commit();
        return marked;
    }

    /// <summary>Onay (F05) ve sürüm, çağıranın işleminde (<see cref="MarkSyncedBatch"/>).</summary>
    private static int WriteAcks(IDbConnection conn, IDbTransaction tx, IReadOnlyList<LabelPushAck> acks, long syncedAt)
    {
        var marked = conn.Execute(MarkSyncedSql, AckParams(acks, syncedAt), tx);
        var versioned = acks.Where(a => a.ServerVersion is not null)
            .Select(a => (object)new { id = a.Id, serverVersion = a.ServerVersion })
            .ToArray();
        if (versioned.Length > 0) conn.Execute(AppliedVersionSql, versioned, tx);
        return marked;
    }

    private const string MarkSyncedSql =
        "UPDATE Label SET SyncedAt=@syncedAt, StockSyncedAt=@syncedAt WHERE Id=@id AND Revision=@revision";

    private const string AppliedVersionSql =
        @"UPDATE Label SET ServerVersion = @serverVersion
          WHERE Id = @id AND (ServerVersion IS NULL OR ServerVersion < @serverVersion)";

    private static object[] AckParams(IReadOnlyList<LabelPushAck> acks, long syncedAt) =>
        acks.Select(a => (object)new { id = a.Id, revision = a.Revision, syncedAt }).ToArray();

    private bool IsSalesModeOn()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long?>("SELECT Mode FROM SalesSyncState WHERE Id = 1") == 1;
    }

    /// <summary>
    /// Mod On'da onay (spec 2026-10-13 §5.2, K4): kargo bağı sunucunundur, push onu almaz. <c>applied</c> satırda
    /// sunucudaki bağ yereldekinden farklıysa onayla aynı işlemde, guard altında yazılır (<see cref="SyncApplyScope"/>:
    /// <c>TR_Label_ShipmentLink</c> ABORT etmez, <c>TR_Label_Enqueue</c> kuyruğa almaz, Revision değişmez). F05 aynı:
    /// uçuşta değişen satıra dokunulmaz. Mod işlem içinde yeniden denetlenir. Dosya tutarlarına dokunulmaz: mod On'da
    /// yerel dosya sunucunun aynasıdır (K7).
    /// </summary>
    private int MarkSyncedWithServerLinks(IReadOnlyList<LabelPushAck> acks, long syncedAt)
    {
        using var scope = SyncApplyScope.Begin(_factory);
        scope.Connection.Execute(
            @"UPDATE Label SET ShipmentId = @shipmentId
              WHERE Id = @id AND Revision = @revision AND ShipmentId IS NOT @shipmentId
                AND (SELECT Mode FROM SalesSyncState WHERE Id = 1) = 1",
            acks.Where(a => a.ServerShipmentKnown)
                .Select(a => new { id = a.Id, revision = a.Revision, shipmentId = a.ServerShipmentId })
                .ToArray(),
            scope.Transaction);
        var marked = WriteAcks(scope.Connection, scope.Transaction, acks, syncedAt);
        scope.Commit();
        return marked;
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
    ///  * Kod eşleşmesi harf duyarsız (<see cref="SameCode"/> — Türkçe harfler
    ///    dahil; FİYAT kutusunun kaynağıyla aynı kural); kod kutusu büyük harfe
    ///    zorlamıyor, eski satırlar karışık olabilir.
    /// İptal ve onaylanmamış yedek dışlaması ise AYNI — iki sayaç birbirini
    /// tutmalı.
    /// </summary>
    public int CountSessionLabelsByCode(string sessionId, string code)
    {
        using var conn = _factory.Open();
        return conn.Query<string>(
            """
            SELECT Code
            FROM Label
            WHERE SessionId = @sessionId
              AND Code IS NOT NULL
              AND length(Code) = length(@code)
              AND CancelledAt IS NULL
              AND IsTentativeBackup = 0
            """,
            new { sessionId, code }).Count(c => SameCode(c, code));
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

    /// <summary>Yayında alan müşteriler, satır (Customer) başına, ciroya göre azalan.
    /// <see cref="TopCustomer.PersonTotalAmount"/>: satırın kişisi (<see cref="CustomerGroup"/>) bu
    /// raporda birden çok satırla görünüyorsa kişinin yayın toplamı — ödeme isteği bu tutarı ister
    /// (sahip kararı 3). Toplam bütün satırlar üzerinden, <paramref name="limit"/>'ten ÖNCE alınır;
    /// tanım <see cref="GetPrintedTotal"/>'ın yayın süzgeçli hâliyle aynıdır (aynı etiket süzgeci, aynı kişi kuralı).</summary>
    public IReadOnlyList<TopCustomer> GetTopCustomersBySession(string sessionId, int limit = 10)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<TopCustomerRow>(
            @"SELECT l.CustomerId,
                     c.GroupId,
                     c.Username,
                     c.DisplayName,
                     l.Platform,
                     COUNT(*)   AS LabelCount,
                     SUM(l.Price) AS TotalAmount
              FROM Label l
              JOIN Customer c ON c.Id = l.CustomerId
              WHERE l.SessionId=@sessionId AND l.PrintedAt IS NOT NULL
                AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0
              GROUP BY l.CustomerId
              ORDER BY SUM(l.Price) DESC",
            new { sessionId }).ToList();
        var persons = rows
            .GroupBy(r => CustomerGroup.PersonKey(r.CustomerId, r.GroupId))
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.TotalAmount));
        return rows
            .Take(limit)
            .Select(r => new TopCustomer(r.Username, r.Platform, r.LabelCount, r.TotalAmount, r.DisplayName, r.CustomerId,
                persons.TryGetValue(CustomerGroup.PersonKey(r.CustomerId, r.GroupId), out var personTotal)
                    ? personTotal
                    : null))
            .ToList();
    }

    /// <summary>
    /// Ödeme isteğinin ürün toplamı (spec 2026-10-13 §6.1 "para yolları sayacı okumaz", §6.4 istemci adım 2):
    /// verilen müşterilerin (kişinin bütün üyeleri) katkısı olan etiketlerinin fiyat toplamı ve Id'leri.
    /// <c>Customer.TotalAmount</c> okunmaz: o ekranın önbelleği, bu para.
    ///
    /// <para><b>Süzgeç</b> <see cref="OrderDeck.Shared.Sales.LabelRules.Contribution"/>'ın SQL karşılığıdır ve müşteri
    /// toplamlarını türeten metnin aynısıdır (<see cref="CustomerAggregateSql.ContributesCondition"/>): basılmış ∧
    /// iptal edilmemiş ∧ geçici yedek değil. Kargo ücreti satırı dahildir; <see cref="GetTopCustomersBySession"/> ve
    /// sunucu toplamı da onu sayar.</para>
    ///
    /// <para><paramref name="sessionId"/> verilirse yalnız o yayının etiketleri (<c>session:{id}</c> kapsamı);
    /// verilmezse bütün yayınlar (<c>cumulative</c>). Ödeme akışı, liste/rapor anlık görüntüsünün Id başına tuttuğu
    /// tutarı kiraladığı GÜNCEL kişi için bununla yeniden okur (U12, Bölüm C): görüntü bir yerel taşımadan önce
    /// alındıysa kopyanın satırı yalnız kendi yazımının payını taşır.</para>
    ///
    /// <para><see cref="PrintedTotal.LabelIds"/> toplama giren etiketlerdir — <c>begin</c>'in <c>orderIds</c>'i; sunucu
    /// kümeyi kendi toplamıyla karşılaştırır (§6.4 f).</para>
    /// </summary>
    /// <param name="memberIds">Kişinin üyeleri, ham Id (GUID'siz olanlar dahil).</param>
    /// <param name="sessionId">Yayın kapsamının yayını; kümülatif kapsamda boş.</param>
    public PrintedTotal GetPrintedTotal(IReadOnlyCollection<string> memberIds, string? sessionId = null)
    {
        if (memberIds.Count == 0) return new PrintedTotal(0m, []);
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, decimal Price)>(
            $@"SELECT l.Id, l.Price
               FROM Label l
               WHERE l.CustomerId IN @memberIds
                 AND (@sessionId IS NULL OR l.SessionId = @sessionId)
                 AND {CustomerAggregateSql.ContributesCondition}
               ORDER BY l.Id",
            new { memberIds, sessionId }).ToList();
        return new PrintedTotal(rows.Sum(r => r.Price), rows.Select(r => r.Id).ToList());
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
        // U12: ödeme onayı akışı müşteri Id'sini ekranda açık kalmış bir karttan alabilir.
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId, Revision
              FROM Label
              WHERE CustomerId = " + CustomerIdSql.Resolve("@customerId") + @"
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
            ProductVariantId: r.ProductVariantId,
            Revision: r.Revision);

    private sealed class PushRow
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
        public int IsShippingFee { get; init; }
        public int IsBackupPromoted { get; init; }
        public int IsTentativeBackup { get; init; }
        public string? ProductId { get; init; }
        public string? ProductVariantId { get; init; }
        public string? ParentLabelId { get; init; }
        public string? ShipmentId { get; init; }
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long? PriceCorrectedAt { get; init; }
        public decimal? PaidPrice { get; init; }
        public long? ServerVersion { get; init; }
        public long Revision { get; init; }
    }

    private sealed class PrintedRow : Row
    {
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long? PriceCorrectedAt { get; init; }
        public string? ShipmentStatus { get; init; }
    }

    private class Row
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
        public long Revision { get; init; }
    }

    private sealed class TotalsRow
    {
        public int PrintedCount { get; init; }
        public decimal TotalAmount { get; init; }
        public int UniqueCustomers { get; init; }
    }

    private sealed class TopCustomerRow
    {
        public string CustomerId { get; init; } = "";
        public string? GroupId { get; init; }
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

/// <summary>Kişinin basılmış toplamı (<see cref="LabelRepository.GetPrintedTotal"/>): katkısı olan etiketlerin fiyat
/// toplamı ve Id'leri (Id sırasıyla).</summary>
public sealed record PrintedTotal(decimal Total, IReadOnlyList<string> LabelIds);

/// <summary>v2 etiket gönderiminin bir satırı (<see cref="LabelRepository.GetUnsyncedForPush"/>): yerel biçimde
/// (TEXT kimlikler, unix saniye); tel biçimine çeviri gönderim servisinde.</summary>
/// <param name="ServerVersion">Son <c>applied</c> onayında sunucunun bildirdiği sürüm; hiç onaylanmadıysa boş.</param>
/// <param name="Revision">F05: onay yalnız bu değer değişmemişse yazar.</param>
public sealed record LabelPushRow(
    string Id,
    string SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string? DisplayName,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    long? PrintedAt,
    long? CancelledAt,
    string? CancelReason,
    bool IsShippingFee,
    bool IsBackupPromoted,
    bool IsTentativeBackup,
    string? ProductId,
    string? ProductVariantId,
    string? ParentLabelId,
    string? ShipmentId,
    string? PrintBatchId,
    long? PrintSeq,
    long? PriceCorrectedAt,
    decimal? PaidPrice,
    long? ServerVersion,
    long Revision);

/// <summary>Bir gönderim onayı (<see cref="LabelRepository.MarkSyncedBatch"/>).</summary>
/// <param name="Revision">Gönderilen satırın <c>Revision</c>'ı (F05).</param>
/// <param name="ServerVersion">v2 <c>applied</c> yanıtının sürümü; eski sunucuda ve sürümsüz onayda boş — o zaman
/// mevcut değer korunur.</param>
/// <param name="ServerShipmentKnown">Yanıt sunucudaki kargo bağını taşıyor (v2 <c>applied</c>).</param>
/// <param name="ServerShipmentId">Sunucudaki kargo bağı ("N" biçiminde; bağ yoksa boş). Yalnız mod On'da yazılır
/// (K4, §5.2).</param>
public readonly record struct LabelPushAck(
    string Id, long Revision, int? ServerVersion, bool ServerShipmentKnown = false, string? ServerShipmentId = null);

/// <summary>"Basılanlar" sekmesinin satırı (göç 048). <see cref="BatchKey"/> grubun anahtarıdır:
/// basışın kimliği, 048 öncesi basılmış etiketlerde aynı PrintedAt'i taşıyanlar tek grup.
/// <see cref="ShipmentStatus"/> bağlı kargo dosyasının durumu (bağlı değilse null).</summary>
public sealed record PrintedLabelRow(
    Label Label, string? PrintBatchId, int? PrintSeq, long? PriceCorrectedAt, ShipmentStatus? ShipmentStatus)
{
    public string BatchKey => PrintBatchId ?? "t:" + Label.PrintedAt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

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
/// <see cref="CustomerId"/>: satırın toplandığı yerel müşteri Id'si (U12, Bölüm C — rapor açıkken
/// yerel taşıma o Id'yi silmişse ödeme isteği satırı bayat sayar).
/// <see cref="PersonTotalAmount"/>: aynı kişinin bu raporda başka satırı da varsa kişinin yayın
/// toplamı (yoksa null). Ödeme isteği satırın değil kişinin toplamını ister (sahip kararı 3); satır
/// 100 gösterip mesaj 300 yazdığında operatör farkı burada görür.
/// </summary>
public sealed record TopCustomer(
    string Username, string Platform, int LabelCount, decimal TotalAmount, string? DisplayName = null,
    string? CustomerId = null, decimal? PersonTotalAmount = null)
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
