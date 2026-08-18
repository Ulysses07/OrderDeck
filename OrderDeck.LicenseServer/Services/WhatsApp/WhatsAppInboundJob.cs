using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Hangfire job: doğrulanmış webhook gövdesini ayrıştırıp sohbet/mesaj olarak
/// kalıcılaştırır.
///
/// <para><b>Neden arka planda:</b> Meta webhook'a ~5 sn içinde 200 bekler; geç
/// yanıt verirsek olayı tekrar gönderir ve arka arkaya başarısızlıkta abonelik
/// devre dışı bırakılabilir. Controller imzayı doğrulayıp hemen 200 döner,
/// asıl iş buraya kuyruklanır.</para>
///
/// <para><b>Idempotency:</b> <c>WaMessage.WamId</c> unique. İşlemeden önce var mı
/// diye bakarız; ayrıca DB unique index son savunma hattıdır — Meta aynı olayı
/// tekrar gönderse de sohbette çift mesaj oluşmaz.</para>
/// </summary>
public sealed class WhatsAppInboundJob
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly ILogger<WhatsAppInboundJob> _log;
    private readonly LabelRuleApplier _labels;
    private readonly WaDekontExtractor _dekonts;
    private readonly WhatsAppMediaDownloader? _media;

    public WhatsAppInboundJob(
        LicenseDbContext db,
        WhatsAppAccountService accounts,
        ILogger<WhatsAppInboundJob> log,
        LabelRuleApplier labels,
        WaDekontExtractor dekonts,
        WhatsAppMediaDownloader? media = null)
    {
        _db = db;
        _accounts = accounts;
        _log = log;
        _labels = labels;
        _dekonts = dekonts;
        _media = media;
    }

    /// <summary>Mesajlar commit edildikten SONRA uygulanacak etiket işi.
    /// Sohbet varlığı taşınır çünkü paket işlenirken henüz kaydedilmemiş
    /// olabilir; telefonu ancak sıra boşaltılırken okuruz.</summary>
    private readonly record struct PendingLabel(
        Guid LicenseId, WaLabelEvent Event, WaConversation Conversation);

    public async Task ProcessAsync(string rawJson, CancellationToken ct = default)
    {
        var events = WhatsAppWebhookParser.Parse(rawJson);
        if (events.IsEmpty) return;

        var pendingLabels = new List<PendingLabel>();
        await ProcessMessagesAsync(events, pendingLabels, ct);
        await ProcessStatusesAsync(events, ct);
        await _db.SaveChangesAsync(ct);

        // Etiket AYRI kaydedilir, mesajlarla aynı işlemi paylaşmaz.
        //
        // Sebep: etiket ekleme "önce bak, sonra yaz" ve son savunma hattı
        // IX_WaConversationLabels_ConversationId_WaLabelId. Aynı müşterinin iki
        // paketi eşzamanlı işlenirse biri yarışı kaybeder; tek SaveChanges
        // olsaydı o çakışma mesajları, durumları ve dekont özetini de geri
        // alırdı. Hangfire retry'ında medya baştan iner ve R2'ye yetim bir
        // kopya düşer; Meta da arka arkaya başarısızlıkta webhook aboneliğini
        // kapatabiliyor. Etiket tavsiye niteliğinde, bu bedele değmez.
        //
        // Sıra da bilinçli: sohbet müşteri ilk kez yazdığında YALNIZ yukarıdaki
        // SaveChanges'ten sonra var olur, etiket ancak ondan sonra bağlanabilir.
        //
        // Kabul edilen bedel: mesaj yazıldıktan sonra etiket kaydı düşerse
        // retry mesajı WamId'den atlar ve etiket hiç yapışmaz. Eksik bir
        // "Dekont geldi" bir tıkla telafi edilir; geri alınan paketin bedeli
        // yeniden indirme + retry fırtınası.
        foreach (var p in pendingLabels)
        {
            await _labels.TryApplyAndSaveAsync(
                p.LicenseId, p.Event, p.Conversation.CustomerPhone, ct);
        }
    }

    private async Task ProcessMessagesAsync(
        WhatsAppWebhookEvents events, List<PendingLabel> pendingLabels, CancellationToken ct)
    {
        foreach (var m in events.Messages)
        {
            var account = await _accounts.GetByPhoneNumberIdAsync(m.PhoneNumberId, ct);
            if (account is null)
            {
                // Bize ait olmayan/henüz bağlanmamış numara — sessizce at, retry etme.
                _log.LogWarning(
                    "WhatsApp webhook: bilinmeyen phone_number_id {Pnid}, mesaj atlandı", m.PhoneNumberId);
                continue;
            }

            if (await _db.WaMessages.AnyAsync(x => x.WamId == m.WamId, ct)) continue;

            var convo = await GetOrCreateConversationAsync(account, m, ct);

            // Medya, mesaj satırı yazılmadan ÖNCE indirilir: Meta'nın verdiği
            // URL 5 dakikada ölüyor, dolayısıyla erteleyecek lüksümüz yok.
            // Başarısızlık mesajı düşürmez — media null döner, metin/metadata kalır.
            var media = await TryFetchMediaAsync(account, m, ct);

            var message = new WaMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = convo.Id,
                Conversation = convo,
                LicenseId = account.LicenseId,
                WamId = m.WamId,
                Direction = m.IsEcho ? "out" : "in",
                Origin = m.IsEcho ? "echo" : null,
                Type = m.Type,
                Body = m.Body,
                MediaR2Key = media?.ObjectKey,
                MediaMimeType = media?.MimeType ?? m.MediaMimeType,
                MediaSizeBytes = media?.SizeBytes,
                Status = m.IsEcho ? "sent" : "received",
                Timestamp = m.Timestamp,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.WaMessages.Add(message);

            // Baytlar YALNIZ PDF için dolu gelir (bkz. WhatsAppMediaRef.Bytes).
            // Görsel dekontlar AI gerektirir, ayrı faz.
            if (!m.IsEcho && media?.Bytes is { Length: > 0 })
            {
                var extraction = _dekonts.TryExtract(account.LicenseId, message.Id, media.Bytes);
                if (extraction is not null)
                {
                    // Mesaj da bu SaveChanges'te yazılıyor; gezinme özelliği
                    // bağı açıkça kurup EF'e ekleme sırasını anlatıyor (PK = FK).
                    extraction.WaMessage = message;
                    _db.WaDekontExtractions.Add(extraction);
                }
            }

            if (convo.LastMessageAt is null || m.Timestamp > convo.LastMessageAt)
                convo.LastMessageAt = m.Timestamp;

            if (!m.IsEcho)
            {
                // Pencereyi YALNIZ müşteriden gelen mesaj açar.
                if (convo.LastInboundAt is null || m.Timestamp > convo.LastInboundAt)
                    convo.LastInboundAt = m.Timestamp;
                convo.UnreadCount++;
                // Operatör kapatmış olsa bile yeni mesaj sohbeti geri açar.
                convo.Status = "open";

                // Dekont olabilecek her şey tek olay: gelenin gerçekten dekont
                // olduğu bilinemez, yanlış etiketin bedeli bir tık.
                //
                // Burada yalnız sıraya alınır, yazılmaz (bkz. ProcessAsync).
                if (m.Type is "document" or "image")
                {
                    // Tek pakette iki belge gelebiliyor; aynı sohbet için
                    // gereksiz ikinci bir SaveChanges turu açmayalım.
                    var queued = pendingLabels.Any(p =>
                        p.Conversation.Id == convo.Id
                        && p.Event == WaLabelEvent.CustomerSentDocument);
                    if (!queued)
                    {
                        pendingLabels.Add(new PendingLabel(
                            account.LicenseId, WaLabelEvent.CustomerSentDocument, convo));
                    }
                }
            }
        }
    }

    /// <summary>Mesajda medya varsa indirip R2'ye yazar. Downloader kayıtlı
    /// değilse (dev/log provider) sessizce atlanır — mesaj yine kaydedilir.</summary>
    private async Task<WhatsAppMediaRef?> TryFetchMediaAsync(
        WhatsAppAccount account, WhatsAppInboundMessage m, CancellationToken ct)
    {
        if (_media is null || string.IsNullOrWhiteSpace(m.MediaId)) return null;

        var ctx = await _accounts.ResolveSendContextAsync(account.LicenseId, ct);
        if (ctx is null)
        {
            // Token çözülemiyor (DataProtection anahtarı döndü) — hesap yeniden
            // bağlanana kadar medya indirilemez, mesaj metadata'sıyla kalır.
            _log.LogWarning(
                "WhatsApp media atlandı: lisans {LicenseId} için token çözülemedi", account.LicenseId);
            return null;
        }

        return await _media.FetchAsync(m.MediaId, m.Type, ctx, account.LicenseId, ct);
    }

    private async Task<WaConversation> GetOrCreateConversationAsync(
        WhatsAppAccount account, WhatsAppInboundMessage m, CancellationToken ct)
    {
        var convo = _db.WaConversations.Local
            .FirstOrDefault(c => c.LicenseId == account.LicenseId && c.CustomerPhone == m.FromPhone)
            ?? await _db.WaConversations.FirstOrDefaultAsync(
                c => c.LicenseId == account.LicenseId && c.CustomerPhone == m.FromPhone, ct);

        if (convo is null)
        {
            convo = new WaConversation
            {
                Id = Guid.NewGuid(),
                LicenseId = account.LicenseId,
                CustomerPhone = m.FromPhone,
                PhoneNumberId = account.PhoneNumberId,
                ProfileName = m.ProfileName,
                Status = "open",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _db.WaConversations.Add(convo);
        }
        else if (!string.IsNullOrWhiteSpace(m.ProfileName) && convo.ProfileName != m.ProfileName)
        {
            convo.ProfileName = m.ProfileName;
        }

        return convo;
    }

    private async Task ProcessStatusesAsync(WhatsAppWebhookEvents events, CancellationToken ct)
    {
        foreach (var s in events.Statuses)
        {
            var msg = await _db.WaMessages.FirstOrDefaultAsync(x => x.WamId == s.WamId, ct);
            // Bizim kaydımız yoksa (ör. başka sistemden gönderilmiş) yok sayılır.
            if (msg is null) continue;

            // Durumlar sırasız gelebilir; geriye gitmeyelim (read → delivered olmasın).
            if (Rank(s.Status) < Rank(msg.Status)) continue;

            msg.Status = s.Status;
            if (s.ErrorCode is not null) msg.ErrorCode = s.ErrorCode;
            if (s.ErrorMessage is not null) msg.ErrorMessage = s.ErrorMessage;
        }
    }

    private static int Rank(string status) => status switch
    {
        "queued" => 0,
        "sent" => 1,
        "delivered" => 2,
        "read" => 3,
        "failed" => 4,   // terminal — hiçbir şey üzerine yazmasın
        _ => -1,
    };
}
