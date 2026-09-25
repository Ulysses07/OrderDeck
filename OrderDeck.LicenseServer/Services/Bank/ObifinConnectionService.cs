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
    /// <summary><c>BankConnection.Label</c> sütunu 80, <c>BankaKodu</c> 32 (<c>LicenseDbContext</c>). Obifin çağrısından
    /// ÖNCE denetlenir: SQL Server'da INSERT "truncated" ile patlasaydı Obifin'de banka kimliğiyle açılmış, yerelde
    /// bilinmeyen bir yetim kayıt kalır, admin'in tekrarı ikincisini açardı.</summary>
    private const int LabelMaxLength = 80;
    private const int BankaKoduMaxLength = 32;

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
    /// için anlamsızdır: imleç sıfırlanır, hareket/hesap/eşleşme/IBAN hafızası/boşluk satırları ve eski hesabın
    /// <c>BankaApiId</c>'lerini taşıyan banka bağlantıları silinir. Aynı hesaba yeni parola girmek hiçbir şeye
    /// dokunmaz.</para></summary>
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
        string? explicitBaseUrl = null;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!(Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
                throw new ArgumentException(BaseUrlMessage, nameof(baseUrl));
            // Normalize saklanır ve karşılaştırılır: AbsoluteUri şema + ana makineyi küçük harfe çeker (DNS ve şema
            // harf duyarsız), boş yola eklediği '/' ile kullanıcının yazdığı sondaki '/' atılır (istemci zaten
            // TrimEnd('/') yapıyor). Aşağıdaki geri alınamaz gölge veri silmesi kozmetik bir farka bağlanamaz.
            explicitBaseUrl = uri.AbsoluteUri.TrimEnd('/');
        }

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
            // Saklı adres de kırpılarak karşılaştırılır: yapılandırmadan gelen varsayılan adres '/' ile bitebilir.
            credentialChanged = !string.Equals(conn.UserCode, userCode, StringComparison.Ordinal)
                || (explicitBaseUrl is not null && !string.Equals(conn.BaseUrl.TrimEnd('/'), explicitBaseUrl, StringComparison.Ordinal));
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
            // Durum + hesaplar tek SaveChanges'te (UpsertAccountsAsync'in sonunda): hesap yazımı patlarsa
            // Verified de kalıcı olmaz; çekim işi hesabı yazılmamış bir bağlantıyı çekmeye başlamaz.
            await UpsertAccountsAsync(conn, accounts, now, ct);
            return new ObifinVerifyResult(true, null, accounts.Count);
        }
        // Çağıranın kendi iptali (sayfadan ayrıldı) Obifin'in suçu değil: Failed yazılmaz, istisna yukarı gider.
        // Zaman aşımı iptali (HttpClient.Timeout) ise geçici hata gibi ele alınır.
        catch (Exception ex) when (DescribeClientFailure(ex, ct) is { } msg)
        {
            await MarkFailedAsync(conn, ex, msg, "doğrulaması", now, ct);
            return new ObifinVerifyResult(false, msg, 0);
        }
    }

    /// <summary>Banka kimliklerini Obifin'e iletir, listeden etiketle `BankaApiId`'yi bulur; kimlikleri saklamaz.
    /// İstemci hatası doğrulamadaki gibi sınıflandırılıp bağlantıya yazılır; dönüş tipi başarısızlık taşıyamadığından
    /// istisna yine yukarı gider.</summary>
    public async Task<BankConnection> AddBankConnectionAsync(Guid licenseId, string bankaKodu, string label,
        IReadOnlyDictionary<string, string> bankForm, CancellationToken ct)
    {
        // Yerel sınırlar ağ çağrısından önce (bkz. LabelMaxLength).
        label = (label ?? "").Trim();
        if (label.Length > LabelMaxLength)
            throw new ArgumentException($"Etiket en fazla {LabelMaxLength} karakter olabilir.", nameof(label));
        bankaKodu = (bankaKodu ?? "").Trim().ToLowerInvariant();
        if (bankaKodu.Length is 0 or > BankaKoduMaxLength)
            throw new ArgumentException($"Banka kodu 1–{BankaKoduMaxLength} karakter olmalı.", nameof(bankaKodu));

        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Önce Obifin bağlantısı kaydedilmeli.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        // Obifin tarafında tekil etiket: liste dönüşünde bunu ararız. Dakika damgası tek başına yetmez (aynı
        // dakikada iki ekleme ya da "görünmedi" hatasından sonra hemen tekrar aynı adı üretirdi; ikinci kayıt
        // ilkinin BankaApiId'sini alır, tekil index patlar, Obifin'deki kayıt yetim kalırdı) — rastgele son ek
        // ayırır. Uzunluk: 18 + 1 + BankaKodu(≤32) + 1 + 12 + 1 + 8 ≤ 73 < 80 (Label sütunu; etiket verilmezse
        // bu ad saklanır).
        var obifinLabel = $"OrderDeck-{licenseId.ToString("N")[..8]}-{bankaKodu}-{DateTimeOffset.UtcNow:yyyyMMddHHmm}-{Guid.NewGuid().ToString("N")[..8]}";
        var form = new Dictionary<string, string>(bankForm) { ["BankaApiAdi"] = obifinLabel };
        IReadOnlyList<ObifinBankConnectionDto> listed;
        try
        {
            await _client.AddBankConnectionAsync(creds, bankaKodu, form, ct);
            listed = await _client.ListBankConnectionsAsync(creds, ct);
        }
        catch (Exception ex) when (DescribeClientFailure(ex, ct) is { } msg)
        {
            await MarkFailedAsync(conn, ex, msg, "banka bağlantısı ekleme", DateTimeOffset.UtcNow, ct);
            throw;
        }
        // Aynı etiket birden çok satırda görünürse (beklenmez) en büyük BankaApiId = en yeni kayıt.
        var match = listed.Where(x => string.Equals(x.Name, obifinLabel, StringComparison.Ordinal))
                .OrderByDescending(x => x.BankaApiId).FirstOrDefault()
            ?? throw new InvalidOperationException("Banka bağlantısı Obifin'de görünmedi; listeyi kontrol edin.");
        var bc = new BankConnection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, ObifinConnectionId = conn.Id, BankaKodu = bankaKodu,
            BankaApiId = match.BankaApiId, Label = label.Length == 0 ? obifinLabel : label,
            Status = BankConnectionStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.BankConnections.Add(bc);
        await _db.SaveChangesAsync(ct);
        return bc;
    }

    /// <summary>Hesap listesini yeniler. İstemci hatası doğrulamadaki gibi sınıflandırılıp bağlantıya yazılır;
    /// dönüş tipi başarısızlık taşıyamadığından istisna yine yukarı gider.</summary>
    public async Task<int> RefreshAccountsAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Obifin bağlantısı yok.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        IReadOnlyList<ObifinAccountDto> accounts;
        try
        {
            accounts = await _client.ListAccountsAsync(creds, ct);
        }
        catch (Exception ex) when (DescribeClientFailure(ex, ct) is { } msg)
        {
            await MarkFailedAsync(conn, ex, msg, "hesap yenileme", DateTimeOffset.UtcNow, ct);
            throw;
        }
        await UpsertAccountsAsync(conn, accounts, DateTimeOffset.UtcNow, ct);
        return accounts.Count;
    }

    /// <summary>Sınıflandırılmış istemci hatasını bağlantıya yazar ve kaydeder. Kimlik bilgisi ne loga ne
    /// LastError'a girer; istisna mesajlarında kimlik yok.</summary>
    private async Task MarkFailedAsync(ObifinConnection conn, Exception ex, string msg, string operation,
        DateTimeOffset now, CancellationToken ct)
    {
        _log.LogWarning(ex, "Obifin {Operation} başarısız — lisans={LicenseId}: {Error}", operation, conn.LicenseId, msg);
        conn.Status = ObifinConnectionStatus.Failed; conn.LastError = msg; conn.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
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
    /// değil: o hemen ve işlem dışı koşar, InMemory'de de yok. Nadir bir admin işlemi; satır sayısı küçük.
    /// <para>Banka bağlantıları da gider: <c>BankaApiId</c> eski Obifin hesabının kimliğidir — yeni hesapta ya
    /// yoktur ya da başkasının kaydıdır. Kalsalardı "kaldır" yeni kimlikle yabancı bir Id'ye <c>bankaapi/sil</c>
    /// atar, hesap tazeleme Id çakışmasında yanlış bağlantıya bağlardı ve yeni hesapta aynı Id ile eklenen
    /// kayıt tekil index'e (LicenseId, BankaApiId) takılırdı. Obifin'deki eski kayıtlara dokunulmaz: o hesabın
    /// kimliği artık elimizde değil.</para></summary>
    private async Task ResetShadowDataAsync(ObifinConnection conn, CancellationToken ct)
    {
        var licenseId = conn.LicenseId;
        var matches = await _db.PaymentMatches.Where(m => m.LicenseId == licenseId).ToListAsync(ct);
        var transactions = await _db.BankTransactions.Where(t => t.LicenseId == licenseId).ToListAsync(ct);
        var accounts = await _db.BankAccounts.Where(a => a.LicenseId == licenseId).ToListAsync(ct);
        var memories = await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId).ToListAsync(ct);
        var gaps = await _db.PaymentMatchGaps.Where(g => g.LicenseId == licenseId).ToListAsync(ct);
        var bankConnections = await _db.BankConnections.Where(b => b.LicenseId == licenseId).ToListAsync(ct);
        _db.PaymentMatches.RemoveRange(matches);
        _db.BankTransactions.RemoveRange(transactions);
        _db.BankAccounts.RemoveRange(accounts);
        _db.CustomerIbanMemories.RemoveRange(memories);
        _db.PaymentMatchGaps.RemoveRange(gaps);
        _db.BankConnections.RemoveRange(bankConnections);

        var hadCursor = conn.LastObifinTransactionId is not null || conn.BackfillCompletedAt is not null || conn.LastPolledAt is not null;
        conn.LastObifinTransactionId = null;
        conn.BackfillCompletedAt = null;
        conn.LastPolledAt = null;
        conn.LastError = null;

        // Kimlik bilgisi (kullanıcı kodu dahil) loga girmez; yalnız sayılar.
        _log.LogWarning(
            "Obifin kimliği değişti — lisans={LicenseId}: imleç sıfırlandı (vardı={HadCursor}), gölge veri silindi " +
            "(hareket={Transactions}, eşleşme={Matches}, hesap={Accounts}, IBAN hafızası={Memories}, boşluk={Gaps}, " +
            "banka bağlantısı={BankConnections})",
            licenseId, hadCursor, transactions.Count, matches.Count, accounts.Count, memories.Count, gaps.Count,
            bankConnections.Count);
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
