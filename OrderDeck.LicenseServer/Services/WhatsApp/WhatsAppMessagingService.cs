using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Giden WhatsApp mesajlarının tek kapısı: tenant kimliklerini çözer, 24 saat
/// service penceresini <b>Graph'a gitmeden önce</b> zorlar, gönderir ve sonucu
/// <see cref="WaConversation"/>/<see cref="WaMessage"/> olarak kalıcılaştırır.
///
/// <para><b>Neden pencereyi burada kontrol ediyoruz:</b> pencere kapalıyken text
/// göndermek Meta'dan 131047 hatası alır ve yayıncıya "gitti sandım" yanılgısı
/// yaşatır. Önden kesip <c>window_closed</c> döndürerek panel "template gönder"
/// yoluna yönlendirebiliyor.</para>
///
/// <para><b>Başarısız gönderim de yazılır</b> (Status="failed"): operatör panelde
/// neden gitmediğini görsün. Bu satırların <c>WamId</c>'si yoktur → yerel
/// <c>local:{guid}</c> anahtar kullanılır (unique index'i bozmamak için).</para>
/// </summary>
public sealed class WhatsAppMessagingService
{
    private readonly LicenseDbContext _db;
    private readonly IWhatsAppSender _sender;
    private readonly WhatsAppAccountService _accounts;
    private readonly ILogger<WhatsAppMessagingService> _log;

    public WhatsAppMessagingService(
        LicenseDbContext db,
        IWhatsAppSender sender,
        WhatsAppAccountService accounts,
        ILogger<WhatsAppMessagingService> log)
    {
        _db = db;
        _sender = sender;
        _accounts = accounts;
        _log = log;
    }

    /// <summary>Gönderim sonucu + oluşan mesaj satırının id'si (varsa).</summary>
    public sealed record SendOutcome(bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId)
    {
        public static SendOutcome Fail(string code, string message) => new(false, code, message, null);
    }

    /// <summary>Hesap bağlı değil / token çözülemiyor.</summary>
    public const string ErrNoAccount = "no_account";

    /// <summary>24s pencere kapalı — serbest metin yerine template gerekiyor.</summary>
    public const string ErrWindowClosed = "window_closed";

    /// <summary>Graph çağrısının KENDİ bütçesi — çağıranın token'ı buraya
    /// geçmiyor.
    ///
    /// <para><b>Neden:</b> WPF'in HttpClient timeout'u 10 sn ve ASP.NET Core
    /// istemci koptuğunda <c>RequestAborted</c>'ı iptal ediyor. O token'ı Graph
    /// POST'una taşırsak çağrı bilinmeyen bir noktada kesilir: Meta mesajı çoktan
    /// kabul edip faturalamış olabilir, biz hiç <c>WaMessage</c> yazmayız. Sonucu
    /// "bilinemez" yapan asıl kaynak buydu. Kendi bütçesiyle çağrı ya biter ya
    /// da kendi şartlarıyla düşer — her iki halde de sonuç kaydedilebilir.</para>
    ///
    /// <para>Gönderenin kendi HTTP timeout'u (<c>OrderDeck:WhatsApp:TimeoutSeconds</c>,
    /// varsayılan 15 sn — bkz. Program.cs) normalde önce dolar ve yapısal bir
    /// <c>network</c> hatası döner; buradaki bütçe onun üstünde duran son
    /// emniyet kemeri, iki timeout birbiriyle yarışmasın diye bilerek daha
    /// gevşek.</para></summary>
    private static readonly TimeSpan GraphCallBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Serbest-metin (service) mesajı. Pencere kapalıysa Graph'a gitmez,
    /// <see cref="ErrWindowClosed"/> döner.
    /// </summary>
    public Task<SendOutcome> SendTextAsync(
        Guid licenseId, string toPhone, string text, string origin, CancellationToken ct)
        => SendWithFallbackAsync(licenseId, toPhone, text, null, origin, ct);

    /// <summary>
    /// Pencere AÇIKSA serbest metin, KAPALIYSA <paramref name="fallback"/> şablonu
    /// gönderir. <paramref name="fallback"/> null ise kapalı pencerede
    /// <see cref="ErrWindowClosed"/> döner (eski davranış).
    ///
    /// <para><b>Neden tek metot:</b> önce <c>SendTextAsync</c> deneyip
    /// <c>window_closed</c> görünce <c>SendTemplateAsync</c> çağırmak, sohbeti
    /// İKİ KEZ çözerdi. Kapalı pencerede hiçbir şey kaydedilmediği için ilk
    /// çağrının <c>Add</c>'lediği <see cref="WaConversation"/> izleyicide
    /// kaydedilmemiş kalır; ikinci çağrının DB sorgusu onu göremez ve aynı
    /// (lisans, numara) için ikinci bir satır eklenir. Pencere kararı ve sohbet
    /// çözümü aynı yerde kalmak zorunda.</para>
    /// </summary>
    public async Task<SendOutcome> SendWithFallbackAsync(
        Guid licenseId, string toPhone, string text, WhatsAppTemplate? fallback,
        string origin, CancellationToken ct)
    {
        var phone = WaPhone.Canonical(toPhone);
        if (phone.Length == 0) return SendOutcome.Fail("bad_phone", "Geçersiz telefon numarası.");
        if (string.IsNullOrWhiteSpace(text)) return SendOutcome.Fail("empty_body", "Mesaj boş olamaz.");

        var ctx = await _accounts.ResolveSendContextAsync(licenseId, ct);
        if (ctx is null)
            return SendOutcome.Fail(ErrNoAccount, "Bu lisansa bağlı aktif WhatsApp hesabı yok.");

        var now = DateTimeOffset.UtcNow;
        var convo = await GetOrCreateConversationAsync(licenseId, phone, ctx.PhoneNumberId, null, now, ct);
        var windowOpen = WhatsAppServiceWindow.IsOpen(convo.LastInboundAt, now);

        if (!windowOpen && fallback is null)
        {
            return SendOutcome.Fail(
                ErrWindowClosed,
                "24 saatlik yanıt penceresi kapalı — onaylı şablon (template) gönderilmeli.");
        }

        // ct BİLEREK geçilmiyor: bkz. GraphCallBudget. İstemcinin kopması,
        // Meta'nın kabul edip etmediğini bilemediğimiz bir gönderim bırakmamalı.
        using var sendCts = new CancellationTokenSource(GraphCallBudget);

        WhatsAppSendResult result;
        WaMessage msg;
        if (windowOpen)
        {
            result = await _sender.SendTextAsync(ctx, phone, text, sendCts.Token);
            msg = PersistOutbound(convo, licenseId, "text", text, null, origin, result, now);
        }
        else
        {
            result = await _sender.SendTemplateAsync(ctx, phone, fallback!, sendCts.Token);
            msg = PersistOutbound(convo, licenseId, "template",
                TemplateBody(fallback!), fallback!.Name, origin, result, now);
        }

        // Graph çağrısı DÖNDÜKTEN sonra kayıt iptal edilebilir olmamalı: istemcinin
        // zaman aşımı HttpContext.RequestAborted'ı tetikliyor ve ct'yi buraya
        // geçirirsek Meta'nın kabul ettiği — yani faturalanmış, müşteriye ulaşmış —
        // mesaj DB'de hiç görünmez. Bilerek CancellationToken.None.
        await _db.SaveChangesAsync(CancellationToken.None);

        if (!result.Ok)
            _log.LogWarning(
                "WhatsApp {Type} gönderilemedi (license={License}, code={Code}): {Msg}",
                windowOpen ? "text" : "template", licenseId, result.ErrorCode, result.ErrorMessage);

        return new SendOutcome(result.Ok, result.ErrorCode, result.ErrorMessage, msg.Id);
    }

    /// <summary>
    /// Onaylı template mesajı — pencereden bağımsız gönderilebilir (business-initiated,
    /// <b>ücretli</b>).
    /// </summary>
    public async Task<SendOutcome> SendTemplateAsync(
        Guid licenseId, string toPhone, WhatsAppTemplate template, string origin, CancellationToken ct)
    {
        var phone = WaPhone.Canonical(toPhone);
        if (phone.Length == 0) return SendOutcome.Fail("bad_phone", "Geçersiz telefon numarası.");

        var ctx = await _accounts.ResolveSendContextAsync(licenseId, ct);
        if (ctx is null)
            return SendOutcome.Fail(ErrNoAccount, "Bu lisansa bağlı aktif WhatsApp hesabı yok.");

        var now = DateTimeOffset.UtcNow;
        var convo = await GetOrCreateConversationAsync(licenseId, phone, ctx.PhoneNumberId, null, now, ct);

        // Serbest metinle aynı gerekçe (bkz. GraphCallBudget): template gönderimi
        // business-initiated ve ÜCRETLİ, yarıda kesilmesi daha da pahalı.
        using var sendCts = new CancellationTokenSource(GraphCallBudget);
        var result = await _sender.SendTemplateAsync(ctx, phone, template, sendCts.Token);
        var msg = PersistOutbound(convo, licenseId, "template",
            TemplateBody(template), template.Name, origin, result, now);
        // Gönderim bittikten sonra kayıt iptal edilebilir olamaz — Graph'a kendi
        // bütçesiyle çıkmanın tek anlamı sonucun HER ZAMAN yazılabilmesi. ct
        // burada kalsaydı kopan istemci, faturalanmış ama izi olmayan bir mesaj
        // bırakırdı (SendTextAsync ile aynı gerekçe).
        await _db.SaveChangesAsync(CancellationToken.None);

        if (!result.Ok)
            _log.LogWarning("WhatsApp template '{Name}' gönderilemedi (license={License}, code={Code}): {Msg}",
                template.Name, licenseId, result.ErrorCode, result.ErrorMessage);

        return new SendOutcome(result.Ok, result.ErrorCode, result.ErrorMessage, msg.Id);
    }

    /// <summary>Şablon gönderiminde <c>WaMessage.Body</c>'ye yazılan iz: onaylı
    /// gövde Meta'da durduğu için burada yalnız parametreler saklanır, panelde
    /// "hangi değerlerle gitti" sorusunu bu cevaplıyor.</summary>
    private static string? TemplateBody(WhatsAppTemplate template) =>
        template.BodyParams.Count == 0 ? null : string.Join(" | ", template.BodyParams);

    /// <summary>Sohbeti bulur, yoksa oluşturur. Yeni satır <c>SaveChanges</c> ile birlikte yazılır.</summary>
    public async Task<WaConversation> GetOrCreateConversationAsync(
        Guid licenseId, string canonicalPhone, string phoneNumberId,
        string? profileName, DateTimeOffset now, CancellationToken ct)
    {
        var convo = await _db.WaConversations
            .FirstOrDefaultAsync(c => c.LicenseId == licenseId && c.CustomerPhone == canonicalPhone, ct);

        if (convo is null)
        {
            convo = new WaConversation
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CustomerPhone = canonicalPhone,
                PhoneNumberId = phoneNumberId,
                ProfileName = profileName,
                Status = "open",
                CreatedAt = now,
            };
            _db.WaConversations.Add(convo);
        }
        else if (!string.IsNullOrWhiteSpace(profileName) && convo.ProfileName != profileName)
        {
            convo.ProfileName = profileName;
        }

        return convo;
    }

    // WaMessage kolon sınırları (bkz. LicenseDbContext). Buraya yazdığımız
    // değerlerin bir kısmı DIŞARIDAN geliyor — Meta'nın hata gövdesi, wamid,
    // çağıranın verdiği origin/template adı — ve hiçbir üst sınır garantisi yok.
    private const int MaxWamIdLength = 128;
    private const int MaxOriginLength = 16;
    private const int MaxBodyLength = 8000;
    private const int MaxTemplateNameLength = 200;
    private const int MaxErrorCodeLength = 32;
    private const int MaxErrorMessageLength = 1000;

    /// <summary>Kolon sınırına keser. EF InMemory <c>HasMaxLength</c>'i yok sayar,
    /// yani aşırı uzun değer testte hiç görünmez ve yalnız prod'da SQL 8152 olarak
    /// patlar. Burada patlamak sadece "kayıt yazılamadı" demek değil: gönderim
    /// zaten yapılmış olur, dolayısıyla faturalı mesaj izsiz kalır ve idempotency
    /// rezervasyonu pending'e çakılır.</summary>
    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    private WaMessage PersistOutbound(
        WaConversation convo, Guid licenseId, string type, string? body,
        string? templateName, string origin, WhatsAppSendResult result, DateTimeOffset now)
    {
        var msg = new WaMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = convo.Id,
            Conversation = convo,
            LicenseId = licenseId,
            // Başarısız gönderimde Meta id yok → unique index'i bozmayan yerel anahtar.
            WamId = result.Ok && !string.IsNullOrEmpty(result.MessageId)
                ? Truncate(result.MessageId, MaxWamIdLength)!
                : "local:" + Guid.NewGuid().ToString("N"),
            Direction = "out",
            Origin = Truncate(origin, MaxOriginLength),
            Type = type,
            Body = Truncate(body, MaxBodyLength),
            TemplateName = Truncate(templateName, MaxTemplateNameLength),
            Status = result.Ok ? "sent" : "failed",
            ErrorCode = Truncate(result.ErrorCode, MaxErrorCodeLength),
            ErrorMessage = Truncate(result.ErrorMessage, MaxErrorMessageLength),
            Timestamp = now,
            CreatedAt = now,
        };
        _db.WaMessages.Add(msg);

        // Giden mesaj pencereyi AÇMAZ — yalnız listedeki sıralamayı günceller.
        convo.LastMessageAt = now;
        return msg;
    }
}
