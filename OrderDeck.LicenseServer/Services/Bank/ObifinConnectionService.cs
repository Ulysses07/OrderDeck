using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

public sealed record ObifinVerifyResult(bool Ok, string? Error, int AccountCount);

/// <summary>
/// Lisans başına Obifin bağlantısı: kimlikleri şifreli saklar (DataProtection, Netgsm kalıbı),
/// doğrular (`hesaplistesi`), banka bağlantısı ekler (banka kimliği SAKLANMAZ — spec §3) ve
/// hesap listesini yeniler. Çekim işi (<c>ObifinPollJob</c>) kimliği buradan çözer.
///
/// <para><b>Eşzamanlılık jetonu BİLEREK yok:</b> EF yalnız değişen sütunları yazar; çekim işinin imleç
/// güncellemesi ile admin'in kimlik kaydı birbirini ezmez.</para>
/// </summary>
public sealed class ObifinConnectionService
{
    /// <summary>Korunan her alanın KENDİ purpose'u olur (bkz. <c>NetgsmAccountService</c>): ortak purpose'la
    /// bir sütunun şifreli metni öbür sütuna taşınıp okutulabilirdi. Değiştirilirse mevcut kayıtlar çözülemez.</summary>
    private const string PasswordPurpose = "OrderDeck.Obifin.Password.v1";
    private const string ApiKeyPurpose = "OrderDeck.Obifin.ApiKey.v1";

    public const string UndecryptableMessage = "Saklı Obifin kimliği çözülemedi. Kimlik bilgilerini yeniden girin.";
    public const string NonAsciiMessage = "Obifin kimlik bilgileri yalnız ASCII karakter içerebilir.";
    public const string BaseUrlMessage = "BaseUrl mutlak bir https adresi olmalı.";

    /// <summary>DB sütunu 500 (<c>LicenseDbContext</c>).</summary>
    private const int LastErrorMaxLength = 500;

    // Windows "Turkey Standard Time", Linux "Europe/Istanbul" (PanelStatsController kalıbı).
    public static readonly TimeZoneInfo TrZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly IDataProtector _passwordProtector;
    private readonly IDataProtector _apiKeyProtector;
    private readonly BankHasher _hasher;
    private readonly ObifinOptions _opt;
    private readonly ILogger<ObifinConnectionService> _log;

    public ObifinConnectionService(LicenseDbContext db, IObifinClient client, IDataProtectionProvider protection,
        BankHasher hasher, IOptions<ObifinOptions> opt, ILogger<ObifinConnectionService> log)
    {
        _db = db; _client = client;
        _passwordProtector = protection.CreateProtector(PasswordPurpose);
        _apiKeyProtector = protection.CreateProtector(ApiKeyPurpose);
        _hasher = hasher; _opt = opt.Value; _log = log;
    }

    public static DateTimeOffset TrToUtc(DateTime trLocal)
        => new DateTimeOffset(DateTime.SpecifyKind(trLocal, DateTimeKind.Unspecified), TrZone.GetUtcOffset(trLocal)).ToUniversalTime();

    /// <summary>Boş <paramref name="password"/>/<paramref name="apiKey"/> = mevcut değeri koru. Boş
    /// <paramref name="baseUrl"/> = varsayılan adres.
    /// <para>Kullanıcı kodu ya da (boş olmayan) adres değişirse eski hesabın imleci ve gölge verisi yeni hesap
    /// için anlamsızdır: imleç sıfırlanır, hareket/hesap/eşleşme/IBAN hafızası/boşluk satırları silinir.
    /// Aynı hesaba yeni parola girmek hiçbir şeye dokunmaz.</para></summary>
    public async Task<ObifinConnection> UpsertAsync(Guid licenseId, string baseUrl, string userCode,
        string? password, string? apiKey, CancellationToken ct)
    {
        userCode = (userCode ?? "").Trim();
        if (userCode.Length == 0) throw new ArgumentException("Kullanıcı adı boş olamaz.", nameof(userCode));
        // Kimlik HTTP header'ında gider; SocketsHttpHandler ASCII dışı değeri gönderim anında anlaşılmaz bir ağ
        // hatasıyla reddeder. Kayıtta yakalanır, doğrulamada değil.
        if (!IsPrintableAscii(userCode)) throw new ArgumentException(NonAsciiMessage, nameof(userCode));
        if (!string.IsNullOrWhiteSpace(password) && !IsPrintableAscii(password)) throw new ArgumentException(NonAsciiMessage, nameof(password));
        if (!string.IsNullOrWhiteSpace(apiKey) && !IsPrintableAscii(apiKey)) throw new ArgumentException(NonAsciiMessage, nameof(apiKey));
        var explicitBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
        if (explicitBaseUrl is not null
            && !(Uri.TryCreate(explicitBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            throw new ArgumentException(BaseUrlMessage, nameof(baseUrl));

        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct);
        var now = DateTimeOffset.UtcNow;
        var credentialChanged = false;
        if (conn is null)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("İlk kayıtta şifre ve API anahtarı zorunlu.");
            conn = new ObifinConnection { Id = Guid.NewGuid(), LicenseId = licenseId, CreatedAt = now, Status = ObifinConnectionStatus.Unverified };
            _db.ObifinConnections.Add(conn);
        }
        else
        {
            // Boş adres "görüş yok"tur, değişiklik sayılmaz: varsayılan adres yapılandırmadan değişse de
            // aynı hesap kalır, veri silinmez.
            credentialChanged = !string.Equals(conn.UserCode, userCode, StringComparison.Ordinal)
                || (explicitBaseUrl is not null && !string.Equals(conn.BaseUrl, explicitBaseUrl, StringComparison.Ordinal));
        }
        conn.BaseUrl = explicitBaseUrl ?? _opt.DefaultBaseUrl;
        conn.UserCode = userCode;
        if (!string.IsNullOrWhiteSpace(password)) conn.PasswordProtected = _passwordProtector.Protect(password);
        if (!string.IsNullOrWhiteSpace(apiKey)) conn.ApiKeyProtected = _apiKeyProtector.Protect(apiKey);
        if (credentialChanged) await ResetShadowDataAsync(conn, ct);
        conn.Status = conn.Status == ObifinConnectionStatus.Disabled && !credentialChanged
            ? ObifinConnectionStatus.Disabled : ObifinConnectionStatus.Unverified;
        conn.UpdatedAt = now;
        // Silme + kimlik yazımı tek SaveChanges = tek işlem: yarım kalmış sıfırlama olmaz.
        await _db.SaveChangesAsync(ct);
        return conn;
    }

    /// <summary>Şifre çözülemezse null (anahtar halkası kaybı) — çağıran Failed'a çeker.</summary>
    public ObifinCredentials? TryResolveCredentials(ObifinConnection conn)
    {
        try
        {
            return new ObifinCredentials(conn.BaseUrl, conn.UserCode,
                _passwordProtector.Unprotect(conn.PasswordProtected), _apiKeyProtector.Unprotect(conn.ApiKeyProtected));
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task<ObifinVerifyResult> VerifyAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Obifin bağlantısı yok.");
        var creds = TryResolveCredentials(conn);
        var now = DateTimeOffset.UtcNow;
        if (creds is null)
        {
            conn.Status = ObifinConnectionStatus.Failed; conn.LastError = UndecryptableMessage; conn.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return new ObifinVerifyResult(false, UndecryptableMessage, 0);
        }
        try
        {
            var accounts = await _client.ListAccountsAsync(creds, ct);
            conn.Status = ObifinConnectionStatus.Verified; conn.LastError = null; conn.LastVerifiedAt = now; conn.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await UpsertAccountsAsync(conn, accounts, now, ct);
            return new ObifinVerifyResult(true, null, accounts.Count);
        }
        // Çağıranın kendi iptali (sayfadan ayrıldı) Obifin'in suçu değil: Failed yazılmaz, istisna yukarı gider.
        // Zaman aşımı iptali (HttpClient.Timeout) ise geçici hata gibi ele alınır.
        catch (Exception ex) when (DescribeClientFailure(ex, ct) is { } msg)
        {
            // Kimlik bilgisi ne loga ne LastError'a girer; istisna mesajlarında kimlik yok.
            _log.LogWarning(ex, "Obifin doğrulaması başarısız — lisans={LicenseId}: {Error}", licenseId, msg);
            conn.Status = ObifinConnectionStatus.Failed; conn.LastError = msg; conn.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return new ObifinVerifyResult(false, msg, 0);
        }
    }

    /// <summary>Banka kimliklerini Obifin'e iletir, listeden etiketle `BankaApiId`'yi bulur; kimlikleri saklamaz.</summary>
    public async Task<BankConnection> AddBankConnectionAsync(Guid licenseId, string bankaKodu, string label,
        IReadOnlyDictionary<string, string> bankForm, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Önce Obifin bağlantısı kaydedilmeli.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        bankaKodu = bankaKodu.Trim().ToLowerInvariant();
        // Obifin tarafında tekil, tahmin edilemez etiket: liste dönüşünde bunu ararız.
        var obifinLabel = $"OrderDeck-{licenseId.ToString("N")[..8]}-{bankaKodu}-{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
        var form = new Dictionary<string, string>(bankForm) { ["BankaApiAdi"] = obifinLabel };
        await _client.AddBankConnectionAsync(creds, bankaKodu, form, ct);
        var listed = await _client.ListBankConnectionsAsync(creds, ct);
        var match = listed.FirstOrDefault(x => string.Equals(x.Name, obifinLabel, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Banka bağlantısı Obifin'de görünmedi; listeyi kontrol edin.");
        var bc = new BankConnection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, ObifinConnectionId = conn.Id, BankaKodu = bankaKodu,
            BankaApiId = match.BankaApiId, Label = string.IsNullOrWhiteSpace(label) ? obifinLabel : label.Trim(),
            Status = BankConnectionStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.BankConnections.Add(bc);
        await _db.SaveChangesAsync(ct);
        return bc;
    }

    public async Task<int> RefreshAccountsAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Obifin bağlantısı yok.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        var accounts = await _client.ListAccountsAsync(creds, ct);
        await UpsertAccountsAsync(conn, accounts, DateTimeOffset.UtcNow, ct);
        return accounts.Count;
    }

    private async Task UpsertAccountsAsync(ObifinConnection conn, IReadOnlyList<ObifinAccountDto> accounts,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await _db.BankAccounts.Where(a => a.LicenseId == conn.LicenseId).ToListAsync(ct);
        var connections = await _db.BankConnections.Where(b => b.LicenseId == conn.LicenseId).ToListAsync(ct);
        foreach (var dto in accounts)
        {
            var row = existing.FirstOrDefault(a => a.ObifinAccountId == dto.Id);
            if (row is null)
            {
                row = new BankAccount { Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinAccountId = dto.Id };
                _db.BankAccounts.Add(row);
                existing.Add(row);
            }
            row.BankConnectionId = connections.FirstOrDefault(b => b.BankaApiId == dto.BankaApiId)?.Id;
            row.BankaKodu = dto.BankaKodu;
            row.IbanMasked = BankHasher.MaskIban(dto.Iban);
            row.IbanHash = _hasher.HashIban(dto.Iban);
            row.Currency = dto.Currency;
            row.Active = dto.Active;
            row.LastBankSyncAt = dto.UpdatedAtTr is { } tr ? TrToUtc(tr) : null;
            row.NotificationNote = dto.NotificationNote;
            row.RefreshedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Kimlik değişti: imleç alanları null, lisansın gölge satırları silinmek üzere işaretlenir
    /// (kaydetmez — çağıranın SaveChanges'i ile tek işlem). İzlenen <c>RemoveRange</c>, <c>ExecuteDelete</c>
    /// değil: o hemen ve işlem dışı koşar, InMemory'de de yok. Nadir bir admin işlemi; satır sayısı küçük.</summary>
    private async Task ResetShadowDataAsync(ObifinConnection conn, CancellationToken ct)
    {
        var licenseId = conn.LicenseId;
        var matches = await _db.PaymentMatches.Where(m => m.LicenseId == licenseId).ToListAsync(ct);
        var transactions = await _db.BankTransactions.Where(t => t.LicenseId == licenseId).ToListAsync(ct);
        var accounts = await _db.BankAccounts.Where(a => a.LicenseId == licenseId).ToListAsync(ct);
        var memories = await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId).ToListAsync(ct);
        var gaps = await _db.PaymentMatchGaps.Where(g => g.LicenseId == licenseId).ToListAsync(ct);
        _db.PaymentMatches.RemoveRange(matches);
        _db.BankTransactions.RemoveRange(transactions);
        _db.BankAccounts.RemoveRange(accounts);
        _db.CustomerIbanMemories.RemoveRange(memories);
        _db.PaymentMatchGaps.RemoveRange(gaps);

        var hadCursor = conn.LastObifinTransactionId is not null || conn.BackfillCompletedAt is not null || conn.LastPolledAt is not null;
        conn.LastObifinTransactionId = null;
        conn.BackfillCompletedAt = null;
        conn.LastPolledAt = null;
        conn.LastError = null;

        // Kimlik bilgisi (kullanıcı kodu dahil) loga girmez; yalnız sayılar.
        _log.LogWarning(
            "Obifin kimliği değişti — lisans={LicenseId}: imleç sıfırlandı (vardı={HadCursor}), gölge veri silindi " +
            "(hareket={Transactions}, eşleşme={Matches}, hesap={Accounts}, IBAN hafızası={Memories}, boşluk={Gaps})",
            licenseId, hadCursor, transactions.Count, matches.Count, accounts.Count, memories.Count, gaps.Count);
    }

    /// <summary>İstemci hatasını admin ekranı için sınıflandırır; sınıf dışı istisna (ya da çağıranın kendi
    /// iptali) için null → yukarı gider. Obifin'in kendi mesajları aynen; ağ/vekil/zaman aşımı için İngilizce
    /// ağ metni ya da vekil HTML'i yerine kısa Türkçe metin + tür adı. Kimlik hiçbir dalda yer almaz.</summary>
    private static string? DescribeClientFailure(Exception ex, CancellationToken callerToken)
    {
        var msg = ex switch
        {
            ObifinApiException api => string.Join(" | ", api.Messages),
            ObifinProtocolException or HttpRequestException => $"Obifin'e ulaşılamadı ({ex.GetType().Name})",
            OperationCanceledException when !callerToken.IsCancellationRequested => $"Obifin'e ulaşılamadı ({ex.GetType().Name})",
            _ => null,
        };
        return msg is { Length: > LastErrorMaxLength } ? msg[..LastErrorMaxLength] : msg;
    }

    private static bool IsPrintableAscii(string value) => value.All(c => c is >= ' ' and <= '~');
}
