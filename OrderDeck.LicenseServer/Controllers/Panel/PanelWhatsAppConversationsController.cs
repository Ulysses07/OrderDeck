using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panelin sohbet listesi, sohbetin mesajları, serbest-metin cevap gönderme,
/// etiket filtresi ve elle etiketleme.
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
    private readonly WhatsAppMessagingService _messaging;

    public PanelWhatsAppConversationsController(
        LicenseDbContext db, WhatsAppMessagingService messaging)
    {
        _db = db;
        _messaging = messaging;
    }

    public sealed record DekontDto(
        string? PayerName, decimal? Amount, DateTimeOffset? PaidAt,
        string? ReferansNo, string ParserConfidence);

    public sealed record ConversationLabelDto(Guid WaLabelId, string Name, string Color, string Source);

    /// <summary>
    /// <c>WindowExpiresAt</c>: 24 saatlik service penceresinin kapanacağı an;
    /// müşteri hiç yazmamışsa null.
    ///
    /// <para><b>Neden "kapalı mı" bayrağı değil de mutlak an:</b> panel bu listeyi
    /// önbelleğe alıyor. Boolean gönderseydik pencere kullanıcı ekrana bakarken
    /// kapanır, yazma kutusu açık kalır ve gönderilen mesaj Meta'dan 131047 ile
    /// geri dönerdi. Mutlak anı istemci her render'da <c>Date.now()</c> ile
    /// karşılaştırabiliyor — bayat önbellek yanlış cevap üretmiyor.</para>
    /// </summary>
    public sealed record ConversationDto(
        Guid Id, string CustomerPhone, string? ProfileName, string Status,
        int UnreadCount, DateTimeOffset? LastMessageAt, DateTimeOffset? WindowExpiresAt,
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
                c.Id, c.CustomerPhone, c.ProfileName, c.Status, c.UnreadCount,
                c.LastMessageAt, c.LastInboundAt,
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
                WhatsAppServiceWindow.ExpiresAt(c.LastInboundAt),
                labelsByConversation.TryGetValue(c.Id, out var ls) ? ls : new List<ConversationLabelDto>(),
                latestDekont.TryGetValue(c.Id, out var d) ? d : null))
            .ToList();

        return Ok(rows);
    }

    public sealed record MessageDto(
        Guid Id, string Direction, string Type, string? Body, string Status,
        string? Origin, string? TemplateName, string? MediaMimeType,
        string? ErrorCode, string? ErrorMessage, DateTimeOffset Timestamp);

    /// <summary>
    /// Bir sohbetin mesajları, eskiden yeniye.
    ///
    /// <para>Sıralama <c>Timestamp</c> ile, satırın yazılma anıyla değil: gecikmeli
    /// webhook eski bir mesajı sonradan yazabiliyor ve o zaman ekranda sohbet
    /// karışık görünürdü.</para>
    ///
    /// <para>Okunmamış sayacı burada sıfırlanmıyor; onun ucu ayrı
    /// (<see cref="MarkRead"/>). Sebep: bu GET'i panel yeniden odaklanmada,
    /// yeniden bağlanmada ve arka planda tazeliyor — yan etkisi olsaydı rozet
    /// yayıncı hiç bakmadan sessizce sıfırlanırdı.</para>
    /// </summary>
    [HttpGet("{conversationId:guid}/messages")]
    public async Task<IActionResult> Messages(
        Guid conversationId, [FromQuery] int limit, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        // Sohbet id'si tahmin edilse bile başka yayıncının yazışması okunamamalı.
        var owns = await _db.WaConversations.AnyAsync(
            c => c.Id == conversationId && c.LicenseId == licenseId.Value, ct);
        if (!owns) return NotFound();

        var take = limit is > 0 and <= 500 ? limit : 100;

        // Önce SON mesajlar alınıyor (Take + azalan), sonra ekran için ters
        // çevriliyor: uzun bir sohbette istenen "en yeni 100", "en eski 100" değil.
        var rows = await _db.WaMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LicenseId == licenseId.Value)
            .OrderByDescending(m => m.Timestamp)
            .Take(take)
            .ToListAsync(ct);

        return Ok(rows
            .OrderBy(m => m.Timestamp)
            .Select(m => new MessageDto(
                m.Id, m.Direction, m.Type, m.Body, m.Status, m.Origin, m.TemplateName,
                m.MediaMimeType, m.ErrorCode, m.ErrorMessage, m.Timestamp))
            .ToList());
    }

    public sealed record SendRequest(string Text);

    public sealed record SendResponse(
        bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    /// <summary>Meta'nın serbest-metin gövde sınırı. Aşan mesajı Graph reddeder,
    /// yani göndermeden kesmek tek doğru davranış.</summary>
    private const int MaxTextLength = 4096;

    /// <summary>
    /// Sohbete serbest-metin (service) cevabı gönderir.
    ///
    /// <para><b>Yalnız serbest metin:</b> 24 saatlik pencere kapalıysa
    /// <see cref="WhatsAppMessagingService"/> Graph'a hiç gitmeden
    /// <c>window_closed</c> döner. Onaylı template gönderimi bilerek kapsam
    /// dışı — Meta'da onaylı şablon listesini çekmek ve parametrelerini
    /// doldurtmak ayrı bir iş.</para>
    ///
    /// <para><b>Neden hata durumunda da 200:</b> "gönderilemedi" ≠ "istek
    /// hatalı". Pencere kapalı, hesap bağlı değil ya da Meta reddetti —
    /// üçünde de istek geçerliydi ve sebebi gövdede taşınıyor. 4xx dönseydi
    /// panelin fetch katmanı bunları genel bir ağ hatasına indirger, yayıncı
    /// da gerçek sebebi hiç görmezdi. Gönderilemeyen mesaj ayrıca
    /// <c>Status="failed"</c> satırı olarak yazılıyor, yani sohbette görünüyor.</para>
    /// </summary>
    [HttpPost("{conversationId:guid}/send")]
    public async Task<IActionResult> Send(
        Guid conversationId, [FromBody] SendRequest req, CancellationToken ct)
    {
        var text = req.Text?.Trim() ?? "";
        if (text.Length == 0)
            return Problem(title: "empty-body", statusCode: 400, detail: "Mesaj boş olamaz.");
        if (text.Length > MaxTextLength)
            return Problem(
                title: "text-too-long", statusCode: 400,
                detail: $"Mesaj en fazla {MaxTextLength} karakter olabilir.");

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        // Numarayı istekten DEĞİL sohbetten alıyoruz: aksi hâlde uç, sohbet
        // listesinde hiç görünmeyen rastgele numaralara mesaj atmanın yolu olurdu.
        var convo = await _db.WaConversations.AsNoTracking().FirstOrDefaultAsync(
            c => c.Id == conversationId && c.LicenseId == licenseId.Value, ct);
        if (convo is null) return NotFound();

        var outcome = await _messaging.SendTextAsync(
            licenseId.Value, convo.CustomerPhone, text, origin: "panel", ct);

        return Ok(new SendResponse(
            outcome.Ok, outcome.ErrorCode, outcome.ErrorMessage, outcome.MessageId));
    }

    /// <summary>Okunmamış rozetini sıfırlar — sohbeti açan panel çağırır.</summary>
    [HttpPost("{conversationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid conversationId, CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        var convo = await _db.WaConversations.FirstOrDefaultAsync(
            c => c.Id == conversationId && c.LicenseId == licenseId.Value, ct);
        if (convo is null) return NotFound();

        // Zaten sıfırsa yazmıyoruz: panel her sohbet açılışında çağırıyor, aksi
        // hâlde her bakış boşuna bir UPDATE olurdu.
        if (convo.UnreadCount != 0)
        {
            convo.UnreadCount = 0;
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
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
