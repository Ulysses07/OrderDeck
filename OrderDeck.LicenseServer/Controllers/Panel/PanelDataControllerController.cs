using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının veri sorumlusu bilgileri (KVKK K-3): kayıt formunda ve Shopper'da ilgili kişiye gösterilir. Okuma her panel
/// kullanıcısına açık; değiştirme yalnız hesap sahibi (personel değiştiremez). Kiracı müşteri düzeyinde (lisans gerekmez).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/panel/data-controller")]
public sealed class PanelDataControllerController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelDataControllerController(LicenseDbContext db) => _db = db;

    /// <param name="Profile">Hiç girilmediyse null.</param>
    public sealed record DataControllerResponse(DataControllerView? Profile);

    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
        => Ok(new DataControllerResponse(await DataControllerProfiles.ReadAsync(_db, User.GetTenantCustomerId(), ct)));

    [HttpPut]
    public async Task<IActionResult> PutAsync([FromBody] DataControllerInput input, CancellationToken ct)
    {
        if (User.IsOperator())
            return Problem(title: "owner-only", detail: "Veri sorumlusu bilgilerini yalnız hesap sahibi değiştirebilir.", statusCode: 403);
        if (DataControllerProfiles.Validate(input, out var valid) is { } error)
            return Problem(title: "invalid-field", detail: error, statusCode: 400);
        try
        {
            var view = await DataControllerProfiles.UpsertAsync(_db, User.GetTenantCustomerId(), valid, "panel", ct);
            return Ok(new DataControllerResponse(view));
        }
        catch (DataControllerConflictException ex)
        {
            return Problem(title: "concurrent-update", detail: ex.Message, statusCode: 409);
        }
    }
}
