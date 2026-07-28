using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayıncının kendi lisansından tek WhatsApp mesajı göndermesi — WPF'in ödeme
/// hatırlatma akışı bunu çağırır.
///
/// <para><b>Neden admin ucundan ayrı:</b> <see cref="AdminWhatsAppSendController"/>
/// <c>Bearer-Admin</c> ile herhangi bir lisansa gönderir; WPF <c>Bearer-Customer</c>
/// taşır ve yalnız kendi lisansına gönderebilmelidir. Sahiplik kontrolü burada,
/// tek yerde.</para>
///
/// <para><b>Yalnız serbest metin.</b> 24 saatlik pencere kapalıysa Graph'a
/// çıkılmaz, gövdede <c>window_closed</c> döner ve WPF eski <c>wa.me</c>
/// davranışına düşer. Onaylı şablon gönderimi (pencereden bağımsız) otomatik
/// hatırlatma merdiveniyle birlikte gelecek.</para>
///
/// <para><b>Idempotency sözleşmesi:</b> istemci gövdede bir
/// <c>idempotencyKey</c> gönderirse, aynı anahtarla gelen ikinci istek yeni
/// mesaj göndermez — ilk gönderimin sonucu (başarı ya da hata) aynen tekrar
/// döner. İlk gönderim hâlâ uçuştaysa <c>in_progress</c> döner. Bu, WPF'in
/// HttpClient dayanıklılık katmanının 5xx/ağ hatasında POST'u yeniden
/// denemesine karşı koruma: gönderim gerçek para ve gerçek müşteri demek.
/// Anahtar gönderilmezse eski davranış (her istek yeni gönderim) sürer.</para>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/whatsapp/send")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWhatsAppSendController : ControllerBase
{
    /// <summary>WhatsApp metin gövdesi 4096 karakter; daha uzun istek Graph'a
    /// çıkmadan 400 ile reddedilir.</summary>
    private const int MaxTextLength = 4096;

    /// <summary><c>WaMessage.Origin</c> kolonunun uzunluğunu birebir yansıtır
    /// (bkz. <see cref="LicenseDbContext"/>); daha uzun değer SaveChanges'te
    /// truncation hatası verir.</summary>
    private const int MaxOriginLength = 16;

    /// <summary>Aynı anahtarla gelen gönderim hâlâ uçuşta.</summary>
    public const string ErrInProgress = "in_progress";

    private const string StatusPending = "pending";
    private const string StatusDone = "done";

    /// <summary>Bu süreden eski "pending" satır terk edilmiş sayılır (istek iptal edilmiş,
    /// sonuç hiç yazılmamış olabilir) — devralınıp yeniden denenir.</summary>
    private static readonly TimeSpan StalePendingAfter = TimeSpan.FromMinutes(2);

    private readonly LicenseDbContext _db;
    private readonly WhatsAppMessagingService _messaging;

    public LicensesWhatsAppSendController(LicenseDbContext db, WhatsAppMessagingService messaging)
    {
        _db = db;
        _messaging = messaging;
    }

    public sealed record SendRequest(string ToPhone, string Text, string? Origin, Guid? IdempotencyKey);

    public sealed record SendResponse(
        bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    [HttpPost]
    public async Task<IActionResult> Send(
        Guid licenseId, [FromBody] SendRequest req, CancellationToken ct)
    {
        if (WaPhone.Canonical(req.ToPhone).Length == 0)
            return Problem(title: "invalid-phone", statusCode: 400, detail: "Geçerli bir numara gerekli.");
        if (string.IsNullOrWhiteSpace(req.Text))
            return Problem(title: "empty-body", statusCode: 400, detail: "Mesaj boş olamaz.");
        if (req.Text.Length > MaxTextLength)
            return Problem(title: "text-too-long", statusCode: 400,
                detail: $"Mesaj en fazla {MaxTextLength} karakter olabilir.");

        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var origin = string.IsNullOrWhiteSpace(req.Origin) ? "wpf" : req.Origin.Trim();
        if (origin.Length > MaxOriginLength) origin = origin[..MaxOriginLength];

        // Rezervasyon Graph çağrısından ÖNCE yazılır: ilk deneme hâlâ uçuştayken
        // gelen bir tekrar da böylece tanınır.
        WaSendAttempt? attempt = null;
        if (req.IdempotencyKey is { } key && key != Guid.Empty)
        {
            var existing = await _db.WaSendAttempts.FirstOrDefaultAsync(a => a.Id == key, ct);
            if (existing is not null)
            {
                // Anahtar başka lisansa aitse bu çağıran onu görmemeli.
                if (existing.LicenseId != licenseId) return NotFound();
                var replay = ReplayIfKnown(existing);
                if (replay is not null) return Ok(replay);
                // Terk edilmiş rezervasyon → devral.
                existing.CreatedAt = DateTimeOffset.UtcNow;
                attempt = existing;
            }
            else
            {
                attempt = new WaSendAttempt
                {
                    Id = key,
                    LicenseId = licenseId,
                    Status = StatusPending,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _db.WaSendAttempts.Add(attempt);
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Yarış: iki istek aynı anda rezerve etmeye çalıştı. Kazananın satırını oku.
                _db.ChangeTracker.Clear();
                var winner = await _db.WaSendAttempts.FirstOrDefaultAsync(a => a.Id == key, ct);
                if (winner is null) throw;
                return Ok(ReplayIfKnown(winner)
                    ?? new SendResponse(false, ErrInProgress, "Aynı gönderim hâlâ işleniyor.", null));
            }
        }

        var outcome = await _messaging.SendTextAsync(licenseId, req.ToPhone, req.Text, origin, ct);

        if (attempt is not null)
        {
            attempt.Status = StatusDone;
            attempt.Ok = outcome.Ok;
            attempt.ErrorCode = Truncate(outcome.ErrorCode, 32);
            attempt.ErrorMessage = Truncate(outcome.ErrorMessage, 1000);
            attempt.MessageId = outcome.MessageId;
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        // Gönderilemedi ≠ istek hatalı: sebep (window_closed / no_account / Meta
        // hata kodu) gövdede taşınır. WPF wa.me'ye düşme kararını buna bakarak
        // verdiği için tek bir okuma yolu olması şart.
        return Ok(new SendResponse(
            outcome.Ok, outcome.ErrorCode, outcome.ErrorMessage, outcome.MessageId));
    }

    /// <summary>Bilinen bir sonuç varsa onu, hâlâ uçuşta ise in_progress döner;
    /// terk edilmiş (bayat) rezervasyon için null döner → çağıran devralır.</summary>
    private SendResponse? ReplayIfKnown(WaSendAttempt a) =>
        a.Status == StatusDone
            ? new SendResponse(a.Ok ?? false, a.ErrorCode, a.ErrorMessage, a.MessageId)
            : DateTimeOffset.UtcNow - a.CreatedAt < StalePendingAfter
                ? new SendResponse(false, ErrInProgress, "Aynı gönderim hâlâ işleniyor.", null)
                : null;

    /// <summary>Kolon sınırına kesme: EF InMemory <c>HasMaxLength</c>'i yok sayar,
    /// aşırı uzun değer testte görünmez ve yalnız prod'da SQL 8152 olarak patlar.</summary>
    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}
