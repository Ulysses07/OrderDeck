using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Lisans geneli kargo ayarı — spec 2026-10-13 §4.8, §6.2 (sahip kararı Y4). Desen satış uçlarıyla aynı
/// (<see cref="LicensesSalesController"/>): <c>Bearer-Customer</c> + rotadaki lisans çağırana ait olmalı, değilse 404.
/// <list type="bullet">
///   <item><c>GET shipping-settings</c> — ayar ve sürümü; satır yoksa sürüm 0 ve boş değerler.</item>
///   <item><c>PUT shipping-settings</c> — WPF ayar ekranı, yalnız mod On'da (§4.8). Mod Off'ta ayarı yalnız
///   <c>sales-sync shipping set</c> CLI'ı yazar: mod açılmadan önceki hazırlıktır; istemciler değeri mod On'da kullanır,
///   Off'ta kendi yerel ayarlarını yazmaya devam eder.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/shipping-settings")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesShippingSettingsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly SalesReplicaService _replicas;
    private readonly ILogger<LicensesShippingSettingsController> _log;

    public LicensesShippingSettingsController(
        LicenseDbContext db, SalesReplicaService replicas, ILogger<LicensesShippingSettingsController> log)
    {
        _db = db;
        _replicas = replicas;
        _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid licenseId, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();
        return Ok(await LicenseShippingSettingsStore.ReadAsync(_db, licenseId, ct));
    }

    /// <param name="ExpectedVersion">İstemcinin gördüğü sürüm (satır yoksa 0); güncel değilse 409 <c>stale</c>.</param>
    /// <param name="FreeShippingThreshold">Ücretsiz kargo eşiği; <paramref name="Fee"/> ile birlikte boş = kapalı.</param>
    /// <param name="Fee">Kargo ücreti.</param>
    public sealed record PutRequest(
        int? ExpectedVersion,
        decimal? FreeShippingThreshold,
        decimal? Fee,
        Guid? ReplicaId = null,
        int? Protocol = null,
        int? SeenMode = null) : ISalesEnvelopeFields;

    /// <summary>
    /// Ayarı yazar (§4.8). Sıra: zarf (yalnız v2; <c>UpdatedBy</c> çağıran kopyadır) ve değerler doğrulanır → istek
    /// kalp atışı olarak kaydedilir → mod Off ise 409 <c>mode-off</c> → beklenen sürüm güncel değilse 409
    /// <c>stale</c> ve güncel satır (<c>current</c>) → <c>Version + 1</c>, <c>UpdatedBy</c> = ReplicaId. Eşzamanlı iki
    /// yazımdan kaybeden de 409 <c>stale</c> alır (<see cref="LicenseShippingSettingsStore"/>).
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Put(Guid licenseId, [FromBody] PutRequest req, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        if (!SalesEnvelope.TryRead(req, out var envelope, out var envelopeError) || !envelope.IsV2)
            return Problem(
                title: "invalid-envelope", detail: envelopeError ?? "shipping-settings v2 isteğidir.", statusCode: 400);
        if (req.ExpectedVersion is not { } expectedVersion || expectedVersion < 0)
            return Problem(
                title: "invalid-expected-version", detail: "expectedVersion 0 ya da daha büyük olmalı.", statusCode: 400);
        if (!LicenseShippingSettings.IsValid(req.FreeShippingThreshold, req.Fee))
            return Problem(
                title: "invalid-shipping",
                detail: "Eşik ve ücret ya ikisi de boş (kargo kapalı) ya da ikisi de 0'dan büyük olmalı; en çok iki "
                    + "ondalık basamak.",
                statusCode: 400);

        var context = await _replicas.AcceptAsync(
            licenseId, envelope, Request.Headers.UserAgent.ToString(), "shipping-settings", ct);
        if (context.Mode != SalesSyncMode.On)
            return Problem(
                title: "mode-off",
                detail: "Mod Off'ta lisans geneli kargo ayarı sunucudan yazılmaz; yerel ayar kullanılır.",
                statusCode: 409);

        var result = await LicenseShippingSettingsStore.WriteAsync(
            _db, licenseId, expectedVersion, req.FreeShippingThreshold, req.Fee, envelope.ReplicaId.ToString(),
            DateTimeOffset.UtcNow, ct);
        if (result is LicenseShippingSettingsStore.Stale stale)
        {
            _log.LogInformation(
                "shipping-settings bayat yazım: beklenen {Expected}, güncel {Current} (license={LicenseId}, "
                + "replica={ReplicaId})",
                expectedVersion, stale.Current.Version, licenseId, envelope.ReplicaId);
            var conflict = Problem(
                title: "stale", detail: "Kargo ayarı bu arada değişti; güncel değerle yeniden deneyin.", statusCode: 409);
            ((ProblemDetails)conflict.Value!).Extensions["current"] = stale.Current;
            return conflict;
        }

        _log.LogInformation(
            "shipping-settings yazıldı: sürüm {Version} (license={LicenseId}, replica={ReplicaId})",
            result.Current.Version, licenseId, envelope.ReplicaId);
        return Ok(result.Current);
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        return await _db.Licenses.AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
    }
}
