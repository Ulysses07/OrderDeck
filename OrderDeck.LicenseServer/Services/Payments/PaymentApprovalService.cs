using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Push;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Services.Payments;

/// <summary>Ödeme kararının sonucu; HTTP karşılığını çağıran verir.</summary>
public enum PaymentDecisionOutcome
{
    /// <summary>Karar kaydedildi ve yan etkileri yürütüldü (kuyruk, etiket ve bildirim arızası kararı düşürmez).</summary>
    Decided,

    /// <summary>Bu yayıncının böyle bir ödemesi yok.</summary>
    NotFound,

    /// <summary>Ödeme zaten karara bağlanmış ya da yarışan başka bir karar kazandı; hiçbir şey yazılmadı, yan etki yok.</summary>
    NotPending,
}

/// <summary>
/// Dekont kararının (onay/ret) tek çekirdeği (spec 2026-10-10 §4.1). Adımların sırası sözleşmedir:
/// <list type="number">
/// <item>Karar kaydı, <c>Payment.Status</c> eşzamanlılık jetonuyla: tek karar kazanır.</item>
/// <item>Yalnız onayda: gecikmeli gölge bağdaştırma (<see cref="ScheduleShadowReconcile"/>).</item>
/// <item>Müşterinin sohbetine otomatik etiket (<see cref="ApplyConversationLabelAsync"/>).</item>
/// <item>Shopper'a bildirim (<see cref="NotifyShopperPaymentDecisionAsync"/>).</item>
/// </list>
/// 2-4 yalnız kazanan karardan sonra çalışır ve onu geri almaz.
/// </summary>
public sealed class PaymentApprovalService
{
    private readonly LicenseDbContext _db;
    private readonly INotificationSender _push;
    private readonly LabelRuleApplier _labels;
    private readonly ILogger<PaymentApprovalService> _log;
    private readonly IBackgroundJobClient _jobs;

    public PaymentApprovalService(
        LicenseDbContext db,
        INotificationSender push,
        LabelRuleApplier labels,
        ILogger<PaymentApprovalService> log,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _push = push;
        _labels = labels;
        _log = log;
        _jobs = jobs;
    }

    /// <param name="customerId">Yayıncının (lisans sahibinin) kimliği; personel oturumunda da odur
    /// (<c>TenantClaims.GetTenantCustomerId</c>).</param>
    public async Task<PaymentDecisionOutcome> ApproveAsync(Guid paymentId, Guid customerId, CancellationToken ct)
    {
        var payment = await _db.Payments
            .Include(p => p.License)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.License.CustomerId == customerId, ct);
        if (payment is null) return PaymentDecisionOutcome.NotFound;

        if (payment.Status != PaymentStatus.Pending)
            return PaymentDecisionOutcome.NotPending;

        var now = DateTimeOffset.UtcNow;
        payment.Status = PaymentStatus.Approved;
        payment.ApprovedAt = now;
        payment.ApprovedByCustomerId = customerId;
        payment.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Status jetonu (bkz. LicenseDbContext): biz okuduktan sonra
            // yarışan başka bir karar kazandı. Etiket/push yalnız kazanandan
            // gider; buradan sessizce NotPending dönüyoruz — pending kontrolündeki
            // sonucun aynısı, istemci iki durumu ayırt etmek zorunda değil.
            return PaymentDecisionOutcome.NotPending;
        }

        // Onay commit edildi. Ölçüm etiket ve bildirimden ÖNCE zamanlanır (bkz. ScheduleShadowReconcile).
        ScheduleShadowReconcile(payment.Id);
        await ApplyConversationLabelAsync(payment, WaLabelEvent.PaymentApproved, ct);
        await NotifyShopperPaymentDecisionAsync(payment, approved: true, ct);
        return PaymentDecisionOutcome.Decided;
    }

    /// <param name="customerId">Bkz. <see cref="ApproveAsync"/>.</param>
    /// <param name="reason">Serbest metin; boşsa gerekçesiz ret, 500 karakterden uzunu kesilir.</param>
    public async Task<PaymentDecisionOutcome> RejectAsync(
        Guid paymentId, Guid customerId, string? reason, CancellationToken ct)
    {
        var payment = await _db.Payments
            .Include(p => p.License)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.License.CustomerId == customerId, ct);
        if (payment is null) return PaymentDecisionOutcome.NotFound;

        if (payment.Status != PaymentStatus.Pending)
            return PaymentDecisionOutcome.NotPending;

        reason = (reason ?? "").Trim();
        if (reason.Length > 500) reason = reason[..500];

        var now = DateTimeOffset.UtcNow;
        payment.Status = PaymentStatus.Rejected;
        payment.RejectedAt = now;
        payment.RejectedByCustomerId = customerId;
        payment.RejectReason = reason.Length > 0 ? reason : null;
        payment.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Yarışan karar kazandı — bkz. ApproveAsync'teki açıklama.
            return PaymentDecisionOutcome.NotPending;
        }

        await ApplyConversationLabelAsync(payment, WaLabelEvent.PaymentRejected, ct);
        await NotifyShopperPaymentDecisionAsync(payment, approved: false, ct);
        return PaymentDecisionOutcome.Decided;
    }

    /// <summary>
    /// Gölge banka eşleştirmesi ölçümü (spec §6): onayı gölge öneriyle bağdaştıran işi
    /// <see cref="PaymentMatchReconcileJob.ApprovalDelay"/> sonrasına zamanlar — dekontun kendi havalesi çekilmeden
    /// koşsaydı aynı tutarlı başka bir havaleyi bağlardı. Onay isteğinde KOŞMAZ: eşleştirme onayı yavaşlatmamalı, hatası
    /// onayı düşürmemeli. Ödeme SaveChanges'inden SONRA: commit olmamış onay için koşan iş "onaylı değil" diye çıkardı.
    /// Etiket ve bildirimden ÖNCE: onlar istek jetonunu kullanır, etiketin telefon sorgusu da try dışında; istemci commit'ten
    /// sonra koparsa ya da o sorgu atarsa eylem orada biter ve onay Faz 2 ölçümünden sessizce düşerdi (ne iş, ne gap, ne
    /// günlük). Ret bağlanmaz (spec §6 "ret öğretmez").
    /// </summary>
    private void ScheduleShadowReconcile(Guid paymentId)
    {
        try
        {
            _jobs.Schedule<PaymentMatchReconcileJob>(j => j.RunAsync(paymentId, CancellationToken.None),
                PaymentMatchReconcileJob.ApprovalDelay);
        }
        catch (Exception ex)
        {
            // Onay COMMIT EDİLDİ; kuyruk arızası onu geri almaz, 500 göstermek yalan olur. Bu onay ölçümden düşer (gap de
            // yazılmaz). Mesaj DEĞİL yalnız tür adı: kuyruk deposunun istisnası bağlantı ayrıntısı taşıyabilir.
            _log.LogWarning("Gölge eşleştirme işi zamanlanamadı (ödeme {PaymentId}): {ErrorType}",
                paymentId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Karar sonrası müşterinin WhatsApp sohbetine otomatik etiket.
    ///
    /// <para>Ödeme SaveChanges'inden SONRA çağrılır: etiket yazılamasa bile
    /// onay kaydı yerinde kalır. Telefon kaynağı <c>Shopper.Phone</c> —
    /// ShopperId null ise (eski WhatsApp akışından gelen dekont) elimizde
    /// telefon yoktur, sessizce atlanır.</para>
    /// </summary>
    private async Task ApplyConversationLabelAsync(
        Payment payment, WaLabelEvent eventKey, CancellationToken ct)
    {
        if (payment.ShopperId is null) return;

        var phone = await _db.Shoppers
            .Where(s => s.Id == payment.ShopperId.Value)
            .Select(s => s.Phone)
            .FirstOrDefaultAsync(ct);

        await _labels.TryApplyAndSaveAsync(payment.LicenseId, eventKey, phone, ct);
    }

    /// <summary>
    /// Yayıncı dekontu onayladıktan / reddettikten sonra shopper'a push.
    /// ShopperId NULL ise legacy WhatsApp akışından gelen dekont — kimseye
    /// bildirim yok. NotificationsEnabledPayments kapalıysa skip. Best-effort:
    /// fail throw etmez, çağıranın yanıtını engellemez.
    /// Gövdede tutar ve ret gerekçesi yok (KVKK K-5): FCM üçüncü taraf, ayrıntı uygulamada.
    /// </summary>
    private async Task NotifyShopperPaymentDecisionAsync(
        Payment payment, bool approved, CancellationToken ct)
    {
        if (payment.ShopperId is null) return;

        try
        {
            var shopper = await _db.Shoppers
                .Where(s => s.Id == payment.ShopperId.Value
                    && s.DeletedAt == null
                    && s.NotificationsEnabledPayments)
                .Select(s => new { s.Id })
                .FirstOrDefaultAsync(ct);
            if (shopper is null) return;

            var broadcasterName = await _db.Licenses
                .Where(l => l.Id == payment.LicenseId)
                .Select(l => l.Customer.Name)
                .FirstOrDefaultAsync(ct) ?? "Yayıncı";

            // KVKK K-5: tutar ve ret gerekçesi FCM'ye gitmez; yayıncı adı (başlık) ve olgu kalır.
            var title = broadcasterName;
            var body = approved ? PaymentPushTexts.PaymentApprovedPushBody : PaymentPushTexts.PaymentRejectedPushBody;

            await _push.SendToShoppersAsync(
                new[] { shopper.Id },
                title: title,
                body: body,
                data: new Dictionary<string, string>
                {
                    ["type"] = approved ? "payment-approved" : "payment-rejected",
                    ["paymentId"] = payment.Id.ToString(),
                    ["licenseId"] = payment.LicenseId.ToString(),
                },
                ct: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Shopper payment decision push failed for payment={PaymentId}", payment.Id);
        }
    }
}
