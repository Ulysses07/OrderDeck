using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

public sealed record ObifinVerifyResult(bool Ok, string? Error, int AccountCount);

/// <summary><see cref="ObifinConnectionService.UpsertWithResultAsync"/> sonucu. <see cref="ShadowDataReset"/>: kimlik
/// değişti ve lisansın gölge satırları silindi (audit ve admin bildirimi için — silme kararı yine servisin).</summary>
public sealed record ObifinUpsertResult(ObifinConnection Connection, bool ShadowDataReset);

/// <summary>Kimlik (kullanıcı kodu ya da adres) değişimi lisansın gölge verisini silecekti ve çağıran buna izin vermedi
/// (<c>allowShadowReset</c>). Hiçbir şey değiştirilmemiş, hiçbir şey kaydedilmemiştir.</summary>
public sealed class ShadowResetConfirmationRequiredException : InvalidOperationException
{
    public ShadowResetConfirmationRequiredException() : base(ObifinConnectionService.ResetConfirmMessage) { }
}

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
    public const string ResetConfirmMessage =
        "Kimlik değişikliği bu lisansın banka verisini siler (hareketler, eşleşmeler, IBAN hafızası). Onaylamak için kutuyu işaretleyip tekrar kaydedin.";

    /// <summary>DB sütunu 500 (<c>LicenseDbContext</c>).</summary>
    private const int LastErrorMaxLength = 500;
    /// <summary><c>BankConnection.Label</c> sütunu 80, <c>BankaKodu</c> 32 (<c>LicenseDbContext</c>). Obifin çağrısından
    /// ÖNCE denetlenir: SQL Server'da INSERT "truncated" ile patlasaydı Obifin'de banka kimliğiyle açılmış, yerelde
    /// bilinmeyen bir yetim kayıt kalır, admin'in tekrarı ikincisini açardı.</summary>
    private const int LabelMaxLength = 80;
    private const int BankaKoduMaxLength = 32;
    /// <summary><c>ObifinConnection.BaseUrl</c> ve <c>UserCode</c> sütunları 200 (<c>LicenseDbContext</c>). Kayıtta
    /// denetlenir: SQL Server "truncated" hatası admin formuna anlaşılmaz düşerdi.</summary>
    private const int BaseUrlMaxLength = 200;
    private const int UserCodeMaxLength = 200;
    /// <summary><c>BankAccount</c> sütunları (<c>LicenseDbContext</c>): Obifin'den gelen değer kırpılarak yazılır —
    /// uzun bir bildirim notu başarılı doğrulamayı DbUpdateException'a çevirmesin.</summary>
    private const int NotificationNoteMaxLength = 500;
    private const int CurrencyMaxLength = 3;
    private const int IbanMaskedMaxLength = 40;

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
    /// dokunmaz.</para>
    /// <para><b>Silme kararının tek yeri burası.</b> Silinecek satır varsa ve <paramref name="allowShadowReset"/> verilmediyse
    /// (varsayılan — güvenli olan) HİÇBİR ŞEY değiştirilmeden <see cref="ShadowResetConfirmationRequiredException"/>
    /// fırlatılır. Karar, silinecek satır kümesinin kendisine bakılarak verilir (ayrı bir "var mı" sorgusuyla değil): onaysız
    /// geçen küme boştur ve silinen de odur. Silinecek satır yoksa onay gerekmez (imleç yine sıfırlanır).</para></summary>
    public async Task<ObifinConnection> UpsertAsync(Guid licenseId, string baseUrl, string userCode,
        string? password, string? apiKey, CancellationToken ct, bool allowShadowReset = false)
        => (await UpsertWithResultAsync(licenseId, baseUrl, userCode, password, apiKey, ct, allowShadowReset)).Connection;

    /// <summary><see cref="UpsertAsync"/>'in aynısı; gölge verinin silinip silinmediğini de döner (admin sayfasının audit
    /// kaydı ve bildirimi için — karar burada verilir, çağıran yalnız izni taşır).</summary>
    public async Task<ObifinUpsertResult> UpsertWithResultAsync(Guid licenseId, string baseUrl, string userCode,
        string? password, string? apiKey, CancellationToken ct, bool allowShadowReset = false)
    {
        userCode = (userCode ?? "").Trim();
        if (userCode.Length == 0) throw new ArgumentException("Kullanıcı adı boş olamaz.", nameof(userCode));
        // Kimlik HTTP header'ında gider; SocketsHttpHandler ASCII dışı değeri gönderim anında anlaşılmaz bir ağ
        // hatasıyla reddeder. Kayıtta yakalanır, doğrulamada değil.
        if (!IsPrintableAscii(userCode)) throw new ArgumentException(NonAsciiMessage, nameof(userCode));
        if (userCode.Length > UserCodeMaxLength)
            throw new ArgumentException($"Kullanıcı adı en fazla {UserCodeMaxLength} karakter olabilir.", nameof(userCode));
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
            if (explicitBaseUrl.Length > BaseUrlMaxLength)
                throw new ArgumentException($"BaseUrl en fazla {BaseUrlMaxLength} karakter olabilir.", nameof(baseUrl));
        }

        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct);
        var now = DateTimeOffset.UtcNow;
        var credentialChanged = false;
        ShadowRows? shadow = null;
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
            if (credentialChanged)
            {
                // Onay kararı ve silme AYNI yüklenmiş kümeye bakar: yalnız burada görülen satırlar silinir, onaysız geçen
                // (boş) küme hiçbir şey silmez. Yüklemeden sonra çekim işinin eklediği satır kümede yoktur, silinmez (çekim
                // işi kimlik değişimini kendi kaydından önce denetler). Bağlantıya henüz dokunulmadı.
                shadow = await LoadShadowRowsAsync(licenseId, ct);
                if (shadow.Any && !allowShadowReset) throw new ShadowResetConfirmationRequiredException();
            }
        }
        conn.BaseUrl = explicitBaseUrl ?? _opt.DefaultBaseUrl;
        conn.UserCode = userCode;
        if (!string.IsNullOrWhiteSpace(password)) conn.PasswordProtected = _passwordProtector.Protect(password);
        if (!string.IsNullOrWhiteSpace(apiKey)) conn.ApiKeyProtected = _apiKeyProtector.Protect(apiKey);
        if (shadow is not null) ResetShadowData(conn, shadow);
        conn.Status = conn.Status == ObifinConnectionStatus.Disabled && !credentialChanged
            ? ObifinConnectionStatus.Disabled : ObifinConnectionStatus.Unverified;
        conn.UpdatedAt = now;
        // Silme + kimlik yazımı tek SaveChanges = tek işlem: yarım kalmış sıfırlama olmaz.
        await _db.SaveChangesAsync(ct);
        return new ObifinUpsertResult(conn, shadow?.Any ?? false);
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
            MarkFailed(conn, UndecryptableMessage, now);
            await _db.SaveChangesAsync(ct);
            return new ObifinVerifyResult(false, UndecryptableMessage, 0);
        }
        try
        {
            var accounts = await _client.ListAccountsAsync(creds, ct);
            MarkVerified(conn, now);
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
    /// istisna yine yukarı gider. Ekle + liste başarısı da doğrulamadaki gibi kaydedilir (bkz.
    /// <see cref="MarkVerified"/>).
    /// <para>Obifin ya da bankanın SOAP hatası gönderilen banka alanını (web servis kullanıcısı/şifresi) yankılayabilir:
    /// <see cref="ObifinApiException"/> mesajları LastError'a, loga ve yukarıya gitmeden önce maskelenir
    /// (<see cref="ObifinRedaction"/>, istemcinin tanı günlüğüyle ortak kural) ve istisna maskeli mesajlarla YENİDEN
    /// kurulur — asıl istisna iç istisna olarak da taşınmaz. Günlüğe istisna nesnesi değil, yalnız türü ve maskeli metin
    /// gider.</para></summary>
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
        var now = DateTimeOffset.UtcNow;
        var obifinLabel = $"OrderDeck-{licenseId.ToString("N")[..8]}-{bankaKodu}-{now:yyyyMMddHHmm}-{Guid.NewGuid().ToString("N")[..8]}";
        var form = new Dictionary<string, string>(bankForm) { ["BankaApiAdi"] = obifinLabel };
        IReadOnlyList<ObifinBankConnectionDto> listed;
        try
        {
            await _client.AddBankConnectionAsync(creds, bankaKodu, form, ct);
            listed = await _client.ListBankConnectionsAsync(creds, ct);
        }
        catch (Exception ex) when (DescribeClientFailure(ex, ct) is not null)
        {
            // Diğer sınıfların (ağ/vekil/zaman aşımı) kayda geçen metni zaten sabit ("Obifin'e ulaşılamadı (Tür)").
            var redacted = ex is ObifinApiException api
                ? new ObifinApiException(api.Messages.Select(m => ObifinRedaction.Redact(m, bankForm.Values)).ToList())
                : null;
            var msg = DescribeClientFailure(redacted ?? ex, ct)!;
            _log.LogWarning("Obifin banka bağlantısı ekleme başarısız ({ExceptionType}) — lisans={LicenseId}: {Error}",
                ex.GetType().Name, conn.LicenseId, msg);
            MarkFailed(conn, msg, now);
            await _db.SaveChangesAsync(ct);
            if (redacted is not null) throw redacted;
            throw;
        }
        // İki çağrı da geçti = kimlik çalışıyor; durum + satır aşağıdaki tek SaveChanges'te.
        MarkVerified(conn, now);
        // Aynı etiket birden çok satırda görünürse (beklenmez) en büyük BankaApiId = en yeni kayıt.
        var match = listed.Where(x => string.Equals(x.Name, obifinLabel, StringComparison.Ordinal))
                .OrderByDescending(x => x.BankaApiId).FirstOrDefault()
            ?? throw new InvalidOperationException("Banka bağlantısı Obifin'de görünmedi; listeyi kontrol edin.");
        var bc = new BankConnection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, ObifinConnectionId = conn.Id, BankaKodu = bankaKodu,
            BankaApiId = match.BankaApiId, Label = label.Length == 0 ? obifinLabel : label,
            Status = BankConnectionStatus.Active, CreatedAt = now,
        };
        _db.BankConnections.Add(bc);
        await _db.SaveChangesAsync(ct);
        return bc;
    }

    /// <summary>Hesap listesini yeniler. İstemci hatası doğrulamadaki gibi sınıflandırılıp bağlantıya yazılır;
    /// dönüş tipi başarısızlık taşıyamadığından istisna yine yukarı gider. Başarı da doğrulamadaki gibi
    /// kaydedilir — aynı uç (<c>hesaplistesi</c>), aynı kanıt (bkz. <see cref="MarkVerified"/>).
    /// <para>Yalnız Obifin'in reddi (<see cref="ObifinApiException"/>) durumu Failed'a çevirir; ağ/vekil/zaman aşımı
    /// kimlik aleyhine kanıt değildir, yalnız son hata yazılır (çekim işindeki ayrımın aynısı). Saatlik yenileme işi
    /// ve çekim yalnız Verified bağlantıya baktığından, geçici hatada Failed yazmak tek bir 502'yi admin "Doğrula"ya
    /// basana dek sessizce duran bir banka çekimine çevirirdi.</para></summary>
    public async Task<int> RefreshAccountsAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new InvalidOperationException("Obifin bağlantısı yok.");
        var creds = TryResolveCredentials(conn) ?? throw new InvalidOperationException(UndecryptableMessage);
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<ObifinAccountDto> accounts;
        try
        {
            accounts = await _client.ListAccountsAsync(creds, ct);
        }
        catch (Exception ex) when (DescribeClientFailure(ex, ct) is { } msg)
        {
            if (ex is ObifinApiException) await MarkFailedAsync(conn, ex, msg, "hesap yenileme", now, ct);
            else await RecordTransientFailureAsync(conn, ex, msg, "hesap yenileme", now, ct);
            throw;
        }
        MarkVerified(conn, now);
        // Durum + hesaplar tek SaveChanges'te (doğrulamadaki gibi).
        await UpsertAccountsAsync(conn, accounts, now, ct);
        return accounts.Count;
    }

    /// <summary>Obifin kanıtını bağlantıya yazar (kaydetmez — çağıranın SaveChanges'i ile). Başarısızlık ile
    /// toparlanma kanıtı aynı uçtan gelir; yalnız başarısızlığı kaydetmek Obifin'in reddiyle Failed olmuş bir
    /// bağlantıyı, aynı uç yeniden başarı verdiğinde bile (ör. admin'in elle yenilemesi ya da banka eklemesi) insan
    /// "Doğrula"ya basana kadar yapışkan bir Failed'da bırakırdı. (Geçici hata hesap yenilemede zaten Failed yazmaz —
    /// bkz. <see cref="RefreshAccountsAsync"/>.)
    /// <para><see cref="ObifinConnectionStatus.Disabled"/> admin'in anahtarıdır: kanıt (başarı ya da hata) onu
    /// çevirmez, yalnız açık bir etkinleştirme çevirir. Aksi hâlde yenileme işi kapatılmış bağlantıyı sessizce
    /// yeniden açardı. Kanıt alanları (<c>LastVerifiedAt</c>, <c>LastError</c>) yine yazılır: admin ekranı
    /// "kapalı ama kimlik çalışıyor / son hata şu" diyebilsin.</para></summary>
    private static void MarkVerified(ObifinConnection conn, DateTimeOffset now)
    {
        if (conn.Status != ObifinConnectionStatus.Disabled) conn.Status = ObifinConnectionStatus.Verified;
        conn.LastError = null; conn.LastVerifiedAt = now; conn.UpdatedAt = now;
    }

    /// <summary><see cref="MarkVerified"/>'ın hata yüzü: Disabled kalır, son hata yazılır.</summary>
    private static void MarkFailed(ObifinConnection conn, string msg, DateTimeOffset now)
    {
        if (conn.Status != ObifinConnectionStatus.Disabled) conn.Status = ObifinConnectionStatus.Failed;
        conn.LastError = msg; conn.UpdatedAt = now;
    }

    /// <summary>Sınıflandırılmış istemci hatasını bağlantıya yazar ve kaydeder. Kimlik bilgisi ne loga ne
    /// LastError'a girer; istisna mesajlarında kimlik yok.</summary>
    private async Task MarkFailedAsync(ObifinConnection conn, Exception ex, string msg, string operation,
        DateTimeOffset now, CancellationToken ct)
    {
        _log.LogWarning(ex, "Obifin {Operation} başarısız — lisans={LicenseId}: {Error}", operation, conn.LicenseId, msg);
        MarkFailed(conn, msg, now);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Geçici istemci hatası (ağ/vekil/zaman aşımı): durum korunur, yalnız son hata yazılır ve kaydedilir.
    /// Kimlik ne loga ne LastError'a girer.</summary>
    private async Task RecordTransientFailureAsync(ObifinConnection conn, Exception ex, string msg, string operation,
        DateTimeOffset now, CancellationToken ct)
    {
        _log.LogWarning(ex, "Obifin {Operation} geçici hatayla düştü, durum korunuyor — lisans={LicenseId}: {Error}",
            operation, conn.LicenseId, msg);
        conn.LastError = msg; conn.UpdatedAt = now;
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
            // Obifin kaynaklı metinler sütun sınırına kırpılır (bkz. NotificationNoteMaxLength). Hash 64 hex sabittir.
            row.BankaKodu = Truncate(dto.BankaKodu, BankaKoduMaxLength);
            row.IbanMasked = Truncate(BankHasher.MaskIban(dto.Iban), IbanMaskedMaxLength);
            row.IbanHash = _hasher.HashIban(dto.Iban);
            row.Currency = Truncate(dto.Currency, CurrencyMaxLength);
            row.Active = dto.Active;
            row.LastBankSyncAt = dto.UpdatedAtTr is { } tr ? TrToUtc(tr) : null;
            row.NotificationNote = dto.NotificationNote is null ? null : Truncate(dto.NotificationNote, NotificationNoteMaxLength);
            row.RefreshedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Kimlik değişiminde silinecek gölge satırlar — onay kararı ve silme bu TEK kümeye bakar.</summary>
    private sealed record ShadowRows(List<PaymentMatch> Matches, List<BankTransaction> Transactions,
        List<BankAccount> Accounts, List<CustomerIbanMemory> Memories, List<PaymentMatchGap> Gaps,
        List<BankConnection> BankConnections)
    {
        public bool Any => Matches.Count + Transactions.Count + Accounts.Count + Memories.Count + Gaps.Count
            + BankConnections.Count > 0;
    }

    /// <summary>Lisansın gölge satırlarını izlenen olarak yükler; bir şeyi değiştirmez.</summary>
    private async Task<ShadowRows> LoadShadowRowsAsync(Guid licenseId, CancellationToken ct) => new(
        await _db.PaymentMatches.Where(m => m.LicenseId == licenseId).ToListAsync(ct),
        await _db.BankTransactions.Where(t => t.LicenseId == licenseId).ToListAsync(ct),
        await _db.BankAccounts.Where(a => a.LicenseId == licenseId).ToListAsync(ct),
        await _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId).ToListAsync(ct),
        await _db.PaymentMatchGaps.Where(g => g.LicenseId == licenseId).ToListAsync(ct),
        await _db.BankConnections.Where(b => b.LicenseId == licenseId).ToListAsync(ct));

    /// <summary>Kimlik değişti: imleç alanları null, yüklenmiş gölge satırlar silinmek üzere işaretlenir
    /// (kaydetmez — çağıranın SaveChanges'i ile tek işlem). İzlenen <c>RemoveRange</c>, <c>ExecuteDelete</c>
    /// değil: o hemen ve işlem dışı koşar, InMemory'de de yok. Nadir bir admin işlemi; satır sayısı küçük.
    /// <para>Banka bağlantıları da gider: <c>BankaApiId</c> eski Obifin hesabının kimliğidir — yeni hesapta ya
    /// yoktur ya da başkasının kaydıdır. Kalsalardı "kaldır" yeni kimlikle yabancı bir Id'ye <c>bankaapi/sil</c>
    /// atar, hesap tazeleme Id çakışmasında yanlış bağlantıya bağlardı ve yeni hesapta aynı Id ile eklenen
    /// kayıt tekil index'e (LicenseId, BankaApiId) takılırdı. Obifin'deki eski kayıtlara dokunulmaz: o hesabın
    /// kimliği artık elimizde değil.</para></summary>
    private void ResetShadowData(ObifinConnection conn, ShadowRows shadow)
    {
        var licenseId = conn.LicenseId;
        var (matches, transactions, accounts, memories, gaps, bankConnections) = shadow;
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
    /// ağ metni ya da vekil HTML'i yerine kısa Türkçe metin + tür adı. Kimlik hiçbir dalda yer almaz.
    /// Çekim işi (<see cref="ObifinPollJob"/>) de aynı sınıflandırmayı kullanır — LastError metni tek yerden.</summary>
    public static string? DescribeClientFailure(Exception ex, CancellationToken callerToken)
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

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
