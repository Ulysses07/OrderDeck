using System.Security.Claims;
using System.Text.RegularExpressions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LiveDeck.LicenseServer.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class IntakeFormController : ControllerBase
{
    private static readonly Regex E164 = new(@"^\+[1-9]\d{6,14}$", RegexOptions.Compiled);

    private readonly IntakeFormService _service;
    private readonly LicenseDbContext _db;
    private readonly string _publicBaseUrl;

    public IntakeFormController(IntakeFormService service, LicenseDbContext db, IConfiguration config)
    {
        _service = service;
        _db = db;
        _publicBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
    }

    public sealed record IntakeFormBody(
        string Slug, string WhatsAppPhone, string? CustomTitle, bool IsActive, string FormUrl);

    public sealed record UpdateRequest(
        string Slug, string WhatsAppPhone, string? CustomTitle, bool? IsActive);

    public sealed record SubmissionBody(
        Guid Id, string Username, string FullName, string Address, string? Phone, DateTimeOffset SubmittedAt);

    [HttpGet("api/v1/me/intake-form")]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var cfg = await _service.GetByCustomerAsync(customerId, ct);
        if (cfg is null) return NotFound();
        return Ok(new IntakeFormBody(cfg.Slug, cfg.WhatsAppPhone, cfg.CustomTitle, cfg.IsActive,
            $"{_publicBaseUrl}/r/{cfg.Slug}"));
    }

    [HttpPut("api/v1/me/intake-form")]
    public async Task<IActionResult> Upsert([FromBody] UpdateRequest req, CancellationToken ct)
    {
        var slug = req.Slug?.Trim().ToLowerInvariant() ?? "";
        var slugResult = SlugValidator.Validate(slug);
        if (slugResult != SlugValidationResult.Valid)
            return Problem(title: $"invalid-slug-{slugResult.ToString().ToLowerInvariant()}", statusCode: 400);

        var phone = req.WhatsAppPhone?.Trim() ?? "";
        if (!E164.IsMatch(phone))
            return Problem(title: "invalid-phone-format", statusCode: 400);

        var customerId = GetCustomerId();
        try
        {
            var cfg = await _service.UpsertConfigAsync(
                customerId, slug, phone, req.CustomTitle?.Trim(),
                req.IsActive ?? true, ct);
            return Ok(new IntakeFormBody(cfg.Slug, cfg.WhatsAppPhone, cfg.CustomTitle, cfg.IsActive,
                $"{_publicBaseUrl}/r/{cfg.Slug}"));
        }
        catch (IntakeFormService.SlugAlreadyTakenException)
        {
            return Conflict(new { error = "slug-already-taken" });
        }
    }

    [HttpGet("api/v1/me/form-submissions")]
    public async Task<IActionResult> GetSubmissions(
        [FromQuery] DateTimeOffset? since,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        if (limit < 1 || limit > 200) limit = 50;
        var customerId = GetCustomerId();
        var rows = await _service.GetSubmissionsSinceAsync(
            customerId, since ?? DateTimeOffset.MinValue, limit, ct);
        return Ok(rows.Select(s => new SubmissionBody(s.Id, s.Username, s.FullName, s.Address, s.Phone, s.SubmittedAt)));
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
