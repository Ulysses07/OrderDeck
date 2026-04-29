using LiveDeck.LicenseServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Skus;

[ApiController]
[Route("api/v1/admin/skus")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminSkusController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public AdminSkusController(LicenseDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var skus = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
        return Ok(skus);
    }
}
