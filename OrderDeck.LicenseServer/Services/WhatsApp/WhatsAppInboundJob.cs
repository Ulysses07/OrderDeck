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
    /// Sohbetin KENDİSİ taşınır, telefonu değil: telefonla arayan yol numarayı
    /// TR formatına normalize etmek zorunda ve yurt dışı numaralarını eliyor.
    /// Elimizdeki varlıkla eşleştirmenin böyle bir sınırı yok.
    ///
    /// <para>Alan değil PARAMETRE olarak dolaşır: job Hangfire çağrıları
    /// arasında durum tutmamalı.</para></summary>
    private readonly record struct PendingLabel(
        Guid LicenseId, WaLabelEvent Event, WaConversation Conversation);

    /// <summary>Tek webhook paketinin BİR sohbete yaptığı toplam etki.
    ///
    /// <para><b>Neden birikiyor:</b> okunmamış sayacı ve "en yeni zaman damgası"
    /// oku-değiştir-yaz alanları; izlenen varlık üzerinde güncellenirse EF bunu
    /// <c>SET UnreadCount = &lt;okunan+1&gt;</c> diye yazar ve aynı müşterinin iki
    /// paketini işleyen iki Hangfire çalışanı aynı değeri okuyup birbirinin
    /// artışını siler. Paket başına tek atomik UPDATE'e indirgeyip kararı
    /// veritabanına bırakıyoruz — bkz.
    /// <see cref="ApplyConversationDeltasAsync"/>.</para></summary>
    private sealed class ConversationDelta
    {
        public int UnreadDelta;
        public DateTimeOffset LastMessageAt;
        public DateTimeOffset? LastInboundAt;
        public bool Reopen;
    }

    public async Task ProcessAsync(string rawJson, CancellationToken ct = default)
    {
        var events = WhatsAppWebhookParser.Parse(rawJson);
        if (events.IsEmpty) return;

        var pendingLabels = new List<PendingLabel>();
        var deltas = new Dictionary<Guid, ConversationDelta>();
        await ProcessMessagesAsync(events, pendingLabels, deltas, ct);
        await ProcessStatusesAsync(events, ct);
        await _db.SaveChangesAsync(ct);
        await ApplyConversationDeltasAsync(deltas, ct);

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
        //
        // Eşleştirme telefonla DEĞİL sohbet nesnesiyle yapılır: telefon yolu
        // numarayı TR'ye normalize etmek zorunda ve yurt dışı bir wa_id'yi
        // sessizce eliyor. Sohbet zaten elimizde, çözmeye gerek yok.
        foreach (var p in pendingLabels)
        {
            try
            {
                // ApplyToConversationAsync yalnız satırı hazırlar; kaydı biz atarız.
                await _labels.ApplyToConversationAsync(p.LicenseId, p.Event, p.Conversation, ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // SİLME: yarışı kaybeden satır değişiklik izleyicisinde "Added"
                // olarak KALIR. Temizlemezsek bir sonraki turun SaveChanges'i onu
                // yeniden denemeye kalkar ve o turdaki masum etiketi de beraberinde
                // düşürür — tek bir çakışma sıradaki her şeyi zehirler. Bu yüzden
                // hâlâ eklenmeyi bekleyen tüm etiket satırlarını izleyiciden
                // ayırıp döngüyü temiz bir durumla sürdürüyoruz.
                foreach (var entry in _db.ChangeTracker
                             .Entries<WaConversationLabel>()
                             .Where(e => e.State == EntityState.Added)
                             .ToList())
                {
                    entry.State = EntityState.Detached;
                }

                _log.LogWarning(ex,
                    "Otomatik etiket uygulanamadı: sohbet {ConversationId}, lisans {LicenseId}, olay {Event}",
                    p.Conversation.Id, p.LicenseId, p.Event);
            }
        }
    }

    private async Task ProcessMessagesAsync(
        WhatsAppWebhookEvents events, List<PendingLabel> pendingLabels,
        Dictionary<Guid, ConversationDelta> deltas, CancellationToken ct)
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

            // Sohbet toplamları izlenen varlığa YAZILMAZ, biriktirilir; paket
            // sonunda tek atomik UPDATE'e dönüşür (bkz. ConversationDelta).
            if (!deltas.TryGetValue(convo.Id, out var delta))
                deltas[convo.Id] = delta = new ConversationDelta();

            if (m.Timestamp > delta.LastMessageAt) delta.LastMessageAt = m.Timestamp;

            if (!m.IsEcho)
            {
                // Pencereyi YALNIZ müşteriden gelen mesaj açar.
                if (delta.LastInboundAt is null || m.Timestamp > delta.LastInboundAt)
                    delta.LastInboundAt = m.Timestamp;
                delta.UnreadDelta++;
                // Operatör kapatmış olsa bile yeni mesaj sohbeti geri açar.
                delta.Reopen = true;

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

    /// <summary>Paketin sohbet toplamlarını (okunmamış sayacı, son mesaj/son
    /// gelen zamanı, yeniden açma) sohbet başına TEK atomik UPDATE ile uygular.
    ///
    /// <para><b>Neden atomik:</b> Meta aynı müşteriden arka arkaya paket
    /// gönderebiliyor ve Hangfire bunları paralel çalışanlarda işliyor. Alanlar
    /// izlenen varlıkta güncellenseydi her çalışan "okuduğu değeri + kendi
    /// katkısı"nı yazar, sonuncusu diğerini silerdi. Bedeli görünmez değil:
    /// kaybolan <c>UnreadCount</c> artışı rozeti eksik gösterir ve müşteri mesajı
    /// gözden kaçar; geriye giden <c>LastInboundAt</c> ise 24 saatlik hizmet
    /// penceresini erken kapatır — serbest metin hâlâ mümkünken sunucu ÜCRETLİ
    /// şablona düşer. Karar veritabanına bırakılıyor: sayaç <c>UnreadCount + @d</c>
    /// ile artıyor, zaman damgaları yalnız İLERİ gidebiliyor.</para>
    ///
    /// <para><b>Neden SaveChanges'ten SONRA:</b> müşteri ilk kez yazdığında
    /// sohbet satırı ancak o kayıtla var olur; önce çalışsaydı WHERE hiçbir satır
    /// bulmaz ve toplamlar sessizce kaybolurdu.</para></summary>
    private async Task ApplyConversationDeltasAsync(
        Dictionary<Guid, ConversationDelta> deltas, CancellationToken ct)
    {
        if (deltas.Count == 0) return;

        // InMemory sağlayıcısında ExecuteUpdate yok; orada eşzamanlılık semantiği
        // de olmadığı için aynı monoton kuralları izlenen varlığa uygulamak
        // davranışı birebir korur. Aynı ikili yol WaSendAttemptCleanupJob'da da var.
        if (!_db.Database.IsRelational())
        {
            foreach (var (id, d) in deltas)
            {
                var convo = await _db.WaConversations.FirstOrDefaultAsync(c => c.Id == id, ct);
                if (convo is null) continue;

                convo.UnreadCount += d.UnreadDelta;
                if (convo.LastMessageAt is null || convo.LastMessageAt < d.LastMessageAt)
                    convo.LastMessageAt = d.LastMessageAt;
                if (d.LastInboundAt is { } inbound
                    && (convo.LastInboundAt is null || convo.LastInboundAt < inbound))
                {
                    convo.LastInboundAt = inbound;
                }
                if (d.Reopen) convo.Status = "open";
            }

            await _db.SaveChangesAsync(ct);
            return;
        }

        foreach (var (id, d) in deltas)
        {
            var unreadDelta = d.UnreadDelta;
            var lastMessageAt = d.LastMessageAt;
            var lastInboundAt = d.LastInboundAt;
            // null → sohbetin mevcut durumu korunur (COALESCE).
            var reopenTo = d.Reopen ? "open" : null;

            await _db.WaConversations
                .Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.UnreadCount, c => c.UnreadCount + unreadDelta)
                    .SetProperty(c => c.LastMessageAt,
                        c => c.LastMessageAt == null || c.LastMessageAt < lastMessageAt
                            ? lastMessageAt
                            : c.LastMessageAt)
                    .SetProperty(c => c.LastInboundAt,
                        c => lastInboundAt != null
                             && (c.LastInboundAt == null || c.LastInboundAt < lastInboundAt)
                            ? lastInboundAt
                            : c.LastInboundAt)
                    .SetProperty(c => c.Status, c => reopenTo ?? c.Status),
                    ct);
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
