using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// KVKK düzeltmelerinden (2026-10-19 hukuki ön inceleme §5.1) önce yazılmış banka verisini yeni kurallara indirir. Tek
/// seferlik; <c>bank-data-cleanup</c> komutu çağırır. Kuru koşu uygulamanın sayılarını hesaplar, hiçbir şey yazmaz.
/// <list type="number">
/// <item>Giden ve sıfır tutarlı hareketler eşleşme satırlarıyla silinir; insan kararına bağlı olan atlanır ve sayılır.</item>
/// <item>Çapası olmayan, ilk çekimi bitmiş bağlantıya çapa (<see cref="AnchorFor"/>) yazılır; çapası olan HER bağlantıda
/// (çekim işinin yazdığı dahil) çapadan eski, kararsız, IBAN öğretmemiş ve gap çözmemiş hareketler silinir.</item>
/// <item><c>CounterpartyTaxIdHash</c> boşaltılır.</item>
/// <item>Ham kayıt beyaz listeye indirilir (<see cref="BankRawJsonRedactor"/>); okunamayan ham kayıt boşaltılır.</item>
/// <item>Açıklamadaki IBAN ve 11 haneli numara maskelenir (<see cref="BankDescriptionMasker"/>).</item>
/// <item>IBAN maskesi biçiminde olmayan eski maske (ham kısa değer, <c>1234…890</c>) <see cref="BankHasher.ShortValueMask"/>
/// olur (<see cref="NeedsRemask"/>; KVKK Ö-6).</item>
/// </list>
/// Yeniden koşu idempotent. Partiler <see cref="BatchSize"/>'lık; eşzamanlı bir eşleştirme yazısıyla çakışan silme partisi geri
/// alınır, <see cref="Report.FailedBatches"/> artar ve o adım biter — komut yeniden çalıştırılır. Maskeleme adımları
/// oku-değiştir-yaz yapar (<c>BankTransaction</c>'da jeton yok): saklama işi ya da bir silme sürerken koşturulmaz. Bağlantının
/// <c>UpdatedAt</c>'i BİLEREK değişmez (çekim işinin kimlik denetimi). Günlüğe yalnız sayılar yazılır.
/// <para><b>Kuru koşunun sayıları uygulamanınkiler.</b> Uygulama silme adımlarını önce koşar; silinen satırın VKN kodu, ham
/// kaydı, açıklaması ve maskesi sonraki adımlara hiç gelmez. Kuru koşu silmediği için bu satırları
/// <see cref="_dryRunDeleted"/>'da işaretler ve sonraki adımlarda saymaz: tutanağa önce kuru koşunun çıktısı girer, uygulamanın
/// çıktısıyla aynı olmalı (eşzamanlı yazı yoksa).</para>
/// </summary>
public sealed class BankDataCleanupJob
{
    public const int BatchSize = 500;

    public sealed class Report
    {
        public int OutgoingOrZeroDeleted { get; set; }
        public int OutgoingOrZeroSkippedDecided { get; set; }
        public int AnchorsSet { get; set; }
        public int PreAnchorDeleted { get; set; }
        public int PreAnchorSkippedLinked { get; set; }
        public int MatchesDeleted { get; set; }
        public int TaxIdHashesCleared { get; set; }
        public int RawJsonRewritten { get; set; }
        public int RawJsonUnreadableCleared { get; set; }
        public int DescriptionsMasked { get; set; }
        public int ShortMasksReplaced { get; set; }
        public int FailedBatches { get; set; }
    }

    private readonly LicenseDbContext _db;
    private readonly ILogger<BankDataCleanupJob> _log;

    /// <summary>Kuru koşuda silme adımlarının seçtiği hareketler (sınıf özeti). Uygulamada boş kalır: silinen satır sorgulardan
    /// zaten düşer.</summary>
    private readonly HashSet<Guid> _dryRunDeleted = new();

    public BankDataCleanupJob(LicenseDbContext db, ILogger<BankDataCleanupJob> log) { _db = db; _log = log; }

    public async Task<Report> RunAsync(Guid? licenseId, bool apply, CancellationToken ct)
    {
        _dryRunDeleted.Clear();
        var r = new Report();
        await DeleteOutgoingOrZeroAsync(licenseId, apply, r, ct);
        await ApplyInitialAnchorAsync(licenseId, apply, r, ct);
        await ClearTaxIdHashesAsync(licenseId, apply, r, ct);
        await RewriteRawJsonAsync(licenseId, apply, r, ct);
        await MaskDescriptionsAsync(licenseId, apply, r, ct);
        await RemaskShortValuesAsync(licenseId, apply, r, ct);
        _log.LogInformation("bank-data-cleanup ({Mode}): giden/sıfır={Outgoing} (atlanan {Skipped}), çapa={Anchors}, " +
            "çapa öncesi={PreAnchor} (bağlı {Linked}), eşleşme={Matches}, VKN kodu={TaxIds}, ham kayıt={Raw} (okunamayan {Unreadable}), " +
            "açıklama={Descriptions}, kısa maske={ShortMasks}, düşen parti={Failed}", apply ? "uygula" : "kuru", r.OutgoingOrZeroDeleted,
            r.OutgoingOrZeroSkippedDecided, r.AnchorsSet, r.PreAnchorDeleted, r.PreAnchorSkippedLinked, r.MatchesDeleted,
            r.TaxIdHashesCleared, r.RawJsonRewritten, r.RawJsonUnreadableCleared, r.DescriptionsMasked, r.ShortMasksReplaced,
            r.FailedBatches);
        return r;
    }

    // İmha kaydı eylemleri (Görev 9'daki DataDestruction.Actions ile aynı değerler; Görev 9 onlara bağlar).
    private const string Deleted = "deleted", Nulled = "nulled", Masked = "masked";

    /// <summary>Bir UYGULAMA partisinin imha kaydı (KVKK Ö-13): partinin SaveChanges'inden hemen önce çağrılır, kayıt silmeyle
    /// aynı işlemde yazılır. Kuru koşuda çağrılmaz. P0'da kalıcı imha tablosu yok: yalnız günlüğe (sayılar) yazar, P0 koşusu
    /// tutanakla kayda geçer (Prod adımları); Görev 9 gövdeyi <c>DataDestruction.Record</c>'a bağlar (tetikleyici
    /// <c>cleanup</c>, yapan <c>cli</c>).</summary>
    private void RecordBatch(Guid? licenseId, string target, string action, int count, string criterion)
    {
        if (count > 0)
            _log.LogDebug("bank-data-cleanup partisi: {Target} {Action} {Count} ({Criterion}), lisans={LicenseId}",
                target, action, count, criterion, licenseId);
    }

    /// <summary>İlk çekimin TR takvim günü − <see cref="ObifinPollJob.InitialGraceDays"/> (çekim işiyle aynı kural).</summary>
    public static DateOnly AnchorFor(DateTimeOffset backfillCompletedAt)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(backfillCompletedAt, ObifinConnectionService.TrZone).DateTime)
            .AddDays(-ObifinPollJob.InitialGraceDays);

    private IQueryable<BankTransaction> Transactions(Guid? licenseId)
        => licenseId is { } l ? _db.BankTransactions.Where(t => t.LicenseId == l) : _db.BankTransactions;

    /// <summary>Kuru koşuda sonraki adımların gezeceği kimlikler: uygulamanın önceki adımlarda sileceği satırlar çıkarılır.</summary>
    private List<Guid> NotDeletedInDryRun(IEnumerable<Guid> ids) => ids.Where(id => !_dryRunDeleted.Contains(id)).ToList();

    /// <summary>İnsan kararına bağlı hareketler: karar anı, dekont ya da gerçek müşteri dolu.</summary>
    private IQueryable<Guid> DecidedTransactionIds()
        => _db.PaymentMatches.Where(m => m.DecidedAt != null || m.PaymentId != null || m.ActualWpfCustomerId != null)
            .Select(m => m.BankTransactionId);

    private async Task DeleteOutgoingOrZeroAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var decided = DecidedTransactionIds();
        var candidates = Transactions(licenseId)
            .Where(t => t.Direction == BankTransactionDirection.Outgoing || t.Amount == 0);
        r.OutgoingOrZeroSkippedDecided = await candidates.CountAsync(t => decided.Contains(t.Id), ct);
        var deletable = candidates.Where(t => !decided.Contains(t.Id));
        if (!apply)
        {
            var (wouldDelete, wouldDeleteMatches) = await CountDryRunDeletionAsync(deletable, ct);
            r.OutgoingOrZeroDeleted = wouldDelete;
            r.MatchesDeleted += wouldDeleteMatches;
            return;
        }
        var (deleted, matches, completed) = await DeleteInBatchesAsync(deletable, licenseId, "giden ya da sıfır tutarlı (K-4)", ct);
        r.OutgoingOrZeroDeleted = deleted;
        r.MatchesDeleted += matches;
        if (!completed) r.FailedBatches++;
    }

    /// <summary>Çapa yalnız çapası olmayan, ilk çekimi bitmiş bağlantıya (eski kodun 90 günlük ilk çekimi) yazılır. Çapadan eski
    /// kararsız hareketler ise çapası olan HER bağlantıda silinir: P0 anında eski kodun ilk çekimi yarıda kalmış bir bağlantıya
    /// yeni çekim işi ilk turda çapa yazar (<c>BackfillCompletedAt</c> o an boş); sayfa sayfa kaydedilmiş eski satırları yalnız
    /// bu adım siler. Çapası da ilk çekimi de olmayan bağlantı atlanır (çapası bilinemez). Yeniden koşu idempotent: çapa
    /// yazılmıştır, silinecek satır kalmamıştır.</summary>
    private async Task ApplyInitialAnchorAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var connections = _db.ObifinConnections.AsNoTracking()
            .Where(c => c.InitialCollectFrom != null || c.BackfillCompletedAt != null);
        if (licenseId is { } l) connections = connections.Where(c => c.LicenseId == l);
        foreach (var c in await connections.Select(c => new { c.Id, c.LicenseId, c.InitialCollectFrom, c.BackfillCompletedAt })
                     .ToListAsync(ct))
        {
            var anchor = c.InitialCollectFrom ?? AnchorFor(c.BackfillCompletedAt!.Value);
            if (c.InitialCollectFrom is null)
            {
                if (apply)
                {
                    // Önce çapa: silme partisi geri alınırsa yeniden koşu bu bağlantıyı yazılmış çapasıyla yeniden dener. Satır
                    // taze okunur: arada kimlik değiştiyse (UpsertAsync çapayı ve ilk çekimi boşaltır, gölge veriyi siler) eski
                    // ilk çekimin çapası yeni kimliğe yazılmaz — çekim işi onu ilk tur sanıp bugünden eskisini isterdi.
                    var conn = await _db.ObifinConnections.SingleOrDefaultAsync(x => x.Id == c.Id && x.InitialCollectFrom == null
                        && x.BackfillCompletedAt == c.BackfillCompletedAt, ct);
                    if (conn is null)
                    {
                        _db.ChangeTracker.Clear();
                        r.FailedBatches++; // eşzamanlı değişiklik: yeniden koşu bağlantının güncel hâline bakar
                        continue;
                    }
                    conn.InitialCollectFrom = anchor; // UpdatedAt BİLEREK değişmez (sınıf özeti)
                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                }
                r.AnchorsSet++;
            }
            var anchorUtc = ObifinConnectionService.TrToUtc(anchor.ToDateTime(TimeOnly.MinValue));
            var decided = DecidedTransactionIds();
            var taught = _db.CustomerIbanMemories.Where(m => m.SourceBankTransactionId != null)
                .Select(m => m.SourceBankTransactionId!.Value);
            var resolved = _db.PaymentMatchGaps.Where(g => g.ResolvedBankTransactionId != null)
                .Select(g => g.ResolvedBankTransactionId!.Value);
            var before = _db.BankTransactions.Where(t => t.LicenseId == c.LicenseId && t.OccurredAt < anchorUtc);
            var deletable = before.Where(t => !decided.Contains(t.Id) && !taught.Contains(t.Id) && !resolved.Contains(t.Id));
            if (!apply)
            {
                // Bağlı kalan, giden/sıfır adımının sileceği satırları saymaz (uygulamada o satırlar burada yoktur).
                var beforeCount = NotDeletedInDryRun(await before.Select(t => t.Id).ToListAsync(ct)).Count;
                var (wouldDelete, wouldDeleteMatches) = await CountDryRunDeletionAsync(deletable, ct);
                r.PreAnchorDeleted += wouldDelete;
                r.PreAnchorSkippedLinked += beforeCount - wouldDelete;
                r.MatchesDeleted += wouldDeleteMatches;
                continue;
            }
            var deletableCount = await deletable.CountAsync(ct);
            r.PreAnchorSkippedLinked += await before.CountAsync(ct) - deletableCount;
            var (deleted, matches, completed) = await DeleteInBatchesAsync(deletable, c.LicenseId, "ilk çekim çapasından eski (Ö-5)", ct);
            r.PreAnchorDeleted += deleted;
            r.MatchesDeleted += matches;
            if (!completed) r.FailedBatches++; // çapa yazıldı: yeniden koşu aynı bağlantının kalanını siler
        }
    }

    /// <summary>Kuru koşu: sorgunun seçtiği ve önceki bir adımın henüz saymadığı hareketleri ve onların eşleşme satırlarını sayar;
    /// kimlikleri sonraki adımlar için <see cref="_dryRunDeleted"/>'a işaretler.</summary>
    private async Task<(int Transactions, int Matches)> CountDryRunDeletionAsync(IQueryable<BankTransaction> query,
        CancellationToken ct)
    {
        var ids = NotDeletedInDryRun(await query.Select(t => t.Id).ToListAsync(ct));
        var matches = 0;
        foreach (var chunk in ids.Chunk(BatchSize))
            matches += await _db.PaymentMatches.CountAsync(m => chunk.Contains(m.BankTransactionId), ct);
        foreach (var id in ids) _dryRunDeleted.Add(id);
        return (ids.Count, matches);
    }

    /// <summary>Sorgunun seçtiği hareketleri eşleşme satırlarıyla partiler hâlinde siler. Silinen satır sorgudan düşer: her
    /// parti baştan sorgulanır. Eşzamanlı eşleştirme yazısı (jeton) partiyi geri aldırır: Completed = false.</summary>
    private async Task<(int Transactions, int Matches, bool Completed)> DeleteInBatchesAsync(
        IQueryable<BankTransaction> query, Guid? licenseId, string criterion, CancellationToken ct)
    {
        int transactions = 0, matches = 0;
        while (true)
        {
            var batch = await query.OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return (transactions, matches, true);
            var ids = batch.Select(t => t.Id).ToList();
            var rows = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
            _db.PaymentMatches.RemoveRange(rows);
            _db.BankTransactions.RemoveRange(batch);
            RecordBatch(licenseId, "BankTransactions", Deleted, batch.Count, criterion);
            RecordBatch(licenseId, "PaymentMatches", Deleted, rows.Count, criterion);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                return (transactions, matches, false);
            }
            _db.ChangeTracker.Clear();
            transactions += batch.Count;
            matches += rows.Count;
            if (batch.Count < BatchSize) return (transactions, matches, true);
        }
    }

    private async Task ClearTaxIdHashesAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var query = Transactions(licenseId).Where(t => t.CounterpartyTaxIdHash != null);
        if (!apply)
        {
            r.TaxIdHashesCleared = NotDeletedInDryRun(await query.Select(t => t.Id).ToListAsync(ct)).Count;
            return;
        }
        while (true)
        {
            var batch = await query.OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return;
            foreach (var t in batch) t.CounterpartyTaxIdHash = null;
            RecordBatch(licenseId, "BankTransactions.CounterpartyTaxIdHash", Nulled, batch.Count, "VKN/TCKN kodu amaçsız (Ö-2)");
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            r.TaxIdHashesCleared += batch.Count;
            if (batch.Count < BatchSize) return;
        }
    }

    /// <summary>Kimlik anlık görüntüsüyle gezilir: yeniden yazılan satır sorgudan düşmediği için "baştan sorgula" kalıbı
    /// burada döngüye girerdi.</summary>
    private async Task RewriteRawJsonAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var ids = NotDeletedInDryRun(await Transactions(licenseId).Where(t => t.RawJson != null).OrderBy(t => t.Id)
            .Select(t => t.Id).ToListAsync(ct));
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id) && t.RawJson != null).ToListAsync(ct);
            int rewrittenHere = 0, unreadableHere = 0;
            foreach (var t in rows)
            {
                string? rewritten;
                try
                {
                    rewritten = BankRawJsonRedactor.Redact(t.RawJson!);
                }
                catch (ObifinProtocolException)
                {
                    // Okunamayan ham kayıt tanıya da yaramaz: boşaltılır.
                    unreadableHere++;
                    t.RawJson = null;
                    continue;
                }
                if (rewritten == t.RawJson) continue;
                rewrittenHere++;
                t.RawJson = rewritten;
            }
            r.RawJsonRewritten += rewrittenHere;
            r.RawJsonUnreadableCleared += unreadableHere;
            if (apply && rewrittenHere + unreadableHere > 0)
            {
                RecordBatch(licenseId, "BankTransactions.RawJson", Masked, rewrittenHere, "ham kayıt beyaz listesi (Ö-2)");
                RecordBatch(licenseId, "BankTransactions.RawJson", Nulled, unreadableHere, "okunamayan ham kayıt");
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }
    }

    private async Task MaskDescriptionsAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var ids = NotDeletedInDryRun(await Transactions(licenseId).Where(t => t.Description != null).OrderBy(t => t.Id)
            .Select(t => t.Id).ToListAsync(ct));
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id) && t.Description != null).ToListAsync(ct);
            var maskedHere = 0;
            foreach (var t in rows)
            {
                var masked = BankDescriptionMasker.Mask(t.Description);
                if (masked == t.Description) continue;
                maskedHere++;
                t.Description = masked;
            }
            r.DescriptionsMasked += maskedHere;
            if (apply && maskedHere > 0)
            {
                RecordBatch(licenseId, "BankTransactions.Description", Masked, maskedHere, "açıklamada IBAN/11 haneli numara (Ö-16)");
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Saklı maske IBAN maskesi biçiminde (<c>AA00…XXX</c>, <see cref="BankHasher.MaskIban"/>) değil mi? Eski
    /// <c>MaskIban</c>'ın ham kısa değeri ve <c>1234…890</c> biçimi evet. Yer tutucu "?", boş ve
    /// <see cref="BankHasher.ShortValueMask"/> hayır (dokunulmaz).</summary>
    public static bool NeedsRemask(string? masked)
    {
        if (string.IsNullOrEmpty(masked) || masked == "?" || masked == BankHasher.ShortValueMask) return false;
        var parts = masked.Split('…');
        var ibanStyle = parts.Length == 2 && parts[0].Length == 4 && parts[1].Length == 3
            && char.IsAsciiLetterUpper(parts[0][0]) && char.IsAsciiLetterUpper(parts[0][1])
            && char.IsAsciiDigit(parts[0][2]) && char.IsAsciiDigit(parts[0][3])
            && parts[1].All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
        return !ibanStyle;
    }

    /// <summary>KVKK Ö-6: üç tablodaki eski biçimli ya da kısa maskeyi <see cref="BankHasher.ShortValueMask"/>'a çevirir. Maske
    /// SQL'de süzülemediği için hareketler kimlik anlık görüntüsüyle partiler hâlinde, IBAN hafızası ve hesaplar (küçük tablolar)
    /// tek seferde gezilir.</summary>
    private async Task RemaskShortValuesAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        const string criterion = "eski kısa ya da hesap numarası maskesi (Ö-6)";
        var ids = NotDeletedInDryRun(await Transactions(licenseId).Where(t => t.CounterpartyIbanMasked != null).OrderBy(t => t.Id)
            .Select(t => t.Id).ToListAsync(ct));
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id)).ToListAsync(ct);
            var changed = 0;
            foreach (var t in rows.Where(t => NeedsRemask(t.CounterpartyIbanMasked)))
            {
                t.CounterpartyIbanMasked = BankHasher.ShortValueMask;
                changed++;
            }
            r.ShortMasksReplaced += changed;
            if (apply && changed > 0)
            {
                RecordBatch(licenseId, "BankTransactions.CounterpartyIbanMasked", Masked, changed, criterion);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }

        var memories = await (licenseId is { } ml ? _db.CustomerIbanMemories.Where(m => m.LicenseId == ml) : _db.CustomerIbanMemories)
            .ToListAsync(ct);
        var accounts = await (licenseId is { } al ? _db.BankAccounts.Where(a => a.LicenseId == al) : _db.BankAccounts)
            .ToListAsync(ct);
        var memoriesChanged = 0;
        foreach (var m in memories.Where(m => NeedsRemask(m.IbanMasked)))
        {
            m.IbanMasked = BankHasher.ShortValueMask;
            memoriesChanged++;
        }
        var accountsChanged = 0;
        foreach (var a in accounts.Where(a => NeedsRemask(a.IbanMasked)))
        {
            a.IbanMasked = BankHasher.ShortValueMask;
            accountsChanged++;
        }
        r.ShortMasksReplaced += memoriesChanged + accountsChanged;
        if (apply && memoriesChanged + accountsChanged > 0)
        {
            RecordBatch(licenseId, "CustomerIbanMemories.IbanMasked", Masked, memoriesChanged, criterion);
            RecordBatch(licenseId, "BankAccounts.IbanMasked", Masked, accountsChanged, criterion);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }
}
