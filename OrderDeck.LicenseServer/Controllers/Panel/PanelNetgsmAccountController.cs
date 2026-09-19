using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm + İYS kurulumu (spec §2). Dört alan girilir;
/// kaydetme anında <c>/iys/search</c> ile senkron doğrulanır.
///
/// <para><b>Owner-only.</b> Bunlar yayıncının faturalı Netgsm hesabının
/// kimlikleri; staff operatör ne görür ne değiştirir.</para>
///
/// <para><b>Şifre tek yön.</b> Panel yalnız <c>passwordSet</c> bayrağını
/// görür. Geri okunabilen bir alan, panel oturumu ele geçiren birine
/// yayıncının Netgsm hesabını da verirdi.</para>
/// </summary>
[ApiController]
[Route("api/panel/netgsm/account")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelNetgsmAccountController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelNetgsmAccountController(LicenseDbContext db) => _db = db;

    /// <param name="Status">none | failed | verified | disabled.</param>
    /// <param name="SmsEnabled">Yetki tablosunun (spec §2.1) tek cevabı:
    /// kampanya ve onay toplama yalnız bu true iken açık.</param>
    public sealed record AccountView(
        string Status,
        bool SmsEnabled,
        string? UserCode,
        string? Header,
        string? BrandCode,
        bool PasswordSet,
        string? LastError,
        DateTimeOffset? LastVerifiedAt);

    private IActionResult? OwnerOnly() =>
        User.IsOperator()
            ? Problem(title: "owner-only",
                detail: "Netgsm kurulumunu yalnız hesap sahibi görüntüleyip değiştirebilir.",
                statusCode: 403)
            : null;

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        if (OwnerOnly() is { } forbidden) return forbidden;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var acc = await _db.NetgsmAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.LicenseId == licenseId, ct);

        return Ok(ToView(acc));
    }

    internal static AccountView ToView(NetgsmAccount? acc) => acc is null
        ? new AccountView("none", false, null, null, null, false, null, null)
        : new AccountView(
            Status: acc.Status switch
            {
                NetgsmAccountStatus.Verified => "verified",
                NetgsmAccountStatus.Disabled => "disabled",
                _ => "failed",
            },
            SmsEnabled: acc.Status == NetgsmAccountStatus.Verified,
            UserCode: acc.UserCode,
            Header: acc.Header,
            BrandCode: acc.BrandCode,
            PasswordSet: !string.IsNullOrEmpty(acc.PasswordProtected),
            LastError: acc.LastError,
            LastVerifiedAt: acc.LastVerifiedAt);
}
