using Microsoft.AspNetCore.Mvc;

namespace LiveDeck.LicenseServer.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "livedeck-license-server" });
}
