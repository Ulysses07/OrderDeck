using OrderDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OrderDeck.LicenseServer.Controllers.Activations;

[ApiController]
[Route("api/v1/admin/activations")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminActivationsController : ControllerBase
{
    private readonly ActivationManager _activations;

    public AdminActivationsController(ActivationManager activations) => _activations = activations;

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> ForceDeactivate(Guid id, CancellationToken ct)
    {
        var ok = await _activations.ForceDeactivateAsync(id, ct);
        if (!ok) return NotFound();
        return NoContent();
    }
}
