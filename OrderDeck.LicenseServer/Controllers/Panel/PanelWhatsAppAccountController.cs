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

        var phone = await _graph.ReadPhoneNumberAsync(phoneNumberId, token, ct);
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

        var pin = await ResolvePinAsync(licenseId.Value, ct);
        var register = await _graph.RegisterPhoneNumberAsync(phoneNumberId, pin, token, ct);

        var result = await _accounts.UpsertAsync(
            licenseId.Value,
            new WhatsAppAccountUpsert(
                wabaId, phoneNumberId, display,
                token, phone.Value.VerifiedName,
                // Meta reddettiyse PIN'i YAZMA: saklı PIN kaybolursa numara bir
                // daha register edilemez, kurtarma yolu yalnız Meta desteği.
                register.Ok ? pin : null),
            ct);

        if (result.Conflict)
        {
            return Problem(
                title: "phone-number-id-taken", statusCode: 409,
                detail: "Bu numara başka bir hesaba bağlı. Destekle iletişime geç.");
        }

        var account = result.Account!;

        if (!register.Ok)
        {
            // Hesap çalışıyor olabilir (numara zaten kayıtlı) ama gönderim
            // "not registered" verirse panelin sebebi gösterebilmesi lazım.
            // Metin Meta'dan geliyor ve kolon nvarchar(500): kırpmazsak uzun bir
            // mesaj SaveChangesAsync'i patlatır — hesap satırı ZATEN yazılmış
            // olduğu için panel, bağlanmış bir hesap için 500 görür.
            account.LastError = Truncate(
                $"register: {register.ErrorCode} {register.ErrorMessage}".Trim(),
                MaxLastErrorLength);
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "Embedded Signup tamamlandı: lisans {LicenseId}, phone_number_id {Pnid}",
            licenseId, account.PhoneNumberId);

        return Ok(ToView(account));
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var account = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId.Value, ct);
        return account is null ? NotFound() : Ok(ToView(account));
    }

    public sealed record SignupConfig(string AppId, string ConfigId, string GraphApiVersion);

    /// <summary>Panelin FB JS SDK'yı açmak için ihtiyaç duyduğu genel değerler.
    /// App Secret BURAYA GİRMEZ — o değerle tenant token'ı üretilebiliyor.</summary>
    [HttpGet("signup-config")]
    public IActionResult GetSignupConfig() =>
        Ok(new SignupConfig(_opt.AppId, _opt.EmbeddedSignupConfigId, _opt.GraphApiVersion));

    private static AccountView ToView(WhatsAppAccount a) => new(
        a.WabaId, a.PhoneNumberId, a.DisplayPhoneNumber, a.VerifiedName,
        a.Status, a.LastError, a.ConnectedAt);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string Detail(string? code, string? message) =>
        string.IsNullOrWhiteSpace(message) ? code ?? "bilinmeyen hata" : $"{code}: {message}";

    /// <summary>
    /// Register'a verilecek PIN. Lisansın saklı PIN'i varsa AYNISI kullanılır:
    /// Meta yeniden kayıtta numaranın mevcut PIN'ini istiyor, yenisi 133005
    /// ("PIN mismatch") ile döner. Çözülemeyen şifre (anahtar döndü) saklı PIN
    /// yok demektir — yeni üretip register'ın söyleyeceğine bakarız.
    /// </summary>
    private async Task<string> ResolvePinAsync(Guid licenseId, CancellationToken ct)
    {
        var stored = await _db.WhatsAppAccounts
            .Where(a => a.LicenseId == licenseId)
            .Select(a => a.TwoStepPinProtected)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(stored))
        {
            var pin = _accounts.TryUnprotectToken(stored);
            if (!string.IsNullOrWhiteSpace(pin)) return pin;
        }

        return NewPin();
    }

    /// <summary>Numaranın iki adımlı PIN'i. Tahmin edilebilir olmamalı — şifreli
    /// saklanıyor ve yayıncı adına numarayı kilitleyen değer bu.</summary>
    private static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
