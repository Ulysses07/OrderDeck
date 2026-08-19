using System.Security.Cryptography;
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

        // Sahiplik kontrolü Graph'tan ÖNCE. UpsertAsync'teki aynı kontrol dört
        // çağrının ARDINDAN çalışıyor: 409'u gördüğümüzde code çoktan yanmış,
        // uygulama abone edilmiş ve /register asıl sahibin numarasına YENİ bir
        // PIN yazmış oluyor. O PIN'i saklamadan attığımız için numara bir daha
        // register edilemiyor — kurtarma yolu yalnız Meta desteği. Kötü niyet
        // gerekmiyor: tek Meta Business altındaki ajans, elle bağlanmış numara
        // ya da iki lisanslı müşteri bu isteği doğal olarak üretiyor.
        // UpsertAsync'teki kontrol yarış yedeği olarak DURUYOR.
        var phoneNumberId = req.PhoneNumberId.Trim();
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

        var subscribe = await _graph.SubscribeAppAsync(req.WabaId.Trim(), token, ct);
        if (!subscribe.Ok)
        {
            return Problem(
                title: "whatsapp-subscribe-failed", statusCode: 502,
                detail: Detail(subscribe.ErrorCode, subscribe.ErrorMessage));
        }

        var phone = await _graph.ReadPhoneNumberAsync(req.PhoneNumberId.Trim(), token, ct);
        if (!phone.Ok)
        {
            return Problem(
                title: "whatsapp-phone-read-failed", statusCode: 502,
                detail: Detail(phone.ErrorCode, phone.ErrorMessage));
        }

        var pin = await ResolvePinAsync(licenseId.Value, ct);
        var register = await _graph.RegisterPhoneNumberAsync(req.PhoneNumberId.Trim(), pin, token, ct);

        var result = await _accounts.UpsertAsync(
            licenseId.Value,
            new WhatsAppAccountUpsert(
                req.WabaId, req.PhoneNumberId, phone.Value!.DisplayPhoneNumber,
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
            account.LastError = $"register: {register.ErrorCode} {register.ErrorMessage}".Trim();
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
