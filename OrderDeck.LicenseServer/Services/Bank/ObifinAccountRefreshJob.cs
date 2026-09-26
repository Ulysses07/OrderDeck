using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Saatte bir hesap listesi (Durum, BildirimNotu, GuncellemeTarihi). Bir bağlantının hatası diğerini
/// durdurmaz: <see cref="ObifinConnectionService.RefreshAccountsAsync"/> istemci hatasını zaten bağlantıya yazıp
/// yeniden fırlatır (Obifin'in reddi Failed + LastError; ağ/vekil/zaman aşımı yalnız LastError, durum Verified kalır —
/// çekim sürer, sonraki saat yeniden dener); burada yalnız loglanır ve sıradakine geçilir. Koşu başarılı sayılır
/// (çekim işi asıl sinyal). Yalnız işin kendi iptali koşuyu keser.
/// <para>Kilit çekimle ORTAK (<see cref="ObifinPollJob.LockResource"/>): başarılı yenileme <c>UpdatedAt</c>'i yazar;
/// eşzamanlı bir çekim koşusu bunu kimlik değişimi sanıp boşuna iptal olur, yanlış "kimlik değişti" uyarısı
/// basardı. Çekimin bilinmeyen hesap için açtığı yer tutucu ile buradaki gerçek hesabın aynı
/// (LicenseId, ObifinAccountId) satırı için tekil index yarışı da böylece kalkar.</para></summary>
[DisableConcurrentExecution(ObifinPollJob.LockResource, 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class ObifinAccountRefreshJob
{
    private readonly LicenseDbContext _db;
    private readonly ObifinConnectionService _connections;
    private readonly ILogger<ObifinAccountRefreshJob> _log;

    public ObifinAccountRefreshJob(LicenseDbContext db, ObifinConnectionService connections, ILogger<ObifinAccountRefreshJob> log)
    { _db = db; _connections = connections; _log = log; }

    /// <summary>Başarıyla yenilenen bağlantı sayısı.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var licenseIds = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Verified).Select(c => c.LicenseId).ToListAsync(ct);
        var ok = 0;
        foreach (var licenseId in licenseIds)
        {
            try { await _connections.RefreshAccountsAsync(licenseId, ct); ok++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Sınıflandırılmış istemci hatası serviste Failed + LastError olarak yazıldı; çözülemeyen kimlik
                // (InvalidOperationException) ya da DB hatası da bu bağlantıya özgüdür — sıradaki beklemesin.
                // Yarım kalmış izleme (ör. yazılamayan hesap satırları) sıradakinin SaveChanges'ine taşınmasın.
                _db.ChangeTracker.Clear();
                _log.LogWarning(ex, "Obifin hesap yenileme düştü (lisans {LicenseId}); sıradakine geçiliyor", licenseId);
            }
        }
        return ok;
    }
}
