using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Otomatik etiket kuralları: SABİT bir olay → yayıncının DİNAMİK etiketi.
///
/// <para>Olay listesi genişleyebilir ama panel tarafından tanımlanamaz —
/// her olayın sunucuda onu tetikleyen gerçek bir kod yolu var.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-label-rules")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppLabelRulesController : ControllerBase
{
    /// <summary>Panelde olayın yanında görünen Türkçe açıklama. Sunucuda
    /// duruyor ki yeni bir olay eklendiğinde panel güncellenmeden de anlamlı
    /// bir metin görünsün.</summary>
    private static readonly IReadOnlyDictionary<WaLabelEvent, string> Descriptions =
        new Dictionary<WaLabelEvent, string>
        {
            [WaLabelEvent.PaymentApproved] = "Ödeme onaylandı",
            [WaLabelEvent.PaymentRejected] = "Ödeme reddedildi",
            [WaLabelEvent.OrderReceived] = "Yeni sipariş geldi",
            [WaLabelEvent.ShipmentStatusChanged] = "Kargo durumu değişti",
            [WaLabelEvent.CustomerSentDocument] = "Müşteri belge/görsel gönderdi",
        };

    private readonly LicenseDbContext _db;

    public PanelWhatsAppLabelRulesController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record RuleDto(string EventKey, string Description, Guid? WaLabelId);

    public sealed class RuleRequest
    {
        /// <summary>null → kuralı kaldır.</summary>
        public Guid? WaLabelId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);

        var assigned = licenseId is null
            ? new Dictionary<WaLabelEvent, Guid>()
            : await _db.WaLabelRules
                .Where(r => r.LicenseId == licenseId.Value)
                .ToDictionaryAsync(r => r.EventKey, r => r.WaLabelId, ct);

        var rows = Descriptions
            .Select(kv => new RuleDto(
                kv.Key.ToString(),
                kv.Value,
                assigned.TryGetValue(kv.Key, out var id) ? id : null))
            .ToList();

        return Ok(rows);
    }

    [HttpPut("{eventKey}")]
    public async Task<IActionResult> Put(
        string eventKey, [FromBody] RuleRequest req, CancellationToken ct)
    {
        // Tel biçimi olay ADI. Enum.TryParse bilerek kullanılmıyor: sayıyı ("3")
        // ve virgüllü listeyi de kabul ediyor, yani panel sunucu enum'unun sayı
        // değerlerine bağlanabilirdi. Büyük/küçük harf de duyarlı — "paymentapproved"
        // geçseydi panelde yazım hatası sessizce çalışır, sonra düzeltilemezdi.
        var match = Descriptions.Keys.Cast<WaLabelEvent?>()
            .FirstOrDefault(k => k.ToString() == eventKey);
        if (match is null) return Problem(title: "unknown-event", statusCode: 400);
        var parsed = match.Value;

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var existing = await _db.WaLabelRules
            .FirstOrDefaultAsync(r => r.LicenseId == licenseId.Value && r.EventKey == parsed, ct);

        if (req.WaLabelId is null)
        {
            if (existing is not null) _db.WaLabelRules.Remove(existing);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // Etiketin BU yayıncıya ait olduğunu doğrula: aksi hâlde başka bir
        // yayıncının etiketi bizim sohbetlerimize yapıştırılabilirdi.
        var owned = await _db.WaLabels.AnyAsync(
            l => l.Id == req.WaLabelId.Value && l.LicenseId == licenseId.Value, ct);
        if (!owned) return NotFound();

        if (existing is null)
        {
            _db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId.Value,
                EventKey = parsed,
                WaLabelId = req.WaLabelId.Value,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.WaLabelId = req.WaLabelId.Value;
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
