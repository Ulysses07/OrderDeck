using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının kendi WhatsApp numarasını panelden bağlaması (Embedded Signup).
///
/// <para><b>Sıra önemli:</b> code→token, sonra WABA aboneliği, sonra numara
/// bilgisi, en son register. Abonelik olmadan o numaraya gelen mesajlar
/// webhook'umuza HİÇ düşmez — o yüzden ölümcül. Register ise ölümcül değil:
/// numara zaten kayıtlıysa Meta hata döner ama hesap çalışır.</para>
///
/// <para><b>Kod saklanmaz:</b> Embedded Signup'ın <c>code</c>'u 30 saniye
/// yaşıyor ve tek kullanımlık; loglanmaz, DB'ye yazılmaz.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp/account")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppAccountController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly IWhatsAppOnboardingClient _graph;
    private readonly ILogger<PanelWhatsAppAccountController> _log;
    private readonly WhatsAppOptions _opt;

    public PanelWhatsAppAccountController(
        LicenseDbContext db,
        WhatsAppAccountService accounts,
        IWhatsAppOnboardingClient graph,
        ILogger<PanelWhatsAppAccountController> log,
        IOptions<WhatsAppOptions> opt)
    {
        _db = db;
        _accounts = accounts;
        _graph = graph;
        _log = log;
        _opt = opt.Value;
    }

    /// <summary>WABA id ve Phone Number ID Meta'da saf rakam. 32 hane, bugünkü
    /// ~15 haneli id'lerin çok üstünde ama kolonun 64'ünün altında.</summary>
    private static readonly Regex MetaId =
        new("^[0-9]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>Kolon sınırları — <c>LicenseDbContext</c>'teki eşleştirmeyle
    /// aynı kalmalı, yoksa sınır ihlali DB'ye kadar gider.</summary>
    private const int MaxDisplayPhoneNumberLength = 20;
    private const int MaxLastErrorLength = 500;

    public sealed record EmbeddedSignupRequest(string Code, string WabaId, string PhoneNumberId);

    /// <summary>
    /// Numara bağlamak/koparmak hesap düzeyinde bir karar: yayıncının WhatsApp
    /// kimliğini personel devralamamalı. Aynı kapı okuma uçlarında da duruyor —
    /// bağlı numara ile Meta yapılandırması personelin göreceği bilgi değil.
    /// </summary>
    private IActionResult? OwnerOnly(string detail) =>
        User.IsOperator()
            ? Problem(title: "owner-only", detail: detail, statusCode: 403)
            : null;

    public sealed record AccountView(
        string WabaId,
        string PhoneNumberId,
        string DisplayPhoneNumber,
        string? VerifiedName,
        string Status,
        string? LastError,
        DateTimeOffset ConnectedAt);

    [HttpPost("embedded-signup")]
    public async Task<IActionResult> Complete(
        [FromBody] EmbeddedSignupRequest req, CancellationToken ct)
    {
        if (OwnerOnly("WhatsApp numarasını yalnız hesap sahibi bağlayabilir.")
            is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        if (string.IsNullOrWhiteSpace(req.Code) ||
            string.IsNullOrWhiteSpace(req.WabaId) ||
            string.IsNullOrWhiteSpace(req.PhoneNumberId))
        {
            return Problem(
                title: "invalid-embedded-signup-payload", statusCode: 400,
                detail: "code, wabaId ve phoneNumberId zorunlu.");
        }

        var wabaId = req.WabaId.Trim();
        var phoneNumberId = req.PhoneNumberId.Trim();

        // Bu iki değer Meta'nın sayısal id'leri. Doğrulanmadan geçirilirse iki
        // ayrı yoldan zarar veriyorlar: (1) Graph yoluna aynen giriyorlar
        // ("{Root}/{wabaId}/subscribed_apps"), yani sorgu dizesi eklenebiliyor
        // ve ".." parçaları Uri tarafından başka bir düğüme normalleştiriliyor;
        // (2) 64 karakteri aşan bir değer UpsertAsync'te DbUpdateException'a
        // dönüşüyor — üstelik /register PIN'i yazdıktan SONRA, yani sakladığımız
        // PIN olmadan numara kilitleniyor. Sınırda kesmek ikisini de bitiriyor.
        if (!MetaId.IsMatch(wabaId) || !MetaId.IsMatch(phoneNumberId))
        {
            return Problem(
                title: "invalid-embedded-signup-payload", statusCode: 400,
                detail: "wabaId ve phoneNumberId yalnız rakamlardan oluşabilir.");
        }

        // Sahiplik kontrolü Graph'tan ÖNCE. UpsertAsync'teki aynı kontrol dört
        // çağrının ARDINDAN çalışıyor: 409'u gördüğümüzde code çoktan yanmış,
        // uygulama abone edilmiş ve /register asıl sahibin numarasına YENİ bir
        // PIN yazmış oluyor. O PIN'i saklamadan attığımız için numara bir daha
        // register edilemiyor — kurtarma yolu yalnız Meta desteği. Kötü niyet
        // gerekmiyor: tek Meta Business altındaki ajans, elle bağlanmış numara
        // ya da iki lisanslı müşteri bu isteği doğal olarak üretiyor.
        // UpsertAsync'teki kontrol yarış yedeği olarak DURUYOR.
        var takenByAnother = await _db.WhatsAppAccounts.AnyAsync(
            a => a.PhoneNumberId == phoneNumberId && a.LicenseId != licenseId.Value, ct);
        if (takenByAnother)
        {
            return Problem(
                title: "phone-number-id-taken", statusCode: 409,
                detail: "Bu numara başka bir hesaba bağlı. Destekle iletişime geç.");
        }

        var exchange = await _graph.ExchangeCodeAsync(req.Code.Trim(), ct);
        if (!exchange.Ok)
        {
            return Problem(
                title: "whatsapp-code-exchange-failed", statusCode: 502,
                detail: Detail(exchange.ErrorCode, exchange.ErrorMessage));
        }

        var token = exchange.Value!;

        var subscribe = await _graph.SubscribeAppAsync(wabaId, token, ct);
        if (!subscribe.Ok)
        {
            return Problem(
                title: "whatsapp-subscribe-failed", statusCode: 502,
                detail: Detail(subscribe.ErrorCode, subscribe.ErrorMessage));
        }

        // WABA ÜZERİNDEN okunuyor: numaranın gerçekten o WABA'ya ait olduğunu
        // doğrulayan tek yer burası. İkisi bağımsız geldiği için eşleşmeyen bir
        // çift satıra yazılabilir ve abonelik yanlış hesaba gider — o numaraya
        // gelen mesajlar webhook'umuza HİÇ düşmezken panel "bağlı" gösterir.
        var phone = await _graph.ReadPhoneNumberAsync(wabaId, phoneNumberId, token, ct);
        if (!phone.Ok)
        {
            return Problem(
                title: "whatsapp-phone-read-failed", statusCode: 502,
                detail: Detail(phone.ErrorCode, phone.ErrorMessage));
        }

        // Numara Meta'nın verdiği hâliyle saklanıyor (kanonikleştirmiyoruz),
        // ama kolon nvarchar(20): beklenmedik uzunlukta bir değer UpsertAsync'te
        // DbUpdateException'a dönerdi — yine /register'dan SONRA, yine
        // saklanmayan PIN'le. Register'a girmeden burada temiz başarısız ol.
        var display = phone.Value!.DisplayPhoneNumber;
        if (display.Length > MaxDisplayPhoneNumberLength)
        {
            _log.LogWarning(
                "Meta beklenenden uzun display_phone_number döndü ({Length} karakter), lisans {LicenseId}",
                display.Length, licenseId);
            return Problem(
                title: "whatsapp-phone-read-failed", statusCode: 502,
                detail: "Meta beklenmedik uzunlukta bir numara döndü.");
        }

        // Coexistence: numara yayıncının telefonundaki WhatsApp Business
        // uygulamasında yaşıyor ve ZATEN kayıtlı. Meta'nın Business App
        // onboarding rehberi kayıt adımını açıkça atlatıyor. Atlamazsak
        // yayıncının günlük kullandığı numaraya yeni bir iki adımlı PIN
        // yazmaya kalkardık — en iyi ihtimalle hata, en kötüsünde yayıncıyı
        // kendi telefonundaki uygulamadan kilitleyen bir değişiklik.
        //
        // Meta alanı hiç göndermezse coexistence VARSAYILMIYOR: register'ı
        // gereksiz yere atlamak, kaydı hiç yapılmamış bir numarayı sessizce
        // gönderemez hâlde bırakırdı.
        var isCoexistence = phone.Value.IsBusinessApp;

        string? pinToStore = null;
        GraphResult<bool>? register = null;
        if (!isCoexistence)
        {
            var pin = await ResolvePinAsync(licenseId.Value, phoneNumberId, ct);
            register = await _graph.RegisterPhoneNumberAsync(phoneNumberId, pin, token, ct);
            // Meta reddettiyse PIN'i YAZMA: saklı PIN kaybolursa numara bir
            // daha register edilemez, kurtarma yolu yalnız Meta desteği.
            if (register.Ok) pinToStore = pin;
        }

        var result = await _accounts.UpsertAsync(
            licenseId.Value,
            new WhatsAppAccountUpsert(
                wabaId, phoneNumberId, display,
                token, phone.Value.VerifiedName,
                pinToStore),
            ct);

        if (result.Conflict)
        {
            return Problem(
                title: "phone-number-id-taken", statusCode: 409,
                detail: "Bu numara başka bir hesaba bağlı. Destekle iletişime geç.");
        }

        var account = result.Account!;

        // Coexistence'ta kayıt yerine SENKRON borcumuz var: Meta, onboarding'den
        // sonra 24 saat içinde geçmiş ve rehber senkronu başlatılmazsa müşterinin
        // offboard edilmesini şart koşuyor. Bu yüzden hemen burada, isteğin
        // içinde çağrılıyor — arka plana atmak, kuyruğun tıkandığı bir günde
        // yayıncının hesabını sessizce kaybettirirdi.
        var syncError = isCoexistence ? await StartCoexistenceSyncAsync(account, token, ct) : null;

        var failure = syncError
            ?? (register is { Ok: false }
                // Hesap çalışıyor olabilir (numara zaten kayıtlı) ama gönderim
                // "not registered" verirse panelin sebebi gösterebilmesi lazım.
                ? $"register: {Detail(register.ErrorCode, register.ErrorMessage)}"
                : null);

        if (failure is not null)
        {
            // Metin Meta'dan geliyor ve kolon nvarchar(500): kırpmazsak uzun bir
            // mesaj SaveChangesAsync'i patlatır — hesap satırı ZATEN yazılmış
            // olduğu için panel, bağlanmış bir hesap için 500 görür.
            account.LastError = Truncate(failure, MaxLastErrorLength);
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "Embedded Signup tamamlandı: lisans {LicenseId}, phone_number_id {Pnid}, coexistence {Coexistence}",
            licenseId, account.PhoneNumberId, isCoexistence);

        return Ok(ToView(account));
    }

    /// <summary>
    /// Coexistence senkronunu başlatır: önce sohbet geçmişi, sonra rehber.
    /// Veri webhook'la parça parça gelir; burada yalnız tetikleniyor.
    ///
    /// <para><b>Ölümcül değil.</b> Numara bu noktada bağlanmış ve çalışıyor;
    /// senkron düştü diye onboarding'i başarısız saymak, yayıncıyı çalışan bir
    /// hesabı olmadan bırakırdı. Ama sessiz de kalınamaz: senkronsuz geçen 24
    /// saatin sonunda hesabın offboard edilmesi gerekiyor, yani hata panele
    /// taşınmak zorunda. Kurtarma yolu aynı süre içinde Embedded Signup'ı
    /// tekrar çalıştırmak.</para>
    ///
    /// <para>Geçmiş düşse bile rehber DENENİYOR: ikisi ayrı senkron ve birinin
    /// düşmesi diğerini imkânsız kılmıyor.</para>
    /// </summary>
    private async Task<string?> StartCoexistenceSyncAsync(
        WhatsAppAccount account, string token, CancellationToken ct)
    {
        var failures = new List<string>();

        foreach (var syncType in new[]
                 {
                     WhatsAppSmbSyncTypes.History,
                     WhatsAppSmbSyncTypes.Contacts,
                 })
        {
            var sync = await _graph.SyncSmbAppDataAsync(
                account.PhoneNumberId, syncType, token, ct);

            if (sync.Ok)
            {
                // request_id yalnız Meta desteğine verilecek referans.
                _log.LogInformation(
                    "Coexistence senkronu başladı: lisans {LicenseId}, tür {SyncType}, request_id {RequestId}",
                    account.LicenseId, syncType, sync.Value);
                continue;
            }

            _log.LogWarning(
                "Coexistence senkronu başlatılamadı: lisans {LicenseId}, tür {SyncType} ({Code})",
                account.LicenseId, syncType, sync.ErrorCode);
            failures.Add($"{syncType}: {Detail(sync.ErrorCode, sync.ErrorMessage)}");
        }

        return failures.Count == 0 ? null : $"senkron: {string.Join("; ", failures)}";
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (OwnerOnly("Bağlı WhatsApp numarasını yalnız hesap sahibi görebilir.")
            is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        // Koparılmış satır DURUYOR (PIN'i taşıyor) ama panel için "bağlı numara
        // yok" demek: aksi hâlde yayıncı kopardığı numarayı bağlıymış gibi görür.
        var account = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId.Value && a.DisconnectedAt == null, ct);
        return account is null ? NotFound() : Ok(ToView(account));
    }

    /// <summary><paramref name="MetaUnsubscribed"/> false ise yerel bağ koptu ama
    /// uygulamamız WABA'ya abone kaldı — panel bunu söyleyebilmeli.</summary>
    public sealed record DisconnectResult(
        DateTimeOffset DisconnectedAt, bool MetaUnsubscribed, string? MetaError);

    /// <summary>
    /// Bağlı numarayı koparır.
    ///
    /// <para><b>Satır silinmez.</b> Şifreli PIN satırda duruyor ve Meta yeniden
    /// register'da AYNI PIN'i istiyor — silseydik yayıncı kendi numarasını bir daha
    /// bağlayamaz, tek çıkış Meta desteği olurdu. Bunun yerine
    /// <see cref="WhatsAppAccount.DisconnectedAt"/> damgalanıp durum "disconnected"
    /// oluyor (<c>GetActiveByLicenseAsync</c> "active" filtreliyor, yani gönderim
    /// anında duruyor) ve token satırdan siliniyor.</para>
    ///
    /// <para><b>Meta ayağı ölümcül değil.</b> Token süresi dolmuşsa abonelik hiçbir
    /// zaman kaldırılamaz; bunu ölümcül saymak yayıncıyı yanlış numarada kilitlerdi.
    /// Yerel kopma her hâlükârda oluyor, kalan iş yanıtta söyleniyor.</para>
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        if (OwnerOnly("WhatsApp numarasını yalnız hesap sahibi koparabilir.")
            is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var account = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId.Value && a.DisconnectedAt == null, ct);
        if (account is null) return NotFound();

        // Abonelik kaldırılmazsa o numaraya gelen mesajlar webhook'umuza düşmeye
        // devam eder — artık kime ait olduğunu bilmediğimiz hâlde.
        string? metaError;
        var token = _accounts.TryUnprotectToken(account.AccessTokenProtected);
        if (string.IsNullOrEmpty(token))
        {
            metaError = "Saklı token çözülemedi; Meta aboneliği kaldırılamadı.";
        }
        else
        {
            var unsubscribe = await _graph.UnsubscribeAppAsync(account.WabaId, token, ct);
            metaError = unsubscribe.Ok
                ? null
                : Detail(unsubscribe.ErrorCode, unsubscribe.ErrorMessage);
        }

        var now = DateTimeOffset.UtcNow;
        account.Status = "disconnected";
        account.DisconnectedAt = now;
        account.LastError = metaError is null
            ? null
            : Truncate($"disconnect: {metaError}", MaxLastErrorLength);
        // Token satırda kalsaydı "kopardım" diyen yayıncının numarasından hâlâ
        // mesaj gönderilebilirdi. PIN kalıyor, token gidiyor.
        account.AccessTokenProtected = "";
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "WhatsApp bağlantısı koparıldı: lisans {LicenseId}, phone_number_id {Pnid}, Meta aboneliği {Meta}",
            licenseId, account.PhoneNumberId, metaError is null ? "kaldırıldı" : "KALDIRILAMADI");

        return Ok(new DisconnectResult(now, metaError is null, metaError));
    }

    public sealed record SignupConfig(string AppId, string ConfigId, string GraphApiVersion);

    /// <summary>Panelin FB JS SDK'yı açmak için ihtiyaç duyduğu genel değerler.
    /// App Secret BURAYA GİRMEZ — o değerle tenant token'ı üretilebiliyor.</summary>
    [HttpGet("signup-config")]
    public IActionResult GetSignupConfig()
    {
        if (OwnerOnly("WhatsApp bağlama ayarlarını yalnız hesap sahibi görebilir.")
            is { } forbidden) return forbidden;

        // Boş değerlerle dönmek en kötüsü: panel FB SDK'yı yine de açar ve
        // yayıncı, sebebi sunucu yapılandırması olan anlaşılmaz bir Meta
        // hatasıyla karşılaşır. Eksikliği söyleyen tek yer burası.
        if (string.IsNullOrWhiteSpace(_opt.AppId) ||
            string.IsNullOrWhiteSpace(_opt.EmbeddedSignupConfigId))
        {
            return Problem(
                title: "embedded-signup-not-configured", statusCode: 503,
                detail: "WhatsApp bağlama sunucuda henüz yapılandırılmadı.");
        }

        return Ok(new SignupConfig(_opt.AppId, _opt.EmbeddedSignupConfigId, _opt.GraphApiVersion));
    }

    private static AccountView ToView(WhatsAppAccount a) => new(
        a.WabaId, a.PhoneNumberId, a.DisplayPhoneNumber, a.VerifiedName,
        a.Status, a.LastError, a.ConnectedAt);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string Detail(string? code, string? message) =>
        string.IsNullOrWhiteSpace(message) ? code ?? "bilinmeyen hata" : $"{code}: {message}";

    /// <summary>
    /// Register'a verilecek PIN. Lisansın saklı PIN'i AYNI numaraya aitse
    /// aynısı kullanılır: Meta yeniden kayıtta numaranın mevcut PIN'ini istiyor,
    /// yenisi 133005 ("PIN mismatch") ile döner. Çözülemeyen şifre (anahtar
    /// döndü) saklı PIN yok demektir — yeni üretip register'ın söyleyeceğine
    /// bakarız.
    ///
    /// <para><paramref name="phoneNumberId"/> eşleşmesi şart: yayıncı numara
    /// değiştirdiğinde eski numaranın PIN'ini yenisine göndermek 133005 üretir,
    /// hata yüzünden yeni PIN saklanmaz ve satırdaki PIN eski numaranınki olarak
    /// kalır — her denemede aynı yanlış PIN gider, çıkış yolu kalmaz.</para>
    /// </summary>
    private async Task<string> ResolvePinAsync(
        Guid licenseId, string phoneNumberId, CancellationToken ct)
    {
        var stored = await _db.WhatsAppAccounts
            .Where(a => a.LicenseId == licenseId && a.PhoneNumberId == phoneNumberId)
            .Select(a => a.TwoStepPinProtected)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(stored))
        {
            var pin = _accounts.TryUnprotectPin(stored);
            if (!string.IsNullOrWhiteSpace(pin)) return pin;
        }

        return NewPin();
    }

    /// <summary>Numaranın iki adımlı PIN'i. Tahmin edilebilir olmamalı — şifreli
    /// saklanıyor ve yayıncı adına numarayı kilitleyen değer bu.</summary>
    private static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
