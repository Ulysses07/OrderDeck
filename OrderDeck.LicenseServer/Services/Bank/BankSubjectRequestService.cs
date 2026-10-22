using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// IBAN ile banka verisi talebi (KVKK K-7, Ö-11): kayıt, doğrulama kaydı, arama, dışa aktarma, silme, kapatma. Shopper hesabı
/// olmayanların (yalnız formu dolduranlar, WPF'te kayıtlı müşteriler, müşteri olmayan karşı taraflar) tek yolu. Kimlik ve IBAN
/// sahipliğinin doğrulanması yöneticinin işidir; NASIL doğrulandığı (kişisel veri içermeyen yöntem) kaydedilmeden dışa aktarma ve
/// silme yapılmaz — ispat yükü veri sorumlusunda. <see cref="BankHasher"/> yalnız talep açarken gerekir (çağıran verir): modül
/// anahtarsız kapalıyken açık talepler yine kapatılabilir ve silinebilir.
/// <para>Talep bir kez kapanır: <see cref="BankSubjectRequest.ClosedAt"/> eşzamanlılık jetonudur. Aynı anda iki silme ya da
/// kapatma (çift tık, iki yönetici) olursa ikincisi <see cref="ClosedMessage"/> alır; sonucu ve imha kayıtlarını ikinci kez
/// yazmaz.</para>
/// </summary>
public sealed class BankSubjectRequestService
{
    public const int ResponseDays = 30;
    public const string InvalidIbanMessage = "Geçerli bir IBAN girin (boşluklu yazılabilir). Kısaltılmış ya da maskeli IBAN aranamaz.";
    public const string LicenseNotFoundMessage = "Lisans bulunamadı.";
    public const string FutureReceivedMessage = "Talebin alındığı gün bugünden sonra olamaz.";
    public const string NotFoundMessage = "Talep bulunamadı.";
    public const string ClosedMessage = "Talep kapatılmış.";
    public const string NotVerifiedMessage = "Önce başvuranın kimliğini ve IBAN'ın ona ait olduğunu doğrulayıp yöntemi kaydedin.";
    public const string NoMethodMessage = "Doğrulama yöntemini seçin.";
    public const string WrongKindMessage = "Erişim talebiyle silme yapılmaz; silme için ayrı bir silme talebi açın.";
    public const string ExportWrongKindMessage = "Silme talebinde dışa aktarım yapılmaz; başvuran verinin kopyasını da istiyorsa " +
        "ayrı bir erişim talebi açın.";
    public const string ResolutionNotAllowedMessage = "Bu sonuç bu talebe uygulanamaz: 'Yanıtlandı' yalnız doğrulanmış erişim talebinde, " +
        "'Doğrulanamadı' yalnız doğrulama kaydı olmayan talepte seçilir; silme 'Sil ve kapat' ile yapılır.";
    /// <summary>Silme yeniden denemede de eşzamanlı bir yazıyla çakıştı: hiçbir şey silinmedi (tek kayıt), talep açık kaldı.</summary>
    public const string EraseConflictMessage = "Silme eşzamanlı bir eşleştirme yazımıyla iki kez çakıştı; hiçbir şey silinmedi, " +
        "talep açık. Yeniden deneyin.";
    /// <summary>Silmenin bildirimine eklenir: bu aracın silmediği kopyalar. Havaleye bağlı dekontta (müşteri adına ödeyen üçüncü
    /// kişi olabilir, Ö-3) ödeyenin adı, dekont dosyası ve okunan metni durur; araç onlara dokunmaz (ödeme kaydı; gerekçe avukat
    /// kararı, plan karar 33). Yönetici bunu başvurana gerekçeli kısmi ret olarak yazar (Silme Yönetmeliği md. 12/1-c).</summary>
    public const string OtherCopiesNotice = "Havaleye bağlı dekont kaydı (ödeyen adı, dekont dosyası ve okunan metni) bu araçla " +
        "silinmez; bunu başvurana gerekçesiyle yazılı bildirin. Obifin'deki kopya için Obifin'e yazılı talep gönderin; bankanın " +
        "kendi kayıtları bankanın sorumluluğundadır.";
    /// <summary>Silmenin imha kayıtlarının ölçütü (<see cref="DataDestructionRecord.Criterion"/>).</summary>
    public const string EraseCriterion = "ilgili kişi talebi";
    /// <summary>Kapanışta boşalan IBAN özetinin imha kaydı hedefi. Özet takma adlı kişisel veridir, talep kapanınca amacı biter
    /// (Silme Yönetmeliği md. 7/1): her kapanış yolu (silme, kapatma, lisansın verisinin silinmesi) talep başına bir satır yazar.</summary>
    public const string IbanHashTarget = "BankSubjectRequests.IbanHash";

    /// <summary>Özet boşaltmasının ölçütü: talebin sonuç kategorisi (kişisel veri değil). Kayıt tek başına yeniden uygulanabilsin
    /// (Görev 13 kuralı): yedekten geri yüklenen açık talep aynı sonuçla yeniden kapatılır.</summary>
    public static string ClosedCriterion(BankSubjectResolution resolution) => $"talep kapandı, amaç bitti ({resolution})";

    public sealed record TransactionRow(Guid Id, DateTimeOffset OccurredAt, decimal Amount, string Currency, string BankaKodu,
        string? IbanMasked, string? Description, string? CounterpartyName, string? TransactionCode, string? BankReference,
        PaymentMatchStatus? MatchStatus, bool LinkedToCustomer);

    /// <param name="LinkedReceipts">Bu havalelere eşleşme satırıyla bağlı dekont (ödeme) sayısı: dekontta ödeyenin adı ve dosyası
    /// durur, araç onları dışa aktarmaz ve silmez (<see cref="OtherCopiesNotice"/>). Sayı kişisel veri değil.</param>
    public sealed record Lookup(BankSubjectRequest Request, IReadOnlyList<TransactionRow> Transactions, int IbanMemories,
        int LinkedReceipts = 0);

    /// <summary>Gerçekten silinenler: metni hâlâ dolu hareket, IBAN bağı, kimliği ya da kanıtı boşalan eşleşme satırı, müşteri
    /// kimliği boşalan elle eşleme denetim satırı. <paramref name="LinkedReceipts"/>: silinMEYEN, havalelere bağlı dekont sayısı
    /// (<see cref="Lookup.LinkedReceipts"/>).</summary>
    public sealed record EraseResult(int Transactions, int IbanMemories, int Matches, int ManualMatchAudits, int LinkedReceipts = 0);

    private readonly LicenseDbContext _db;

    public BankSubjectRequestService(LicenseDbContext db) => _db = db;

    /// <summary>Talebi açar. IBAN yalnız geçerliyse özetlenir (<see cref="BankHasher.HashIban"/>; maskeli ya da kısaltılmış değer
    /// reddedilir), düz saklanmaz. <paramref name="receivedOnTr"/>: talebin OrderDeck'e ulaştığı TR günü (son tarih ondan 30 gün);
    /// bugünden sonrası reddedilir.</summary>
    public async Task<BankSubjectRequest> CreateAsync(BankHasher hasher, Guid licenseId, string iban, BankSubjectRequestKind kind,
        DateOnly receivedOnTr, string createdBy, CancellationToken ct)
    {
        var hash = hasher.HashIban(iban) ?? throw new ObifinValidationException(InvalidIbanMessage);
        var now = DateTimeOffset.UtcNow;
        // Son tarih alıştan sayılır: ileri bir alış günü gecikmeyi gizlerdi.
        if (receivedOnTr > DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ObifinConnectionService.TrZone).DateTime))
            throw new ObifinValidationException(FutureReceivedMessage);
        if (!await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct))
            throw new ObifinValidationException(LicenseNotFoundMessage);
        var receivedAt = ObifinConnectionService.TrToUtc(receivedOnTr.ToDateTime(TimeOnly.MinValue));
        var request = new BankSubjectRequest
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Kind = kind, IbanHash = hash, IbanMasked = BankHasher.MaskIban(iban),
            ReceivedAt = receivedAt, DueAt = receivedAt.AddDays(ResponseDays), CreatedAt = now, CreatedBy = createdBy,
        };
        _db.BankSubjectRequests.Add(request);
        await _db.SaveChangesAsync(ct);
        return request;
    }

    /// <summary>Talep + aynı lisansta aynı özetli hareketler (yeniden eskiye) + bu özetle kurulmuş IBAN bağı sayısı. Kapanmış
    /// talepte özet yoktur: liste boş.</summary>
    public async Task<Lookup?> FindAsync(Guid requestId, CancellationToken ct)
    {
        var request = await _db.BankSubjectRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null) return null;
        if (request.IbanHash is null) return new Lookup(request, [], 0);
        var txs = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == request.LicenseId && t.CounterpartyIbanHash == request.IbanHash)
            .OrderByDescending(t => t.OccurredAt)
            .Select(t => new { t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.CounterpartyIbanMasked, t.Description,
                t.CounterpartyName, t.TransactionCode, t.BankReference })
            .ToListAsync(ct);
        var ids = txs.Select(t => t.Id).ToList();
        var matches = await _db.PaymentMatches.AsNoTracking().Where(m => ids.Contains(m.BankTransactionId))
            .Select(m => new { m.BankTransactionId, m.Status, m.PaymentId,
                Linked = m.ActualWpfCustomerId != null || m.PaymentId != null })
            .ToDictionaryAsync(m => m.BankTransactionId, ct);
        var receipts = matches.Values.Where(m => m.PaymentId != null).Select(m => m.PaymentId).Distinct().Count();
        var memories = await _db.CustomerIbanMemories
            .CountAsync(m => m.LicenseId == request.LicenseId && m.IbanHash == request.IbanHash, ct);
        var rows = txs.Select(t =>
        {
            matches.TryGetValue(t.Id, out var m);
            return new TransactionRow(t.Id, t.OccurredAt, t.Amount, t.Currency, t.BankaKodu, t.CounterpartyIbanMasked, t.Description,
                t.CounterpartyName, t.TransactionCode, t.BankReference, m?.Status, m?.Linked ?? false);
        }).ToList();
        return new Lookup(request, rows, memories, receipts);
    }

    /// <summary>Kimlik ve IBAN sahipliğinin doğrulama YÖNTEMİNİ kaydeder (KVKK Ö-11). Açık talepte, dışa aktarma ve silmeden önce
    /// zorunlu; yeniden kaydedilebilir (son yöntem geçerli).</summary>
    public async Task VerifyAsync(Guid requestId, BankSubjectVerification method, string actor, CancellationToken ct)
    {
        if (method == BankSubjectVerification.None) throw new ObifinValidationException(NoMethodMessage);
        var request = await _db.BankSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new ObifinValidationException(NotFoundMessage);
        if (request.ClosedAt is not null) throw new ObifinValidationException(ClosedMessage);
        request.Verification = method;
        request.VerifiedAt = DateTimeOffset.UtcNow;
        request.VerifiedBy = actor;
        await SaveOrClosedAsync(ct);
    }

    /// <summary>Dışa aktarım (<see cref="ToCsv"/>): yalnız açık, doğrulaması kaydedilmiş ERİŞİM talebinde. Silme talebinde yapılmaz
    /// (KVKK m. 4/2-ç): başvuran yalnız silme istedi, silmeden önce alınan CSV sistem dışında imha kaydına girmeyen bir kopya
    /// bırakırdı. Kopya isteyen başvurana ayrı bir erişim talebi açılır; yönetici veriyi <see cref="FindAsync"/> ile görür.</summary>
    public async Task<byte[]> ExportAsync(Guid requestId, CancellationToken ct)
    {
        var lookup = await FindAsync(requestId, ct) ?? throw new ObifinValidationException(NotFoundMessage);
        if (lookup.Request.ClosedAt is not null) throw new ObifinValidationException(ClosedMessage);
        if (lookup.Request.Kind != BankSubjectRequestKind.Access) throw new ObifinValidationException(ExportWrongKindMessage);
        if (lookup.Request.Verification == BankSubjectVerification.None) throw new ObifinValidationException(NotVerifiedMessage);
        return ToCsv(lookup);
    }

    /// <summary>Lisansların açık taleplerini kapatır (bağlantı kaldırma, hizmet sonu yerel silmesi, yayıncı silmesi): özet boşalır
    /// (imha kaydı talep başına, tetikleyici <paramref name="trigger"/>: çağıranın silmesi — <c>admin-remove</c>,
    /// <c>service-end</c>, <c>customer-purge</c>), sonuç <see cref="BankSubjectResolution.DataDeleted"/>, <paramref name="outcome"/>
    /// (çağıranın sabit metni, yönetici girdisi değil) yazılır. Yönetici başvurana yine yanıt vermelidir. KAYDETMEZ: çağıranın
    /// silmesiyle aynı SaveChanges. Arada başka bir yol talebi kapattıysa o kayıt jetonla düşer (çağıranın çakışma kalıbı).
    /// Kapatılan sayısını döner.</summary>
    public static async Task<int> CloseOpenForLicensesAsync(LicenseDbContext db, IReadOnlyCollection<Guid> licenseIds, string outcome,
        string trigger, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (licenseIds.Count == 0) return 0;
        var open = await db.BankSubjectRequests.Where(r => licenseIds.Contains(r.LicenseId) && r.ClosedAt == null).ToListAsync(ct);
        foreach (var r in open) MarkClosed(db, r, BankSubjectResolution.DataDeleted, outcome, trigger, actor, now);
        return open.Count;
    }

    /// <summary>Kapanışın ortak adımı (silme, kapatma, lisansın verisinin silinmesi): sonuç yazılır, IBAN özeti boşalır ve
    /// boşalması imha kaydına girer (<see cref="IbanHashTarget"/>, talep kimliğiyle; Silme Yönetmeliği md. 7/1 — özet takma adlı
    /// kişisel veri, amacı bitti). KAYDETMEZ: kayıt kapanışla aynı SaveChanges'te, <c>ClosedAt</c> jetonu çakışırsa birlikte
    /// düşer.</summary>
    private static void MarkClosed(LicenseDbContext db, BankSubjectRequest request, BankSubjectResolution resolution, string outcome,
        string trigger, string actor, DateTimeOffset now)
    {
        DataDestruction.Record(db, request.LicenseId, IbanHashTarget, DataDestruction.Actions.Nulled, request.IbanHash is null ? 0 : 1,
            ClosedCriterion(resolution), trigger, actor, request.Id);
        request.ClosedAt = now;
        request.ClosedBy = actor;
        request.IbanHash = null;
        request.Resolution = resolution;
        request.Outcome = outcome;
    }

    /// <summary>Erişim talebinin dışa aktarımı (KVKK m. 11/1-b). Müşteri kimliği ve kullanıcı adı YAZILMAZ: havaleyi gönderen ile
    /// ödemesi sayılan müşteri farklı kişi olabilir. UTF-8 BOM'lu (Excel), noktalı virgül ayraçlı; '=', '+', '-', '@', sekme ve
    /// satır başı ile başlayan hücre kesme işaretiyle başlar (formül enjeksiyonu, OWASP CSV injection listesi). Doğrulama denetimi
    /// ve tür denetimi <see cref="ExportAsync"/>'te.</summary>
    public static byte[] ToCsv(Lookup lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine("islem_zamani_tr;tutar;para_birimi;banka;kisaltilmis_iban;aciklama;gonderen_adi;islem_kodu;banka_referansi;eslestirme");
        foreach (var t in lookup.Transactions)
        {
            var tr = TimeZoneInfo.ConvertTime(t.OccurredAt, ObifinConnectionService.TrZone);
            sb.AppendLine(string.Join(';',
                Cell(tr.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Cell(t.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                Cell(t.Currency), Cell(t.BankaKodu), Cell(t.IbanMasked), Cell(t.Description), Cell(t.CounterpartyName),
                Cell(t.TransactionCode), Cell(t.BankReference),
                Cell(t.LinkedToCustomer ? "bir müşteriyle eşleşti" : "eşleşmedi")));
        }
        sb.AppendLine($"# Bu IBAN'ın bağlı olduğu müşteri kaydı sayısı: {lookup.IbanMemories} (müşteri kimliği yazılmaz)");
        sb.AppendLine($"# Bu havalelere bağlı dekont kaydı sayısı: {lookup.LinkedReceipts} (dekonttaki ödeyen adı ve dekont " +
            "dosyası bu dosyada yok; ayrıca saklanır)");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // OWASP CSV injection: sekme ve satır başı da formülün önüne geçebilir.
        if ("=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Silme talebi (KVKK m. 11/1-e), yalnız açık, doğrulaması kayıtlı SİLME talebinde. Kurallar KVKK silmesiyle ortak
    /// (<see cref="BankSubjectScrub"/>):
    /// <list type="bullet">
    /// <item>Bu özetle kurulmuş IBAN bağları silinir.</item>
    /// <item>Aynı özetli hareketlerin açıklaması, adı ve ham kaydı silinir; tutar, tarih, kısaltılmış IBAN ve özet kalır (karar 6).</item>
    /// <item>Eşleşme satırında bu IBAN'ın bağlı olduğu her müşterinin kimliği ve kanıt silinir
    /// (<see cref="BankSubjectScrub.MatchForCustomer"/>); bağ yoksa yalnız kanıt — gönderen ile ödemesi sayılan müşteri farklı kişi
    /// olabilir (karar 7).</item>
    /// <item>Kararın müşterisi boşalan havalenin elle eşleme denetim satırında o müşterinin (ve ona birleştirilmiş kopyaların)
    /// kimliği silinir (<see cref="BankSubjectScrub.ManualMatchAudit"/>); kişinin başka havalelerinin satırı kalır (talep bu IBAN'ın
    /// verisidir).</item>
    /// </list>
    /// Yalnız gerçekten silinen sayılır (saklama işinin boşalttığı metin, zaten boş kanıt sayılmaz, o satırlara yazılmaz); sayım
    /// kuraldan ÖNCE. Talep kapanır, özet boşalır, imha kayıtları (özetinki dahil) talep kimliğiyle aynı SaveChanges'te. Eşzamanlı bir yazıyla
    /// (eşleşme jetonu, arada silinen hareket, hafıza ya da budanan denetim satırı, talebi kapatan başka bir istek) çakışırsa bir kez
    /// baştan yeniden denenir; ikinci çakışmada izleyici boşalır, <see cref="EraseConflictMessage"/> fırlar (tek kayıt: hiçbir şey
    /// silinmedi).</summary>
    public async Task<EraseResult> EraseAsync(Guid requestId, string actor, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await EraseOnceAsync(requestId, actor, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // Kayıt düştü (ilişkisel veritabanında bütünüyle geri alındı): baştan taze okunur. Talep arada kapandıysa yeniden
                // deneme ClosedMessage verir.
                _db.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                throw new ObifinValidationException(EraseConflictMessage);
            }
        }
    }

    private async Task<EraseResult> EraseOnceAsync(Guid requestId, string actor, CancellationToken ct)
    {
        var request = await _db.BankSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new ObifinValidationException(NotFoundMessage);
        if (request.ClosedAt is not null || request.IbanHash is null) throw new ObifinValidationException(ClosedMessage);
        // Erişim talebiyle silme yapılmaz: başvuranın istediği bu değil.
        if (request.Kind != BankSubjectRequestKind.Erasure) throw new ObifinValidationException(WrongKindMessage);
        // KVKK Ö-11: yanlış kişinin talebiyle silinmesin — doğrulama yöntemi kayıtlı olmalı.
        if (request.Verification == BankSubjectVerification.None) throw new ObifinValidationException(NotVerifiedMessage);
        var now = DateTimeOffset.UtcNow;
        var licenseId = request.LicenseId;
        var hash = request.IbanHash;

        // Lisans + özet tekil (CustomerIbanMemories dizini): en çok bir müşteri. Kural yine her biri için uygulanır.
        var memories = await _db.CustomerIbanMemories
            .Where(m => m.LicenseId == licenseId && m.IbanHash == hash).ToListAsync(ct);
        var customers = memories.Select(m => m.WpfCustomerId).ToHashSet();
        var txs = await _db.BankTransactions
            .Where(t => t.LicenseId == licenseId && t.CounterpartyIbanHash == hash).ToListAsync(ct);
        var ids = txs.Select(t => t.Id).ToList();
        var matches = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);

        // Sayım kuraldan ÖNCE (kural kimlikleri ve kanıtı boşaltır). Saklama işinin çoktan boşalttığı hareket ve kanıtı zaten boş,
        // kişiye bağlı olmayan eşleşme silinmiş sayılmaz; onlara yazılmaz (eşleşme jetonu boşuna ilerlemez).
        var scrubbedTxs = txs.Where(BankSubjectScrub.HasText).ToList();
        // Havalelere bağlı dekontlar silinmez (ödeme kaydı; plan karar 33): sonuç metni ve bildirim onları anar.
        var receipts = matches.Where(m => m.PaymentId != null).Select(m => m.PaymentId).Distinct().Count();
        var linked = matches.Where(m => customers.Any(c => BankSubjectScrub.References(m, c))).ToList();
        var evidenceOnly = matches.Where(m => !linked.Contains(m) && m.Evidence is not null).ToList();
        // Görev 16 kuralı (KVKK Ö-14): kararın müşterisi boşalan havalenin elle eşleme denetim satırı da o müşteriyi göstermesin.
        // Yalnız bu havalelerin satırları; birleştirmeden önce yazılmış satır kopyanın kimliğini taşır. Yalnız önerisi boşalan
        // satırın denetimi yoktur (elle eşleme kararı yazar, öneriyi yazmaz).
        var decided = linked.Where(m => m.ActualWpfCustomerId is { } a && customers.Contains(a)).ToList();
        var audits = new List<AuditLogEntry>();
        if (decided.Count > 0)
        {
            var auditCustomers = await BankSubjectScrub.WithMergedCopiesAsync(_db, licenseId,
                decided.Select(m => m.ActualWpfCustomerId!.Value), ct);
            var decidedTargets = decided.Select(m => m.BankTransactionId.ToString()).ToList();
            audits = await BankSubjectScrub.ManualMatchAudits(_db.AuditLogs, auditCustomers)
                .Where(a => a.TargetId != null && decidedTargets.Contains(a.TargetId))
                .ToListAsync(ct);
        }

        _db.CustomerIbanMemories.RemoveRange(memories);
        foreach (var t in scrubbedTxs) BankSubjectScrub.Transaction(t, now);
        foreach (var m in linked)
            foreach (var c in customers) BankSubjectScrub.MatchForCustomer(m, c, now);
        foreach (var m in evidenceOnly) BankSubjectScrub.MatchEvidence(m, now);
        foreach (var a in audits) BankSubjectScrub.ManualMatchAudit(a);

        var matchCount = linked.Count + evidenceOnly.Count;
        // KVKK Ö-13: kişisel veri içermeyen imha kaydı, silmeyle aynı SaveChanges'te; talep kimliğiyle (geri yüklemeden sonra
        // yeniden uygulama başvurunun aslına dayanır: özet kapanışta boşaldı — boşalması da kayıtta). Sıfır satırlık hedef
        // yazılmaz.
        MarkClosed(_db, request, BankSubjectResolution.Erased,
            $"Silindi: {scrubbedTxs.Count} hareketin açıklaması/adı/ham kaydı, {memories.Count} IBAN bağı, " +
            $"{matchCount} eşleşme satırının kimliği/kanıtı, {audits.Count} elle eşleme denetim satırının müşteri kimliği. " +
            "Tutar, tarih, kısaltılmış IBAN ve tanıma kodu kaldı." +
            (receipts > 0 ? $" {receipts} bağlı dekont kaydı (ödeyen adı, dekont dosyası) bu araçla silinmedi." : ""),
            DataDestruction.Triggers.SubjectRequest, actor, now);
        void Record(string target, string action, int count) => DataDestruction.Record(_db, licenseId, target, action, count,
            EraseCriterion, DataDestruction.Triggers.SubjectRequest, actor, request.Id);
        Record(BankSubjectScrub.IbanMemoryTarget, DataDestruction.Actions.Deleted, memories.Count);
        Record(BankSubjectScrub.TransactionTextTarget, DataDestruction.Actions.Nulled, scrubbedTxs.Count);
        Record(BankSubjectScrub.LinkedMatchTarget, DataDestruction.Actions.Nulled, linked.Count);
        Record(BankSubjectScrub.MatchEvidenceTarget, DataDestruction.Actions.Nulled, evidenceOnly.Count);
        Record(BankSubjectScrub.ManualMatchAuditTarget, DataDestruction.Actions.Nulled, audits.Count);
        await _db.SaveChangesAsync(ct);
        return new EraseResult(scrubbedTxs.Count, memories.Count, matchCount, audits.Count, receipts);
    }

    /// <summary>Talebi bir sonuç KATEGORİSİYLE kapatır (KVKK m. 13/3; Silme Yönetmeliği md. 12/1-c gerekçeli ret): sonuç metni
    /// kategoriden üretilir (<see cref="OutcomeText"/>), serbest metin yok; özet boşalır (imha kaydıyla, aynı SaveChanges'te; jeton
    /// çakışırsa birlikte düşer). Kurallar talep kaydının ispat değerini
    /// korur: <see cref="BankSubjectResolution.Answered"/> yalnız doğrulaması kayıtlı ERİŞİM talebinde (dışa aktarım ancak orada
    /// mümkün); <see cref="BankSubjectResolution.RejectedNotVerified"/> yalnız doğrulama kaydı OLMAYAN talepte;
    /// <see cref="BankSubjectResolution.RejectedLegalGround"/> ve <see cref="BankSubjectResolution.Withdrawn"/> her talepte.
    /// <see cref="BankSubjectResolution.Erased"/>'ı yalnız <see cref="EraseAsync"/>, <see cref="BankSubjectResolution.DataDeleted"/>'ı
    /// yalnız <see cref="CloseOpenForLicensesAsync"/> yazar.</summary>
    public async Task CloseAsync(Guid requestId, BankSubjectResolution resolution, string actor, CancellationToken ct)
    {
        var request = await _db.BankSubjectRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new ObifinValidationException(NotFoundMessage);
        if (request.ClosedAt is not null) throw new ObifinValidationException(ClosedMessage);
        var verified = request.Verification != BankSubjectVerification.None;
        var allowed = resolution switch
        {
            BankSubjectResolution.Answered => request.Kind == BankSubjectRequestKind.Access && verified,
            BankSubjectResolution.RejectedNotVerified => !verified,
            BankSubjectResolution.RejectedLegalGround or BankSubjectResolution.Withdrawn => true,
            _ => false,
        };
        if (!allowed) throw new ObifinValidationException(ResolutionNotAllowedMessage);
        MarkClosed(_db, request, resolution, OutcomeText(resolution), DataDestruction.Triggers.SubjectRequest, actor,
            DateTimeOffset.UtcNow);
        await SaveOrClosedAsync(ct);
    }

    /// <summary>Kapatma kategorisinin sonuç metni (talep listesi ve kayıt). Kişisel veri yok.</summary>
    public static string OutcomeText(BankSubjectResolution resolution) => resolution switch
    {
        BankSubjectResolution.Answered => "Yanıtlandı: dışa aktarım başvurana iletildi.",
        BankSubjectResolution.RejectedNotVerified => "Reddedildi: başvuranın kimliği ya da IBAN'ın ona ait olduğu doğrulanamadı.",
        BankSubjectResolution.RejectedLegalGround => "Reddedildi (tamamen ya da kısmen): kanuni gerekçe başvurana yazılı bildirildi.",
        BankSubjectResolution.Withdrawn => "Başvuran talebi geri çekti.",
        _ => "",
    };

    /// <summary>Açık talebe yazan kayıt: <see cref="BankSubjectRequest.ClosedAt"/> jetondur, talep arada kapandıysa (silme,
    /// kapatma, lisansın verisinin silinmesi) kayıt düşer ve <see cref="ClosedMessage"/> döner.</summary>
    private async Task SaveOrClosedAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new ObifinValidationException(ClosedMessage);
        }
    }
}
