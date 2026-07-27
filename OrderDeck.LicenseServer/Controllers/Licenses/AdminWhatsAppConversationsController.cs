using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Bir lisansın WhatsApp sohbetlerini <b>salt-okunur</b> gösterir.
///
/// <para><b>Neden var:</b> gelen mesajlar DB'ye yazılıyordu ama onları görecek
/// hiçbir uç yoktu — "webhook çalıştı mı" sorusu ancak sunucuya SSH'lanıp
/// tabloya bakarak yanıtlanabiliyordu. Panel/WPF arayüzü yazılana kadar
/// doğrulama ve destek yolu burası.</para>
///
/// <para><see cref="ConversationSummary.WindowOpen"/> özellikle önemli: serbest
/// metin gönderimi neden <c>window_closed</c> döndü sorusunun cevabı bu alanda,
/// tahmin etmeye gerek kalmıyor.</para>
/// </summary>
[ApiController]
[Route("api/v1/admin/licenses/{licenseId:guid}/whatsapp/conversations")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminWhatsAppConversationsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public AdminWhatsAppConversationsController(LicenseDbContext db) => _db = db;

    public sealed record ConversationSummary(
        Guid Id,
        string CustomerPhone,
        string? ProfileName,
        string Status,
        bool WindowOpen,
        DateTimeOffset? WindowExpiresAt,
        DateTimeOffset? LastInboundAt,
        DateTimeOffset? LastMessageAt,
        int UnreadCount);

    public sealed record MessageItem(
        Guid Id,
        string Direction,
        string Type,
        string? Body,
        string Status,
        string? Origin,
        string? TemplateName,
        string? MediaMimeType,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset Timestamp);

    /// <summary>Son hareket görene göre sıralı sohbet listesi.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid licenseId, [FromQuery] int limit, CancellationToken ct)
    {
        if (!await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct)) return NotFound();

        var take = limit is > 0 and <= 200 ? limit : 50;
        var now = DateTimeOffset.UtcNow;

        var rows = await _db.WaConversations
            .AsNoTracking()
            .Where(c => c.LicenseId == licenseId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        return Ok(rows.Select(c => new ConversationSummary(
            c.Id, c.CustomerPhone, c.ProfileName, c.Status,
            WhatsAppServiceWindow.IsOpen(c.LastInboundAt, now),
            WhatsAppServiceWindow.ExpiresAt(c.LastInboundAt),
            c.LastInboundAt, c.LastMessageAt, c.UnreadCount)));
    }

    /// <summary>
    /// Bir sohbetin mesajları, eskiden yeniye. Sıralama <c>Timestamp</c> ile —
    /// gecikmeli webhook'ta satırın yazılma anı yanıltıcı olabiliyor.
    /// </summary>
    [HttpGet("{conversationId:guid}/messages")]
    public async Task<IActionResult> Messages(
        Guid licenseId, Guid conversationId, [FromQuery] int limit, CancellationToken ct)
    {
        // LicenseId koşulu şart: sohbet id'si tahmin edilse bile başka tenant'ın
        // yazışması okunamamalı.
        var exists = await _db.WaConversations
            .AnyAsync(c => c.Id == conversationId && c.LicenseId == licenseId, ct);
        if (!exists) return NotFound();

        var take = limit is > 0 and <= 500 ? limit : 100;

        var rows = await _db.WaMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LicenseId == licenseId)
            .OrderByDescending(m => m.Timestamp)
            .Take(take)
            .ToListAsync(ct);

        return Ok(rows
            .OrderBy(m => m.Timestamp)
            .Select(m => new MessageItem(
                m.Id, m.Direction, m.Type, m.Body, m.Status, m.Origin, m.TemplateName,
                m.MediaMimeType, m.ErrorCode, m.ErrorMessage, m.Timestamp)));
    }
}
