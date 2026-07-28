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
/// Anahtar gönderilmezse (alan hiç yoksa) eski davranış (her istek yeni
/// gönderim) sürer; boş Guid ise istek 400 ile reddedilir.</para>
///
/// <para><b>in_progress asla "çöktük" demez.</b> <b>Gönderim</b> beklenmedik bir
/// hatayla patlarsa satır <c>done</c>/<c>server_error</c> olarak damgalanır —
/// yoksa saniyeler sonra gelen yeniden-deneme in_progress alır, WPF sanki
/// gitmiş gibi davranır ve müşteriye hiçbir şey ulaşmaz.</para>
///
/// <para><b>Ama "yazamadım" da "gönderemedim" demez.</b> Gönderim başarılıysa ve
/// yalnız sonuç yazımı düşerse satır server_error damgalanmaz ve yanıt yine
/// 200/ok döner: aksi halde tekrar isteği bir hatayı oynatır, WPF wa.me'ye düşer
/// ve operatör aynı müşteriye ikinci faturalı mesajı yollar.</para>
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

    /// <summary><c>WaSendAttempt.ErrorCode</c> kolonunun uzunluğunu birebir
    /// yansıtır (bkz. <see cref="LicenseDbContext"/>).</summary>
    private const int MaxErrorCodeLength = 32;

    /// <summary><c>WaSendAttempt.ErrorMessage</c> kolonunun uzunluğunu birebir
    /// yansıtır (bkz. <see cref="LicenseDbContext"/>). Meta'nın hata gövdesi
    /// bundan uzun gelebiliyor.</summary>
    private const int MaxErrorMessageLength = 1000;

    /// <summary>Aynı anahtarla gelen gönderim hâlâ uçuşta.</summary>
    public const string ErrInProgress = "in_progress";

    /// <summary>Gönderim sırasında beklenmedik sunucu hatası oluştu — sonuç
    /// bilinmiyor ama "uçuşta" da değil.</summary>
    public const string ErrServerError = "server_error";

    private const string StatusPending = "pending";
    private const string StatusDone = "done";

    /// <summary>Bu süreden eski "pending" satır terk edilmiş sayılır (istek iptal edilmiş,
    /// sonuç hiç yazılmamış olabilir) — devralınıp yeniden denenir.</summary>
    private static readonly TimeSpan StalePendingAfter = TimeSpan.FromMinutes(2);

    private readonly LicenseDbContext _db;
    private readonly WhatsAppMessagingService _messaging;
    private readonly ILogger<LicensesWhatsAppSendController> _log;

    public LicensesWhatsAppSendController(
        LicenseDbContext db,
        WhatsAppMessagingService messaging,
        ILogger<LicensesWhatsAppSendController> log)
    {
        _db = db;
        _messaging = messaging;
        _log = log;
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

        // Boş Guid "anahtar yok" demek DEĞİL: istemci bozuk bir anahtar üretmişse
        // idempotency sessizce kapanır ve para yolunda çift gönderim serbest kalır.
        // Alanı hiç göndermemek (null) eski davranışı korur, Guid.Empty reddedilir.
        if (req.IdempotencyKey == Guid.Empty)
        {
            return Problem(title: "invalid-idempotency-key", statusCode: 400,
                detail: "Idempotency anahtarı boş Guid olamaz.");
        }

        // Rezervasyon Graph çağrısından ÖNCE yazılır: ilk deneme hâlâ uçuştayken
        // gelen bir tekrar da böylece tanınır.
        WaSendAttempt? attempt = null;
        if (req.IdempotencyKey is { } key)
        {
            var existing = await _db.WaSendAttempts.FirstOrDefaultAsync(a => a.Id == key, ct);
            var isNewReservation = existing is null;
            if (existing is not null)
            {
                // Anahtar başka lisansa aitse bu çağıran onu görmemeli.
                if (existing.LicenseId != licenseId)
                {
                    _log.LogWarning(
                        "WhatsApp idempotency anahtarı başka lisansa ait (key={Key}, license={LicenseId})",
                        key, licenseId);
                    return NotFound();
                }

                var replay = ReplayIfKnown(existing);
                if (replay is not null)
                {
                    LogReplay(replay, key, licenseId);
                    return Ok(replay);
                }

                // Terk edilmiş rezervasyon → devral (yeni denemenin başlangıcı).
                _log.LogWarning(
                    "Bayat WhatsApp rezervasyonu devralındı (key={Key}, license={LicenseId}, önceki başlangıç={StartedAt:o})",
                    key, licenseId, existing.StartedAt);
                existing.StartedAt = DateTimeOffset.UtcNow;
                attempt = existing;
            }
            else
            {
                attempt = new WaSendAttempt
                {
                    Id = key,
                    LicenseId = licenseId,
                    Status = StatusPending,
                    StartedAt = DateTimeOffset.UtcNow,
                };
                _db.WaSendAttempts.Add(attempt);
                _log.LogInformation(
                    "WhatsApp gönderimi rezerve edildi (key={Key}, license={LicenseId})", key, licenseId);
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (isNewReservation)
            {
                // Yalnız EKLEME çakışması in_progress'e dönüşebilir: devralma yolu
                // UPDATE'tir, orada DbUpdateException "başkası kazandı" değil gerçek
                // bir DB hatasıdır ve in_progress diye yutulursa WPF "gönderildi"
                // der, oysa hiçbir şey gitmemiştir.
                _db.ChangeTracker.Clear();
                var winner = await _db.WaSendAttempts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == key, ct);
                var winnerReplay = winner is null ? null : ReplayIfKnown(winner);
                // Ortada gerçek bir kazanan yoksa (satır yok ya da o da bayat)
                // yarış hikâyesi tutmuyor → hatayı sakla, 500 ile dışarı ver.
                if (winnerReplay is null) throw;
                _log.LogWarning(
                    "WhatsApp rezervasyon yarışı: anahtarı başka istek kazandı (key={Key}, license={LicenseId})",
                    key, licenseId);
                return Ok(winnerReplay);
            }
        }

        // GÖNDERİM ile SONUÇ YAZIMI ayrı try'larda: ikisi tek blokta olduğunda
        // gönderim başarılı olup yalnız yazım patladığında da "server_error"
        // damgalanıyordu. Yeniden-deneme o hatayı oynatır, WPF wa.me'ye düşer ve
        // operatör AYNI müşteriye ikinci faturalı mesajı yollar — bu özelliğin
        // önlemek için var olduğu şeyin ta kendisi.
        WhatsAppMessagingService.SendOutcome outcome;
        try
        {
            outcome = await _messaging.SendTextAsync(licenseId, req.ToPhone, req.Text, origin, ct);
        }
        catch (Exception ex) when (attempt is not null &&
                                   !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Rezervasyon "pending" kalırsa saniyeler sonra gelen yeniden-deneme
            // in_progress alır ve WPF "gönderildi" der — oysa hiçbir şey gitmedi.
            // in_progress "gerçekten uçuşta" demek zorunda, "çöktük" demek değil.
            // (Gerçek iptalde satır bilerek pending bırakılır: orada in_progress
            // dürüst bir cevap, iki dakika sonra da bayat sayılıp devralınır.)
            await TryStampAsync(attempt.Id, ServerErrorOutcome);
            _log.LogError(ex,
                "WhatsApp gönderimi hata verdi (key={Key}, license={LicenseId}) — rezervasyon hata olarak damgalandı",
                attempt.Id, licenseId);
            throw;
        }

        if (attempt is not null)
        {
            try
            {
                attempt.Status = StatusDone;
                attempt.Ok = outcome.Ok;
                attempt.ErrorCode = Truncate(outcome.ErrorCode, MaxErrorCodeLength);
                attempt.ErrorMessage = Truncate(outcome.ErrorMessage, MaxErrorMessageLength);
                attempt.MessageId = outcome.MessageId;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                // Graph çağrısı DÖNDÜKTEN sonra iptal edilebilir kayıt olmaz:
                // istemcinin zaman aşımı RequestAborted'ı tetikliyor ve ct'yi
                // buraya geçirirsek Meta'nın kabul ettiği (faturalanmış, müşteriye
                // ulaşmış) mesaj DB'de hiç görünmez. Bilerek CancellationToken.None.
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Mesaj GİTTİ; sonucu yazamamak bunu başarısızlığa çeviremez.
                // Damgayı temiz bir izleyiciyle bir kez daha deniyoruz (tekrar
                // isteği gerçek sonucu oynatabilsin), ama başarısız olsa bile
                // yanıt 200/ok olarak çıkar — 500 dönmek yeniden-denemeyi ve
                // ardından çift gönderimi davet ederdi. Damga hiç yazılamazsa
                // satır pending kalır ve tekrar in_progress alır; WPF bunu artık
                // "gönderildi" değil "doğrula" olarak gösteriyor.
                _log.LogError(ex,
                    "WhatsApp gönderim sonucu yazılamadı (key={Key}, license={LicenseId}, ok={Ok}) — gönderim yapıldı, yanıt başarı olarak dönüyor",
                    attempt.Id, licenseId, outcome.Ok);
                await TryStampAsync(attempt.Id, outcome);
            }
        }

        // Gönderilemedi ≠ istek hatalı: sebep (window_closed / no_account / Meta
        // hata kodu) gövdede taşınır. WPF wa.me'ye düşme kararını buna bakarak
        // verdiği için tek bir okuma yolu olması şart.
        return Ok(new SendResponse(
            outcome.Ok, outcome.ErrorCode, outcome.ErrorMessage, outcome.MessageId));
    }

    private void LogReplay(SendResponse replay, Guid key, Guid licenseId)
    {
        if (replay.ErrorCode == ErrInProgress)
        {
            _log.LogWarning(
                "Aynı anahtarla gelen WhatsApp gönderimi hâlâ uçuşta (key={Key}, license={LicenseId})",
                key, licenseId);
        }
        else
        {
            _log.LogInformation(
                "WhatsApp gönderim sonucu tekrar oynatıldı (key={Key}, license={LicenseId}, ok={Ok}, code={Code})",
                key, licenseId, replay.Ok, replay.ErrorCode);
        }
    }

    /// <summary>Gönderim yolu patladığında yazılan sonuç. Genel metin saklanıyor,
    /// istisnanın kendisi DEĞİL: bu alan tekrar isteğine aynen geri oynatılıyor,
    /// yani istemciye çıkıyor. Ham exception metni sunucu/şema gibi iç detay
    /// taşıyabilir; teşhis için gereken her şey zaten LogError'da duruyor.</summary>
    private static readonly WhatsAppMessagingService.SendOutcome ServerErrorOutcome =
        new(false, ErrServerError, "Gönderim sırasında sunucu hatası oluştu.", null);

    /// <summary>Rezervasyonu verilen sonuçla kapatır. Damgalama başarısız olsa
    /// bile çağıranı gölgelememeli (asıl hata ya da başarılı gönderim) — bu
    /// yüzden kendi içinde yutulur.</summary>
    private async Task TryStampAsync(Guid key, WhatsAppMessagingService.SendOutcome outcome)
    {
        try
        {
            // Patlayan SaveChanges'in izlediği yarım kalmış değişiklikler (ör. çok
            // uzun ErrorMessage taşıyan WaMessage) hâlâ takipte; temizlemezsek
            // damgalama da aynı hatayla düşer.
            _db.ChangeTracker.Clear();
            var row = await _db.WaSendAttempts.FirstOrDefaultAsync(a => a.Id == key, CancellationToken.None);
            if (row is null) return;
            row.Status = StatusDone;
            row.Ok = outcome.Ok;
            row.ErrorCode = Truncate(outcome.ErrorCode, MaxErrorCodeLength);
            row.ErrorMessage = Truncate(outcome.ErrorMessage, MaxErrorMessageLength);
            row.MessageId = outcome.MessageId;
            row.CompletedAt = DateTimeOffset.UtcNow;
            // İstek iptal edilmiş olsa bile damga yazılmalı; yoksa satır pending
            // kalır ve tekrar in_progress alır.
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception stampEx)
        {
            _log.LogError(stampEx,
                "WhatsApp rezervasyonu damgalanamadı (key={Key}) — satır pending kalabilir", key);
        }
    }

    /// <summary>Bilinen bir sonuç varsa onu, hâlâ uçuşta ise in_progress döner;
    /// terk edilmiş (bayat) rezervasyon için null döner → çağıran devralır.</summary>
    private SendResponse? ReplayIfKnown(WaSendAttempt a) =>
        a.Status == StatusDone
            ? new SendResponse(a.Ok ?? false, a.ErrorCode, a.ErrorMessage, a.MessageId)
            : DateTimeOffset.UtcNow - a.StartedAt < StalePendingAfter
                ? new SendResponse(false, ErrInProgress, "Aynı gönderim hâlâ işleniyor.", null)
                : null;

    /// <summary>Kolon sınırına kesme: EF InMemory <c>HasMaxLength</c>'i yok sayar,
    /// aşırı uzun değer testte görünmez ve yalnız prod'da SQL 8152 olarak patlar.</summary>
    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];
}
