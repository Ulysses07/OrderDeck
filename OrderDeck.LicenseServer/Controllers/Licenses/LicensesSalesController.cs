using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayın ve etiket senkronunun kopya uçları — spec 2026-10-13 §4.5. Desen satış gönderim uçlarıyla aynı
/// (<see cref="LicensesSessionsSyncController"/>): <c>Bearer-Customer</c> + rotadaki lisans çağırana ait olmalı,
/// değilse 404.
/// <list type="bullet">
///   <item><c>POST sales/hello</c> — kopyanın kimliği, yetenek düzeyi, bildirdiği kargo ayarı ve geçiş bayrakları.</item>
///   <item><c>GET sales/state</c> — her turda (30 sn) okunan lisans durumu; <c>replicaId</c> ve <c>seenMode</c>
///   verilirse kalp atışı sayılır.</item>
///   <item><c>GET sales/changes</c> — değişiklik akışı (§4.4, <see cref="SalesChangesFeed"/>); kalp atışı state ile
///   aynı.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sales")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSalesController : ControllerBase
{
    /// <summary>Mod kapısının "son 14 günde görülen" penceresi (§4.10-1, -2); eski sürüm listesi de bununla.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private const int MaxNameLength = 128;

    private readonly LicenseDbContext _db;
    private readonly SalesReplicaService _replicas;
    private readonly SalesChangesFeed _feed;
    private readonly SalesSyncOptions _options;

    public LicensesSalesController(
        LicenseDbContext db,
        SalesReplicaService replicas,
        SalesChangesFeed feed,
        IOptions<SalesSyncOptions> options)
    {
        _db = db;
        _replicas = replicas;
        _feed = feed;
        _options = options.Value;
    }

    // ─── hello ────────────────────────────────────────────────────────────

    public sealed record ReportedShippingDto(decimal? Threshold, decimal? Fee);

    /// <param name="PreviousReplicaId">Geri yüklemeden önceki kimlik (§8.7); bir kez yazılır.</param>
    /// <param name="FeatureLevel">Derlemenin yetenek düzeyi; mod kapısı 3 ister (§4.10-2).</param>
    /// <param name="ReportedShipping">Kopyanın yerel kargo ayarı; lisans geneli ayar yazılmadan önce yan yana
    /// gösterilir (§6.2). Verilmezse saklanan değer değişmez.</param>
    /// <param name="BackfillCompleted">Geri doldurma ve birim ayrışması onarımı bitti (§8.2-5). Yalnız
    /// <c>true</c> etkilidir; damga hiç silinmez.</param>
    /// <param name="PaymentHistoryCompleted">Ödeme geçmişi geri doldurması bitti (§8.2-6).</param>
    public sealed record HelloRequest(
        Guid? ReplicaId,
        Guid? PreviousReplicaId,
        string? MachineName,
        string? HardwareFingerprint,
        int? Protocol,
        int? FeatureLevel,
        int? SeenMode,
        ReportedShippingDto? ReportedShipping,
        bool? BackfillCompleted,
        bool? PaymentHistoryCompleted);

    [HttpPost("hello")]
    public async Task<IActionResult> Hello(Guid licenseId, [FromBody] HelloRequest req, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        // hello yalnız v2 konuşur: protokolsüz istek burada zarf hatasıdır.
        if (!SalesEnvelope.TryRead(req.ReplicaId, req.Protocol, req.SeenMode, out var envelope, out var error)
            || !envelope.IsV2)
            return Problem(title: "invalid-envelope", detail: error ?? "hello v2 isteğidir.", statusCode: 400);

        if (req.PreviousReplicaId == Guid.Empty || req.PreviousReplicaId == envelope.ReplicaId)
            return Problem(title: "invalid-previous-replica",
                detail: "previousReplicaId boş GUID ya da kopyanın kendisi olamaz.", statusCode: 400);
        if (req.MachineName is { Length: > MaxNameLength } || req.HardwareFingerprint is { Length: > MaxNameLength })
            return Problem(title: "field-too-long",
                detail: $"machineName ve hardwareFingerprint en çok {MaxNameLength} karakter.", statusCode: 400);
        if (req.FeatureLevel is not { } featureLevel || featureLevel is < 0 or > byte.MaxValue)
            return Problem(title: "invalid-feature-level", detail: "featureLevel 0–255 olmalı.", statusCode: 400);
        if (req.ReportedShipping is { } shipping && (shipping.Threshold < 0 || shipping.Fee < 0))
            return Problem(title: "invalid-shipping", detail: "Kargo eşiği ve ücreti negatif olamaz.", statusCode: 400);

        await _replicas.HelloAsync(
            licenseId,
            new ReplicaHello(
                envelope,
                req.PreviousReplicaId,
                req.MachineName,
                req.HardwareFingerprint,
                (byte)featureLevel,
                ReportsShipping: req.ReportedShipping is not null,
                req.ReportedShipping?.Threshold,
                req.ReportedShipping?.Fee,
                BackfillCompleted: req.BackfillCompleted == true,
                PaymentHistoryCompleted: req.PaymentHistoryCompleted == true),
            Request.Headers.UserAgent.ToString(),
            ct);

        return NoContent();
    }

    // ─── state ────────────────────────────────────────────────────────────

    /// <param name="State"><c>live</c> (<c>EndedAt</c> boş) ya da <c>handing-over</c> (bitti, devredilmedi).</param>
    /// <param name="OwnerLastSeenAt">Sahip kopyanın son kalp atışı (<c>WpfReplicas.LastSeenAt</c>); oturum
    /// satırına yazılmaz.</param>
    public sealed record LiveSessionDto(
        Guid Id,
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string? OwnerMachineName,
        DateTimeOffset? OwnerLastSeenAt,
        string State,
        int? UnsentLabelCount);

    /// <summary>Lisans geneli kargo ayarı (§6.2). Tablo S2'de gelir; S1'de alan hep null.</summary>
    public sealed record ShippingSettingsDto(int Version, decimal? FreeShippingThreshold, decimal? Fee);

    public sealed record LegacyMachineDto(string? MachineName, string? AppVersion, DateTimeOffset LastSeenAt);

    /// <param name="Mode">0 = Off, 1 = On (<see cref="SalesSyncMode"/>).</param>
    /// <param name="MinClientVersion">Tanımsız ya da ayrıştırılamıyorsa null (kapı kapalı).</param>
    public sealed record SalesStateDto(
        int Mode,
        DateTimeOffset? ModeChangedAt,
        string? MinClientVersion,
        IReadOnlyList<LiveSessionDto> LiveSessions,
        ShippingSettingsDto? Shipping,
        bool ScrubOrderTextOnPurge,
        IReadOnlyList<LegacyMachineDto> LegacyMachines);

    /// <summary>
    /// Lisansın satış senkronu durumu (§4.5). Kalp atışı oturum satırına değil kopya satırına yazılır: canlı
    /// yayının <c>ownerLastSeenAt</c>'i sahip kopyanın <c>LastSeenAt</c>'inden okunur, böylece her 30 sn'lik tur
    /// oturumun rowversion'ını ve değişiklik akışını ilerletmez.
    /// </summary>
    [HttpGet("state")]
    public async Task<IActionResult> State(
        Guid licenseId, [FromQuery] Guid? replicaId, [FromQuery] int? seenMode, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();
        if (await HeartbeatAsync(licenseId, replicaId, seenMode, "sales/state", ct) is { } invalid) return invalid;

        var license = await _db.Licenses.AsNoTracking()
            .Where(l => l.Id == licenseId)
            .Select(l => new { l.SalesSyncMode, l.SalesSyncModeChangedAt })
            .SingleAsync(ct);

        // Devredilmemiş oturumlar: filtreli IX_StreamSessions_LicenseId_Unreleased.
        var liveSessions = await _db.StreamSessions.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && s.ReleasedAt == null)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.Id)
            .Select(s => new LiveSessionDto(
                s.Id,
                s.Title,
                s.StartedAt,
                s.EndedAt,
                s.OwnerMachineName,
                _db.WpfReplicas
                    .Where(r => r.LicenseId == s.LicenseId && r.ReplicaId == s.OwnerReplicaId)
                    .Select(r => (DateTimeOffset?)r.LastSeenAt)
                    .FirstOrDefault(),
                s.EndedAt == null ? "live" : "handing-over",
                s.UnsentLabelCount))
            .ToListAsync(ct);

        var minClientVersion = _options.ParseMinClientVersion() is null ? null : _options.MinClientVersion!.Trim();

        return Ok(new SalesStateDto(
            (int)license.SalesSyncMode,
            license.SalesSyncModeChangedAt,
            minClientVersion,
            liveSessions,
            Shipping: null,
            _options.ScrubOrderTextOnPurge,
            await LegacyMachinesAsync(licenseId, ct)));
    }

    // ─── changes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Değişiklik akışı (§4.4): devredilmiş yayınlar, siparişleri ve kargo dosyaları ChangeSeq sırasıyla; yan listede
    /// sayfadaki siparişlerin oturumları ve müşteri taslakları. Mod Off'ta da çalışır. Ayrıntı
    /// <see cref="SalesChangesFeed"/>'te.
    /// </summary>
    /// <param name="take">Sayfa boyu; 1–500 aralığına kırpılır.</param>
    /// <param name="replicaId">Verilirse (<paramref name="seenMode"/> ile) kalp atışı sayılır, state ile aynı.</param>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes(
        Guid licenseId,
        [FromQuery] long afterSeq = 0,
        [FromQuery] int take = SalesChangesFeed.MaxTake,
        [FromQuery] Guid? replicaId = null,
        [FromQuery] int? seenMode = null,
        CancellationToken ct = default)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();
        if (await HeartbeatAsync(licenseId, replicaId, seenMode, "sales/changes", ct) is { } invalid) return invalid;

        return Ok(await _feed.ReadAsync(licenseId, afterSeq, take, ct));
    }

    /// <summary>
    /// Okuma uçlarının kalp atışı: <c>replicaId</c> ya da <c>seenMode</c> verildiyse ikisi birlikte v2 zarfıdır ve
    /// kopya satırına yazılır; hiçbiri yoksa hiçbir şey yazılmaz (protokolsüz okuma iz bırakmaz). Yarım zarf 400.
    /// </summary>
    private async Task<IActionResult?> HeartbeatAsync(
        Guid licenseId, Guid? replicaId, int? seenMode, string endpoint, CancellationToken ct)
    {
        if (replicaId is null && seenMode is null) return null;
        if (!SalesEnvelope.TryRead(replicaId, SalesEnvelope.V2, seenMode, out var envelope, out var error))
            return Problem(title: "invalid-envelope", detail: error, statusCode: 400);
        await _replicas.AcceptAsync(licenseId, envelope, Request.Headers.UserAgent.ToString(), endpoint, ct);
        return null;
    }

    /// <summary>
    /// Eski sürüm makineler: son 14 günde görülmüş, devre dışı bırakılmamış etkinleştirmeler (§4.10-1).
    /// <c>MinClientVersion</c> tanımlıysa sürümü bilinmeyen ya da ondan eski olanlar. Tanımsızsa (Ws henüz
    /// yayımlanmadı) sürümle ayırmak mümkün değil: aynı donanım parmak izinde v2 konuşmuş bir kopyası
    /// olmayanlar.
    /// </summary>
    private async Task<IReadOnlyList<LegacyMachineDto>> LegacyMachinesAsync(Guid licenseId, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - RecentWindow;
        var activations = await _db.Activations.AsNoTracking()
            .Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .OrderByDescending(a => a.LastSeenAt)
            .Select(a => new { a.HardwareFingerprint, a.MachineName, a.AppVersion, a.LastSeenAt })
            .ToListAsync(ct);
        if (activations.Count == 0) return Array.Empty<LegacyMachineDto>();

        var min = _options.ParseMinClientVersion();
        IEnumerable<LegacyMachineDto> legacy;
        if (min is not null)
        {
            legacy = activations
                .Where(a => !ClientVersion.IsAtLeast(ClientVersion.Parse(a.AppVersion), min))
                .Select(a => new LegacyMachineDto(a.MachineName, a.AppVersion, a.LastSeenAt));
        }
        else
        {
            var v2Fingerprints = (await _db.WpfReplicas.AsNoTracking()
                    .Where(r => r.LicenseId == licenseId
                        && r.Protocol == SalesEnvelope.V2
                        && r.HardwareFingerprint != null)
                    .Select(r => r.HardwareFingerprint!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            legacy = activations
                .Where(a => !v2Fingerprints.Contains(a.HardwareFingerprint))
                .Select(a => new LegacyMachineDto(a.MachineName, a.AppVersion, a.LastSeenAt));
        }

        return legacy.ToList();
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        return await _db.Licenses.AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
    }
}
