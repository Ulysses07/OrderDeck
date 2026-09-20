using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Hangfire job: bir <see cref="Domain.SmsCampaign"/>'in alıcılarına SMS gönderir.
/// Kampanya oluşturulurken krediler rezerve edilmiştir; burada alıcı başına
/// gönderim yapılır ve başarısız/atlanan alıcılar için kredi iade edilir
/// (yalnızca kabul edilen gönderim ücretlenir).
///
/// F08 (denetim 2026-09-09) — kesintiye dayanıklılık:
/// - Kampanya CAS ile üstlenilir (ClaimedAt concurrency token). Yarışı
///   kaybeden job iz bırakmadan çıkar → aynı kampanyayı iki işçi işleyemez.
/// - Her alıcının sonucu ANINDA kaydedilir (tek toplu SaveChanges değil) ve
///   ClaimedAt tazelenir (lease kalp atışı). Süreç ölürse en fazla 1 alıcı
///   belirsiz kalır; kalanı "pending" durur.
/// - Job "sending"de takılı kalmış kampanyayı da kabul eder — lease
///   (<see cref="ClaimLease"/>) bayatladıysa devralır ve yalnız "pending"
///   alıcıları gönderir: gönderilmiş SMS tekrarlanmaz.
/// - İade tutarı bellekteki sayaçtan değil DB'deki failed sayısından
///   hesaplanır → devralınan koşuda da doğru.
///
/// Görev 16 — alıcı yaşam döngüsü üçe çıktı:
/// <c>pending → sending (talep) → sent | failed</c>. Satır fiziksel
/// gönderimden ÖNCE <c>sending</c> olarak talep edilir ve talep yazımı
/// <see cref="Domain.SmsCampaignRecipient.ClaimedAt"/> jetonuyla CAS'tan
/// geçer; iki işçi aynı alıcıya asla gönderemez. <c>sending</c>'de takılı
/// kalan satır BİLİNÇLİ olarak kurtarılmaz ve iade edilmez — gerekçe o
/// alanın doc'unda.
/// </summary>
public sealed class SmsCampaignSendJob
{
    /// <summary>
    /// Bir claim'in bayat sayılması için geçmesi gereken süre. Kalp atışı
    /// alıcı başına attığı için canlı bir job'ın damgası bundan çok daha
    /// tazedir; 15 dk yalnız ölü süreçleri yakalar.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly ISmsSender _sms;
    private readonly LicenseSmsBalanceService _balance;
    private readonly NetgsmAccountService _accounts;
    private readonly ILogger<SmsCampaignSendJob> _log;

    public SmsCampaignSendJob(
        LicenseDbContext db,
        ISmsSender sms,
        LicenseSmsBalanceService balance,
        NetgsmAccountService accounts,
        ILogger<SmsCampaignSendJob> log)
    {
        _db = db;
        _sms = sms;
        _balance = balance;
        _accounts = accounts;
        _log = log;
    }

    /// <summary>
    /// Sahiplik damgasının bir sonraki değeri. Ham <c>UtcNow</c> ataması
    /// yetmez: saat monoton değil, üstelik duraklatma damgayı ileri
    /// atabiliyor. Aynı ya da geri giden bir damga, duraklatmadan ÖNCE
    /// kampanyayı okumuş işçinin üstlenmeyi geri kazanmasına yol açar.
    ///
    /// <para><c>NetgsmAccountService</c>'te aynı isimde bir metot var —
    /// bu AYRI bir sınıfın özel metodu, ortaklaştırılmadı: iki taraf da
    /// tek satırlık ve birbirine bağımlı değil.</para>
    ///
    /// <para><c>internal</c>: <see cref="SmsCampaignRecoveryJob"/>'ın asılı
    /// kalmış kampanyayı doğrudan tamamlayan dalı (Görev 15) AYNI sütunu
    /// yazıyor. Orada ayrı bir kopya tutmak, monotonluk kuralının iki yerde
    /// yaşaması ve birinin ileride sessizce ayrışması demek olurdu.</para>
    /// </summary>
    internal static DateTimeOffset NextClaimedAt(DateTimeOffset? previous)
    {
        var now = DateTimeOffset.UtcNow;
        return previous.HasValue && now <= previous.Value
            ? previous.Value.AddTicks(1)
            : now;
    }

    /// <summary>
    /// Alıcı sonucunu + kalp atışını kaydeder. Kampanya bu arada başkası
    /// tarafından yazıldıysa (duraklatma, devam ettirme) <c>false</c> döner
    /// ve çağıran koşuyu bitirir.
    ///
    /// <para><b>Neden kampanyayı yazımdan düşürüp yeniden kaydediyoruz?</b>
    /// SMS çağrısının dış etkisi GERÇEKLEŞTİ — operatöre gitti, geri alınamaz.
    /// Çakışmayı olduğu gibi dışarı bıraksaydık alıcı satırı <c>pending</c>
    /// kalırdı ve kampanya devam ettirildiğinde AYNI KİŞİYE ikinci kez SMS
    /// giderdi. Kampanyayı <c>Unchanged</c>'a çekip yeniden kaydetmek, karşı
    /// tarafın kararına (paused/pending) dokunmadan yalnız alıcının sonucunu
    /// diske indirir.</para>
    ///
    /// <para><b>Neden <c>Detached</c> değil?</b> Alıcı satırının kampanyaya
    /// zorunlu FK'si var; kampanyayı detach etmek izlenen alıcıları da
    /// ÇAĞLAYARAK detach eder ve az önce yazdığımız <c>sent</c> sonucu
    /// sessizce kaybolur (SaveChanges 0 satır yazar). <c>Unchanged</c> ilişkiyi
    /// koparmadan kampanyayı SaveChanges'in dışında bırakır.</para>
    ///
    /// <para><c>ReferenceEquals</c> kasıtlı: çakışan tek şey BU kampanya
    /// nesnesi değilse (ör. alıcı satırı) burası sorumlu değildir, istisna
    /// dışarı çıkar.</para>
    /// </summary>
    private async Task<bool> SaveRecipientResultAsync(
        SmsCampaign campaign, CancellationToken ct)
    {
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex) when (
            ex.Entries.Count > 0
            && ex.Entries.All(e => ReferenceEquals(e.Entity, campaign)))
        {
            _db.Entry(campaign).State = EntityState.Unchanged;
            await _db.SaveChangesAsync(ct);
            return false;
        }
    }

    /// <summary>
    /// Alıcı satırını fiziksel gönderimden ÖNCE talep eder (CAS). Satır
    /// başkasınınsa <c>false</c> döner ve çağıran o alıcıyı GÖNDERMEDEN atlar.
    ///
    /// <para><b>Neden gerekli.</b> Tur başı sahiplik yoklaması gönderimden
    /// ÖNCE koşuyor; yarış ise gönderim ile sonuç yazımı ARASINDA. İşçi A
    /// 1. alıcıya SMS gönderip sonucu henüz yazmamışken kampanya devralınırsa
    /// (duraklat+devam ettir ya da bayat kira) işçi B o alıcıyı hâlâ
    /// <c>pending</c> görür ve aynı kişiye ikinci ticari SMS gider. Klasik
    /// TOCTOU; kapatan tek şey satırın gönderimden önce talep edilmesi.</para>
    ///
    /// <para><b>Neden <c>ExecuteUpdateAsync</c> değil.</b> InMemory sağlayıcı
    /// desteklemiyor ve bu sınıfın testlerinin tamamı InMemory. Jeton +
    /// <c>SaveChanges</c> kalıbı her iki sağlayıcıda da çalışıyor ve
    /// <c>SmsCampaign.ClaimedAt</c> kalıbının birebir kardeşi.</para>
    ///
    /// <para><b>Çakışan varlık burada ALICI, kampanya değil</b> — kardeşi
    /// <see cref="SaveRecipientResultAsync"/> ile tek farkı bu. Kampanya
    /// çakışırsa istisna dışarı çıkar: sahiplik kaybı zaten bir sonraki turun
    /// yoklamasının işi, burada yutulursa sessizce yanlış yere maskelenir.</para>
    ///
    /// <para><c>Detached</c> değil <c>Unchanged</c> + yeniden okuma:
    /// gerekçe <see cref="SaveRecipientResultAsync"/>'te. Bellekteki kopya
    /// bayat olduğu için diskten tazelenmeli, yoksa kirli değerler bir sonraki
    /// <c>SaveChanges</c>'e biner.</para>
    /// </summary>
    private async Task<bool> ClaimRecipientAsync(
        SmsCampaignRecipient recipient, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex) when (
            ex.Entries.Count > 0
            && ex.Entries.All(e => ReferenceEquals(e.Entity, recipient)))
        {
            _db.Entry(recipient).State = EntityState.Unchanged;
            await _db.Entry(recipient).ReloadAsync(ct);

            _log.LogInformation(
                "SmsCampaignSendJob: recipient {RecipientId} claimed by another worker, skipping",
                recipient.Id);
            return false;
        }
    }

    public async Task RunAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _db.SmsCampaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId, ct);

        if (campaign is null)
        {
            _log.LogWarning("SmsCampaignSendJob: campaign {Id} not found", campaignId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var staleSending = campaign.Status == "sending"
            && (campaign.ClaimedAt is null || now - campaign.ClaimedAt >= ClaimLease);

        // "paused" bu kapıdan zaten geçemez: ne "pending" ne bayat "sending".
        if (campaign.Status != "pending" && !staleSending)
        {
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} status={Status} claimedAt={ClaimedAt}, skipping",
                campaignId, campaign.Status, campaign.ClaimedAt);
            return;
        }

        var resumed = campaign.Status == "sending";
        campaign.Status = "sending";
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Delik 3: okuma ile üstlenme arasında biri kampanyayı yazdı
            // (büyük olasılıkla duraklatma). Elimizdeki karar bayat — SESSİZCE
            // ÇEKİL. Detach şart: bu bağlamın kirli kopyası sonraki
            // SaveChanges'e binmemeli.
            _db.Entry(campaign).State = EntityState.Detached;
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} claim changed, skipping", campaignId);
            return;
        }

        if (resumed)
        {
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} resumed from stale 'sending' state",
                campaignId);
        }

        var recipients = await _db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId && r.Status == "pending")
            .ToListAsync(ct);

        var phones = recipients.Select(r => r.Phone).Distinct().ToList();
        var brandCode = await _accounts.GetBrandCodeAsync(campaign.LicenseId, ct);

        if (brandCode is null)
        {
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} lisansının doğrulanmış İYS markası yok",
                campaignId);
        }

        var consents = brandCode is null
            ? new Dictionary<string, IysConsent>()
            : await _db.IysConsents
                .Where(c => c.BrandCode == brandCode
                    && c.ChannelType == "MESAJ"
                    && c.RecipientType == "BIREYSEL"
                    && phones.Contains(c.Recipient))
                .ToDictionaryAsync(c => c.Recipient, ct);

        foreach (var recipient in recipients)
        {
            // §2.5: kurulum koşu sırasında kapatılabilir. Skaler projeksiyon
            // BİLİNÇLİ — anonim tipe `Select` kimlik çözümlemesine girmez,
            // yani bu bağlamda izlenen "sending" kopyasını değil DİSKTEKİ
            // değeri okur. Entity çekseydik kendi yazdığımızı geri okurduk.
            //
            // `ClaimedAt` karşılaştırması `Status` kontrolünün üstüne şunu
            // ekliyor: kampanya duraklatılıp YENİDEN "pending"/"sending"
            // yapıldıysa (Görev 13 devam ettirme) durum yine "sending"
            // görünebilir ama sahip ARTIK BİZ DEĞİLİZ. Damga bunu yakalar.
            var current = await _db.SmsCampaigns
                .Where(c => c.Id == campaignId)
                .Select(c => new { c.Status, c.ClaimedAt })
                .FirstOrDefaultAsync(ct);

            if (current is null
                || current.Status != "sending"
                || current.ClaimedAt != campaign.ClaimedAt)
            {
                // İade YOK: kalan alıcılar "pending" ve rezervasyon onların
                // karşılığı. Burada iade edersek kampanya devam ettirildiğinde
                // aynı krediyi ikinci kez harcarız.
                _log.LogWarning(
                    "SmsCampaignSendJob: campaign {Id} ownership lost mid-run, stopping after {Sent} sends",
                    campaignId, recipients.Count(x => x.Status == "sent"));
                return;
            }

            // Görev 16 — satırı GÖNDERİMDEN ÖNCE talep et. Sıra kutsal:
            // talep diske inmeden yapılan bir gönderim, çökme anında
            // "pending" görünen ama fiilen gitmiş bir SMS bırakır.
            // İzin kapısından da önce: kapı `failed` yazacaksa bile o yazımın
            // sahibi olduğumuzu bilmemiz gerekir.
            recipient.Status = "sending";
            recipient.ClaimedAt = NextClaimedAt(recipient.ClaimedAt);

            if (!await ClaimRecipientAsync(recipient, ct)) continue;

            consents.TryGetValue(recipient.Phone, out var consent);

            if (!IysConsentGate.CanSend(consent))
            {
                recipient.Status = "failed";
                recipient.Error = brandCode is null
                    ? "iys-brand-missing"
                    : consent is null
                        ? "iys-consent-missing"
                        : "iys-consent-not-onay";
                recipient.SentAt = null;

                if (!await SaveRecipientResultAsync(campaign, ct)) return;
                continue;
            }

            try
            {
                await _sms.SendAsync(
                    recipient.Phone, campaign.MessageBody, SmsKind.Commercial, ct);

                recipient.Status = "sent";
                recipient.SentAt = DateTimeOffset.UtcNow;
                recipient.Error = null;
            }
            catch (Exception ex)
            {
                recipient.Status = "failed";
                recipient.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;

                _log.LogWarning(ex,
                    "SmsCampaignSendJob: send failed for campaign {Id} recipient {RecipientId}",
                    campaignId, recipient.Id);
            }

            if (!await SaveRecipientResultAsync(campaign, ct)) return;
        }

        // İade, bu koşunun sayacından değil DB'deki toplam failed sayısından:
        // devralınan koşuda önceki koşunun failed'ları da iade edilmeli
        // (önceki koşu tamamlanamadığı için hiç iade yapmamıştı).
        var failedCount = await _db.SmsCampaignRecipients
            .CountAsync(r => r.CampaignId == campaignId && r.Status == "failed", ct);

        campaign.Status = "completed";
        campaign.CompletedAt = DateTimeOffset.UtcNow;
        // Delik 2: tamamlanmada da damga tazelenir. Tazelemezsek, elinde
        // tamamlanma ÖNCESİ kopya tutan bir "duraklat" yazımı (Görev 12)
        // çakışma ALMAZ ve bitmiş kampanyayı paused'a çevirir. Tazeleyince o
        // yazım DbUpdateConcurrencyException alır, yeniden okur ve kampanyayı
        // artık pending/sending listesinde bulamaz — doğru olanı yapar.
        campaign.ClaimedAt = NextClaimedAt(campaign.ClaimedAt);

        // Delik 1 — iade İDEMPOTENT: hak edilen toplamın, bugüne dek FİİLEN
        // iade edilenin üstünde kalan kısmı ödenir. Kampanya duraklatılıp
        // devam ettirilerek ikinci kez tamamlanırsa failedCount aynı kalır,
        // fark sıfır çıkar ve ikinci bir iade yazılmaz.
        //
        // İKİZİ: SmsCampaignRecoveryJob.CompleteStrandedAsync. Orası asılı
        // kalmış "paused" kampanyaları tamamlıyor; formül BİREBİR aynı olmak
        // zorunda, biri değişirse öbürü de değişmeli.
        var owed = failedCount * campaign.SegmentsPerMessage;
        var refund = owed - campaign.RefundedCredits;

        if (refund > 0)
        {
            // N05: gerçekleşen iade kampanyaya da yazılır — iade tx'iyle aynı
            // SaveChanges'te (atomik), raporlama hesap yerine bunu okur.
            campaign.RefundedCredits = owed;
            await _balance.ApplyAndSaveAsync(
                campaign.LicenseId, refund, "send-refund",
                reason: $"campaign:{campaignId} failed={failedCount}",
                createdByCustomerId: null, disallowNegative: false, ct);
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "SmsCampaignSendJob: campaign {Id} completed — {Sent} sent this run, {Failed} failed total",
            campaignId, recipients.Count(r => r.Status == "sent"), failedCount);
    }
}
