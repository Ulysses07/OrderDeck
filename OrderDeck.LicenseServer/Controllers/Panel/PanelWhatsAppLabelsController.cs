using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının WhatsApp sohbet etiketleri. Etiketler tamamen dinamik: biz hiç
/// etiket tanımlamıyoruz, her yayıncı kendi listesini yazar.
///
/// <para>Meta'nın Cloud API'sinde sohbet etiketi yok; bu tablo tamamen bize
/// ait. Yani şablon onayı beklerken de çalışır.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-labels")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppLabelsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelWhatsAppLabelsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record LabelDto(Guid Id, string Name, string Color, DateTimeOffset CreatedAt);

    public sealed class LabelRequest
    {
        // 40 = WaLabel.Name kolon genişliği. Daha geniş bırakılırsa doğrulama
        // geçer, SQL Server kırpma hatası verir ve panel 500 görür.
        [Required, MaxLength(40)]
        public string Name { get; set; } = "";

        [Required, MaxLength(7)]
        public string Color { get; set; } = "";
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Ok(Array.Empty<LabelDto>());

        var rows = await _db.WaLabels
            .Where(l => l.LicenseId == licenseId.Value)
            .OrderBy(l => l.Name)
            .Select(l => new LabelDto(l.Id, l.Name, l.Color, l.CreatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>
    /// Kabul edilen renkler. Palet sunucuda sabit ve <see cref="Create"/> yalnız
    /// buradakileri kaydeder; panel listeyi kendi kaynağına kopyalasaydı iki
    /// taraf sessizce ayrışır ve yayıncı kendi seçtiği rengi kaydedemezdi.
    ///
    /// <para>Lisans çözmez: palet yayıncıya ait değil, kod sabiti.</para>
    /// </summary>
    [HttpGet("palette")]
    public IActionResult Palette() => Ok(WaLabelColors.Palette);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LabelRequest req, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var name = req.Name.Trim();
        if (name.Length == 0) return Problem(title: "empty-name", statusCode: 400);

        var color = WaLabelColors.Normalize(req.Color);
        if (color is null) return Problem(title: "invalid-color", statusCode: 400);

        var taken = await _db.WaLabels
            .AnyAsync(l => l.LicenseId == licenseId.Value && l.Name == name, ct);
        if (taken) return Problem(title: "duplicate-name", statusCode: 409);

        var row = new WaLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            Name = name,
            Color = color,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.WaLabels.Add(row);
        await _db.SaveChangesAsync(ct);

        var dto = new LabelDto(row.Id, row.Name, row.Color, row.CreatedAt);
        return Created($"/api/panel/whatsapp-labels/{row.Id}", dto);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LabelRequest req, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var row = await _db.WaLabels
            .FirstOrDefaultAsync(l => l.Id == id && l.LicenseId == licenseId.Value, ct);
        if (row is null) return NotFound();

        var name = req.Name.Trim();
        if (name.Length == 0) return Problem(title: "empty-name", statusCode: 400);

        var color = WaLabelColors.Normalize(req.Color);
        if (color is null) return Problem(title: "invalid-color", statusCode: 400);

        var taken = await _db.WaLabels
            .AnyAsync(l => l.LicenseId == licenseId.Value && l.Name == name && l.Id != id, ct);
        if (taken) return Problem(title: "duplicate-name", statusCode: 409);

        row.Name = name;
        row.Color = color;
        await _db.SaveChangesAsync(ct);

        return Ok(new LabelDto(row.Id, row.Name, row.Color, row.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var row = await _db.WaLabels
            .FirstOrDefaultAsync(l => l.Id == id && l.LicenseId == licenseId.Value, ct);
        if (row is null) return NotFound();

        // Bağlı satırlar ELLE temizlenir: her iki FK de NoAction — SQL Server
        // License'tan iki cascade yolu olan şemayı kabul etmiyor (bkz. Task 2).
        var rules = await _db.WaLabelRules.Where(r => r.WaLabelId == id).ToListAsync(ct);
        _db.WaLabelRules.RemoveRange(rules);

        var links = await _db.WaConversationLabels.Where(x => x.WaLabelId == id).ToListAsync(ct);
        _db.WaConversationLabels.RemoveRange(links);

        _db.WaLabels.Remove(row);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
