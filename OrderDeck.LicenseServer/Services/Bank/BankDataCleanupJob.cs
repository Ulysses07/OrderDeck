using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

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
/// <item>Maskesi maskeli ya da geçersiz kaynağı ele veren IBAN özeti (<see cref="LooksMasked"/>) aynı özeti taşıyan bütün
/// hareketlerde boşaltılır, o özetle öğrenilmiş IBAN bağı silinir (KVKK Ö-7).</item>
/// <item><c>--reset-iban-memory</c> ile özeti aynı lisansta geçerli IBAN'lı bir harekette görünmeyen bütün IBAN bağları silinir
/// (görünen bağ kanıtlanmış biçimde geçerli IBAN'dandır, kalır); hareket özetleri KALIR (KVKK Ö-7, not karar 5).</item>
/// <item>P1'den önce öğrenilmiş IBAN bağının saklama başlangıcı öğreten havalenin işlem tarihine çekilir (öğrenme anından
/// hiçbir zaman sonra değil; KVKK K-6). Silme değildir; önceki iki adımın sildiği bağa yazılmaz.</item>
/// </list>
/// Yeniden koşu idempotent. Partiler <see cref="BatchSize"/>'lık; eşzamanlı bir eşleştirme yazısıyla çakışan silme partisi geri
/// alınır, <see cref="Report.FailedBatches"/> artar ve o adım biter — komut yeniden çalıştırılır. Maskeleme adımları
/// oku-değiştir-yaz yapar (<c>BankTransaction</c>'da jeton yok): saklama işi ya da bir silme sürerken koşturulmaz. Bağlantının
/// <c>UpdatedAt</c>'i BİLEREK değişmez (çekim işinin kimlik denetimi). Günlüğe yalnız sayılar yazılır.
/// <para><b>Kuru koşunun sayıları uygulamanınkiler.</b> Uygulama silme adımlarını önce koşar; silinen satırın VKN kodu, ham
/// kaydı, açıklaması, maskesi ve özeti sonraki adımlara hiç gelmez. Kuru koşu silmediği için bu satırları
/// <see cref="_dryRunDeleted"/>'da (silinecek IBAN bağlarını <see cref="_dryRunDeletedMemories"/>'de) işaretler ve sonraki
/// adımlarda saymaz: tutanağa önce kuru koşunun çıktısı girer, uygulamanın çıktısıyla aynı olmalı (eşzamanlı yazı yoksa).</para>
/// Her uygulama partisi kalıcı imha kaydını satırın lisansı başına, aynı işlemde yazar (tetikleyici cleanup, yapan cli; KVKK
/// Ö-13).
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
        public int MaskedSourceHashesCleared { get; set; }
        public int MaskedSourceMemoriesDeleted { get; set; }
        public int IbanMemoriesReset { get; set; }
        /// <summary>Sıfırlamada kalan bağ: özeti aynı lisansta geçerli IBAN'lı bir harekette görünüyor
        /// (<see cref="ResetIbanMemoryAsync"/>). Sıfırlama istenmediyse 0.</summary>
        public int IbanMemoriesKeptValid { get; set; }
        public int IbanConfirmationsBackfilled { get; set; }
        public int FailedBatches { get; set; }
    }

    private readonly LicenseDbContext _db;
    private readonly ILogger<BankDataCleanupJob> _log;

    /// <summary>Kuru koşuda silme adımlarının seçtiği hareketler (sınıf özeti). Uygulamada boş kalır: silinen satır sorgulardan
    /// zaten düşer.</summary>
    private readonly HashSet<Guid> _dryRunDeleted = new();

    /// <summary>Kuru koşuda silinecek IBAN bağları (maskeli kaynaklı ya da sıfırlama); sonraki adım (başlangıç yazımı) onları
    /// saymaz. Uygulamada boş kalır.</summary>
    private readonly HashSet<Guid> _dryRunDeletedMemories = new();

    public BankDataCleanupJob(LicenseDbContext db, ILogger<BankDataCleanupJob> log) { _db = db; _log = log; }

    /// <param name="resetIbanMemory">KVKK Ö-7, not karar 5: maskeden anlaşılamayan maskeli kaynaklı özetler için IBAN hafızasını
    /// siler; geçerli IBAN'lı bir harekette görünen bağ kalır (<see cref="ResetIbanMemoryAsync"/>). Hareketlerdeki özetler KALIR (IBAN talep aracı onlarla arar, K-7); yeniden öğrenmeyi
    /// <see cref="BankTransaction.CounterpartyIbanHashValid"/> kapısı engeller.</param>
    public async Task<Report> RunAsync(Guid? licenseId, bool apply, CancellationToken ct, bool resetIbanMemory = false)
    {
        _dryRunDeleted.Clear();
        _dryRunDeletedMemories.Clear();
        var r = new Report();
        await DeleteOutgoingOrZeroAsync(licenseId, apply, r, ct);
        await ApplyInitialAnchorAsync(licenseId, apply, r, ct);
        await ClearTaxIdHashesAsync(licenseId, apply, r, ct);
        await RewriteRawJsonAsync(licenseId, apply, r, ct);
        await MaskDescriptionsAsync(licenseId, apply, r, ct);
        await RemaskShortValuesAsync(licenseId, apply, r, ct);
        await ClearMaskedSourceHashesAsync(licenseId, apply, r, ct);
        if (resetIbanMemory) await ResetIbanMemoryAsync(licenseId, apply, r, ct);
        await BackfillIbanConfirmationAsync(licenseId, apply, r, ct);
        _log.LogInformation("bank-data-cleanup ({Mode}): giden/sıfır={Outgoing} (atlanan {Skipped}), çapa={Anchors}, " +
            "çapa öncesi={PreAnchor} (bağlı {Linked}), eşleşme={Matches}, VKN kodu={TaxIds}, ham kayıt={Raw} (okunamayan {Unreadable}), " +
            "açıklama={Descriptions}, kısa maske={ShortMasks}, maskeli özet={MaskedHashes} (hafıza {MaskedMemories}), " +
            "sıfırlanan hafıza={ResetMemories} (geçerli kalan {KeptMemories}), IBAN bağı başlangıcı={IbanStarts}, " +
            "düşen parti={Failed}",
            apply ? "uygula" : "kuru", r.OutgoingOrZeroDeleted, r.OutgoingOrZeroSkippedDecided, r.AnchorsSet, r.PreAnchorDeleted,
            r.PreAnchorSkippedLinked, r.MatchesDeleted, r.TaxIdHashesCleared, r.RawJsonRewritten, r.RawJsonUnreadableCleared,
            r.DescriptionsMasked, r.ShortMasksReplaced, r.MaskedSourceHashesCleared, r.MaskedSourceMemoriesDeleted,
            r.IbanMemoriesReset, r.IbanMemoriesKeptValid, r.IbanConfirmationsBackfilled, r.FailedBatches);
        return r;
    }

    private const string Deleted = DataDestruction.Actions.Deleted, Nulled = DataDestruction.Actions.Nulled,
        Masked = DataDestruction.Actions.Masked;

    /// <summary>Bir UYGULAMA partisinin kalıcı imha kaydı (KVKK Ö-13): partinin SaveChanges'inden hemen önce çağrılır, kayıt
    /// silmeyle aynı işlemde yazılır (parti eşzamanlılıkla geri alınırsa kayıt da düşer). Kuru koşuda çağrılmaz.
    /// <paramref name="rowLicenses"/> etkilenen her satırın lisansıdır: kayıt satırın lisansı başına yazılır, <c>--license</c>
    /// verilmeden koşan parti lisansları karıştırsa da (yayıncı kendi verisinin imha kanıtını isteyebilir).</summary>
    private void RecordBatch(IEnumerable<Guid> rowLicenses, string target, string action, string criterion)
    {
        foreach (var license in rowLicenses.GroupBy(l => l))
            DataDestruction.Record(_db, license.Key, target, action, license.Count(), criterion, DataDestruction.Triggers.Cleanup,
                DataDestruction.CliActor);
    }

    /// <summary>İlk çekimin TR takvim günü − <see cref="ObifinPollJob.InitialGraceDays"/> (çekim işiyle aynı kural).</summary>
    public static DateOnly AnchorFor(DateTimeOffset backfillCompletedAt)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(backfillCompletedAt, ObifinConnectionService.TrZone).DateTime)
            .AddDays(-ObifinPollJob.InitialGraceDays);

    private IQueryable<BankTransaction> Transactions(Guid? licenseId)
        => licenseId is { } l ? _db.BankTransactions.Where(t => t.LicenseId == l) : _db.BankTransactions;

    private IQueryable<CustomerIbanMemory> Memories(Guid? licenseId)
        => licenseId is { } l ? _db.CustomerIbanMemories.Where(m => m.LicenseId == l) : _db.CustomerIbanMemories;

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
        var (deleted, matches, completed) = await DeleteInBatchesAsync(deletable, "giden ya da sıfır tutarlı (K-4)", ct);
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
            var (deleted, matches, completed) = await DeleteInBatchesAsync(deletable, "ilk çekim çapasından eski (Ö-5)", ct);
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
        IQueryable<BankTransaction> query, string criterion, CancellationToken ct)
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
            RecordBatch(batch.Select(t => t.LicenseId), "BankTransactions", Deleted, criterion);
            RecordBatch(rows.Select(m => m.LicenseId), "PaymentMatches", Deleted, criterion);
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
            RecordBatch(batch.Select(t => t.LicenseId), "BankTransactions.CounterpartyTaxIdHash", Nulled, "VKN/TCKN kodu amaçsız (Ö-2)");
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
            List<Guid> rewrittenHere = new(), unreadableHere = new();
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
                    unreadableHere.Add(t.LicenseId);
                    t.RawJson = null;
                    continue;
                }
                if (rewritten == t.RawJson) continue;
                rewrittenHere.Add(t.LicenseId);
                t.RawJson = rewritten;
            }
            r.RawJsonRewritten += rewrittenHere.Count;
            r.RawJsonUnreadableCleared += unreadableHere.Count;
            if (apply && rewrittenHere.Count + unreadableHere.Count > 0)
            {
                RecordBatch(rewrittenHere, "BankTransactions.RawJson", Masked, "ham kayıt beyaz listesi (Ö-2)");
                RecordBatch(unreadableHere, "BankTransactions.RawJson", Nulled, "okunamayan ham kayıt");
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
            var maskedHere = new List<Guid>();
            foreach (var t in rows)
            {
                var masked = BankDescriptionMasker.Mask(t.Description);
                if (masked == t.Description) continue;
                maskedHere.Add(t.LicenseId);
                t.Description = masked;
            }
            r.DescriptionsMasked += maskedHere.Count;
            if (apply && maskedHere.Count > 0)
            {
                RecordBatch(maskedHere, "BankTransactions.Description", Masked, "açıklamada IBAN/11 haneli numara (Ö-16)");
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
            var changed = new List<Guid>();
            foreach (var t in rows.Where(t => NeedsRemask(t.CounterpartyIbanMasked)))
            {
                t.CounterpartyIbanMasked = BankHasher.ShortValueMask;
                changed.Add(t.LicenseId);
            }
            r.ShortMasksReplaced += changed.Count;
            if (apply && changed.Count > 0)
            {
                RecordBatch(changed, "BankTransactions.CounterpartyIbanMasked", Masked, criterion);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
        }

        var memories = await (licenseId is { } ml ? _db.CustomerIbanMemories.Where(m => m.LicenseId == ml) : _db.CustomerIbanMemories)
            .ToListAsync(ct);
        var accounts = await (licenseId is { } al ? _db.BankAccounts.Where(a => a.LicenseId == al) : _db.BankAccounts)
            .ToListAsync(ct);
        var memoriesChanged = new List<Guid>();
        foreach (var m in memories.Where(m => NeedsRemask(m.IbanMasked)))
        {
            m.IbanMasked = BankHasher.ShortValueMask;
            memoriesChanged.Add(m.LicenseId);
        }
        var accountsChanged = new List<Guid>();
        foreach (var a in accounts.Where(a => NeedsRemask(a.IbanMasked)))
        {
            a.IbanMasked = BankHasher.ShortValueMask;
            accountsChanged.Add(a.LicenseId);
        }
        r.ShortMasksReplaced += memoriesChanged.Count + accountsChanged.Count;
        if (apply && memoriesChanged.Count + accountsChanged.Count > 0)
        {
            RecordBatch(memoriesChanged, "CustomerIbanMemories.IbanMasked", Masked, criterion);
            RecordBatch(accountsChanged, "BankAccounts.IbanMasked", Masked, criterion);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
    }

    /// <summary>Saklı maske (<c>XXXX…YYY</c>, <see cref="BankHasher.MaskIban"/>) maskeli ya da IBAN olmayan bir değerden mi
    /// üretilmiş? Biçim tutmuyorsa (eski ham kısa değer, <c>1234…890</c>, <see cref="BankHasher.ShortValueMask"/>), görünen yedi
    /// karakterden biri harf/rakam değilse ya da ülke + kontrol biçimi yoksa evet. Boş ve yer tutucu "?" hayır: kaynağı
    /// bilinmez. Görünen karakterleri sağlam olan maskeli değer buradan anlaşılamaz: o yüzden <c>--reset-iban-memory</c> var.
    /// <para><see cref="NeedsRemask"/>'tan türetilir, biçim denetimi tek yerde: ikisi yalnız <see cref="BankHasher.ShortValueMask"/>'ta
    /// ayrılır (yeniden maskelenmez, ama IBAN olmayan değerden). Kuru koşu ile uygulamanın aynı sayıları vermesi buna dayanır:
    /// uygulamada önceki adım eski maskeyi <see cref="BankHasher.ShortValueMask"/>'a çevirir, kuru koşuda eski maske kalır;
    /// ikisi de burada evet.</para></summary>
    public static bool LooksMasked(string? masked)
        => masked == BankHasher.ShortValueMask || NeedsRemask(masked);

    /// <summary>KVKK Ö-7: eski kod 8+ karakterlik her değeri özetliyordu (maskeli IBAN, hesap numarası): aynı ilk ve son
    /// karakterleri taşıyan farklı gönderenler aynı özete düşer, IBAN bağı farklı kişileri tek müşteriye bağlar. Şüpheli
    /// (lisans, özet) çifti: o özeti taşıyan bir hareketin ya da bağın maskesi maskeli kaynağı ele veriyor
    /// (<see cref="LooksMasked"/>). Bağın kendi maskesi de sayılır: bağ 12 ay, öğreten havale 180 gün yaşar; öğreten havalesi
    /// silinmiş maskeli kaynaklı bağ yalnız kendi maskesinden tanınır. Önce bağlar silinir, sonra hareketlerin özeti boşaltılır:
    /// bağ partisi düşerse yeniden koşu şüpheyi yine hareketlerden ve bağın maskesinden bulur. Maskeli bir değerin özeti gerçek
    /// bir IBAN'ın özetine eşit olamaz: boşaltmak IBAN talep aramasından bir şey kaybettirmez (talep gerçek IBAN'ın özetiyle
    /// arar). Maske kalır: değerin geldiği görünür, hiçbir karakteri IBAN'ı göstermez (kısa ve eski biçimli maskeyi bir önceki
    /// adım çevirdi, Ö-6). Kayıt satırın lisansı başına (Görev 9 incelemesi).</summary>
    private async Task ClearMaskedSourceHashesAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        const string criterion = "maskeli kaynaklı özet (Ö-7)";
        var hashed = (await Transactions(licenseId).AsNoTracking().Where(t => t.CounterpartyIbanHash != null)
                .Select(t => new { t.Id, t.LicenseId, Hash = t.CounterpartyIbanHash!, t.CounterpartyIbanMasked }).ToListAsync(ct))
            .Where(x => !_dryRunDeleted.Contains(x.Id)).ToList();
        var memories = await Memories(licenseId).AsNoTracking()
            .Select(m => new { m.Id, m.LicenseId, m.IbanHash, m.IbanMasked }).ToListAsync(ct);
        var suspects = hashed.Where(x => LooksMasked(x.CounterpartyIbanMasked)).Select(x => (x.LicenseId, x.Hash))
            .Concat(memories.Where(m => LooksMasked(m.IbanMasked)).Select(m => (m.LicenseId, Hash: m.IbanHash)))
            .ToHashSet();
        if (suspects.Count == 0) return;
        var memoryIds = memories.Where(m => suspects.Contains((m.LicenseId, m.IbanHash))).Select(m => m.Id).ToList();
        var transactionIds = hashed.Where(x => suspects.Contains((x.LicenseId, x.Hash))).Select(x => x.Id).ToList();
        if (!apply)
        {
            r.MaskedSourceMemoriesDeleted = memoryIds.Count;
            r.MaskedSourceHashesCleared = transactionIds.Count;
            foreach (var id in memoryIds) _dryRunDeletedMemories.Add(id);
            return;
        }
        foreach (var chunk in memoryIds.Chunk(BatchSize))
        {
            var rows = await _db.CustomerIbanMemories.Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
            _db.CustomerIbanMemories.RemoveRange(rows);
            RecordBatch(rows.Select(m => m.LicenseId), "CustomerIbanMemories", Deleted, criterion);
            if (!await TrySaveDeletionAsync(ct))
            {
                r.FailedBatches++;
                return;
            }
            r.MaskedSourceMemoriesDeleted += rows.Count;
        }
        foreach (var chunk in transactionIds.Chunk(BatchSize))
        {
            var rows = await _db.BankTransactions.Where(t => chunk.Contains(t.Id) && t.CounterpartyIbanHash != null).ToListAsync(ct);
            foreach (var t in rows)
            {
                t.CounterpartyIbanHash = null;
                t.CounterpartyIbanHashValid = false; // özetsiz hareket öğretemez (değişmez: eski satırda zaten false)
            }
            if (rows.Count > 0)
            {
                RecordBatch(rows.Select(t => t.LicenseId), "BankTransactions.CounterpartyIbanHash", Nulled, criterion);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
            r.MaskedSourceHashesCleared += rows.Count;
        }
    }

    /// <summary>KVKK Ö-7, not karar 5: özeti aynı lisansta geçerli IBAN'lı bir harekette
    /// (<see cref="BankTransaction.CounterpartyIbanHashValid"/>) görünmeyen bütün IBAN bağları silinir — görünen karakterleri
    /// sağlam maskeli değer <see cref="LooksMasked"/>'tan kaçar, eski özetin kaynağı saklı veriden tam anlaşılamaz.
    /// <para><b>Kalan bağ.</b> Maskeli bir değerin özeti geçerli bir IBAN'ın özetine eşit olamaz (bayrak yalnız
    /// <see cref="BankHasher.NormalizeIban"/>'ı geçen değerde yazılır): bayraklı harekette görünen özetin bağı kanıtlanmış
    /// biçimde geçerli IBAN'dandır. Görev 11'den sonra öğrenilmiş bağlar böyledir (<c>LearnIbanAsync</c> yalnız bayraklı
    /// hareketten öğrenir) ve silinselerdi geri gelmezdi: öğreten havalenin kararı verilmiş. Böylece komut ne zaman koşulursa
    /// koşulsun güvenlidir ve idempotenttir. Sınır: kanıt hareketle yaşar (180 gün); o IBAN'dan son 180 günde geçerli havale
    /// gelmemiş bağ silinir (fazlası silinir, KVKK yönünde güvenli). Kanıt yalnız aynı lisansta aranır (<c>--license</c>
    /// kapsamıyla aynı).</para>
    /// Hareketlerdeki özetler BİLEREK KALIR: IBAN talep aracı hareketleri yalnız özetle bulur, boşaltılsaydı 180 güne kadar duran
    /// veri erişim/silme talebinde "yok" görünürdü (K-7). Eski hareketin özeti hafızaya geri giremez: <c>LearnIbanAsync</c>
    /// yalnız bayraklı hareketten öğrenir. Önceki adımın sildiği bağ ve uygulamanın sileceği hareket kuru koşuda sayılmaz
    /// (sınıf özeti). Kayıt satırın lisansı başına.</summary>
    private async Task ResetIbanMemoryAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        const string criterion = "IBAN hafızası sıfırlama (Ö-7, karar 5)";
        var proven = (await Transactions(licenseId).AsNoTracking()
                .Where(t => t.CounterpartyIbanHashValid && t.CounterpartyIbanHash != null)
                .Select(t => new { t.Id, t.LicenseId, Hash = t.CounterpartyIbanHash! }).ToListAsync(ct))
            .Where(x => !_dryRunDeleted.Contains(x.Id))
            .Select(x => (x.LicenseId, x.Hash)).ToHashSet();
        var memories = (await Memories(licenseId).AsNoTracking().Select(m => new { m.Id, m.LicenseId, m.IbanHash }).ToListAsync(ct))
            .Where(m => !_dryRunDeletedMemories.Contains(m.Id)).ToList();
        var ids = memories.Where(m => !proven.Contains((m.LicenseId, m.IbanHash))).Select(m => m.Id).ToList();
        r.IbanMemoriesKeptValid = memories.Count - ids.Count;
        if (!apply)
        {
            r.IbanMemoriesReset = ids.Count;
            foreach (var id in ids) _dryRunDeletedMemories.Add(id);
            return;
        }
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var rows = await _db.CustomerIbanMemories.Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
            if (rows.Count == 0) continue;
            _db.CustomerIbanMemories.RemoveRange(rows);
            RecordBatch(rows.Select(m => m.LicenseId), "CustomerIbanMemories", Deleted, criterion);
            if (!await TrySaveDeletionAsync(ct))
            {
                r.FailedBatches++;
                return;
            }
            r.IbanMemoriesReset += rows.Count;
        }
    }

    /// <summary>Bağ silme partisinin kaydı. Satır okunduktan sonra eşzamanlı bir yol (eşleme kaldırma, saklama işi) onu sildiyse
    /// DELETE satır bulamaz: parti imha kaydıyla birlikte geri alınır, false — adım biter, komut yeniden çalıştırılır (sınıf
    /// özeti).</summary>
    private async Task<bool> TrySaveDeletionAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return false;
        }
        _db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>KVKK K-6: P1'den önce öğrenilmiş IBAN bağının (<see cref="CustomerIbanMemory.LastConfirmedAt"/> boş) saklama
    /// süresi öğrenme anından (<c>CreatedAt</c>) işliyordu. Öğrenme havalenin işlem tarihinden sonra olur (90 günlük ilk çekim,
    /// geç elle eşleme): bağ "son doğrulanmış ödemeden 12 ay"ı ~90 güne kadar aşabilirdi. P1 öncesi kod bağı tazelemiyordu;
    /// öğreten havale (<see cref="CustomerIbanMemory.SourceBankTransactionId"/>) bağın bilinen tek doğrulamasıdır. Duruyorsa
    /// başlangıç onun işlem tarihi olur, öğrenme anına kırpılmış: değer hiçbir zaman <c>CreatedAt</c>'ten sonra değildir, süre
    /// uzamaz (bağdaştırıcının öğrenirken yazdığı değerin aynısı). Öğreten havale yoksa <c>CreatedAt</c> kalır. Öğreten havale 180
    /// günlük kuralla silinmeden koşturulmalı. Veri silinmez, imha kaydına girmez; sayım tutanağa. Oku-değiştir-yaz: arada bağı
    /// tazeleyen bir onay ezilirse başlangıç yalnız daha erkene düşer. Önceki iki adımın sildiği bağ sayılmaz (kuru koşuda
    /// <see cref="_dryRunDeletedMemories"/>).</summary>
    private async Task BackfillIbanConfirmationAsync(Guid? licenseId, bool apply, Report r, CancellationToken ct)
    {
        var memories = licenseId is { } l ? _db.CustomerIbanMemories.Where(m => m.LicenseId == l) : _db.CustomerIbanMemories;
        var targets = (await (from m in memories.AsNoTracking()
                              join t in _db.BankTransactions.AsNoTracking() on m.SourceBankTransactionId equals (Guid?)t.Id
                              where m.LastConfirmedAt == null && t.LicenseId == m.LicenseId
                              select new { m.Id, m.CreatedAt, TransactionId = t.Id, t.OccurredAt })
                .ToListAsync(ct))
            .Where(x => !_dryRunDeleted.Contains(x.TransactionId) && !_dryRunDeletedMemories.Contains(x.Id))
            .ToList();
        if (!apply)
        {
            r.IbanConfirmationsBackfilled = targets.Count;
            return;
        }
        foreach (var chunk in targets.Chunk(BatchSize))
        {
            var start = chunk.ToDictionary(x => x.Id, x => x.OccurredAt < x.CreatedAt ? x.OccurredAt : x.CreatedAt);
            var ids = start.Keys.ToList();
            var rows = await _db.CustomerIbanMemories.Where(m => ids.Contains(m.Id) && m.LastConfirmedAt == null).ToListAsync(ct);
            foreach (var m in rows) m.LastConfirmedAt = start[m.Id];
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            r.IbanConfirmationsBackfilled += rows.Count;
        }
    }
}
