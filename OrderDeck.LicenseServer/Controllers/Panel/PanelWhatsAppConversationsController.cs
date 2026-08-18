using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panelin sohbet listesi: etiketler, etiket filtresi ve elle etiketleme.
///
/// <para><b>Etiketler otomatik DÜŞMEZ.</b> Sunucu hiçbir etiketi kaldırmaz —
/// ödeme onaylansa bile "Dekont geldi" durur. Bu bilinçli: etiket "iş var"
/// demek, işin bittiğine operatör karar verir. Bu yüzden kaldırma tek
/// istektir.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-conversations")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppConversationsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelWhatsAppConversationsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record DekontDto(
        string? PayerName, decimal? Amount, DateTimeOffset? PaidAt,
        string? ReferansNo, string ParserConfidence);

    public sealed record ConversationLabelDto(Guid WaLabelId, string Name, string Color, string Source);

    public sealed record ConversationDto(
        Guid Id, string CustomerPhone, string? ProfileName, string Status,
        int UnreadCount, DateTimeOffset? LastMessageAt,
        List<ConversationLabelDto> Labels, DekontDto? LatestDekont);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? labelId, [FromQuery] int limit, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Ok(Array.Empty<ConversationDto>());

        // Sohbet satırları hiç budanmıyor, hesap yaşadıkça birikiyor. Sınır aynı
        // zamanda ids'i de kapıyor: aşağıdaki iki sorgu onu IN/OPENJSON listesine
        // çeviriyor, sınırsız bırakılırsa o listeler de sınırsız büyür.
        var take = limit is > 0 and <= 200 ? limit : 50;

        var q = _db.WaConversations.Where(c => c.LicenseId == licenseId.Value);

        if (labelId is not null)
        {
            q = q.Where(c => _db.WaConversationLabels
                .Any(x => x.ConversationId == c.Id && x.WaLabelId == labelId.Value));
        }

        var conversations = await q
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Take(take)
            .Select(c => new
            {
                c.Id, c.CustomerPhone, c.ProfileName, c.Status, c.UnreadCount, c.LastMessageAt,
            })
            .ToListAsync(ct);

        var ids = conversations.Select(c => c.Id).ToList();

        // Etiketler tek sorguda: sohbet başına ayrı sorgu 200 satırda 200 tur eder.
        var labels = await (
            from link in _db.WaConversationLabels
            join label in _db.WaLabels on link.WaLabelId equals label.Id
            where ids.Contains(link.ConversationId)
            select new
            {
                link.ConversationId,
                Dto = new ConversationLabelDto(label.Id, label.Name, label.Color, link.Source),
            })
            .ToListAsync(ct);

        var labelsByConversation = labels
            .GroupBy(x => x.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Dto.Name).Select(x => x.Dto).ToList());

        // Sohbetin EN SON ayrıştırılmış dekontu. Mesaj zaman damgasına göre,
        // çünkü webhook'lar sırasız gelebilir ama damga müşterinin gönderdiği andır.
        var dekonts = await (
            from d in _db.WaDekontExtractions.AsNoTracking()
            join m in _db.WaMessages on d.WaMessageId equals m.Id
            where ids.Contains(m.ConversationId)
            select new { m.ConversationId, m.Timestamp, D = d })
            .ToListAsync(ct);

        var latestDekont = dekonts
            .GroupBy(x => x.ConversationId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var newest = g.OrderByDescending(x => x.Timestamp).First().D;
                    return new DekontDto(
                        newest.PayerName, newest.Amount, newest.PaidAt,
                        newest.ReferansNo, newest.ParserConfidence);
                });

        var rows = conversations
            .Select(c => new ConversationDto(
                c.Id, c.CustomerPhone, c.ProfileName, c.Status, c.UnreadCount, c.LastMessageAt,
                labelsByConversation.TryGetValue(c.Id, out var ls) ? ls : new List<ConversationLabelDto>(),
                latestDekont.TryGetValue(c.Id, out var d) ? d : null))
            .ToList();

        return Ok(rows);
    }

    [HttpPost("{conversationId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> Attach(
        Guid conversationId, Guid labelId, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        // Hem sohbet hem etiket BU yayıncıya ait olmalı; ikisinden biri
        // başkasınınsa 404 — varlığını da sızdırmayalım.
        var ownsConversation = await _db.WaConversations.AnyAsync(
            c => c.Id == conversationId && c.LicenseId == licenseId.Value, ct);
        if (!ownsConversation) return NotFound();

        var ownsLabel = await _db.WaLabels.AnyAsync(
            l => l.Id == labelId && l.LicenseId == licenseId.Value, ct);
        if (!ownsLabel) return NotFound();

        var exists = await _db.WaConversationLabels.AnyAsync(
            x => x.ConversationId == conversationId && x.WaLabelId == labelId, ct);
        if (exists) return NoContent();   // idempotent: iki kez tıklamak hata değil

        _db.WaConversationLabels.Add(new WaConversationLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ConversationId = conversationId,
            WaLabelId = labelId,
            Source = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Yarış: operatör tıklarken LabelRuleApplier (ayrı DbContext, gelen
            // webhook işi) aynı etiketi otomatik yapıştırmış olabilir. Benzersiz
            // indeks bunu reddeder ama çağıranın istediği sonuç — bağın var
            // olması — yine sağlandı; uç zaten idempotent.
        }

        return NoContent();
    }

    [HttpDelete("{conversationId:guid}/labels/{labelId:guid}")]
    public async Task<IActionResult> Detach(
        Guid conversationId, Guid labelId, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var link = await _db.WaConversationLabels.FirstOrDefaultAsync(
            x => x.ConversationId == conversationId
                 && x.WaLabelId == labelId
                 && x.LicenseId == licenseId.Value, ct);

        // Zaten yoksa da NoContent: kaldırma idempotent, panel iki kez
        // tıklarsa kullanıcıya anlamsız bir hata göstermeyelim.
        if (link is null) return NoContent();

        // Kaynağı ("auto"/"manual") sormuyoruz: sunucunun yapıştırdığı etiketi
        // de operatör kaldırabilir — kilitli karar.
        _db.WaConversationLabels.Remove(link);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
