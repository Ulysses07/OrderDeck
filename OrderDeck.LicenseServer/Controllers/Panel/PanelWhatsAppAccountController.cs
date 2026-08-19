using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public PanelWhatsAppAccountController(
        LicenseDbContext db,
        WhatsAppAccountService accounts,
        IWhatsAppOnboardingClient graph,
        ILogger<PanelWhatsAppAccountController> log)
    {
        _db = db;
        _accounts = accounts;
        _graph = graph;
        _log = log;
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

        var pin = NewPin();
        var register = await _graph.RegisterPhoneNumberAsync(req.PhoneNumberId.Trim(), pin, token, ct);

        var result = await _accounts.UpsertAsync(
            licenseId.Value,
            new WhatsAppAccountUpsert(
                req.WabaId, req.PhoneNumberId, phone.Value!.DisplayPhoneNumber,
                token, phone.Value.VerifiedName, pin),
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

    private static AccountView ToView(WhatsAppAccount a) => new(
        a.WabaId, a.PhoneNumberId, a.DisplayPhoneNumber, a.VerifiedName,
        a.Status, a.LastError, a.ConnectedAt);

    private static string Detail(string? code, string? message) =>
        string.IsNullOrWhiteSpace(message) ? code ?? "bilinmeyen hata" : $"{code}: {message}";

    /// <summary>Numaranın iki adımlı PIN'i. Tahmin edilebilir olmamalı — şifreli
    /// saklanıyor ve yayıncı adına numarayı kilitleyen değer bu.</summary>
    private static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
