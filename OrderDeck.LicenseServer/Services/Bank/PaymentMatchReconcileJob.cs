using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Dekont onayından sonra gölge bağdaştırma (spec §6). Onay isteği bağdaştırmayı beklemez ve onun hatasıyla
/// düşmez: <c>PanelPaymentsController.Approve</c> onay kaydedildikten sonra bu işi <see cref="ApprovalDelay"/> sonrasına
/// zamanlar.
/// <para>Yalnız hâlâ Approved olan ödeme ve Obifin bağlantısı olan lisans işlenir: bağlantısız lisansta banka hareketi
/// yoktur, her onay anlamsız bir NoCandidate gap'i yazıp ölçümü kirletirdi.</para>
/// <para>İdempotent (ödemenin gap'i ya da bağlı eşleşmesi varsa bir şey yazılmaz; bkz.
/// <see cref="PaymentMatchReconciler"/>): Hangfire yeniden denemesi ve çift teslim güvenli. İkinci eşzamanlılık
/// çakışmasında bağdaştırıcı uyarı loglayıp vazgeçer, iş başarıyla biter.</para></summary>
[AutomaticRetry(Attempts = 3)]
public sealed class PaymentMatchReconcileJob
{
    /// <summary>İş onaydan bu kadar sonra koşar. Dekontun kendi havalesi onay anında çoğu zaman henüz çekilmemiştir (çekim
    /// 5 dakikada bir, üstüne Obifin'in ve bankanın kayıt gecikmesi). Hemen koşsaydı, sabit fiyatlı satışta sık görülen
    /// aynı tutarlı BAŞKA bir müşterinin havalesi tek aday olarak bu dekonta bağlanırdı: öneri doğruyken Contradicted
    /// sayılır, ölçüm iz bırakmadan bozulurdu. Beklenince iki havale de adaydır; gönderen adı ayırır ya da dekont gap'e
    /// düşer. Bundan da geç gelen havale gap çözümüyle bağlanır.</summary>
    public static readonly TimeSpan ApprovalDelay = TimeSpan.FromMinutes(20);

    private readonly LicenseDbContext _db;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly ILogger<PaymentMatchReconcileJob> _log;

    public PaymentMatchReconcileJob(LicenseDbContext db, PaymentMatchReconciler reconciler, ILogger<PaymentMatchReconcileJob> log)
    {
        _db = db; _reconciler = reconciler; _log = log;
    }

    public async Task RunAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Status != PaymentStatus.Approved)
        {
            _log.LogInformation("Gölge bağdaştırma atlandı: ödeme {PaymentId} yok ya da onaylı değil", paymentId);
            return;
        }
        if (!await _db.ObifinConnections.AnyAsync(c => c.LicenseId == payment.LicenseId, ct)) return;
        await _reconciler.ReconcileApprovalAsync(payment, ct);
    }
}
