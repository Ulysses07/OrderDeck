using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.Shoppers;

/// <summary>Purge sonucunun özeti; admin paneline ve audit log'a gider.</summary>
public sealed record ShopperPurgeResult(
    int PaymentsScrubbed,
    int PdfsDeleted,
    int ProjectionsScrubbed,
    int DependentRowsDeleted,
    int PdfsPending,
    int BankTransactionsScrubbed = 0)
{
    /// <summary>
    /// Kovadaki her nesne gerçekten silindi mi. <c>false</c> ise silme
    /// KAPANMAMIŞ demektir; yönetim ekranı bunu toplam başarı gibi
    /// göstermemeli.
    /// </summary>
    public bool FullyCompleted => PdfsPending == 0;

    public string ToNote()
    {
        var note =
            $"{PaymentsScrubbed} ödeme temizlendi, {PdfsDeleted} dekont silindi, "
            + $"{ProjectionsScrubbed} yayıncı kopyası temizlendi, "
            + $"{DependentRowsDeleted} bağlı satır silindi.";
        if (BankTransactionsScrubbed > 0)
            note += $" {BankTransactionsScrubbed} banka hareketinin açıklaması, gönderen adı ve ham kaydı silindi.";
        if (PdfsPending > 0)
            note += $" {PdfsPending} dekont depodan silinemedi, "
                + "temizlik kuyruğunda yeniden denenecek.";
        return note;
    }
}

/// <summary>
/// Shopper'ın kişisel verisini kalıcı olarak siler. Admin panelinden ELLE
/// tetiklenir — zamanlanmış iş yok, silme kararı her zaman bir insanın.
///
/// İzlenen kural, <c>AdminCustomersController.Purge</c> ile aynı: bağlı
/// satırlar silinir, ana satır anonimleştirilerek BIRAKILIR. Shopper satırının
/// silinmemesinin sebebi <c>Payment.ShopperId</c> — ödeme kayıtları mali kayıt
/// olarak duruyor ve hangi kişiye ait olduğunu gösteren bağ kopmamalı; bağ
/// kopsa ödeme itirazında kaydı kimseye bağlayamayız.
///
/// Ödemelerde SİLİNMEYENLER ve gerekçesi: <c>PdfHash</c> ve
/// <c>MetadataHash</c>. Bunlar mükerrer dekont dedektörünün tek dayanağı
/// (bkz. <c>ShopperPaymentSubmissionService</c> adım 5-7); silinirlerse aynı
/// dekont yeniden yüklenip kabul edilebilir hale gelir — üstelik başka bir
/// yayıncıya karşı da. Tek yönlü özet oldukları için kimliğe geri
/// döndürülemezler; tutmanın meşru menfaati sahtekârlık önleme.
/// </summary>
public sealed class ShopperPurgeService
{
    private readonly LicenseDbContext _db;
    private readonly IShopperPaymentStorage _storage;
    private readonly SalesSyncOptions _salesSync;
    private readonly ILogger<ShopperPurgeService> _log;

    public ShopperPurgeService(
        LicenseDbContext db,
        IShopperPaymentStorage storage,
        IOptions<SalesSyncOptions> salesSync,
        ILogger<ShopperPurgeService> log)
    {
        _db = db;
        _storage = storage;
        _salesSync = salesSync.Value;
        _log = log;
    }

    /// <summary>İmha kaydının ölçütü (<see cref="DataDestructionRecord.Criterion"/>).</summary>
    public const string PurgeCriterion = "silme talebi";

    /// <summary>Depodan silinen dekont nesnesinin imha kaydı hedefi; geciken silme (<see cref="OrphanedMediaCleanupJob"/>) da
    /// aynısını yazar.</summary>
    public const string MediaTarget = "ShopperPaymentMedia";

    /// <summary>Ödeme denetimindeki istemci parmak izinin imha kaydı hedefi. 90 günlük güvenlik saklama işi
    /// (<c>SecurityDataRetentionJob</c>) da bunu yazmalı: aynı sütunun imhası tek hedef adıyla gruplansın (kayıt kuralı: hedef =
    /// çoğul DbSet adı + sütunlar).</summary>
    public const string SubmissionAuditFingerprintTarget = "PaymentSubmissionAudits.IpAddress+UserAgent";

    /// <summary>Ödeme denetimindeki dekont tam metninin (<see cref="PaymentSubmissionAudit.ParserRawText"/>) imha kaydı hedefi;
    /// güvenlik saklama işi (<c>SecurityDataRetentionJob</c>, kendi süresiyle) de bunu yazar (KVKK K22).</summary>
    public const string SubmissionAuditRawTextTarget = "PaymentSubmissionAudits.ParserRawText";

    // Banka hedefleri banka veri talebi aracıyla ortak (BankSubjectScrub): aynı sütunun imhası tek hedef adıyla.
    private const string IbanMemoryTarget = BankSubjectScrub.IbanMemoryTarget;
    private const string TransactionTextTarget = BankSubjectScrub.TransactionTextTarget;
    private const string PurgedName = "[Silindi]";

    /// <summary>Yapanı ve talebi verilmeyen silme: imha kaydına <see cref="DataDestruction.AdminActor"/> yazılır (silme her zaman
    /// bir yöneticinin kararıdır; kim olduğu talebin <c>HandledBy</c>'ında ve denetim satırında).</summary>
    public Task<ShopperPurgeResult?> PurgeAsync(Guid shopperId, CancellationToken ct)
        => PurgeAsync(shopperId, DataDestruction.AdminActor, null, ct);

    /// <param name="actor">İmha kaydının yapanı: silmeyi işleyen yöneticinin takma kimliği
    /// (<see cref="DataDestruction.ActorOf"/>), kullanıcı adı değil (KVKK Ö-14).</param>
    /// <param name="requestId">Silmeyi doğuran <see cref="ShopperDeletionRequest"/>'in kimliği; imha kaydına yazılır (KVKK Ö-13:
    /// süresiz kayıt başvuruya birleştirme olmadan bağlanır).</param>
    public async Task<ShopperPurgeResult?> PurgeAsync(Guid shopperId, string actor, Guid? requestId, CancellationToken ct)
    {
        var shopper = await _db.Shoppers.FirstOrDefaultAsync(s => s.Id == shopperId, ct);
        if (shopper is null) return null;

        var now = DateTimeOffset.UtcNow;
        // 1. R2'deki dekont PDF'leri. Satırdaki anahtarı boşaltmadan ÖNCE
        //    silinmeli; sıra ters olsaydı nesneye giden tek referansı
        //    kaybeder, kovada yetim bırakırdık.
        var payments = await _db.Payments
            .Where(p => p.ShopperId == shopperId)
            .ToListAsync(ct);
        var paymentLicenses = payments.ToDictionary(p => p.Id, p => p.LicenseId);

        // R4-05: önceki purge'de silinemeyip kuyruğa alınmış anahtarlar da bu
        // tura dahil. Eskiden niyet yalnız log'a yazılıyordu; ödemedeki anahtar
        // koşulsuz boşaltıldığı için purge tekrarlansa bile silinecek bir şey
        // kalmıyordu — nesne kovada süresiz duruyordu.
        var queued = await _db.OrphanedMediaObjects
            .Where(o => o.ShopperId == shopperId && o.DeletedAt == null)
            .ToListAsync(ct);

        var targets = new Dictionary<string, (Guid? PaymentId, OrphanedMediaObject? Row)>(
            StringComparer.Ordinal);
        foreach (var p in payments.Where(p => p.MediaObjectKey is not null))
            targets[p.MediaObjectKey!] = (p.Id, null);
        foreach (var o in queued)
            targets[o.ObjectKey] = (
                targets.TryGetValue(o.ObjectKey, out var t) ? t.PaymentId ?? o.PaymentId : o.PaymentId,
                o);

        var pdfsDeleted = 0;
        // Silinen her nesnenin lisansı (ödemesinden; ödemesi bilinmeyen kuyruk satırında null): imha kaydı lisans başına.
        var deletedMediaLicenses = new List<Guid?>();
        var pendingRows = new List<OrphanedMediaObject>();
        foreach (var (key, target) in targets)
        {
            var row = target.Row;
            try
            {
                await _storage.DeleteAsync(key, ct);
                pdfsDeleted++;
                deletedMediaLicenses.Add(target.PaymentId is { } paymentId
                    && paymentLicenses.TryGetValue(paymentId, out var paymentLicense) ? paymentLicense : null);
                if (row is not null)
                {
                    row.AttemptCount++;
                    row.LastAttemptAt = now;
                    row.LastError = null;
                    row.DeletedAt = now;   // gerçek silme onayı
                }
            }
            catch (Exception ex)
            {
                // Tek bir nesnenin silinememesi tüm purge'ü durdurmamalı;
                // kalan kişisel veriyi temizlemek daha acil. Ama niyet
                // kaybolmamalı: anahtar sınırlı erişimli temizlik kuyruğunda
                // yaşamaya devam ediyor ve OrphanedMediaCleanupJob yeniden
                // deniyor.
                _log.LogError(ex,
                    "[ShopperPurge] R2 nesnesi silinemedi: {Key} (shopper {ShopperId})",
                    key, shopperId);

                if (row is null)
                {
                    row = new OrphanedMediaObject
                    {
                        Id = Guid.NewGuid(),
                        ObjectKey = key,
                        ShopperId = shopperId,
                        PaymentId = target.PaymentId,
                        CreatedAt = now,
                    };
                    _db.OrphanedMediaObjects.Add(row);
                }
                row.AttemptCount++;
                row.LastAttemptAt = now;
                row.LastError = OrphanedMedia.TruncateError(ex.Message);
                pendingRows.Add(row);
            }
        }

        var pendingPaymentIds = pendingRows
            .Where(r => r.PaymentId is not null)
            .Select(r => r.PaymentId!.Value)
            .ToHashSet();

        // 2. Ödeme satırlarındaki kişisel alanlar. Tutar, tarih, referans no
        //    ve hash'ler kalıyor (sınıf özetindeki gerekçe). İmha kaydı yapılmamış
        //    işi saymaz (hareketlerin ve ödeme denetimlerinin kuralı): tekrarlanan
        //    silmede ödemeler Payment.ShopperId bağıyla yeniden bulunur, kişisel
        //    alanı zaten boş olan sayılmaz. Sayım boşaltmadan ÖNCE.
        var paymentsWithPersonal = payments.Where(HasPersonalFields).ToList();
        foreach (var p in payments)
        {
            p.PayerName = "";
            p.RecipientIban = null;
            p.RecipientName = null;
            p.MediaObjectKey = null;
            p.MediaContentType = null;
            p.UpdatedAt = now;

            // Damga YALNIZCA silinecek bir şey kalmadığında. Dosya hâlâ
            // kovadayken "dekont silindi" demek, kimsenin bir daha
            // sorgulamayacağı kalıcı bir yalan üretiyordu.
            if (!pendingPaymentIds.Contains(p.Id))
                p.PdfPurgedAt = now;
        }

        // 3. Yayıncıya açık olan sunucu kopyası. PurgedAt damgası şart:
        //    yayıncının KENDİ bilgisayarındaki SQLite satırı bu işlemle
        //    silinmiyor (WPF ingest'i var olan kaydı atlıyor, yalnız yeni
        //    ekliyor — ShopperRegistrationIngestService), dolayısıyla oradaki
        //    veri bir sonraki push'ta buraya geri akardı ve temizlik sessizce
        //    geri alınırdı. Damga o kapıyı kapatıyor
        //    (LicensesWpfCustomersSyncController).
        //
        //    Yayıncının diskindeki kopya bu PR'ın kapsamı dışında; WPF
        //    tarafında ayrı bir temizleme dalı gerekiyor ve o ancak yeni
        //    sürüm yayınıyla sahaya iner.
        var links = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId)
            .Select(l => new
            {
                l.LicenseId,
                l.WpfCustomerId,
            })
            .ToListAsync(ct);

        // Yalnız sahipliği daha önce kanıtlanmış WPF kimliği purge kapsamıdır.
        // Pending linkin platform+kullanıcı adı herkese açık olabilir; onu
        // fallback kimlik saymak başka bir kişinin projeksiyonunu sildirebilir.
        // Telefon da purge tarafından boşaltıldığı için retry anahtarı olamaz.
        var projectionKeys = links
            .Where(l => l.WpfCustomerId is not null)
            .Select(l => (
                LicenseId: l.LicenseId,
                WpfCustomerId: l.WpfCustomerId!.Value))
            .ToHashSet();
        var licenseIds = projectionKeys
            .Select(k => k.LicenseId)
            .Distinct()
            .ToList();

        var projections = licenseIds.Count == 0
            ? new List<WpfCustomerProjection>()
            : (await _db.WpfCustomerProjections
                    .Where(c => licenseIds.Contains(c.LicenseId))
                    .ToListAsync(ct))
                .Where(c => projectionKeys.Contains((c.LicenseId, c.Id)))
                .ToList();

        // 3b'nin kullanıcı adı anahtarları (named satırlar) boşaltmadan ÖNCE alınır: kullanıcı adının da boşaltılması avukat
        // kararıdır (ScrubPersonal'da tek satır); o satır eklenince arama sessizce durmasın.
        var usernames = projections.ToDictionary(c => c.Id, c => c.Username);

        // Tek boşaltma listesi WpfCustomerProjection'da yaşıyor (ScrubPersonal)
        // ve WPF'teki aynı KVKK politikasını yansıtıyor: Notes ve kara liste
        // alanları BİLEREK kalır. MarkPurged ayrıca PurgedAt'ı İLK silme
        // tarihinde sabitler (??=) — elle yazılan önceki satır bunu her
        // çağrıda ezip adli kaydı bozuyordu.
        foreach (var c in projections)
        {
            c.MarkPurged(now);
        }

        // 3a. Satış kayıtlarındaki metin (yayın ve etiket senkronu, spec 2026-10-13 §9). Ayar KAPALIYKEN (varsayılan,
        //     bugünkü politika) sipariş satırlarına dokunulmaz; akış asıl kaydı silinmiş müşterinin siparişini okuma
        //     anında maskeler. AÇIKKEN silinen asıl kayda ve kopyalarına (eski Id'yle kalmış geç sipariş) bağlı
        //     siparişlerde takma ad ve yorum aynı kayıtta boşaltılır; rowversion ilerlediği için boşaltma bütün
        //     bilgisayarlara iner. Kullanıcı adı, kod ve fiyat mali kayıt olarak kalır.
        var scrubbedOrders = _salesSync.ScrubOrderTextOnPurge
            ? await OrdersWithTextAsync(projections, ct)
            : new List<Order>();
        foreach (var o in scrubbedOrders) ScrubOrderText(o, now);
        if (scrubbedOrders.Count > 0)
            _log.LogInformation("[ShopperPurge] {Count} siparişin metni boşaltıldı (ScrubOrderTextOnPurge)",
                scrubbedOrders.Count);

        // 3b. Gölge banka eşleştirmesinin izi (Obifin) — KVKK K-7: silme talebi banka verisinde de, ilk periyodik imhayı
        //     beklemeden uygulanır. Kurallar BankSubjectScrub'da (IBAN ile banka veri talebi aracıyla ortak).
        //     - IBAN hafızası "bu IBAN bu kişiyle ilişkili" bağıdır: silinir.
        //     - Kişiye bağlı eşleşme satırında (öneri ya da gerçek müşteri) müşteri kimliği ve kanıt boşaltılır; satır ölçüm
        //       kaydı olarak kalır, sonucu durumdan okunur (BankSubjectScrub.MatchForCustomer).
        //     - Kullanıcı adı anahtarı yalnız kanıtında geçen, başka müşteriye bağlı ya da önerisiz satırda (named) yalnız
        //       kanıt boşaltılır, başka müşterinin kimliği ve kararı kalır. Anahtar kanıtta TAM aranır: PaymentMatcher'ın
        //       biçimleri "username={key}", "username={key};iban-memory" ve "conflict:username={key},iban-memory"; anahtar
        //       yalnız [a-z0-9], ardından son, ';' ya da ',' gelir. Önek araması ("ali" → "username=alican") başka müşterinin
        //       hareketinin metnini geri dönüşsüz siler, imha kaydında da onu bu kişiye yazardı.
        //     - Bu satırların hareketlerinin ve silinen hafıza özetinden gelen hareketlerin açıklaması, gönderen adı ve ham
        //       kaydı silinir; tutar, tarih, maske ve özet kalır (özet avukat kararı, plan karar 6). named satırın hareketi de:
        //       kanıtın kaynağı o açıklamadır ve silinen kişinin kullanıcı adını taşır; yalnız kanıt boşaltılsaydı açıklama,
        //       satır başka müşteriye bağlı olduğu için 180 güne kadar kalırdı.
        //     Kişinin aynı lisansta birden çok bağı olabilir (Instagram + TikTok) ve bir satır ikisini birden taşıyabilir:
        //     kural satırın lisansındaki her kimliğe uygulanır (ScrubMatch). UpdatedAt eşleşmenin eşzamanlılık jetonudur;
        //     çakışma (arada silinen hareket ya da hafıza dahil) SaveAsync'te bir kez yeniden denenir. Kullanıcı adı
        //     (ScrubPersonal) ve Payment.ShopperId bağı avukat
        //     kararına kadar BİLEREK kalır; anahtarlar o yüzden boşaltmadan önce alındı (usernames, adım 3).
        var purgedCustomers = projectionKeys.ToLookup(k => k.LicenseId, k => k.WpfCustomerId);
        var ibanMemories = new List<CustomerIbanMemory>();
        var scrubbedMatches = new HashSet<PaymentMatch>();
        var scrubbedTransactions = new HashSet<BankTransaction>();
        foreach (var (licenseId, wpfCustomerId) in projectionKeys)
        {
            var memories = await _db.CustomerIbanMemories
                .Where(m => m.LicenseId == licenseId && m.WpfCustomerId == wpfCustomerId)
                .ToListAsync(ct);
            ibanMemories.AddRange(memories);

            Guid? id = wpfCustomerId;
            var key = usernames.TryGetValue(wpfCustomerId, out var username)
                ? BankTextNormalizer.UsernameKey(username)
                : "";
            // Anahtar tam: işaret kanıtın sonunda ya da ardından ';' / ',' ile (yukarıdaki biçimler). Hepsi SQL'e çevrilir.
            var hasKey = key.Length > 0;
            var marker = "username=" + key;
            var markerThenSemicolon = marker + ";";
            var markerThenComma = marker + ",";
            var matches = await _db.PaymentMatches
                .Where(m => m.LicenseId == licenseId
                    && (m.ProposedWpfCustomerId == id
                        || m.ActualWpfCustomerId == id
                        || (hasKey && m.Evidence != null
                            && (m.Evidence.EndsWith(marker)
                                || m.Evidence.Contains(markerThenSemicolon)
                                || m.Evidence.Contains(markerThenComma)))))
                .ToListAsync(ct);
            scrubbedMatches.UnionWith(matches);

            var matchedTxIds = matches.Select(m => m.BankTransactionId).ToList();
            var hashes = memories.Select(m => m.IbanHash).ToList();
            if (matchedTxIds.Count == 0 && hashes.Count == 0) continue;
            var txs = await _db.BankTransactions
                .Where(t => t.LicenseId == licenseId
                    && (matchedTxIds.Contains(t.Id)
                        || (t.CounterpartyIbanHash != null && hashes.Contains(t.CounterpartyIbanHash))))
                .ToListAsync(ct);
            // Saklama kuralının çoktan boşalttığı hareket "silindi" diye sayılmaz.
            scrubbedTransactions.UnionWith(txs.Where(BankSubjectScrub.HasText));
        }
        // Sayım kuraldan ÖNCE: kural kimlikleri boşaltır, satırın kişiye bağlı olduğu ondan sonra görünmez.
        var linkedMatches = scrubbedMatches.Where(m => IsLinked(m, purgedCustomers)).ToList();
        var evidenceOnlyMatches = scrubbedMatches.Where(m => !IsLinked(m, purgedCustomers)).ToList();
        _db.CustomerIbanMemories.RemoveRange(ibanMemories);
        foreach (var m in scrubbedMatches) ScrubMatch(m, purgedCustomers, now);
        foreach (var t in scrubbedTransactions) BankSubjectScrub.Transaction(t, now);

        // 3c. Elle eşleme denetimi (KVKK Ö-14, K-7). bank.match.manual ayrıntısı kişinin kayıt numarasını taşır
        //     ({"wpfCustomerId":…}, Görev 16) ve 24 ay durur. 3b eşleşme satırında kimliği boşaltarak "bu kişi bu havaleyi
        //     ödedi" bağını koparır; denetim satırı onu taşımaya devam etmesin: hareketin tutarı, tarihi ve maskeli IBAN'ı
        //     kalır, projeksiyon kullanıcı adını korur (avukat kararı), kimlik kişiye çözülürdü. Ayrıntı boşalır; satır (hangi
        //     yöneticinin hangi havaleyi elle eşlediği) kalır. Aranan kimlikler kişinin bu lisanstaki kayıtları VE onlara
        //     birleştirilmiş kopyalar: elle eşleme o günkü asıl kaydı yazar, birleştirme eşleşmeyi taşır, denetim satırını
        //     taşımaz. Görev 16'dan önceki {"username":…} satırlarını bank-data-cleanup çevirir (silinmiş müşteriye çevirmez,
        //     boşaltır). Satır yeniden denemede tazelenir (SaveAsync); sayım boşaltmadan ÖNCE, lisans ayrıntıdan.
        var auditCustomers = new Dictionary<Guid, Guid>();   // kimlik → lisans
        foreach (var group in projectionKeys.GroupBy(k => k.LicenseId))
            foreach (var id in await WithMergedCopiesAsync(group.Key, group.Select(k => k.WpfCustomerId), ct))
                auditCustomers[id] = group.Key;
        var manualMatchAudits = auditCustomers.Count == 0
            ? new List<AuditLogEntry>()
            : await BankSubjectScrub.ManualMatchAudits(_db.AuditLogs, auditCustomers.Keys).ToListAsync(ct);
        var auditLicenses = LicensesOf(manualMatchAudits, auditCustomers);
        foreach (var a in manualMatchAudits) BankSubjectScrub.ManualMatchAudit(a);

        // 4. Bağlı satırlar. Hepsi ya doğrudan kişisel veri taşıyor
        //    (kullanıcı adı, cihaz kimliği, IP) ya da yalnızca oturum
        //    açmaya yarıyor; hiçbirinin mali kayıt değeri yok.
        var devices = await _db.ShopperPushDevices.Where(d => d.ShopperId == shopperId).ToListAsync(ct);
        var tokens = await _db.ShopperRefreshTokens.Where(t => t.ShopperId == shopperId).ToListAsync(ct);
        var resetCodes = await _db.ShopperPasswordResetCodes.Where(r => r.ShopperId == shopperId).ToListAsync(ct);
        var supportRequests = await _db.ShopperSupportRequests.Where(r => r.ShopperId == shopperId).ToListAsync(ct);
        var linkRows = await _db.ShopperBroadcasterLinks.Where(l => l.ShopperId == shopperId).ToListAsync(ct);

        _db.ShopperPushDevices.RemoveRange(devices);
        _db.ShopperRefreshTokens.RemoveRange(tokens);
        _db.ShopperPasswordResetCodes.RemoveRange(resetCodes);
        _db.ShopperSupportRequests.RemoveRange(supportRequests);
        _db.ShopperBroadcasterLinks.RemoveRange(linkRows);

        var dependents = devices.Count + tokens.Count + resetCodes.Count
            + supportRequests.Count + linkRows.Count + ibanMemories.Count;

        // 5. Ödeme denetim kayıtları: satır mali izin parçası (oran sınırının
        //    defteri, karar gerekçesi), istemci parmak izi ve dekontun okunan
        //    tam metni gidiyor. Metin (ParserRawText) gönderen adını, gönderen
        //    ve alıcı IBAN'ının açık hâlini, referansı ve dekontta yazıyorsa
        //    kimlik numarasını taşır: adım 2'nin sildiği ad ve IBAN'ın kaynağıdır,
        //    kalsaydı silme talebi uygulanmamış olurdu (KVKK K22). Güvenlik
        //    saklama işi ikisini kendi süreleriyle boşaltır; burada hemen.
        var submissionAudits = await _db.PaymentSubmissionAudits
            .Where(a => a.ShopperId == shopperId)
            .ToListAsync(ct);
        // Saklama işi çoktan boşalttıysa kayıt yapılmamış işi söylemesin (o işin kendi ölçütü). Sayım boşaltmadan ÖNCE.
        var fingerprintedAudits = submissionAudits.Where(a => a.IpAddress != "" || a.UserAgent != "").ToList();
        var rawTextAudits = submissionAudits.Where(a => a.ParserRawText is not null).ToList();
        foreach (var a in submissionAudits)
        {
            a.IpAddress = "";
            a.UserAgent = "";
            a.ParserRawText = null;
        }

        // 6. Shopper satırının kendisi. Telefon boşaltılıyor: kimliğin en
        //    güçlü tekil alanı o, ve boşaltılınca kişi isterse aynı numarayla
        //    sıfırdan kayıt olabiliyor. PasswordHash hiçbir doğrulamanın
        //    eşleyemeyeceği bir değere çekiliyor — bu hesaba bir daha
        //    girilemez. Kayıt yalnız silinecek kişisel alan kaldıysa (adım 2'nin
        //    kuralı); DeletedAt ölçüt DEĞİL: hesabını uygulamadan silen kişide
        //    talep anında dolar (ShopperMeController.DeleteMe), veri ise burada gider.
        var shopperHadPersonal = HasPersonalFields(shopper);
        shopper.FullName = PurgedName;
        shopper.Phone = "";
        shopper.Address = "";
        shopper.Email = null;
        shopper.TcProtected = null;
        shopper.PasswordHash = "PURGED";
        shopper.SmsConsent = false;
        shopper.DeletedAt ??= now;
        shopper.UpdatedAt = now;

        // 7. KVKK Ö-13: kişisel veri İÇERMEYEN kalıcı imha kaydı, silmeyle aynı SaveChanges'te. Banka verisi olmayan kişinin
        //    silmesi de yazılır (Ö-12: veritabanı dışındaki saatlik özet yalnız bu tablodan beslenir). SubjectId = ShopperId:
        //    geri yüklemeden sonra silme bununla yeniden uygulanır (deploy/README "Geri yükleme"); RequestId = silme talebi.
        //    Lisansa ait satırların kaydı satırın lisansı başına, sayımlar yüklenen kümelerden (Görev 9 kuralı). IBAN hafızası
        //    DependentRowsDeleted'e de girer; kayıtta yalnız kendi satırında sayılır.
        void Record(Guid? license, string target, string action, int count) => DataDestruction.Record(_db, license, target,
            action, count, PurgeCriterion, DataDestruction.Triggers.ShopperPurge, actor, requestId, shopperId);
        void RecordPerLicense<T>(IEnumerable<T> rows, Func<T, Guid?> license, string target, string action)
        {
            foreach (var g in rows.GroupBy(license)) Record(g.Key, target, action, g.Count());
        }
        const string nulled = DataDestruction.Actions.Nulled, deleted = DataDestruction.Actions.Deleted;
        Record(null, "Shoppers.FullName+Phone+Address+Email+TcProtected", nulled, shopperHadPersonal ? 1 : 0);
        RecordPerLicense(paymentsWithPersonal, p => p.LicenseId, "Payments.PersonalFields", nulled);
        RecordPerLicense(deletedMediaLicenses, l => l, MediaTarget, deleted);
        RecordPerLicense(fingerprintedAudits, a => a.LicenseId, SubmissionAuditFingerprintTarget, nulled);
        RecordPerLicense(rawTextAudits, a => a.LicenseId, SubmissionAuditRawTextTarget, nulled);
        RecordPerLicense(projections, c => c.LicenseId, "WpfCustomerProjections.PersonalFields", nulled);
        RecordPerLicense(scrubbedOrders, o => o.LicenseId, "Orders.DisplayName+MessageText", nulled);
        RecordPerLicense(linkRows, l => l.LicenseId, "ShopperBroadcasterLinks", deleted);
        Record(null, "ShopperPushDevices", deleted, devices.Count);
        Record(null, "ShopperRefreshTokens", deleted, tokens.Count);
        Record(null, "ShopperPasswordResetCodes", deleted, resetCodes.Count);
        Record(null, "ShopperSupportRequests", deleted, supportRequests.Count);
        RecordPerLicense(ibanMemories, m => m.LicenseId, IbanMemoryTarget, deleted);
        RecordPerLicense(linkedMatches, m => m.LicenseId, BankSubjectScrub.LinkedMatchTarget, nulled);
        RecordPerLicense(evidenceOnlyMatches, m => m.LicenseId, BankSubjectScrub.MatchEvidenceTarget, nulled);
        RecordPerLicense(scrubbedTransactions, t => t.LicenseId, TransactionTextTarget, nulled);
        RecordPerLicense(auditLicenses, l => l, BankSubjectScrub.ManualMatchAuditTarget, nulled);

        var late = await SaveAsync(new PendingBankScrub(scrubbedMatches, scrubbedTransactions, ibanMemories, purgedCustomers,
                manualMatchAudits, auditCustomers),
            scrubbedOrders, Record, now, ct);

        return new ShopperPurgeResult(
            PaymentsScrubbed: payments.Count,
            PdfsDeleted: pdfsDeleted,
            ProjectionsScrubbed: projections.Count,
            DependentRowsDeleted: dependents + late.Memories,
            PdfsPending: pendingRows.Count,
            BankTransactionsScrubbed: scrubbedTransactions.Count + late.Transactions);
    }

    /// <summary>Ödemede adım 2'nin silecek kişisel alanı kaldı mı.</summary>
    private static bool HasPersonalFields(Payment p)
        => !string.IsNullOrEmpty(p.PayerName) || p.RecipientIban is not null || p.RecipientName is not null
            || p.MediaObjectKey is not null || p.MediaContentType is not null;

    /// <summary>Shopper satırında adım 6'nın silecek kişisel alanı (kaydın hedefindeki alanlar) kaldı mı.</summary>
    private static bool HasPersonalFields(Shopper s)
        => s.FullName != PurgedName || !string.IsNullOrEmpty(s.Phone) || !string.IsNullOrEmpty(s.Address)
            || s.Email is not null || s.TcProtected is not null;

    /// <summary>Satır silinen kişinin bu lisanstaki müşteri kimliklerinden birine bağlı mı (öneri ya da gerçek müşteri).</summary>
    private static bool IsLinked(PaymentMatch match, ILookup<Guid, Guid> purgedCustomers)
        => purgedCustomers[match.LicenseId].Any(id => BankSubjectScrub.References(match, id));

    /// <summary>Denetim satırlarının lisansları (satır başına bir; imha kaydı lisans başına): ayrıntının gösterdiği kimliğin
    /// lisansı, AuditLogs'ta lisans sütunu yok. Ayrıntı boşaltılmadan ÖNCE çağrılır.</summary>
    private static List<Guid?> LicensesOf(IEnumerable<AuditLogEntry> audits, IReadOnlyDictionary<Guid, Guid> customers)
    {
        var byDetails = customers.ToDictionary(kv => BankSubjectScrub.ManualMatchAuditDetails(kv.Key), kv => kv.Value,
            StringComparer.OrdinalIgnoreCase);
        return audits.Select(a => a.Details is { } d && byDetails.TryGetValue(d, out var l) ? (Guid?)l : null).ToList();
    }

    /// <summary>Eşleşme satırına 3b kuralını uygular: satırın lisansındaki silinen her kimlik için
    /// <see cref="BankSubjectScrub.MatchForCustomer"/>. Kişiye bağlı satırda kimlik ve kanıt, bağlı olmayan (named) satırda
    /// yalnız kanıt boşalır. Yeniden uygulanabilir: yeniden denemede tazelenen satıra da aynısı.</summary>
    private static void ScrubMatch(PaymentMatch match, ILookup<Guid, Guid> purgedCustomers, DateTimeOffset now)
    {
        var ids = purgedCustomers[match.LicenseId];
        if (!ids.Any())
        {
            BankSubjectScrub.MatchEvidence(match, now);
            return;
        }
        foreach (var id in ids) BankSubjectScrub.MatchForCustomer(match, id, now);
    }

    /// <summary>
    /// Silmeyi tek SaveChanges'te kaydeder. <see cref="PaymentMatch.UpdatedAt"/>
    /// eşzamanlılık jetonudur: kanıt boşaltmasının UPDATE'i
    /// <c>WHERE UpdatedAt = özgün</c> taşır. Eşleşme okunduktan sonra gölge
    /// eşleştirme ona yazdıysa (çekimin eşleştiricisi, telafi taraması, dekont
    /// bağdaştırması, admin kararı) kayıt DbUpdateConcurrencyException'la düşer
    /// ve bütün silme geri alınır — R2'deki dekontlar ise çoktan silinmiştir.
    /// Çakışan girdilerin HEPSİ boşaltılan eşleşme, hareket ya da sipariş ise
    /// boşaltılan eşleşmeler DB'den tazelenir (eşzamanlı yazanın değerleri kalır),
    /// 3b kuralı yeniden uygulanır (kişinin kimliği ve kanıt) ve BİR kez daha
    /// kaydedilir; ikinci çakışma yukarı gider. Başka bir varlığın çakışması
    /// eskisi gibi doğrudan yukarı gider.
    ///
    /// <para>Hareketin jetonu yoktur, ama metni boşaltılan hareket okunduktan sonra
    /// başka bir yoldan silinebilir (saklama işi, hesap reddi, "Kaldır ve sil";
    /// eşleşmesi FK kaskadıyla gider): UPDATE 0 satır etkiler. Aynı yollar kişinin
    /// IBAN hafızasını da siler: DELETE 0 satır etkiler. Hareketler ve hafızalar
    /// da tazelenir; silinmiş olan ayrılır, kalan hareketin metni yeniden
    /// boşaltılır, kalan hafıza yeniden silinir. İmha kaydının sayısı ilk
    /// okumadandır: bu yarışta arada silinen satır iki kayıtta (bu ve silen
    /// yolunki) sayılabilir.</para>
    ///
    /// <para>Ters yön: tazelenen eşleşmeyi eşzamanlı yazan (dekont bağdaştırması,
    /// elle eşleme) silinen kişiye bağlamışsa havalenin IBAN'ını da öğretmiş
    /// olabilir. Kimliği 3b kuralı boşaltır; o bağ da
    /// <see cref="ScrubLateIbanMemoriesAsync"/> ile silinir, kaydı ayrıca yazılır.</para>
    ///
    /// <para>Aynısı sipariş metni boşaltması için (ayar açıkken, adım 3a):
    /// <see cref="Order.SyncVersion"/> jetondur ve eşzamanlı bir
    /// <c>orders/sync</c> güncellemesi onu ilerletir. Sipariş tazelenir (eşzamanlı
    /// yazanın birimleri kalır; o yol metne dokunmaz), metin yeniden boşaltılır.
    /// Çakışan girdiler eşleşme, hareket, hafıza, sipariş ya da denetim satırı
    /// olabilir; hepsi her zaman birlikte tazelenir.</para>
    ///
    /// <para>Elle eşleme denetim satırının (adım 3c) jetonu yok, ama okunduktan sonra
    /// 24 aylık denetim budaması onu silebilir: UPDATE 0 satır etkiler. Tazelenir,
    /// silinmiş olan ayrılır, kalanın ayrıntısı yeniden boşaltılır. Tazelenen eşleşmeyi
    /// eşzamanlı bir elle eşleme silinen kişiye bağladıysa sayfa kararın ardından
    /// denetim satırını yazar: kişinin denetim satırları yeniden sorgulanır
    /// (<see cref="ScrubLateManualMatchAuditsAsync"/>), yeni bulunan ayrı kayıtla
    /// boşaltılır. Bu silmenin kaydından SONRA yazılan satırı yakalamaz: elle eşleme
    /// silinmiş müşteriyi reddeder (<c>PurgedAt</c>); kalan pencere, eşlemenin müşteri
    /// denetimiyle sayfanın denetim satırını yazması arasıdır.</para>
    /// </summary>
    /// <returns>Yalnız yeniden denemenin bulduğu, eşzamanlı öğrenilmiş hafızalar ve onların hareketleri (sonuca eklenir).</returns>
    private async Task<(int Memories, int Transactions)> SaveAsync(
        PendingBankScrub bank,
        IReadOnlyCollection<Order> scrubbedOrders,
        Action<Guid?, string, string, int> record,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return (0, 0);
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Count > 0
            && ex.Entries.All(e => (e.Entity is PaymentMatch && bank.Matches.Count > 0)
                || (e.Entity is BankTransaction && bank.Transactions.Count > 0)
                || (e.Entity is CustomerIbanMemory && bank.IbanMemories.Count > 0)
                || (e.Entity is AuditLogEntry && bank.ManualMatchAudits.Count > 0)
                || (e.Entity is Order && scrubbedOrders.Count > 0)))
        {
            _log.LogInformation(
                "[ShopperPurge] Banka eşleşmesi, hareket metni, IBAN hafızası, elle eşleme denetimi ya da sipariş metni "
                + "boşaltılırken eşzamanlı bir yazıyla çakıştı; satırlar tazelenip bir kez yeniden deneniyor");
            // Yalnız bildirilen girdi değil, boşaltılan her satır tazelenir:
            // SQL Server'da EF ilk çakışan komutta durur, sonraki çakışan satırı
            // bildirmez. Küme tek kişinin satırları, küçük.
            foreach (var m in bank.Matches)
            {
                var entry = _db.Entry(m);
                await entry.ReloadAsync(ct);
                // Arada silinmiş (ayrıldı). Tazelenen satıra kural yeniden uygulanır (idempotent).
                if (entry.State == EntityState.Detached)
                    continue;
                ScrubMatch(m, bank.PurgedCustomers, now);
            }
            foreach (var t in bank.Transactions)
            {
                var entry = _db.Entry(t);
                await entry.ReloadAsync(ct);
                if (entry.State == EntityState.Detached) continue;
                BankSubjectScrub.Transaction(t, now);
            }
            foreach (var memory in bank.IbanMemories)
            {
                var entry = _db.Entry(memory);
                await entry.ReloadAsync(ct);
                // Arada silinmiş (ayrıldı). Tazeleme kalanı Unchanged'e çeker: silme yeniden işaretlenir.
                if (entry.State == EntityState.Detached) continue;
                _db.CustomerIbanMemories.Remove(memory);
            }
            foreach (var o in scrubbedOrders)
            {
                var entry = _db.Entry(o);
                await entry.ReloadAsync(ct);
                if (entry.State == EntityState.Detached) continue;
                ScrubOrderText(o, now);
            }
            foreach (var a in bank.ManualMatchAudits)
            {
                var entry = _db.Entry(a);
                await entry.ReloadAsync(ct);
                // Denetim budaması arada sildi (ayrıldı); kalanın ayrıntısı yeniden boşaltılır.
                if (entry.State == EntityState.Detached) continue;
                BankSubjectScrub.ManualMatchAudit(a);
            }
            var late = await ScrubLateIbanMemoriesAsync(bank, record, now, ct);
            await ScrubLateManualMatchAuditsAsync(bank, record, ct);
            await _db.SaveChangesAsync(ct);
            return late;
        }
    }

    /// <summary>3b ve 3c'nin kaydedilmeyi bekleyen işi; yeniden deneme bunları tazeler. <paramref name="AuditCustomers"/>:
    /// 3c'nin aradığı kimlikler (birleştirilmiş kopyalar dahil) → lisans.</summary>
    private sealed record PendingBankScrub(
        IReadOnlyCollection<PaymentMatch> Matches,
        IReadOnlyCollection<BankTransaction> Transactions,
        IReadOnlyCollection<CustomerIbanMemory> IbanMemories,
        ILookup<Guid, Guid> PurgedCustomers,
        IReadOnlyCollection<AuditLogEntry> ManualMatchAudits,
        IReadOnlyDictionary<Guid, Guid> AuditCustomers);

    /// <summary>
    /// Yeniden denemede, ilk okumadan sonra yazılmış elle eşleme denetim satırı: eşzamanlı bir elle eşleme tazelenen eşleşmeyi
    /// silinen kişiye bağladıysa sayfa kararın ardından denetim satırını yazar. 3b kimliği eşleşmede boşaltır; denetim satırı
    /// kalsaydı bağ orada dururdu. Ayrıntı boşaltılır; ilk okumanın kaydı zaten eklendi, bunların kaydı ayrı satırdır.
    /// </summary>
    private async Task ScrubLateManualMatchAuditsAsync(
        PendingBankScrub bank, Action<Guid?, string, string, int> record, CancellationToken ct)
    {
        if (bank.AuditCustomers.Count == 0) return;
        var known = bank.ManualMatchAudits.Select(a => a.Id).ToHashSet();
        // İlk okumanın satırları veritabanında henüz dolu: sorguya yeniden girer (izlenen örnek döner), known ayırır.
        var late = (await BankSubjectScrub.ManualMatchAudits(_db.AuditLogs, bank.AuditCustomers.Keys).ToListAsync(ct))
            .Where(a => !known.Contains(a.Id))
            .ToList();
        if (late.Count == 0) return;
        var licenses = LicensesOf(late, bank.AuditCustomers);
        foreach (var a in late) BankSubjectScrub.ManualMatchAudit(a);
        foreach (var g in licenses.GroupBy(l => l))
            record(g.Key, BankSubjectScrub.ManualMatchAuditTarget, DataDestruction.Actions.Nulled, g.Count());
    }

    /// <summary>
    /// Yeniden denemede, ilk okumadan sonra silinen kişi adına öğrenilmiş IBAN hafızası: eşzamanlı yazan tazelenen eşleşmeyi
    /// ona bağlarken havalenin IBAN'ını öğretmiş olabilir (<c>PaymentMatchReconciler.LearnIbanAsync</c>). Kimliği 3b kuralı
    /// boşaltır; bağ kalsaydı IBAN ile kişi arasındaki ilişki KVKK silmesinden sonra da dururdu. Bağ silinir, özetinden gelen
    /// hareketlerin metni de (3b kuralı; saklama kuralının boşalttığı sayılmaz). İlk okumanın kaydı zaten eklendi: bunların
    /// kaydı ayrı satırdır, sayısı yalnız yeni bulunanlar.
    /// </summary>
    private async Task<(int Memories, int Transactions)> ScrubLateIbanMemoriesAsync(
        PendingBankScrub bank, Action<Guid?, string, string, int> record, DateTimeOffset now, CancellationToken ct)
    {
        var known = bank.IbanMemories.Select(m => m.Id).ToHashSet();
        int memories = 0, transactions = 0;
        foreach (var group in bank.PurgedCustomers)
        {
            var licenseId = group.Key;
            var customers = group.ToList();
            var late = (await _db.CustomerIbanMemories
                    .Where(m => m.LicenseId == licenseId && customers.Contains(m.WpfCustomerId))
                    .ToListAsync(ct))
                .Where(m => !known.Contains(m.Id))
                .ToList();
            if (late.Count == 0) continue;
            _db.CustomerIbanMemories.RemoveRange(late);

            var hashes = late.Select(m => m.IbanHash).ToList();
            // İzlenen hareketler (3b'de okunmuş) izleyicideki değeriyle döner: yeniden boşaltılmış olanın metni zaten yok.
            var txs = (await _db.BankTransactions
                    .Where(t => t.LicenseId == licenseId && t.CounterpartyIbanHash != null
                        && hashes.Contains(t.CounterpartyIbanHash))
                    .ToListAsync(ct))
                .Where(BankSubjectScrub.HasText)
                .ToList();
            foreach (var t in txs) BankSubjectScrub.Transaction(t, now);

            record(licenseId, IbanMemoryTarget, DataDestruction.Actions.Deleted, late.Count);
            record(licenseId, TransactionTextTarget, DataDestruction.Actions.Nulled, txs.Count);
            memories += late.Count;
            transactions += txs.Count;
        }
        return (memories, transactions);
    }

    /// <summary>
    /// Silinen asıl kayıtlara ve kopyalarına (<see cref="WithMergedCopiesAsync"/>)
    /// bağlı, metni henüz boşaltılmamış siparişler (adım 3a). Zaten boşaltılmış
    /// satır yeniden yazılmaz: tekrarlanan silme rowversion'ı ve akışı boşuna
    /// ilerletmez.
    /// </summary>
    private async Task<List<Order>> OrdersWithTextAsync(
        IReadOnlyCollection<WpfCustomerProjection> purged, CancellationToken ct)
    {
        var orders = new List<Order>();
        foreach (var group in purged.GroupBy(p => p.LicenseId))
        {
            var licenseId = group.Key;
            var ids = await WithMergedCopiesAsync(licenseId, group.Select(p => p.Id), ct);

            var hexes = ids.Select(id => id.ToString("N")).ToList();
            orders.AddRange(await _db.Orders
                .Where(o => o.LicenseId == licenseId
                    && hexes.Contains(o.CustomerId)
                    && (o.DisplayName != null || o.MessageText != "" || o.TextScrubbedAt == null))
                .ToListAsync(ct));
        }
        return orders;
    }

    /// <summary>
    /// Verilen asıl kayıtlar ve onlara birleştirilmiş kopyaları (eski Id'ler; asıl
    /// kayıtlar dahil). Kopyalar varsayılan sorgulardan gizli; burada açıkça istenir
    /// ve zincir <see cref="SalesCustomerDirectory.MaxHops"/> adım geriye izlenir
    /// (CustomerIdResolver'ın ileri yönde izlediği kadar). Eski Id'yle kalmış geç
    /// sipariş (3a) ve birleştirmeden önce yazılmış elle eşleme denetimi (3c) için.
    /// Kural banka veri talebinin silmesiyle ortak (<see cref="BankSubjectScrub.WithMergedCopiesAsync"/>).
    /// </summary>
    private Task<HashSet<Guid>> WithMergedCopiesAsync(
        Guid licenseId, IEnumerable<Guid> canonical, CancellationToken ct)
        => BankSubjectScrub.WithMergedCopiesAsync(_db, licenseId, canonical, ct);

    /// <summary>Sipariş metnini boşaltır (<see cref="OrderTextScrub.Apply"/>);
    /// satır değiştiği için <see cref="Order.UpdatedAt"/> ilerler.
    /// <see cref="Order.SyncVersion"/> ilerlemez: metin birim değil, komutların
    /// beklenen sürümü değişmemeli (CustomerIdentityMerger'ın CustomerId
    /// taşımasıyla aynı gerekçe).</summary>
    private static void ScrubOrderText(Order order, DateTimeOffset now)
    {
        OrderTextScrub.Apply(order, now);
        order.UpdatedAt = now;
    }
}
