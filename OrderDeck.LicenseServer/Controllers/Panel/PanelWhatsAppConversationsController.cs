using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panelin sohbet listesi, sohbetin mesajları, cevap gönderme (serbest metin ve
/// onaylı şablon), etiket filtresi ve elle etiketleme.
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
    /// <summary>Medya linkinin ömrü. Ürün fotoğrafındaki 15 dakikadan kısa:
    /// buradaki dosya müşterinin dekontu olabiliyor (IBAN, ad soyad) ve
    /// presigned link imzayı taşıdığı için ömrü boyunca token'sız okunabilir.
    /// Tarayıcı <c>img</c>/<c>video</c> etiketini render anında indirdiğinden
    /// kısaltmanın görünür bir bedeli yok.</summary>
    private static readonly TimeSpan MediaUrlLifetime = TimeSpan.FromMinutes(5);

    private readonly LicenseDbContext _db;
    private readonly WhatsAppMessagingService _messaging;
    private readonly IWhatsAppMediaStore _mediaStore;

    public PanelWhatsAppConversationsController(
        LicenseDbContext db, WhatsAppMessagingService messaging, IWhatsAppMediaStore mediaStore)
    {
        _db = db;
        _messaging = messaging;
        _mediaStore = mediaStore;
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

    /// <summary>
    /// Numarasız geldiği için sohbet listesine hiç düşemeyen mesajların özeti.
    ///
    /// <para><b>Eşik burada değil panelde:</b> uç kümülatif toplamı ve son
    /// görülme anını döndürüyor, "uyarı gösterilsin mi" kararını istemci
    /// veriyor. Aynı sebep <see cref="ConversationDto.WindowExpiresAt"/>'teki
    /// sebep — bu cevap önbelleğe alınıyor; sunucu "göster/gösterme" bayrağı
    /// yollasaydı bayrak bayatlar, uyarı kayıp durduktan günler sonra da
    /// ekranda kalırdı. Mutlak anı istemci her render'da
    /// <c>Date.now()</c> ile karşılaştırabiliyor.</para>
    /// </summary>
    public sealed record DroppedInboundDto(
        int CustomerCount, int MessageCount,
        DateTimeOffset? FirstSeenAt, DateTimeOffset? LastSeenAt);

    [HttpGet("dropped-inbound")]
    public async Task<IActionResult> DroppedInbound(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Ok(new DroppedInboundDto(0, 0, null, null));

        // Satır sayısı = kaç MÜŞTERİ (tablo müşteri başına tek satır tutuyor),
        // MessageCount toplamı = kaç mesaj. İkisi ayrı sorular: "3 müşteri"
        // yayıncıya kaç kişiyi kaçırdığını, "11 mesaj" kaybın hacmini söyler.
        var summary = await _db.WaDroppedInbounds
            .Where(d => d.LicenseId == licenseId.Value)
            .GroupBy(_ => 1)
            .Select(g => new DroppedInboundDto(
                g.Count(),
                g.Sum(x => x.MessageCount),
                g.Min(x => (DateTimeOffset?)x.FirstSeenAt),
                g.Max(x => (DateTimeOffset?)x.LastSeenAt)))
            .FirstOrDefaultAsync(ct);

        // Hiç kayıp yoksa grup oluşmaz; sıfırlı gövde dönüyoruz ki panel
        // "veri yok" ile "kayıp yok" ayrımı yapmak zorunda kalmasın.
        return Ok(summary ?? new DroppedInboundDto(0, 0, null, null));
    }

    /// <summary>
    /// <c>MediaUrl</c>: R2'deki dosyanın kısa ömürlü (<see cref="MediaUrlLifetime"/>)
    /// imzalı indirme linki; medya yoksa ya da indirilememişse null.
    ///
    /// <para><b>Neden vekil uç değil de imzalı link:</b> panel API'ye Bearer
    /// JWT ile konuşuyor, ama <c>img</c>/<c>video</c> etiketi istek başlığı
    /// göndermiyor. Vekilden geçirmek için dosyayı JS'le blob olarak indirmek
    /// gerekirdi; o da video/ses'te aralık (range) isteğini ve tarayıcının
    /// aşamalı oynatmasını kaybettirirdi. Ürün fotoğrafı yolu da (
    /// <c>PanelProductPhotoController</c>) aynı deyimi kullanıyor.</para>
    ///
    /// <para><b>Link her istekte yeniden üretiliyor</b>, saklanmıyor: imza
    /// süreli olduğu için saklanan kopya bayatlar ve panelin arka planda
    /// tazelediği listede kırık görsel olarak görünürdü.</para>
    /// </summary>
    public sealed record MessageDto(
        Guid Id, string Direction, string Type, string? Body, string Status,
        string? Origin, string? TemplateName, string? MediaMimeType, string? MediaUrl,
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

        var dtos = new List<MessageDto>(rows.Count);
        foreach (var m in rows.OrderBy(m => m.Timestamp))
        {
            // Anahtar yoksa dosya bizde hiç yok: medya indirilememiş ya da mesaj
            // geçmiş senkronundan gelmiş olabilir (geçmişte medya bilerek
            // inmiyor). Uydurma link üretmek yerine null bırakılıyor; panel o
            // durumda eskisi gibi yalnız "Görsel"/"Belge" etiketi basıyor.
            var url = string.IsNullOrWhiteSpace(m.MediaR2Key)
                ? null
                : await _mediaStore.CreateDownloadUrlAsync(m.MediaR2Key, MediaUrlLifetime, ct);

            dtos.Add(new MessageDto(
                m.Id, m.Direction, m.Type, m.Body, m.Status, m.Origin, m.TemplateName,
                m.MediaMimeType, url, m.ErrorCode, m.ErrorMessage, m.Timestamp));
        }

        return Ok(dtos);
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
    /// <c>window_closed</c> döner; o hâlde <see cref="SendTemplate"/>
    /// kullanılmalı.</para>
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

    public sealed record SendTemplateRequest(string Name, string Language, List<string>? Params);

    /// <summary>Meta'nın şablon adı alfabesi: küçük harf, rakam, alt çizgi.</summary>
    private static readonly Regex TemplateName =
        new("^[a-z0-9_]{1,512}$", RegexOptions.CultureInvariant);

    /// <summary>Dil kodu: <c>tr</c>, <c>en_US</c>, <c>pt_BR</c>…</summary>
    private static readonly Regex TemplateLanguage =
        new("^[A-Za-z]{2,3}([_-][A-Za-z0-9]{2,4})?$", RegexOptions.CultureInvariant);

    /// <summary>Meta'nın gövde parametresi sınırı.</summary>
    private const int MaxParamLength = 1024;

    /// <summary>Bir şablonda bu kadar değişken olması pratikte imkânsız; sınır
    /// yalnız uydurma bir isteğin uzun bir Graph gövdesine dönüşmesini kesiyor.</summary>
    private const int MaxParamCount = 20;

    /// <summary>
    /// Sohbete Meta'da ONAYLI şablon gönderir — 24 saatlik pencere kapalıyken
    /// gönderilebilen tek mesaj türü.
    ///
    /// <para><b>Pencere kontrolü yok, bilerek:</b> şablon business-initiated ve
    /// pencereden bağımsız geçerli. Pencere açıkken de gönderilebiliyor ama
    /// panel bunu sunmuyor; serbest metin hem bedava hem daha doğal.</para>
    ///
    /// <para><b>Şablonun onaylı olup olmadığını Meta'ya sormuyoruz.</b> Panel
    /// listeyi <c>/api/panel/whatsapp-approved-templates</c>'ten çekip seçtiriyor;
    /// burada ikinci kez sormak her gönderime bir Graph turu ekler ve Meta'nın
    /// zaten verdiği cevabı ("şablon yok", "parametre sayısı tutmuyor")
    /// tekrarlardı. O cevap <c>Status="failed"</c> satırı olarak sohbete
    /// yazıldığı için yayıncı sebebi görüyor.</para>
    ///
    /// <para><b>Yerelde neyi kesiyoruz:</b> Meta parametre içinde satır sonu,
    /// sekme ve 4+ ardışık boşluk kabul etmiyor. Bu, kopyala-yapıştır yapan
    /// operatörün düzenli olarak düşeceği tuzak ve hatası (132000) kriptik —
    /// üstelik şablon <b>ücretli</b>. Göndermeden söylemek doğrusu.</para>
    /// </summary>
    [HttpPost("{conversationId:guid}/send-template")]
    public async Task<IActionResult> SendTemplate(
        Guid conversationId, [FromBody] SendTemplateRequest req, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (!TemplateName.IsMatch(name))
            return Problem(title: "invalid-template-name", statusCode: 400, detail: "Şablon adı geçersiz.");

        var language = req.Language?.Trim() ?? "";
        if (!TemplateLanguage.IsMatch(language))
            return Problem(title: "invalid-template-language", statusCode: 400, detail: "Şablon dili geçersiz.");

        var parameters = req.Params ?? new List<string>();
        if (parameters.Count > MaxParamCount)
            return Problem(
                title: "too-many-parameters", statusCode: 400,
                detail: $"Bir şablonda en fazla {MaxParamCount} değişken olabilir.");

        for (var i = 0; i < parameters.Count; i++)
        {
            var value = parameters[i] ?? "";
            if (value.Trim().Length == 0)
                return Problem(
                    title: "empty-parameter", statusCode: 400,
                    detail: $"{i + 1}. değişken boş olamaz.");
            if (value.Length > MaxParamLength)
                return Problem(
                    title: "parameter-too-long", statusCode: 400,
                    detail: $"{i + 1}. değişken en fazla {MaxParamLength} karakter olabilir.");
            if (HasForbiddenWhitespace(value))
                return Problem(
                    title: "parameter-has-newline", statusCode: 400,
                    detail: $"{i + 1}. değişken satır sonu, sekme ya da arka arkaya boşluk içeremez.");
            parameters[i] = value;
        }

        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return NotFound();

        // Serbest metinle aynı gerekçe: numara istekten değil sohbetten.
        var convo = await _db.WaConversations.AsNoTracking().FirstOrDefaultAsync(
            c => c.Id == conversationId && c.LicenseId == licenseId.Value, ct);
        if (convo is null) return NotFound();

        var outcome = await _messaging.SendTemplateAsync(
            licenseId.Value, convo.CustomerPhone,
            new WhatsAppTemplate(name, language, parameters),
            origin: "panel", ct);

        return Ok(new SendResponse(
            outcome.Ok, outcome.ErrorCode, outcome.ErrorMessage, outcome.MessageId));
    }

    /// <summary>Meta'nın şablon parametresinde reddettiği boşluk biçimleri.</summary>
    private static bool HasForbiddenWhitespace(string value)
    {
        var runOfSpaces = 0;
        foreach (var ch in value)
        {
            if (ch == '\n' || ch == '\r' || ch == '\t') return true;
            runOfSpaces = ch == ' ' ? runOfSpaces + 1 : 0;
            if (runOfSpaces >= 4) return true;
        }
        return false;
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
