using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Lisans SMS kredi bakiyesi üzerinde ledger-tutarlı işlemler. Her değişiklik
/// bir <see cref="LicenseSmsTransaction"/> ekler ve cache <see cref="LicenseSmsBalance"/>
/// .CreditsRemaining'i günceller (invariant: CreditsRemaining = SUM(Amount)).
///
/// Çağıran <c>SaveChangesAsync</c>'i kendisi yapar (aynı transaction'da başka
/// değişikliklerle — ör. kampanya rezervasyonu — atomik kalsın diye).
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
    /// Ledger'a tx ekler ve cache bakiyeyi günceller. <paramref name="amount"/>
    /// işaretli (+ ekle / − kullan). <c>SaveChanges</c> ÇAĞIRMAZ — çağıran yapar.
    /// Yeni bakiyeyi döndürür (in-memory hesap).
    /// </summary>
    public async Task<int> ApplyAsync(
        Guid licenseId,
        int amount,
        string kind,
        string? reason,
        Guid? createdByCustomerId,
        CancellationToken ct)
    {
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
            balance = new LicenseSmsBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreditsRemaining = 0,
                UpdatedAt = now,
            };
            _db.LicenseSmsBalances.Add(balance);
        }

        balance.CreditsRemaining += amount;
        balance.UpdatedAt = now;
        return balance.CreditsRemaining;
    }
}
