using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Lisans SMS kredi bakiyesi üzerinde ledger-tutarlı işlemler. Her değişiklik
/// bir <see cref="LicenseSmsTransaction"/> ekler ve cache <see cref="LicenseSmsBalance"/>
/// .CreditsRemaining'i günceller (invariant: CreditsRemaining = SUM(Amount)).
///
/// F03 (2026-09-09 denetimi): CreditsRemaining okuma-hesapla-yazma ile
/// güncellenir; UpdatedAt concurrency token'ı sayesinde eşzamanlı yazım
/// çakışması SaveChanges'te yakalanır ve <see cref="ApplyAndSaveAsync"/>
/// güncel değer üzerinden yeniden hesaplar. Bu yüzden SaveChanges bu servisin
/// İÇİNDE: retry döngüsü kaydı sarmak zorunda. Çağıranın aynı transaction'da
/// gitmesi gereken diğer değişiklikleri (ör. kampanya + alıcı satırları)
/// çağrıdan ÖNCE context'e eklenmiş olmalı — hepsi aynı SaveChanges'le yazılır.
/// </summary>
public sealed class LicenseSmsBalanceService
{
    private readonly LicenseDbContext _db;
    public LicenseSmsBalanceService(LicenseDbContext db) => _db = db;

    public sealed record BalanceInfo(int CreditsRemaining, DateTimeOffset UpdatedAt);

    /// <summary>Mevcut bakiye (satır yoksa 0).</summary>
    public async Task<BalanceInfo> GetAsync(Guid licenseId, CancellationToken ct)
    {
        var row = await _db.LicenseSmsBalances
            .Where(b => b.LicenseId == licenseId)
            .Select(b => new BalanceInfo(b.CreditsRemaining, b.UpdatedAt))
            .FirstOrDefaultAsync(ct);
        return row ?? new BalanceInfo(0, DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// Ledger'a tx ekler, cache bakiyeyi günceller ve <b>SaveChanges yapar</b>
    /// (context'te bekleyen diğer değişikliklerle birlikte, tek transaction).
    /// <paramref name="amount"/> işaretli (+ ekle / − kullan).
    ///
    /// Eşzamanlı yazım çakışmasında bakiye DB'den yeniden yüklenir, delta
    /// yeniden uygulanır ve <paramref name="disallowNegative"/> kontrolü
    /// GÜNCEL değer üzerinden tekrarlanır.
    /// </summary>
    /// <returns>Yeni bakiye; <c>null</c> = işlem bakiyeyi sıfırın altına
    /// düşürürdü, hiçbir şey yazılmadı.</returns>
    public async Task<int?> ApplyAndSaveAsync(
        Guid licenseId,
        int amount,
        string kind,
        string? reason,
        Guid? createdByCustomerId,
        bool disallowNegative,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        var now = DateTimeOffset.UtcNow;

        _db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Amount = amount,
            Kind = kind,
            Reason = reason,
            CreatedByCustomerId = createdByCustomerId,
            CreatedAt = now,
        });

        var balance = await _db.LicenseSmsBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId, ct);
        if (balance is null)
        {
            if (disallowNegative && amount < 0) return null;
            balance = new LicenseSmsBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreditsRemaining = amount,
                UpdatedAt = now,
            };
            _db.LicenseSmsBalances.Add(balance);
            // Yeni satır insert'i token'la korunmaz; eşzamanlı iki "ilk yazım"
            // unique LicenseId index'ine takılır → gürültülü DbUpdateException.
            await _db.SaveChangesAsync(ct);
            return balance.CreditsRemaining;
        }

        balance.CreditsRemaining += amount;
        balance.UpdatedAt = now;

        for (var attempt = 1; ; attempt++)
        {
            if (disallowNegative && balance.CreditsRemaining < 0) return null;
            try
            {
                await _db.SaveChangesAsync(ct);
                return balance.CreditsRemaining;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                // Araya başka yazım girdi (topup / rezerv / iade): güncel
                // değeri yükle, deltayı yeniden uygula. Added durumundaki
                // satırlar (ledger tx, kampanya, alıcılar) izlenmeye devam
                // eder ve sonraki SaveChanges'te yazılır.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
                balance.CreditsRemaining += amount;
                balance.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
