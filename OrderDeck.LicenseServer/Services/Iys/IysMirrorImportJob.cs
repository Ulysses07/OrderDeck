using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// §6 dönüş yolu — İYS'den AYNA içe aktarım. Geri dönen yayıncının WPF
/// müşteri telefonlarını /iys/search ile sorgular; İYS'de kaydı OLANLARI
/// terminal ayna satırı olarak yazar. Yerel satırı olan numaraya DOKUNMAZ
/// (yerel beyan her zaman kazanır). Unknown = İYS'de kayıt yok → satır
/// yazılmaz ("kayıt yok"u satıra çevirmek yanlış sinyal olur).
/// DisableConcurrentExecution YOK: iş idempotent — eş zamanlı iki koşu en
/// kötü tekil indeks yarışında düşer, sonraki koşu tamamlar.
/// </summary>
public sealed class IysMirrorImportJob
{
    public const string SourceCodeMirror = "IYS_MIRROR";

    internal const int BatchSize = 20;
    // Netgsm ~10 istek/dk — partiler arası 6 sn (ilk partide bekleme yok).
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly NetgsmAccountService _accounts;
    private readonly IIysClient _client;
    private readonly ILogger<IysMirrorImportJob> _log;

    public IysMirrorImportJob(LicenseDbContext db, NetgsmAccountService accounts,
        IIysClient client, ILogger<IysMirrorImportJob> log)
    {
        _db = db;
        _accounts = accounts;
        _client = client;
        _log = log;
    }

    public async Task RunAsync(Guid licenseId, CancellationToken ct)
    {
        var account = await _accounts.GetVerifiedByLicenseAsync(licenseId, ct);
        if (account is null)
        {
            _log.LogWarning("Ayna: doğrulanmış Netgsm hesabı yok (lisans {LicenseId})",
                licenseId);
            return;
        }

        var password = _accounts.TryUnprotectPassword(account.PasswordProtected);
        if (password is null)
        {
            _log.LogError("Ayna: parola çözülemedi (lisans {LicenseId})", licenseId);
            return;
        }

        var ctx = new IysAccountContext(
            licenseId, account.UserCode, password, account.BrandCode);

        var rawPhones = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.PurgedAt == null
                        && p.Phone != null && p.Phone != "")
            .Select(p => p.Phone!)
            .ToListAsync(ct);

        var phones = new List<string>();
        foreach (var raw in rawPhones)
            if (Auth.PhoneNormalizer.TryNormalize(raw, out var e164))
                phones.Add(e164!);
        phones = phones.Distinct().ToList();

        var known = (await _db.IysConsents.AsNoTracking()
                .Where(c => c.BrandCode == account.BrandCode
                            && c.ChannelType == "MESAJ"
                            && c.RecipientType == "BIREYSEL")
                .Select(c => c.Recipient)
                .ToListAsync(ct))
            .ToHashSet();

        var missing = phones.Where(p => !known.Contains(p)).ToList();
        if (missing.Count == 0) return;

        var first = true;
        foreach (var chunk in missing.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;

            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(ctx, chunk, ct);
            }
            catch (IysConfigurationException ex)
            {
                _log.LogError(ex, "Ayna: kalıcı yapılandırma hatası (marka {Brand})",
                    account.BrandCode);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning(ex,
                    "Ayna: geçici ağ hatası — kalan partiler sonraki koşuya "
                    + "(marka {Brand})", account.BrandCode);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var phone in chunk)
            {
                if (!result.Statuses.TryGetValue(phone, out var status)
                    || status == IysConsentStatus.Unknown)
                    continue; // İYS'de kayıt yok — satır yazılmaz.

                var consent = new IysConsent
                {
                    Id = Guid.NewGuid(),
                    BrandCode = account.BrandCode,
                    ChannelType = "MESAJ",
                    RecipientType = "BIREYSEL",
                    Recipient = phone,
                    Status = status,
                    ConsentDate = null,          // /iys/search consentDate DÖNMÜYOR (2026-09-17 ölçümü)
                    SourceCode = SourceCodeMirror,
                    PushState = IysPushState.Confirmed, // TERMİNAL: push Pending'i, verify Pushed'ı tarar
                    LastVerifiedStatus = status,
                    LastVerifiedAt = now,
                    LastLocalEventAt = now,
                    NextVerifyAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.IysConsents.Add(consent);
                _db.IysConsentEvents.Add(new IysConsentEvent
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    BrandCode = account.BrandCode,
                    IysConsentId = consent.Id,
                    Recipient = phone,
                    OccurredAt = now,
                    EventType = IysConsentEventType.SearchResult,
                    Status = status,
                    ApiResponseCode = result.Code,
                });
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogWarning(ex,
                    "Ayna: yazım çakışması (tekil indeks yarışı) — idempotent, "
                    + "sonraki koşu tamamlar (marka {Brand})", account.BrandCode);
            }
        }
    }
}
