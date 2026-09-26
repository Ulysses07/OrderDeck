using System.Diagnostics.CodeAnalysis;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Obifin'den hareket çekimi (spec §5). Bağlantı başına: ilk çekimde 90 gün 31'lik dilimlerle, sonra
/// `[bugün−30, bugün]` + `BaslangicHareketId` imleci. Yazım idempotent ((LicenseId, ObifinId) tekil); imleç
/// yalnız tam başarılı turda ilerler. Webhook yok — bu iş tek kaynak.
///
/// <para><b>Hata sınıfları.</b> Obifin'in kendi mesajı (<see cref="ObifinApiException"/>, ör. kimlik hatalı)
/// kendiliğinden düzelmez → Failed + LastError, koşu BAŞARILI biter (admin düzeltip yeniden doğrular; Failed
/// bağlantı sonraki koşularda atlanır). Ağ/vekil/zaman aşımı geçicidir → LastError yazılır, durum Verified
/// KALIR, istisna DIŞARI çıkar (Hangfire panosunda Failed; 5 dakika sonraki koşu aynı imleçten dener). İşin
/// kendi iptali hiçbir şey yazmaz. Sınıf dışı hata (DB, eşleştirici…) geçici gibi ele alınır ama LastError'a yalnız tür
/// adı yazılır (<see cref="UnexpectedErrorMessage"/>). Bir bağlantının hatası sıradakileri bekletmez (<see cref="RunAsync"/>).</para>
///
/// <para><b>Kimlik değişimi — eşzamanlılık jetonu BİLEREK yok.</b> Admin çekim sürerken kimliği değiştirirse
/// <see cref="ObifinConnectionService.UpsertAsync"/> imleci sıfırlar, gölge veriyi siler. Eski koşu ne eski
/// hesabın satırlarını ne eski imleci ne eski kimliğin hatasını geri yazmalı: hareket/imleç/hata kaydeden HER
/// SaveChanges'ten önce UserCode/BaseUrl/UpdatedAt taze okunur, biri değiştiyse kaydedilmez.</para>
///
/// <para><b>Ham JSON</b> saklanmadan önce IBAN/VKN/TCKN alanları redakte edilir
/// (<see cref="BankRawJsonRedactor"/>); hash + maske DTO'nun ham değerinden üretilir.</para>
/// </summary>
[DisableConcurrentExecution(LockResource, 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class ObifinPollJob
{
    /// <summary>Sabit kilit adı: zamanlanmış <see cref="RunAsync"/> ile admin'in "Şimdi çek"i
    /// (<see cref="PollConnectionAsync"/>) aynı bağlantıyı aynı anda çekip tekil index'te çarpışmasın. Varsayılan
    /// kaynak adı yöntem başınadır; iki yöntemi birbirinden korumazdı. Saatlik hesap yenileme
    /// (<see cref="ObifinAccountRefreshJob"/>) da AYNI kilidi tutar: başarısı <c>UpdatedAt</c>'i yazar ve kimlik
    /// denetimi eşzamanlı çekimi boşuna iptal ederdi; yer tutucu/gerçek hesap satırı da yarışırdı.</summary>
    public const string LockResource = "obifin-poll";
    public const int BackfillDays = 90;
    public const int WindowDays = 31;
    public const int MaxDrainRounds = 10;
    /// <summary>Tek pencerenin (31 gün) sert sayfa tavanı: 1000'lik sayfayla 100 bin hareket — bu işte anomali. Aşılırsa
    /// <see cref="ObifinProtocolException"/> (geçici sınıf: imleç yerinde, LastError yazılır, koşu Failed). Sayfa sayısı
    /// gelmeyen ve hep dolu, hep yeni Id'li sayfa döndüren bir sunucu kilidi sonsuza dek tutamasın.</summary>
    public const int MaxPagesPerWindow = 100;

    /// <summary>Yer tutucu hesabın maskesi; gerçek maske saatlik yenilemeyle gelir.</summary>
    private const string PlaceholderIbanMask = "?";

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly ObifinConnectionService _connections;
    private readonly BankHasher _hasher;
    private readonly IBankTransactionSink _sink;
    /// <summary><see cref="ObifinOptions.PageSize"/>; ≤ 0 → <see cref="ObifinOptions.DefaultPageSize"/> (bozuk yapılandırma
    /// Obifin'e anlamsız bir sayfa boyutu göndermesin — TimeoutSeconds'taki kalıp).</summary>
    private readonly int _pageSize;
    private readonly ILogger<ObifinPollJob> _log;

    /// <summary>Test için: TR takvim günü.</summary>
    public Func<DateOnly> TodayTr { get; set; } = () =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ObifinConnectionService.TrZone));

    public ObifinPollJob(LicenseDbContext db, IObifinClient client, ObifinConnectionService connections,
        BankHasher hasher, IBankTransactionSink sink, IOptions<ObifinOptions> opt, ILogger<ObifinPollJob> log)
    {
        _db = db; _client = client; _connections = connections; _hasher = hasher; _sink = sink; _log = log;
        _pageSize = opt.Value.PageSize <= 0 ? ObifinOptions.DefaultPageSize : opt.Value.PageSize;
    }

    /// <summary>Tüm Verified bağlantılar. Kiracı yalıtımı: bir bağlantının hatası (geçici ya da beklenmeyen) döngüyü
    /// kesmez — toplanır, sıradakine geçilir, sonda tek <see cref="AggregateException"/> fırlar (Hangfire koşuyu yine
    /// Failed gösterir). Aksi hâlde sürekli düşen bir bağlantı, sırada ondan sonra gelen yayıncıları her koşuda aç
    /// bırakırdı. Yalnız işin kendi iptali döngüyü hemen keser.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var ids = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Verified)
            .Select(c => c.Id).ToListAsync(ct);
        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try
            {
                await PollConnectionAsync(id, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // Yarım kalmış izleme (kaydedilmemiş satırlar, yer tutucu hesaplar) sıradakinin SaveChanges'ine taşınmasın.
                _db.ChangeTracker.Clear();
                // Sınıflandırılmış istemci hatası PollConnectionAsync'te zaten loglandı; burada yalnız beklenmeyenler.
                if (ObifinConnectionService.DescribeClientFailure(ex, ct) is null)
                    _log.LogError(ex, "Obifin çekimi beklenmeyen hatayla düştü — bağlantı={ConnectionId}; sıradakine geçiliyor", id);
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException($"Obifin çekimi {failures.Count} bağlantıda düştü.", failures);
    }

    /// <summary>Tek bağlantı; admin "Şimdi çek" de bunu kuyruğa atar.</summary>
    public async Task PollConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn is null || conn.Status != ObifinConnectionStatus.Verified) return;
        var creds = _connections.TryResolveCredentials(conn);
        if (creds is null)
        {
            // Anahtar halkası kaybı: Obifin'e gidilmez, admin kimliği yeniden girer.
            conn.Status = ObifinConnectionStatus.Failed; conn.LastError = ObifinConnectionService.UndecryptableMessage;
            conn.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("Obifin çekimi: saklı kimlik çözülemedi — bağlantı={ConnectionId}", connectionId);
            return;
        }
        var identity = new IdentitySnapshot(conn.UserCode, conn.BaseUrl, conn.UpdatedAt);
        var today = TodayTr();
        try
        {
            if (conn.BackfillCompletedAt is null)
            {
                long? maxId = conn.LastObifinTransactionId;
                var start = today.AddDays(-(BackfillDays - 1));
                for (var from = start; from <= today; from = from.AddDays(WindowDays))
                {
                    var to = from.AddDays(WindowDays - 1) > today ? today : from.AddDays(WindowDays - 1);
                    var (seen, _) = await FetchWindowWithMetaAsync(conn, creds, identity, from, to, sinceId: null, ct);
                    if (seen is { } s && (maxId is null || s > maxId)) maxId = s;
                }
                conn.LastObifinTransactionId = maxId;
                conn.BackfillCompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                var from = today.AddDays(-(WindowDays - 1));
                for (var round = 0; round < MaxDrainRounds; round++)
                {
                    var before = conn.LastObifinTransactionId;
                    var (seen, lastPageFull) = await FetchWindowWithMetaAsync(conn, creds, identity, from, today, before, ct);
                    if (seen is { } s && (before is null || s > before)) conn.LastObifinTransactionId = s;
                    if (!lastPageFull || conn.LastObifinTransactionId == before) break;
                }
            }
            var now = DateTimeOffset.UtcNow;
            conn.LastPolledAt = now;
            conn.LastError = null;
            conn.UpdatedAt = now;
            await SaveIfIdentityUnchangedAsync(connectionId, identity, ct);
        }
        catch (IdentityChangedException)
        {
            _db.ChangeTracker.Clear();
            _log.LogWarning("Obifin çekimi: kimlik değişti, çekim iptal — bağlantı={ConnectionId}", connectionId);
        }
        catch (Exception ex) when (ObifinConnectionService.DescribeClientFailure(ex, ct) is { } msg)
        {
            // İmleç DEĞİŞMEDİ: yalnız yukarıdaki başarılı yolda kaydedilir; kaydedilmemiş satırlar atılır.
            // Obifin "hayır" dedi: kendiliğinden düzelmez, admin düzeltir (Failed, koşu başarılı biter). Geçici hata:
            // durum korunur, istisna yukarı gider.
            var refused = ex is ObifinApiException;
            // Uyarı kayıttan ÖNCE: kayıt da düşerse hatanın ne olduğu günlükte kalsın.
            if (refused)
                _log.LogWarning("Obifin çekimi Obifin hatasıyla durdu — bağlantı={ConnectionId}: {Error}", connectionId, msg);
            else
                _log.LogWarning(ex, "Obifin çekimi geçici hatayla düştü — bağlantı={ConnectionId}: {Error}", connectionId, msg);
            // Hata kaydı düşerse (ör. DB o an gitti) asıl istisnanın sınıfı korunur: kayıt hatası yalnız loglanır, Obifin
            // reddi yine koşuyu düşürmez, geçici hata yine KENDİ türüyle yukarı çıkar (sınıf dışı yoldaki kalıp).
            try
            {
                await RecordErrorIfIdentityUnchangedAsync(connectionId, identity, msg, markFailed: refused, ct);
            }
            catch (Exception recordEx) when (!(recordEx is OperationCanceledException && ct.IsCancellationRequested))
            {
                _log.LogError(recordEx, "Obifin çekimi: hata bağlantıya yazılamadı — bağlantı={ConnectionId}", connectionId);
            }
            if (refused) return;
            throw;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Sınıf dışı hata (DB, eşleştirici…): admin ekranı "son hata yok" demesin. Mesaj DEĞİL yalnız tür adı —
            // istisna mesajı veri (IBAN, ad, SQL parametresi) taşıyabilir. Durum çevrilmez: kimlik aleyhine kanıt değil.
            // Hata kaydı da düşerse (ör. DB tamamen gitti) asıl istisna kaybolmasın: kayıt hatası yalnız loglanır.
            try
            {
                await RecordErrorIfIdentityUnchangedAsync(connectionId, identity, UnexpectedErrorMessage(ex), markFailed: false, ct);
            }
            catch (Exception recordEx) when (!(recordEx is OperationCanceledException && ct.IsCancellationRequested))
            {
                _log.LogError(recordEx, "Obifin çekimi: beklenmeyen hata bağlantıya yazılamadı — bağlantı={ConnectionId}", connectionId);
            }
            throw;
        }
    }

    /// <summary>Sınıf dışı hatanın <c>LastError</c> metni: yalnız tür adı.</summary>
    private static string UnexpectedErrorMessage(Exception ex) => $"Beklenmeyen hata ({ex.GetType().Name})";

    /// <summary>Hata kaydı da kimlik korumalı (bkz. <see cref="SaveIfIdentityUnchangedAsync"/>): koşu sürerken admin kimliği
    /// değiştirdiyse (<see cref="ObifinConnectionService.UpsertAsync"/> UpdatedAt'i yazar, durumu Unverified'a çeker) eski
    /// kimliğin hatası yeni kaydın üstüne yazılmaz — düzeltilmiş bağlantı Failed'a düşmez. İzleyici boşaltılır (kaydedilmemiş
    /// satırlar atılır), bağlantı taze okunur. <paramref name="markFailed"/>: Failed'a çek; Disabled admin'in anahtarıdır,
    /// çevrilmez.</summary>
    private async Task RecordErrorIfIdentityUnchangedAsync(Guid connectionId, IdentitySnapshot identity, string error,
        bool markFailed, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var fresh = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (fresh is null || !identity.Matches(fresh.UserCode, fresh.BaseUrl, fresh.UpdatedAt))
        {
            _log.LogWarning("Obifin çekimi: kimlik değişti, hata kaydı atlandı — bağlantı={ConnectionId}", connectionId);
            return;
        }
        fresh.LastError = error;
        fresh.UpdatedAt = DateTimeOffset.UtcNow;
        if (markFailed && fresh.Status != ObifinConnectionStatus.Disabled) fresh.Status = ObifinConnectionStatus.Failed;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Pencereyi sayfa sayfa çeker, yazar; (görülen en büyük Id, son sayfa dolu muydu) döner.
    /// Sayfalama sunucunun GERÇEK sayfa boyutuna bakar (istenen değil: Obifin 2000 istenince 1000 döndürür):
    /// ToplamSayfaSayisi geldiyse o, gelmediyse "sayfa dolu → devam, eksik ya da boş → dur".
    /// <para><b>Sonsuz döngü koruması.</b> Sunucu istenen sayfayı vermezse (yanıttaki SayfaNo tutmuyor ya da sayfa bir
    /// öncekine göre hiç yeni Id getirmiyor — son sayfaya kısma ya da SayfaNo'yu yok sayma) pencere burada biter; son
    /// dolu sayfa "dolu" kalır, artımlı çekimde <c>BaslangicHareketId</c>'li drenaj turu kalanı yeni imleçle alır. Hep
    /// dolu, hep yeni Id'li sayfalar <see cref="MaxPagesPerWindow"/>'da <see cref="ObifinProtocolException"/> ile
    /// kesilir. Böylece hiçbir sunucu davranışı kilidi (<see cref="LockResource"/>) sonsuza dek tutamaz.</para></summary>
    private async Task<(long? MaxId, bool LastPageFull)> FetchWindowWithMetaAsync(ObifinConnection conn, ObifinCredentials creds,
        IdentitySnapshot identity, DateOnly from, DateOnly to, long? sinceId, CancellationToken ct)
    {
        long? maxId = null; var lastPageFull = false;
        HashSet<long>? previousIds = null;
        for (var page = 1; ; page++)
        {
            var result = await _client.ListTransactionsAsync(creds, from, to, sinceId, page, _pageSize, ct);
            var ordered = result.Items.OrderBy(t => t.Id).ToList();
            var pageIds = ordered.Select(t => t.Id).ToHashSet();
            if (previousIds is not null && pageIds.Count > 0 && (result.PageNo != page || previousIds.IsSupersetOf(pageIds)))
            {
                _log.LogWarning("Obifin çekimi: sunucu {Requested}. sayfa yerine {Returned}. sayfayı verdi ya da sayfa yeni " +
                    "hareket getirmedi; pencere ({From:yyyy-MM-dd}–{To:yyyy-MM-dd}) burada bitiriliyor — bağlantı={ConnectionId}",
                    page, result.PageNo, from, to, conn.Id);
                break;
            }
            await UpsertAsync(conn, identity, ordered, ct);
            if (ordered.Count > 0) maxId = Math.Max(maxId ?? 0, ordered[^1].Id);
            lastPageFull = result.PageSize > 0 && result.Items.Count >= result.PageSize;
            var hasMore = result.TotalPages is { } totalPages ? page < totalPages : lastPageFull;
            if (result.Items.Count == 0 || !hasMore) break;
            if (page >= MaxPagesPerWindow)
                throw new ObifinProtocolException(
                    $"Obifin sayfalaması {MaxPagesPerWindow} sayfada bitmedi ({from:yyyy-MM-dd}–{to:yyyy-MM-dd})");
            previousIds = pageIds;
        }
        return (maxId, lastPageFull);
    }

    private async Task UpsertAsync(ObifinConnection conn, IdentitySnapshot identity, IReadOnlyList<ObifinTransactionDto> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var ids = items.Select(i => i.Id).ToList();
        var existing = await _db.BankTransactions
            .Where(t => t.LicenseId == conn.LicenseId && ids.Contains(t.ObifinId))
            .Select(t => t.ObifinId).ToHashSetAsync(ct);
        var accounts = await _db.BankAccounts.Where(a => a.LicenseId == conn.LicenseId)
            .ToDictionaryAsync(a => a.ObifinAccountId, ct);
        var now = DateTimeOffset.UtcNow;
        var fresh = new List<BankTransaction>();
        foreach (var dto in items)
        {
            // Aynı sayfada tekrar eden Id (beklenmez) de tekil index'e çarpmaz.
            if (!existing.Add(dto.Id)) continue;
            if (!accounts.TryGetValue(dto.AccountId, out var account))
            {
                // Bilinmeyen hesap: yer tutucu; saatlik yenileme (ObifinAccountRefreshJob) tamamlar.
                account = new BankAccount
                {
                    Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinAccountId = dto.AccountId,
                    BankaKodu = Trim(dto.BankaKodu, 32), IbanMasked = PlaceholderIbanMask, Currency = Trim(dto.Currency, 3),
                    Active = false, RefreshedAt = now,
                };
                _db.BankAccounts.Add(account);
                accounts[dto.AccountId] = account;
            }
            // Hash + maske ham DTO değerinden; sütuna ham IBAN/VKN girmez (DB CHECK kısıtı da 64 hex ister).
            // Boş maske ("") null saklanır: MaskIban ham girdiyi döndürmez, boş değer görüntüde yer tutmasın.
            var masked = BankHasher.MaskIban(dto.CounterpartyIban);
            var tx = new BankTransaction
            {
                Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinId = dto.Id, BankAccountId = account.Id,
                ObifinAccountId = dto.AccountId, BankaKodu = Trim(dto.BankaKodu, 32),
                Direction = dto.SignedAmount >= 0 ? BankTransactionDirection.Incoming : BankTransactionDirection.Outgoing,
                Amount = Math.Abs(dto.SignedAmount), Currency = Trim(dto.Currency, 3),
                OccurredAt = ObifinConnectionService.TrToUtc(dto.OccurredAtTr),
                Description = Trim(dto.Description, 512), TransactionCode = Trim(dto.TransactionCode, 32),
                CommonType = Trim(dto.CommonType, 64), BankReference = Trim(dto.BankReference, 64),
                CounterpartyIbanHash = _hasher.HashIban(dto.CounterpartyIban),
                CounterpartyIbanMasked = masked.Length == 0 ? null : masked,
                CounterpartyName = Trim(dto.CounterpartyName, 160),
                CounterpartyTaxIdHash = _hasher.HashTaxId(dto.CounterpartyTaxId),
                RawJson = BankRawJsonRedactor.Redact(dto.RawJson), FetchedAt = now,
            };
            _db.BankTransactions.Add(tx);
            fresh.Add(tx);
        }
        if (fresh.Count == 0) return;
        await SaveIfIdentityUnchangedAsync(conn.Id, identity, ct);
        // Sıfır tutarlı satır (Obifin demo verisinde görüldü: TutarEksiArti "0.00") saklanır ama ödeme olamaz.
        foreach (var tx in fresh.Where(t => t.Direction == BankTransactionDirection.Incoming && t.Amount != 0))
            await _sink.OnNewIncomingAsync(tx, ct);
        // Kaydedilen satırlar koşu boyunca izleyicide birikmesin: 90 günlük ilk çekim on binlerce satır (ham JSON dahil)
        // tutar, her SaveChanges'in DetectChanges'i büyürdü. Bağlantı (conn) izlenmeye devam eder — imleç onun üzerinde;
        // az sayıdaki hesap satırı da kalır.
        foreach (var tx in fresh) _db.Entry(tx).State = EntityState.Detached;
    }

    /// <summary>Hareket/imleç kaydeden TEK yol: kimlik alanları taze (izlenmeyen) okunur; biri değiştiyse ya da
    /// bağlantı silindiyse kaydetmeden <see cref="IdentityChangedException"/>.</summary>
    private async Task SaveIfIdentityUnchangedAsync(Guid connectionId, IdentitySnapshot identity, CancellationToken ct)
    {
        var current = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Id == connectionId)
            .Select(c => new { c.UserCode, c.BaseUrl, c.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (current is null || !identity.Matches(current.UserCode, current.BaseUrl, current.UpdatedAt))
            throw new IdentityChangedException();
        await _db.SaveChangesAsync(ct);
    }

    [return: NotNullIfNotNull(nameof(s))]
    private static string? Trim(string? s, int max) => s is null ? null : (s.Length > max ? s[..max] : s);

    /// <summary>Koşu başında okunan kimlik alanları; her kayıttan önce taze değerle karşılaştırılır.</summary>
    private readonly record struct IdentitySnapshot(string UserCode, string BaseUrl, DateTimeOffset UpdatedAt)
    {
        public bool Matches(string userCode, string baseUrl, DateTimeOffset updatedAt)
            => string.Equals(userCode, UserCode, StringComparison.Ordinal)
               && string.Equals(baseUrl, BaseUrl, StringComparison.Ordinal)
               && updatedAt == UpdatedAt;
    }

    /// <summary>Koşu içi kontrol akışı; <see cref="PollConnectionAsync"/> yakalar, dışarı çıkmaz.</summary>
    private sealed class IdentityChangedException : Exception
    {
        public IdentityChangedException() : base("Obifin kimliği çekim sırasında değişti.") { }
    }
}
