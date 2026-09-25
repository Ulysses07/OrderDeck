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
/// kendi iptali hiçbir şey yazmaz.</para>
///
/// <para><b>Kimlik değişimi — eşzamanlılık jetonu BİLEREK yok.</b> Admin çekim sürerken kimliği değiştirirse
/// <see cref="ObifinConnectionService.UpsertAsync"/> imleci sıfırlar, gölge veriyi siler. Eski koşu ne eski
/// hesabın satırlarını ne eski imleci geri yazmalı: hareket/imleç kaydeden HER SaveChanges'ten önce
/// UserCode/BaseUrl/UpdatedAt taze okunur, biri değiştiyse koşu kaydetmeden iptal edilir.</para>
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
    /// kaynak adı yöntem başınadır; iki yöntemi birbirinden korumazdı.</summary>
    public const string LockResource = "obifin-poll";
    public const int BackfillDays = 90;
    public const int WindowDays = 31;
    public const int MaxDrainRounds = 10;

    /// <summary>Yer tutucu hesabın maskesi; gerçek maske saatlik yenilemeyle gelir.</summary>
    private const string PlaceholderIbanMask = "?";

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly ObifinConnectionService _connections;
    private readonly BankHasher _hasher;
    private readonly IBankTransactionSink _sink;
    private readonly ObifinOptions _opt;
    private readonly ILogger<ObifinPollJob> _log;

    /// <summary>Test için: TR takvim günü.</summary>
    public Func<DateOnly> TodayTr { get; set; } = () =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ObifinConnectionService.TrZone));

    public ObifinPollJob(LicenseDbContext db, IObifinClient client, ObifinConnectionService connections,
        BankHasher hasher, IBankTransactionSink sink, IOptions<ObifinOptions> opt, ILogger<ObifinPollJob> log)
    {
        _db = db; _client = client; _connections = connections; _hasher = hasher; _sink = sink; _opt = opt.Value; _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var ids = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Verified)
            .Select(c => c.Id).ToListAsync(ct);
        foreach (var id in ids)
            await PollConnectionAsync(id, ct);
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
            _db.ChangeTracker.Clear();
            var fresh = await _db.ObifinConnections.FirstAsync(c => c.Id == connectionId, ct);
            fresh.LastError = msg;
            fresh.UpdatedAt = DateTimeOffset.UtcNow;
            if (ex is ObifinApiException)
            {
                // Obifin "hayır" dedi: kendiliğinden düzelmez, admin düzeltir. Disabled admin'in anahtarıdır, çevrilmez.
                if (fresh.Status != ObifinConnectionStatus.Disabled) fresh.Status = ObifinConnectionStatus.Failed;
                await _db.SaveChangesAsync(ct);
                _log.LogWarning("Obifin çekimi Obifin hatasıyla durdu — bağlantı={ConnectionId}: {Error}", connectionId, msg);
                return;
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "Obifin çekimi geçici hatayla düştü — bağlantı={ConnectionId}: {Error}", connectionId, msg);
            throw;
        }
    }

    /// <summary>Pencereyi sayfa sayfa çeker, yazar; (görülen en büyük Id, son sayfa dolu muydu) döner.
    /// Sayfalama sunucunun GERÇEK sayfa boyutuna bakar (istenen değil: Obifin 2000 istenince 1000 döndürür):
    /// ToplamSayfaSayisi geldiyse o, gelmediyse "sayfa dolu → devam, eksik ya da boş → dur".</summary>
    private async Task<(long? MaxId, bool LastPageFull)> FetchWindowWithMetaAsync(ObifinConnection conn, ObifinCredentials creds,
        IdentitySnapshot identity, DateOnly from, DateOnly to, long? sinceId, CancellationToken ct)
    {
        long? maxId = null; var lastPageFull = false;
        for (var page = 1; ; page++)
        {
            var result = await _client.ListTransactionsAsync(creds, from, to, sinceId, page, _opt.PageSize, ct);
            var ordered = result.Items.OrderBy(t => t.Id).ToList();
            await UpsertAsync(conn, identity, ordered, ct);
            if (ordered.Count > 0) maxId = Math.Max(maxId ?? 0, ordered[^1].Id);
            lastPageFull = result.PageSize > 0 && result.Items.Count >= result.PageSize;
            var hasMore = result.TotalPages is { } totalPages ? page < totalPages : lastPageFull;
            if (result.Items.Count == 0 || !hasMore) break;
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
        foreach (var tx in fresh.Where(t => t.Direction == BankTransactionDirection.Incoming))
            await _sink.OnNewIncomingAsync(tx, ct);
    }

    /// <summary>Hareket/imleç kaydeden TEK yol: kimlik alanları taze (izlenmeyen) okunur; biri değiştiyse ya da
    /// bağlantı silindiyse kaydetmeden <see cref="IdentityChangedException"/>.</summary>
    private async Task SaveIfIdentityUnchangedAsync(Guid connectionId, IdentitySnapshot identity, CancellationToken ct)
    {
        var current = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Id == connectionId)
            .Select(c => new { c.UserCode, c.BaseUrl, c.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (current is null
            || !string.Equals(current.UserCode, identity.UserCode, StringComparison.Ordinal)
            || !string.Equals(current.BaseUrl, identity.BaseUrl, StringComparison.Ordinal)
            || current.UpdatedAt != identity.UpdatedAt)
            throw new IdentityChangedException();
        await _db.SaveChangesAsync(ct);
    }

    [return: NotNullIfNotNull(nameof(s))]
    private static string? Trim(string? s, int max) => s is null ? null : (s.Length > max ? s[..max] : s);

    /// <summary>Koşu başında okunan kimlik alanları; her kayıttan önce taze değerle karşılaştırılır.</summary>
    private readonly record struct IdentitySnapshot(string UserCode, string BaseUrl, DateTimeOffset UpdatedAt);

    /// <summary>Koşu içi kontrol akışı; <see cref="PollConnectionAsync"/> yakalar, dışarı çıkmaz.</summary>
    private sealed class IdentityChangedException : Exception
    {
        public IdentityChangedException() : base("Obifin kimliği çekim sırasında değişti.") { }
    }
}
