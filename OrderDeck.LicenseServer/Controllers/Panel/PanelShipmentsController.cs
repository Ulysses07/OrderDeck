using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel app için Shipment (kümülatif kargo dosyası) sorgulama.
/// Read-only — kararlar WPF tarafında verilir, Panel sadece görüntüler.
///
/// Filter desteği:
/// <list type="bullet">
///   <item>status=pending|held|recipientpays|shipped</item>
///   <item>customerId=&lt;guid hex&gt; — tek müşterinin kargo geçmişi</item>
/// </list>
///
/// Pattern: <see cref="PanelPaymentsController"/>. Auth: Bearer-Customer +
/// kullanıcının lisansına ait Shipment'lar.
/// </summary>
[ApiController]
[Route("api/panel/shipments")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelShipmentsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;

    public PanelShipmentsController(LicenseDbContext db, CustomerIdResolver customerIds)
    {
        _db = db;
        _customerIds = customerIds;
    }

    /// <param name="CustomerUsername">Müşterinin asıl kaydındaki kullanıcı adı.
    /// Bu ve sonraki iki alan yalnız ad içindir (panel listesi müşteriyi Id
    /// yerine adıyla gösterir); projeksiyonu bulunmayan, Id'si GUID olmayan ya
    /// da KVKK ile silinmiş müşteride üçü de null. Sona eklendi: eski panel
    /// görmezden gelir.</param>
    /// <param name="CustomerDisplayName">Müşteri detayındaki görünen adla aynı
    /// kural: form adı (FullName) doluysa o, değilse platform takma adı; ikisi
    /// de boşsa null (panel kullanıcı adına, o da yoksa Id'ye düşer). Telefon,
    /// adres vb. kişisel alanlar verilmez.</param>
    /// <param name="CustomerPlatform">Asıl kaydın platformu (küçük harf).</param>
    public sealed record ShipmentDto(
        Guid Id,
        Guid LicenseId,
        string CustomerId,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt,
        DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt,
        string? CustomerUsername,
        string? CustomerDisplayName,
        string? CustomerPlatform);

    /// <summary>
    /// GET /api/panel/shipments?status=held&amp;customerId=...&amp;take=50.
    /// Default sıralama: en yeni (UpdatedAt DESC).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? customerId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var ownerCustomerId = User.GetTenantCustomerId();
        take = Math.Clamp(take, 1, 200);

        var query = _db.Shipments
            .Where(s => s.License.CustomerId == ownerCustomerId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsed))
                return Problem(title: "invalid-status", statusCode: 400);
            query = query.Where(s => s.Status == parsed);
        }

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var cid = customerId.Trim();
            query = query.Where(s => s.CustomerId == cid);
        }

        var rows = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(take)
            .Select(s => new ShipmentDto(
                s.Id, s.LicenseId, s.CustomerId,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt,
                null, null, null))
            .ToListAsync(ct);

        // Müşteri adları sayfa kesildikten SONRA, toplu (satır başına sorgu
        // yok): önce her lisansın Id'leri müşteri detayıyla aynı çözücüyle asıl
        // kayda çevrilir (birleştirme zinciri varsa en çok üç ek okuma), sonra
        // asıl kayıtlar tek sorguda okunur. Müşteri yalnız kargo dosyasının
        // KENDİ lisansında aranır — aynı Id'li başka lisansın kaydı kullanılmaz.
        var canonicalOf = new Dictionary<(Guid LicenseId, string CustomerId), Guid>();
        foreach (var license in rows.GroupBy(r => r.LicenseId))
        {
            var hexes = await _customerIds.CanonicalHexOfAsync(
                license.Key, license.Select(r => r.CustomerId), ct);
            foreach (var (raw, hex) in hexes)
                if (Guid.TryParseExact(hex, "N", out var id))
                    canonicalOf[(license.Key, raw)] = id;
        }

        var ids = canonicalOf.Values.Distinct().ToList();
        var licenseIds = canonicalOf.Keys.Select(k => k.LicenseId).Distinct().ToList();
        var customers = ids.Count == 0
            ? new Dictionary<Guid, CustomerNameRow>()
            : await _db.WpfCustomerProjections.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && licenseIds.Contains(p.LicenseId))
                .Select(p => new CustomerNameRow(
                    p.Id, p.LicenseId, p.Platform, p.Username, p.FullName, p.DisplayName, p.PurgedAt))
                .ToDictionaryAsync(p => p.Id, ct);

        return Ok(rows.Select(r => WithCustomer(r, canonicalOf, customers)).ToList());
    }

    /// <summary>Kargo satırına adı yazılan asıl müşteri kaydının okunan alanları.</summary>
    private sealed record CustomerNameRow(
        Guid Id, Guid LicenseId, string Platform, string Username,
        string? FullName, string? DisplayName, DateTimeOffset? PurgedAt);

    /// <summary>
    /// Satıra müşterinin adını ekler. Id asıl kayda çözülmüştür (birleştirmeden
    /// sonra eski bir bilgisayar dosyayı kopyanın Id'siyle göndermiş olabilir;
    /// kopyanın kişisel alanları boştur). Görünen ad müşteri detayıyla aynı
    /// kuraldan gelir (<see cref="PanelCustomersController.ResolveDisplayName"/>).
    /// Asıl kayıt yoksa, başka lisansınsa ya da KVKK ile silinmişse satır adsız
    /// kalır. Satırın <c>CustomerId</c>'si değişmez.
    /// </summary>
    private static ShipmentDto WithCustomer(
        ShipmentDto row,
        IReadOnlyDictionary<(Guid LicenseId, string CustomerId), Guid> canonicalOf,
        IReadOnlyDictionary<Guid, CustomerNameRow> customers)
    {
        if (!canonicalOf.TryGetValue((row.LicenseId, row.CustomerId), out var id)
            || !customers.TryGetValue(id, out var customer)
            || customer.LicenseId != row.LicenseId
            || customer.PurgedAt is not null)
            return row;
        return row with
        {
            CustomerUsername = NullIfBlank(customer.Username),
            CustomerDisplayName = NullIfBlank(
                PanelCustomersController.ResolveDisplayName(customer.FullName, customer.DisplayName)),
            CustomerPlatform = NullIfBlank(customer.Platform),
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryParseStatus(string raw, out ShipmentStatus result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "pending": result = ShipmentStatus.Pending; return true;
            case "held": result = ShipmentStatus.Held; return true;
            case "recipientpays": result = ShipmentStatus.RecipientPays; return true;
            case "shipped": result = ShipmentStatus.Shipped; return true;
            default: result = ShipmentStatus.Pending; return false;
        }
    }

}
