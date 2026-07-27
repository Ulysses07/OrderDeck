using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Bir lisansın WhatsApp Cloud API hesabını admin olarak bağlar/görüntüler.
///
/// <para><b>Neden var:</b> gelen webhook'lar tenant'a <c>metadata.phone_number_id</c>
/// üzerinden yönlenir (<see cref="WhatsAppAccountService.GetByPhoneNumberIdAsync"/>);
/// karşılığı olan satır yoksa mesaj sessizce düşer. Embedded Signup akışı
/// yazılana kadar bu satırı açmanın başka yolu yoktu.</para>
///
/// <para><b>Token asla geri dönmez.</b> <c>IDataProtector</c> ile şifrelenip
/// saklanır; yanıtlarda yalnızca son 4 hanesi gösterilir. Şifreleme anahtarları
/// prod'da <c>./keys</c> volume'unda — o klasör kaybolursa token çözülemez ve
/// hesabın yeniden bağlanması gerekir.</para>
/// </summary>
[ApiController]
[Route("api/v1/admin/licenses/{licenseId:guid}/whatsapp/account")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminWhatsAppAccountsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly ILogger<AdminWhatsAppAccountsController> _log;

    public AdminWhatsAppAccountsController(
        LicenseDbContext db, WhatsAppAccountService accounts,
        ILogger<AdminWhatsAppAccountsController> log)
    {
        _db = db;
        _accounts = accounts;
        _log = log;
    }

    public sealed record ConnectRequest(
        string WabaId,
        string PhoneNumberId,
        string DisplayPhoneNumber,
        string AccessToken,
        string? VerifiedName);

    public sealed record AccountResponse(
        Guid Id,
        string WabaId,
        string PhoneNumberId,
        string DisplayPhoneNumber,
        string? VerifiedName,
        string Status,
        string TokenHint,
        string? LastError,
        DateTimeOffset ConnectedAt);

    /// <summary>
    /// Hesabı bağlar. Aynı lisans için tekrar çağrılırsa mevcut satır güncellenir
    /// (token yenileme yolu — dashboard token'ı ~24 saatte ölüyor, kalıcı System
    /// User token'ı ile değiştirmek bu uçtan yapılır).
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Connect(
        Guid licenseId, [FromBody] ConnectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WabaId))
            return Problem(title: "invalid-waba-id", statusCode: 400, detail: "WabaId zorunlu.");
        if (string.IsNullOrWhiteSpace(req.PhoneNumberId))
            return Problem(title: "invalid-phone-number-id", statusCode: 400, detail: "PhoneNumberId zorunlu.");
        if (string.IsNullOrWhiteSpace(req.AccessToken))
            return Problem(title: "invalid-access-token", statusCode: 400, detail: "AccessToken zorunlu.");

        var display = WaPhone.Canonical(req.DisplayPhoneNumber);
        if (display.Length == 0)
            return Problem(title: "invalid-display-phone", statusCode: 400, detail: "Geçerli bir numara gir.");

        if (!await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct)) return NotFound();

        var phoneNumberId = req.PhoneNumberId.Trim();

        // PhoneNumberId global unique: aynı numara iki tenant'a bağlanamaz, yoksa
        // webhook yönlendirmesi hangi lisansa gideceğini bilemez.
        var owner = await _db.WhatsAppAccounts
            .FirstOrDefaultAsync(a => a.PhoneNumberId == phoneNumberId, ct);
        if (owner is not null && owner.LicenseId != licenseId)
        {
            return Problem(
                title: "phone-number-id-taken", statusCode: 409,
                detail: "Bu Phone Number ID başka bir lisansa bağlı.");
        }

        var account = await _db.WhatsAppAccounts.FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
        var now = DateTimeOffset.UtcNow;

        if (account is null)
        {
            account = new WhatsAppAccount { Id = Guid.NewGuid(), LicenseId = licenseId, ConnectedAt = now };
            _db.WhatsAppAccounts.Add(account);
        }

        account.WabaId = req.WabaId.Trim();
        account.PhoneNumberId = phoneNumberId;
        account.DisplayPhoneNumber = display;
        account.VerifiedName = string.IsNullOrWhiteSpace(req.VerifiedName) ? null : req.VerifiedName.Trim();
        account.AccessTokenProtected = _accounts.ProtectToken(req.AccessToken.Trim());
        account.Status = "active";
        account.LastError = null;
        account.DisconnectedAt = null;

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "WhatsApp hesabı bağlandı: lisans {LicenseId}, phone_number_id {Pnid}", licenseId, phoneNumberId);

        return Ok(ToResponse(account, req.AccessToken));
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid licenseId, CancellationToken ct)
    {
        var account = await _db.WhatsAppAccounts.FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);
        if (account is null) return NotFound();

        // İpucu için token'ı çözmeye çalışırız; anahtar döndüyse çözülemez —
        // bunu gizlemek yerine göstermek lazım, yoksa "bağlı görünüyor ama
        // gönderim no_account veriyor" bilmecesi çıkıyor.
        var raw = _accounts.TryUnprotectToken(account.AccessTokenProtected);
        return Ok(ToResponse(account, raw));
    }

    private static AccountResponse ToResponse(WhatsAppAccount a, string? rawToken) => new(
        a.Id, a.WabaId, a.PhoneNumberId, a.DisplayPhoneNumber, a.VerifiedName,
        a.Status, TokenHint(rawToken), a.LastError, a.ConnectedAt);

    /// <summary>Token'ın kendisi asla dönmez — sadece "doğru token mu" ayırt
    /// etmeye yetecek kadar son 4 hane.</summary>
    private static string TokenHint(string? rawToken) =>
        rawToken is null or { Length: 0 } ? "çözülemedi"
        : rawToken.Length <= 4 ? "****"
        : "****" + rawToken[^4..];
}
