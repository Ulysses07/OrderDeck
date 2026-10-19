using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sync;
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

    public PanelShipmentsController(LicenseDbContext db)
    {
        _db = db;
    }

    /// <param name="CustomerUsername">Müşterinin asıl kaydındaki kullanıcı adı.
    /// Bu ve sonraki iki alan yalnız ad içindir (panel listesi müşteriyi Id
    /// yerine adıyla gösterir); projeksiyonu bulunmayan, Id'si GUID olmayan ya
    /// da KVKK ile silinmiş müşteride üçü de null. Sona eklendi: eski panel
    /// görmezden gelir.</param>
    /// <param name="CustomerDisplayName">Platform takma adı; yoksa null (panel
    /// kullanıcı adına, o da yoksa Id'ye düşer). Form adı (FullName) BİLEREK
    /// verilmez.</param>
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

        // Müşteri adları sayfa kesildikten SONRA, lisans başına toplu
        // yüklemeyle: satır başına sorgu yok; birleştirme zinciri varsa en çok
        // SalesCustomerDirectory.MaxHops ek okuma. Müşteri yalnız kargo
        // dosyasının KENDİ lisansında aranır — aynı Id'li başka lisansın kaydı
        // kullanılmaz.
        var customersByLicense = new Dictionary<Guid, SalesCustomerDirectory>();
        foreach (var license in rows.GroupBy(r => r.LicenseId))
            customersByLicense[license.Key] = await SalesCustomerDirectory.LoadAsync(
                _db, license.Key, license.Select(r => r.CustomerId), ct);

        return Ok(rows.Select(r => WithCustomer(r, customersByLicense[r.LicenseId])).ToList());
    }

    /// <summary>
    /// Satıra müşterinin adını ekler. Id önce asıl kayda çözülür (birleştirmeden
    /// sonra eski bir bilgisayar dosyayı kopyanın Id'siyle göndermiş olabilir;
    /// kopyanın kişisel alanları boştur) — <see cref="SalesCustomerDirectory"/>,
    /// <c>CustomerIdResolver</c> ile aynı kural. Asıl kayıt yoksa ya da KVKK ile
    /// silinmişse satır adsız kalır. Satırın <c>CustomerId</c>'si değişmez.
    /// </summary>
    private static ShipmentDto WithCustomer(ShipmentDto row, SalesCustomerDirectory customers)
    {
        if (customers.Resolve(row.CustomerId).Canonical is not { PurgedAt: null } canonical)
            return row;
        return row with
        {
            CustomerUsername = NullIfBlank(canonical.Username),
            CustomerDisplayName = NullIfBlank(canonical.DisplayName),
            CustomerPlatform = NullIfBlank(canonical.Platform),
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
