using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Otomatik etiket kurallarının TEK uygulama noktası.
///
/// <para><b>Neden tek servis:</b> olayı üreten beş ayrı yer var (ödeme onay,
/// ödeme ret, sipariş sync, kargo sync, gelen medya). Kural arama + telefon
/// normalize + sohbet bulma mantığı beş yere dağılsaydı, biri düzeltilip
/// diğeri unutulurdu.</para>
///
/// <para><b>Sessiz çıkış tasarımı:</b> kural tanımlı değilse, telefon TR
/// formatına oturmuyorsa ya da müşteri hiç WhatsApp'tan yazmamışsa yapılacak
/// bir şey yoktur. Bunların hiçbiri hata değil — istisna atmak, çağıran
/// tarafta iş akışını kesme riski demek olurdu.</para>
///
/// <para><b>Kaydetmez:</b> metotlar satırı yalnız DbContext'e ekler; commit
/// çağırana ait. Bir ödeme onayının etiket yüzünden geri alınmaması için
/// kural şu: iş kaydı önce commit edilir, etiket sonra
/// (<c>TryApplyAndSaveAsync</c> — Task 4).</para>
/// </summary>
public sealed class LabelRuleApplier
{
    private readonly LicenseDbContext _db;
    private readonly ILogger<LabelRuleApplier> _log;

    public LabelRuleApplier(LicenseDbContext db, ILogger<LabelRuleApplier> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Telefonla eşleştirir. <paramref name="phone"/> serbest formatta
    /// gelebilir (WPF alanı, shopper profili, panel girişi).
    /// </summary>
    public async Task ApplyAsync(
        Guid licenseId, WaLabelEvent eventKey, string? phone, CancellationToken ct)
    {
        var labelId = await FindRuleLabelIdAsync(licenseId, eventKey, ct);
        if (labelId is null) return;

        var canonical = ToConversationPhone(phone);
        if (canonical is null) return;

        var conversationId = await _db.WaConversations
            .Where(c => c.LicenseId == licenseId && c.CustomerPhone == canonical)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (conversationId is null) return;

        await StageAsync(licenseId, conversationId.Value, labelId.Value, "auto", ct);
    }

    /// <summary>
    /// Serbest formattaki numarayı <see cref="WaConversation.CustomerPhone"/>
    /// ile karşılaştırılabilir hâle getirir.
    ///
    /// <para>İKİ ADIM şart: <see cref="PhoneNormalizer"/> "0532…" / "532…" gibi
    /// yerel yazımları E.164'e ("+90532…") çeker ve TR dışını reddeder;
    /// <see cref="WaPhone.Canonical"/> ise '+' işaretini atarak Meta'nın
    /// <c>wa_id</c> formatına indirir — sohbet tablosundaki unique index bu
    /// forma dayanıyor. Tek adım yapılsaydı ya yerel yazımlar kaçardı ya da
    /// başındaki '+' yüzünden hiçbir sohbet eşleşmezdi.</para>
    /// </summary>
    public static string? ToConversationPhone(string? phone)
        => PhoneNormalizer.TryNormalize(phone, out var e164)
            ? WaPhone.Canonical(e164)
            : null;

    private Task<Guid?> FindRuleLabelIdAsync(
        Guid licenseId, WaLabelEvent eventKey, CancellationToken ct)
        => _db.WaLabelRules
            .Where(r => r.LicenseId == licenseId && r.EventKey == eventKey)
            .Select(r => (Guid?)r.WaLabelId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Etiketi sohbete ekler; zaten varsa dokunmaz.
    ///
    /// <para>Mükerrer kontrolü hem DB'ye hem değişiklik izleyicisine bakar:
    /// tek bir <c>SaveChanges</c> öncesinde aynı olay iki kez işlenirse (bir
    /// webhook paketinde iki belge) satır henüz DB'de olmaz ve yalnız sorguya
    /// güvenen bir kontrol unique index'i ihlal ederdi.</para>
    ///
    /// <para><paramref name="conversation"/> yalnız çağıran elinde <b>henüz
    /// kaydedilmemiş</b> bir sohbet varlığı tutuyorsa verilir; gezinme
    /// özelliğini doldurmak EF'e "önce sohbeti ekle" demenin tek güvenilir
    /// yoludur. Kaydedilmiş sohbetlerde <c>null</c> kalır.</para>
    /// </summary>
    private async Task StageAsync(
        Guid licenseId, Guid conversationId, Guid labelId, string source,
        CancellationToken ct, WaConversation? conversation = null)
    {
        var pending = _db.WaConversationLabels.Local
            .Any(x => x.ConversationId == conversationId && x.WaLabelId == labelId);
        if (pending) return;

        var exists = await _db.WaConversationLabels
            .AnyAsync(x => x.ConversationId == conversationId && x.WaLabelId == labelId, ct);
        if (exists) return;

        var row = new WaConversationLabel
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ConversationId = conversationId,
            WaLabelId = labelId,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (conversation is not null) row.Conversation = conversation;

        _db.WaConversationLabels.Add(row);
    }
}
