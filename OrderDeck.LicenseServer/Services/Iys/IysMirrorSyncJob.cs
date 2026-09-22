using Hangfire;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Günlük İYS eşitlemesi (spec §2.2): her DOĞRULANMIŞ hesap için
/// <see cref="IysMirrorImportJob"/>'u kuyruğa atar. Kendisi ayna KOŞTURMAZ —
/// lisans başına kilit, yeniden deneme ve Netgsm kota temposu (20'lik parti,
/// 6 sn) ayna işinde kalır. Maliyet artımlı DEĞİL: ayna yalnız ONAY satırı
/// yazar; İYS satırı (yerel beyanı da) olmayan numaralar her gece yeniden
/// sorulur; günlük yük ≈ (ONAY'sız müşteri sayısı / 20) `/iys/search` çağrısı,
/// yayıncının kendi Netgsm kotasından (paralel yayıncı sayısı büyüyünce bkz.
/// spec §5). Doğrulama anındaki tetik (PanelNetgsmAccountController) ilk
/// yüklemeyi yapar; bu iş sonradan İYS'ye başka yoldan giren onayları getirir.
/// Döngü ortasında oluşan bir arıza bazı hesapları o gece kuyruğa atılmamış
/// bırakabilir; ayna eşlemeli (idempotent) olduğundan bunlar ertesi gece
/// tamamlanır.
/// Kuyruğa atma arızası bilerek yakalanmaz: recurring koşu Hangfire panosunda
/// Failed görünsün (AutomaticRetry 0).
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysMirrorSyncJob
{
    private readonly NetgsmAccountService _accounts;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<IysMirrorSyncJob> _log;

    public IysMirrorSyncJob(
        NetgsmAccountService accounts,
        IBackgroundJobClient jobs,
        ILogger<IysMirrorSyncJob> log)
    {
        _accounts = accounts;
        _jobs = jobs;
        _log = log;
    }

    /// <returns>Kuyruğa atılan ayna işi sayısı (test için).</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var verified = await _accounts.ListVerifiedAsync(ct);
        foreach (var acc in verified)
        {
            var licenseId = acc.LicenseId;
            _jobs.Enqueue<IysMirrorImportJob>(
                j => j.RunAsync(licenseId, CancellationToken.None));
        }

        if (verified.Count > 0)
        {
            _log.LogInformation(
                "İYS eşitleme: {Count} doğrulanmış hesap için ayna işi kuyruğa alındı",
                verified.Count);
        }

        return verified.Count;
    }
}
